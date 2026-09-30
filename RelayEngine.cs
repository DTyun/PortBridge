using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PortBridge {
    public sealed class RelayGroup : IDisposable {
        readonly System.Collections.Generic.List<RelayEngine> engines = new System.Collections.Generic.List<RelayEngine>();
        bool disposed;
        public Action<string> Log;
        public bool Running { get; private set; }
        public long Sent { get { long sum = 0; foreach (var e in engines) sum += e.Sent; return sum; } }
        public long Received { get { long sum = 0; foreach (var e in engines) sum += e.Received; return sum; } }
        public int Connections { get { int sum = 0; foreach (var e in engines) sum += e.Connections; return sum; } }
        public void Start(Settings settings) {
            if (Running || disposed) throw new InvalidOperationException("请创建新的转发实例。");
            settings.Validate();
            var bind = settings.ListenAddress.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(settings.ListenAddress.Trim());
            var target = settings.TargetAddress.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(settings.TargetAddress.Trim());
            try {
                foreach (int port in settings.GetListenPorts()) {
                    var engine = new RelayEngine(); engines.Add(engine);
                    int sourcePort = port;
                    engine.Log = message => { var log = Log; if (log != null) log("[端口 " + sourcePort + "] " + message); };
                    try { engine.Start(bind, port, target, settings.TargetPort, settings.Tcp, settings.Udp); }
                    catch (Exception e) { throw new InvalidOperationException("监听端口 " + port + " 启动失败：" + e.Message, e); }
                }
                Running = true;
            } catch { Dispose(); throw; }
        }
        public void Dispose() { Running = false; disposed = true; foreach (var engine in engines) engine.Dispose(); }
    }
    public sealed class RelayEngine : IDisposable {
        readonly ConcurrentDictionary<TcpClient, byte> clients = new ConcurrentDictionary<TcpClient, byte>();
        readonly System.Collections.Generic.Dictionary<IPAddress, int> tcpSourceCounts = new System.Collections.Generic.Dictionary<IPAddress, int>();
        readonly ConcurrentDictionary<string, UdpSession> sessions = new ConcurrentDictionary<string, UdpSession>();
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        TcpListener tcp;
        UdpClient udp;
        Timer sweep;
        IPEndPoint destination;
        long sent, received;
        int active;
        const int MaxTcpConnectionsPerSource = 32;
        static readonly long TcpIdleTicks = TimeSpan.FromMinutes(2).Ticks;
        public Action<string> Log;
        public long Sent { get { return Interlocked.Read(ref sent); } }
        public long Received { get { return Interlocked.Read(ref received); } }
        public int Connections { get { return Volatile.Read(ref active) + sessions.Count; } }
        public bool Running { get; private set; }
        sealed class UdpSession {
            public UdpClient Socket;
            public IPEndPoint Source;
            public long Last;
        }
        sealed class TcpActivity {
            public long Last = DateTime.UtcNow.Ticks;
            public volatile bool Expired;
        }
        void Report(string message) { var log = Log; if (log != null && !cancel.IsCancellationRequested) log(message); }
        public void Start(IPAddress bind, int port, IPAddress target, int targetPort, bool useTcp, bool useUdp) {
            if (Running || cancel.IsCancellationRequested) throw new InvalidOperationException("请创建新的转发实例。");
            if (!useTcp && !useUdp) throw new ArgumentException("请至少选择 TCP 或 UDP。");
            if (port < 1 || port > 65535 || targetPort < 1 || targetPort > 65535) throw new ArgumentException("端口范围为 1–65535。");
            if (target.Equals(IPAddress.Any) || target.Equals(IPAddress.IPv6Any)) throw new ArgumentException("目标地址不能为通配地址。");
            if (port == targetPort && IsLocalTarget(bind, target)) throw new ArgumentException("监听地址与目标不能指向同一端口，否则会循环转发。");
            destination = new IPEndPoint(target, targetPort);
            try {
                if (useTcp) { tcp = new TcpListener(bind, port); tcp.Server.ExclusiveAddressUse = true; tcp.Start(128); }
                if (useUdp) { udp = new UdpClient(bind.AddressFamily); udp.ExclusiveAddressUse = true; udp.Client.ReceiveBufferSize = 4 * 1024 * 1024; udp.Client.SendBufferSize = 1024 * 1024; udp.Client.Bind(new IPEndPoint(bind, port)); }
                Running = true;
                if (tcp != null) { var ignored = AcceptLoop(); }
                if (udp != null) { var ignored = UdpLoop(); sweep = new Timer(Expire, null, 10000, 10000); }
            } catch { Dispose(); throw; }
        }
        static bool IsLocalTarget(IPAddress bind, IPAddress target) {
            if (bind.Equals(target)) return true;
            if (!bind.Equals(IPAddress.Any) && !bind.Equals(IPAddress.IPv6Any)) return false;
            if (IPAddress.IsLoopback(target)) return true;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var a in nic.GetIPProperties().UnicastAddresses) if (a.Address.Equals(target)) return true;
            return false;
        }
        async Task AcceptLoop() {
            while (!cancel.IsCancellationRequested) {
                try {
                    var incoming = await tcp.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (cancel.IsCancellationRequested || Volatile.Read(ref active) >= 256) { incoming.Close(); continue; }
                    var sourceAddress = ((IPEndPoint)incoming.Client.RemoteEndPoint).Address;
                    if (sourceAddress.IsIPv4MappedToIPv6) sourceAddress = sourceAddress.MapToIPv4();
                    if (!AdmitTcpSource(sourceAddress)) { incoming.Close(); continue; }
                    Interlocked.Increment(ref active);
                    clients.TryAdd(incoming, 0);
                    var ignored = Forward(incoming, sourceAddress);
                } catch (Exception e) {
                    if (!cancel.IsCancellationRequested) Report("TCP 监听异常：" + e.Message);
                }
                if (!cancel.IsCancellationRequested) await Task.Delay(1).ConfigureAwait(false);
            }
        }
        bool AdmitTcpSource(IPAddress address) {
            lock (tcpSourceCounts) {
                int count;
                tcpSourceCounts.TryGetValue(address, out count);
                if (count >= MaxTcpConnectionsPerSource) return false;
                tcpSourceCounts[address] = count + 1;
                return true;
            }
        }
        void ReleaseTcpSource(IPAddress address) {
            lock (tcpSourceCounts) {
                int count = tcpSourceCounts[address] - 1;
                if (count == 0) tcpSourceCounts.Remove(address);
                else tcpSourceCounts[address] = count;
            }
        }
        async Task Forward(TcpClient source, IPAddress sourceAddress) {
            TcpClient target = null;
            Timer idleSweep = null;
            var activity = new TcpActivity();
            try {
                if (cancel.IsCancellationRequested) return;
                target = new TcpClient(destination.AddressFamily);
                clients.TryAdd(target, 0);
                source.NoDelay = target.NoDelay = true;
                var connect = target.ConnectAsync(destination.Address, destination.Port);
                if (await Task.WhenAny(connect, Task.Delay(10000, cancel.Token)).ConfigureAwait(false) != connect) {
                    target.Close();
                    try { await connect.ConfigureAwait(false); } catch { }
                    if (!cancel.IsCancellationRequested) throw new TimeoutException("目标连接超时（10 秒）。");
                    return;
                }
                await connect.ConfigureAwait(false);
                // Capture both streams before either pump can half-close a socket.
                var sourceStream = source.GetStream();
                var targetStream = target.GetStream();
                idleSweep = new Timer(state => {
                    if (DateTime.UtcNow.Ticks - Interlocked.Read(ref activity.Last) <= TcpIdleTicks) return;
                    activity.Expired = true;
                    source.Close(); target.Close();
                }, null, 10000, 10000);
                var up = Copy(sourceStream, targetStream, target.Client, true, activity);
                var down = Copy(targetStream, sourceStream, source.Client, false, activity);
                var first = await Task.WhenAny(up, down).ConfigureAwait(false);
                if (first.IsFaulted || first.IsCanceled) { source.Close(); target.Close(); }
                await Task.WhenAll(up, down).ConfigureAwait(false);
            } catch (Exception e) { if (!activity.Expired) Report("TCP 转发失败：" + e.Message); }
            finally { if (idleSweep != null) idleSweep.Dispose(); byte value; clients.TryRemove(source, out value); if (target != null) clients.TryRemove(target, out value); source.Close(); if (target != null) target.Close(); ReleaseTcpSource(sourceAddress); Interlocked.Decrement(ref active); }
        }
        async Task Copy(NetworkStream input, NetworkStream output, Socket outputSocket, bool upload, TcpActivity activity) {
            byte[] buffer = new byte[32768];
            while (true) {
                int count = await input.ReadAsync(buffer, 0, buffer.Length, cancel.Token).ConfigureAwait(false);
                if (count == 0) { try { outputSocket.Shutdown(SocketShutdown.Send); } catch (SocketException) { } catch (ObjectDisposedException) { } return; }
                await output.WriteAsync(buffer, 0, count, cancel.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref activity.Last, DateTime.UtcNow.Ticks);
                if (upload) Interlocked.Add(ref sent, count); else Interlocked.Add(ref received, count);
            }
        }
        async Task UdpLoop() {
            while (!cancel.IsCancellationRequested) {
                try {
                    var packet = await udp.ReceiveAsync().ConfigureAwait(false);
                    if (cancel.IsCancellationRequested) break;
                    string key = packet.RemoteEndPoint.ToString();
                    UdpSession session;
                    if (!sessions.TryGetValue(key, out session)) {
                        if (sessions.Count >= 256) { Report("UDP 会话已达 256 个，丢弃新来源数据。"); continue; }
                        session = new UdpSession { Socket = new UdpClient(destination.AddressFamily), Source = packet.RemoteEndPoint, Last = DateTime.UtcNow.Ticks };
                        session.Socket.Client.ReceiveBufferSize = 256 * 1024;
                        session.Socket.Connect(destination);
                        sessions[key] = session;
                        if (cancel.IsCancellationRequested) { RemoveSession(key, session); break; }
                        var ignored = ReceiveUdp(key, session);
                    }
                    Interlocked.Exchange(ref session.Last, DateTime.UtcNow.Ticks);
                    try {
                        await session.Socket.SendAsync(packet.Buffer, packet.Buffer.Length).ConfigureAwait(false);
                        Interlocked.Add(ref sent, packet.Buffer.Length);
                    } catch { RemoveSession(key, session); throw; }
                } catch (Exception e) { if (!cancel.IsCancellationRequested) Report("UDP 转发失败：" + e.Message); }
            }
        }
        async Task ReceiveUdp(string key, UdpSession session) {
            try {
                while (!cancel.IsCancellationRequested) {
                    var packet = await session.Socket.ReceiveAsync().ConfigureAwait(false);
                    Interlocked.Exchange(ref session.Last, DateTime.UtcNow.Ticks);
                    await udp.SendAsync(packet.Buffer, packet.Buffer.Length, session.Source).ConfigureAwait(false);
                    Interlocked.Add(ref received, packet.Buffer.Length);
                }
            } catch (Exception e) { if (DateTime.UtcNow.Ticks - Interlocked.Read(ref session.Last) < TimeSpan.FromSeconds(60).Ticks) Report("UDP 回传结束：" + e.Message); }
            finally { RemoveSession(key, session); }
        }
        void RemoveSession(string key, UdpSession value) {
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, UdpSession>>)sessions).Remove(new System.Collections.Generic.KeyValuePair<string, UdpSession>(key, value));
            value.Socket.Close();
        }
        void Expire(object state) {
            foreach (var entry in sessions) if (DateTime.UtcNow.Ticks - Interlocked.Read(ref entry.Value.Last) > TimeSpan.FromSeconds(60).Ticks) RemoveSession(entry.Key, entry.Value);
        }
        public void Dispose() {
            Running = false;
            cancel.Cancel();
            if (sweep != null) sweep.Dispose();
            if (tcp != null) tcp.Stop();
            if (udp != null) udp.Close();
            foreach (var pair in clients) pair.Key.Close();
            foreach (var pair in sessions) RemoveSession(pair.Key, pair.Value);
        }
    }
}
