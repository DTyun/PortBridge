using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PortBridge;
class Tests {
    static int checks;
    static readonly Random portRandom = new Random();
    static void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static int FreePort() {
        for (int i = 0; i < 512; i++) {
            int p = portRandom.Next(15000, 60000); var l = new TcpListener(IPAddress.Loopback, p);
            try { l.Start(); using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, p))) return p; }
            catch (SocketException) { } finally { l.Stop(); }
        }
        throw new Exception("No TCP/UDP test port available");
    }
    static RelayEngine Start(int port, int target, bool tcp, bool udp) { var e = new RelayEngine(); e.Log = s => Console.WriteLine("RELAY " + s); e.Start(IPAddress.Loopback, port, IPAddress.Loopback, target, tcp, udp); return e; }
    static void TcpRoundtrip(int port, int seed) {
        using (var c = new TcpClient()) {
            c.Connect(IPAddress.Loopback, port); c.ReceiveTimeout = c.SendTimeout = 5000;
            byte[] data = new byte[100000]; new Random(seed).NextBytes(data);
            var stream = c.GetStream(); stream.Write(data, 0, data.Length); c.Client.Shutdown(SocketShutdown.Send);
            var output = new MemoryStream(); var b = new byte[8192]; int n; while ((n = stream.Read(b, 0, b.Length)) != 0) output.Write(b, 0, n);
            if (!data.SequenceEqual(output.ToArray())) throw new Exception("TCP payload mismatch seed=" + seed + " length=" + output.Length);
        }
    }
    static void UdpRoundtrip(int port, int seed) {
        using (var c = new UdpClient()) {
            c.Client.ReceiveTimeout = 5000; c.Connect(IPAddress.Loopback, port);
            foreach (int length in new[] { 0, 1, 8192, 60000 }) {
                var bytes = new byte[length]; new Random(seed).NextBytes(bytes); c.Send(bytes, bytes.Length);
                IPEndPoint ep = null; var result = c.Receive(ref ep);
                if (!bytes.SequenceEqual(result)) throw new Exception("UDP payload mismatch");
            }
        }
    }
    static string Headers(Stream s) { var b = new StringBuilder(); while (b.Length < 16384) { int c = s.ReadByte(); if (c < 0) break; b.Append((char)c); if (b.ToString().EndsWith("\r\n\r\n")) break; } return b.ToString(); }
    static byte[] ReadBytes(Stream s, int count) { var b = new byte[count]; int p = 0; while (p < count) { int n = s.Read(b, p, count - p); if (n == 0) throw new IOException(); p += n; } return b; }
    static void Send(Stream s, string value) { byte[] b = Encoding.ASCII.GetBytes(value); s.Write(b, 0, b.Length); }
    static void Diagnostic(bool socks, int status, bool multi = false) {
        var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        int target = ((IPEndPoint)proxy.LocalEndpoint).Port; int entry = FreePort(); int validRequests = 0;
        var worker = Task.Run(async delegate {
            try { while (true) {
                var c = await proxy.AcceptTcpClientAsync();
                var ignored = Task.Run(delegate {
                    using (c) {
                        c.ReceiveTimeout = c.SendTimeout = 2000;
                        try {
                            var stream = c.GetStream(); int first = stream.ReadByte(); if (first < 0) return;
                            if (socks) {
                                if (first != 5) return;
                                ReadBytes(stream, 2); stream.Write(new byte[] { 5, 0 }, 0, 2);
                                var prefix = ReadBytes(stream, 5); if (prefix[3] != 3) throw new Exception("domain required");
                                string domain = Encoding.ASCII.GetString(ReadBytes(stream, prefix[4])); ReadBytes(stream, 2);
                                if (domain != "example.com") throw new Exception("incorrect domain");
                                stream.Write(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, 0, 10);
                            } else {
                                if (first != 'C') return;
                                string header = "C" + Headers(stream); if (!header.StartsWith("CONNECT example.com:80 HTTP/1.1")) throw new Exception("incorrect CONNECT");
                                Send(stream, "HTTP/1.1 200 Connection established\r\n\r\n");
                            }
                            string get = Headers(stream); if (!get.StartsWith("GET /probe HTTP/1.1")) throw new Exception("incorrect GET");
                            Interlocked.Increment(ref validRequests); Send(stream, "HTTP/1.1 " + status + " Test\r\nContent-Length: 0\r\n\r\n");
                        } catch (IOException) { } catch (SocketException) { }
                    }
                });
            } } catch (ObjectDisposedException) { } catch (SocketException) { }
        });
        try {
            int second = FreePort(); while (second == entry) second = FreePort();
            var config = new Settings { ListenPort = entry, ListenPorts = multi ? entry + "," + second : null, TargetPort = target, Udp = false };
            var logs = new System.Collections.Generic.List<string>();
            bool passed = new ConnectionTester { TimeoutMilliseconds = 1000 }.Run(config, false, "http://example.com/probe", logs.Add, CancellationToken.None);
            Assert(passed == (status == 204) && validRequests == (multi ? 2 : 1), "diagnostic " + (multi ? "multiple ports " : "") + (socks ? "SOCKS5 fallback" : "HTTP CONNECT") + " status " + status);
            var probe = new TcpListener(IPAddress.Loopback, entry); probe.Start(); probe.Stop(); Assert(true, "diagnostic temporary relay cleaned up");
            using (var active = new RelayGroup()) {
                active.Start(config);
                bool again = new ConnectionTester { TimeoutMilliseconds = 1000 }.Run(config, true, "http://example.com/probe", logs.Add, CancellationToken.None);
                Assert(again == (status == 204), "diagnostic existing listeners result");
                Assert(active.Running, "diagnostic preserves existing relay");
            }
            if (multi && status == 204) {
                using (var onlyFirst = Start(entry, target, true, false)) {
                    bool partial = new ConnectionTester { TimeoutMilliseconds = 300 }.Run(config, true, "http://example.com/probe", logs.Add, CancellationToken.None);
                    Assert(!partial && logs.Any(s => s.Contains("1/2")), "one failed port prevents overall pass");
                }
            }
        } finally { proxy.Stop(); }
    }
    [STAThread] static int Main() {
        try {
            ThreadPool.SetMinThreads(32, 32);
            string configPath = Path.Combine(Environment.CurrentDirectory, "config-test-" + Guid.NewGuid().ToString("N") + ".xml");
            try {
                var source = new Settings { ListenAddress = "::1", ListenPort = 12345, TargetAddress = "127.0.0.1", TargetPort = 25378, Tcp = true, Udp = false, AutoRelay = true };
                source.SaveTo(configPath); var loaded = Settings.Read(configPath);
                Assert(loaded.ListenAddress == "::1" && loaded.ListenPort == 12345 && loaded.TargetPort == 25378 && !loaded.Udp && loaded.AutoRelay, "configuration export/import roundtrip");
                source.TargetPort = 25379; source.SaveTo(configPath); Assert(Settings.Read(configPath).TargetPort == 25379, "configuration overwrite");
                source.ListenPort = 0; bool rejected = false; try { source.SaveTo(configPath); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && Settings.Read(configPath).ListenPort == 12345, "invalid export preserves existing config");
                File.WriteAllText(configPath, "<Settings><ListenPort>70000</ListenPort></Settings>"); rejected = false;
                try { Settings.Read(configPath); } catch (ArgumentException) { rejected = true; } Assert(rejected, "invalid import port rejected");
                File.WriteAllText(configPath, "<!DOCTYPE Settings [<!ENTITY x SYSTEM 'file:///never-read'>]><Settings><ListenAddress>&x;</ListenAddress></Settings>"); rejected = false;
                try { Settings.Read(configPath); } catch (Exception) { rejected = true; } Assert(rejected, "external XML entities prohibited");
                var defaults = Settings.Read(Path.Combine(Environment.CurrentDirectory, "PortBridge.config.xml")); Assert(defaults.ListenPort == 7890 && defaults.TargetPort == 23578, "bundled example imports");
                source.ListenPorts = "7890, 7891，7892"; source.SaveTo(configPath); loaded = Settings.Read(configPath);
                Assert(loaded.GetListenPorts().SequenceEqual(new[] { 7890, 7891, 7892 }), "multiple ports config roundtrip and Chinese comma");
                foreach (string bad in new[] { "", "7890,", "7890,,7891", "7890,7890", "0", "65536", "abc", "-1" }) {
                    bool failed = false; try { new Settings { ListenPorts = bad }.Validate(); } catch (ArgumentException) { failed = true; }
                    Assert(failed, "reject invalid ports: " + bad);
                }
                File.WriteAllText(configPath, "<Settings><ListenPort>8123</ListenPort></Settings>"); Assert(Settings.Read(configPath).GetListenPorts()[0] == 8123, "legacy single-port XML migration");
            } finally { if (File.Exists(configPath)) File.Delete(configPath); }
            int targetPort = FreePort();
            var server = new TcpListener(IPAddress.Loopback, targetPort); server.Start();
            var udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, targetPort));
            udpServer.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            Task.Run(async delegate {
                try { while (true) {
                    var c = await server.AcceptTcpClientAsync();
                    var ignored = Task.Run(async delegate { using (c) { var s = c.GetStream(); var data = new MemoryStream(); var b = new byte[8192]; int n; while ((n = await s.ReadAsync(b, 0, b.Length)) != 0) data.Write(b, 0, n); byte[] response = data.ToArray(); await s.WriteAsync(response, 0, response.Length); } });
                } } catch (ObjectDisposedException) { } catch (SocketException) { }
            });
            Task.Run(async delegate { try { while (true) { var p = await udpServer.ReceiveAsync(); await udpServer.SendAsync(p.Buffer, p.Buffer.Length, p.RemoteEndPoint); } } catch (ObjectDisposedException) { } catch (SocketException) { } });
            int port = FreePort();
            using (var relay = Start(port, targetPort, true, true)) {
                TcpRoundtrip(port, 1); Assert(true, "TCP 100 KB binary and half-close response");
                Task.WaitAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => TcpRoundtrip(port, i))).ToArray()); Assert(true, "16 concurrent TCP clients");
                UdpRoundtrip(port, 1); Assert(true, "UDP 0 / 1 / 8192 / 60000 byte datagrams");
                Task.WaitAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => UdpRoundtrip(port, i))).ToArray()); Assert(true, "8 independent UDP return mappings");
                Assert(relay.Sent >= 1700000 && relay.Received >= 1700000, "bidirectional traffic counters");
                using (var held = new TcpClient()) {
                    held.Connect(IPAddress.Loopback, port); Thread.Sleep(100); relay.Dispose(); held.ReceiveTimeout = 3000;
                    bool closed = false; try { closed = held.GetStream().ReadByte() == -1; } catch (IOException) { closed = true; }
                    Assert(closed, "pause closes existing TCP sessions");
                }
            }
            using (var restarted = Start(port, targetPort, true, true)) { TcpRoundtrip(port, 3); UdpRoundtrip(port, 3); Assert(true, "immediate restart releases TCP and UDP ports"); }
            var blocker = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            bool conflict = false; try { using (var r = Start(port, targetPort, true, true)) { } } catch (SocketException) { conflict = true; }
            Assert(conflict, "UDP port conflict rejects start");
            var tcpProbe = new TcpListener(IPAddress.Loopback, port); tcpProbe.Start(); tcpProbe.Stop(); blocker.Close(); Assert(true, "partial start rolls back TCP binding");
            bool loop = false; try { using (var r = Start(port, port, true, true)) { } } catch (ArgumentException) { loop = true; } Assert(loop, "self-loop rejected");
            using (var r = Start(port, targetPort, true, false)) { TcpRoundtrip(port, 4); Assert(true, "TCP only"); }
            using (var r = Start(port, targetPort, false, true)) { UdpRoundtrip(port, 4); Assert(true, "UDP only"); }
            int secondPort = FreePort(); while (secondPort == port) secondPort = FreePort();
            var groupSettings = new Settings { ListenPorts = port + "," + secondPort, TargetPort = targetPort };
            using (var group = new RelayGroup()) {
                group.Start(groupSettings);
                TcpRoundtrip(port, 7); TcpRoundtrip(secondPort, 8); UdpRoundtrip(port, 7); UdpRoundtrip(secondPort, 8);
                Assert(group.Sent >= 200000 && group.Received >= 200000, "multiple TCP/UDP listeners and aggregate traffic");
            }
            using (var restarted = new RelayGroup()) { restarted.Start(groupSettings); TcpRoundtrip(secondPort, 9); Assert(true, "all ports released after group pause"); }
            using (var blocked = new UdpClient(new IPEndPoint(IPAddress.Loopback, secondPort))) {
                bool failed = false; using (var group = new RelayGroup()) {
                    try { group.Start(groupSettings); } catch (InvalidOperationException e) { failed = e.Message.Contains(secondPort.ToString()); }
                    Assert(failed && !group.Running, "second port conflict reports port and cancels group");
                }
                using (var firstAgain = Start(port, targetPort, true, true)) { TcpRoundtrip(port, 10); Assert(true, "group rollback frees earlier TCP and UDP bindings"); }
            }
            server.Stop(); udpServer.Close();
            using (var r = Start(port, targetPort, true, false)) {
                using (var c = new TcpClient()) { c.Connect(IPAddress.Loopback, port); c.ReceiveTimeout = 4000; bool closed = false; try { closed = c.GetStream().ReadByte() == -1; } catch (IOException) { closed = true; } Assert(closed, "unavailable target closes client"); }
            }
            Diagnostic(false, 204); Diagnostic(true, 204); Diagnostic(false, 503);
            Diagnostic(false, 204, true); Diagnostic(true, 204, true);
            var diagnosticLogs = new System.Collections.Generic.List<string>();
            Assert(!new ConnectionTester().Run(new Settings(), false, "bad-url", diagnosticLogs.Add, CancellationToken.None), "invalid test URL rejected");
            Assert(!new ConnectionTester().Run(new Settings { Tcp = false }, false, "https://example.com", diagnosticLogs.Add, CancellationToken.None), "UDP-only test not falsely passed");
            using (var cancellation = new CancellationTokenSource()) { cancellation.Cancel(); Assert(!new ConnectionTester().Run(new Settings(), false, "https://example.com", diagnosticLogs.Add, cancellation.Token), "cancelled test not falsely passed"); }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm(false, new Settings { ListenPorts = "7890,7891,7892" })) {
                form.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save("ui-preview.png"); }
                Assert(form.Visible, "native form renders");
                form.WindowState = FormWindowState.Minimized; Application.DoEvents(); Assert(!form.Visible, "minimize hides to tray");
                form.Restore(); Application.DoEvents(); Assert(form.Visible && form.WindowState == FormWindowState.Normal, "restore from tray");
                form.Close(); Application.DoEvents(); Assert(!form.IsDisposed && !form.Visible, "window close keeps tray process alive");
                typeof(MainForm).GetField("exiting", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true); form.Close(); Assert(form.IsDisposed, "explicit exit disposes window");
            }
            using (var dialog = new ConnectionTestForm(new Settings { Tcp = false }, false)) {
                dialog.Show(); var until = DateTime.UtcNow.AddSeconds(4); while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save("test-window-preview.png"); }
                var result = (Label)typeof(ConnectionTestForm).GetField("result", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
                Assert(result.Text.Contains("测试不通过"), "test window logs final failure"); dialog.Close();
            }
            Console.WriteLine("ALL " + checks + " CHECKS PASSED"); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
