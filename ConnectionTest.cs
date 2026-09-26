using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortBridge {
    public sealed class ConnectionTester {
        public int TimeoutMilliseconds = 8000;
        public bool Run(Settings settings, bool alreadyRunning, string url, Action<string> log, CancellationToken token) {
            RelayGroup temporary = null;
            var watch = Stopwatch.StartNew();
            try {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !String.IsNullOrEmpty(uri.UserInfo))
                    throw new ArgumentException("测试网址必须为 http:// 或 https:// 开头，且不能包含登录信息。");
                if (!settings.Tcp) throw new InvalidOperationException("外网网页测试需要启用 TCP；本测试不验证 UDP。");
                settings.Validate();
                var bind = Address(settings.ListenAddress); var target = Address(settings.TargetAddress);
                var entry = bind.Equals(IPAddress.Any) ? IPAddress.Loopback : bind.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : bind;
                log("测试路径：" + entry + ":[" + settings.PortsText() + "] → " + target + ":" + settings.TargetPort + " → " + uri.GetLeftPart(UriPartial.Authority));
                log("仅测试 TCP 网页链路；不会绕过中转直连外网。每种协议最多 20 秒。");
                token.ThrowIfCancellationRequested();
                if (!alreadyRunning) {
                    log("当前已暂停，正在临时启动全部监听端口……"); temporary = new RelayGroup();
                    temporary.Start(settings);
                    log("临时监听成功。");
                } else log("使用当前正在运行的转发。");
                using (var client = new TcpClient(target.AddressFamily)) using (token.Register(client.Close)) {
                    log("检查目标端口是否接受 TCP 连接……"); Connect(client, target, settings.TargetPort, token); log("目标端口连接成功。");
                }
                int passed = 0; int[] ports = settings.GetListenPorts();
                foreach (int port in ports) {
                  bool portPassed = false;
                  log("—— 正在测试监听端口 " + port + " ——");
                  foreach (string protocol in new[] { "HTTP CONNECT", "SOCKS5" }) {
                    token.ThrowIfCancellationRequested();
                    using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                        attempt.CancelAfter(20000);
                        try {
                            log("开始 " + protocol + " 全链路测试……");
                            Probe(entry, port, uri, protocol == "SOCKS5", log, attempt.Token);
                            token.ThrowIfCancellationRequested();
                            log("端口 " + port + " 测试通过：经中转和 " + protocol + " 成功访问测试网址。");
                            portPassed = true; passed++; break;
                        } catch (Exception e) {
                            token.ThrowIfCancellationRequested();
                            log(protocol + " 未通过：" + (attempt.IsCancellationRequested ? "本次协议测试超过 20 秒。" : e.Message));
                        }
                    }
                  }
                  if (!portPassed) log("端口 " + port + " 测试不通过：两种代理协议都未完成网页请求。");
                }
                log((passed == ports.Length ? "测试通过" : "测试不通过") + "：" + passed + "/" + ports.Length + " 个监听端口通过，用时 " + watch.Elapsed.TotalSeconds.ToString("F1") + " 秒。");
                return passed == ports.Length;
            } catch (OperationCanceledException) { log("测试已取消，未得出通过结论。"); return false; }
            catch (Exception e) { if (token.IsCancellationRequested) log("测试已取消。"); else log("测试不通过：" + e.Message); return false; }
            finally { if (temporary != null) { temporary.Dispose(); log("临时转发已关闭，已恢复暂停状态。"); } }
        }
        static IPAddress Address(string text) { return text.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(text.Trim()); }
        void Connect(TcpClient client, IPAddress address, int port, CancellationToken token) {
            var connect = client.ConnectAsync(address, port);
            try {
                if (!connect.Wait(TimeoutMilliseconds, token)) {
                    client.Close(); connect.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException("TCP 连接超时，请检查地址和端口。");
                }
            } catch (AggregateException e) { throw e.GetBaseException(); }
            client.ReceiveTimeout = client.SendTimeout = TimeoutMilliseconds;
        }
        void Probe(IPAddress entry, int port, Uri uri, bool socks, Action<string> log, CancellationToken token) {
            using (var client = new TcpClient(entry.AddressFamily)) using (token.Register(client.Close)) {
                Connect(client, entry, port, token); log("监听端口连接成功，正在通过中转与目标代理握手……");
                Stream stream = client.GetStream(); stream.ReadTimeout = stream.WriteTimeout = TimeoutMilliseconds;
                string host = uri.IdnHost;
                string authority = (host.IndexOf(':') >= 0 ? "[" + host + "]" : host) + ":" + uri.Port;
                if (socks) {
                    stream.Write(new byte[] { 5, 1, 0 }, 0, 3);
                    byte[] greeting = ReadExact(stream, 2);
                    if (greeting[0] != 5 || greeting[1] != 0) throw new IOException("目标不支持免认证 SOCKS5，或需要用户名密码。");
                    byte[] domain = Encoding.ASCII.GetBytes(host);
                    if (domain.Length > 255) throw new IOException("域名过长。");
                    var request = new byte[7 + domain.Length]; request[0] = 5; request[1] = 1; request[3] = 3; request[4] = (byte)domain.Length;
                    Buffer.BlockCopy(domain, 0, request, 5, domain.Length); request[request.Length - 2] = (byte)(uri.Port >> 8); request[request.Length - 1] = (byte)uri.Port;
                    stream.Write(request, 0, request.Length);
                    byte[] reply = ReadExact(stream, 4);
                    if (reply[0] != 5 || reply[2] != 0 || reply[1] != 0) throw new IOException("SOCKS5 无法连接网站，响应代码 " + reply[1] + "。");
                    int length = reply[3] == 1 ? 4 : reply[3] == 4 ? 16 : reply[3] == 3 ? ReadExact(stream, 1)[0] : -1;
                    if (length < 0) throw new IOException("SOCKS5 响应格式错误。"); ReadExact(stream, length + 2);
                } else {
                    Write(stream, "CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\nProxy-Connection: keep-alive\r\n\r\n");
                    int status = Status(ReadHeaders(stream));
                    if (status != 200) throw new IOException("HTTP 代理隧道建立失败，状态 " + status + (status == 407 ? "（代理需要身份认证）。" : "。"));
                }
                log("代理隧道建立成功，网站：" + authority);
                if (uri.Scheme == "https") {
                    log("正在进行 HTTPS 握手并校验网站证书……");
                    var ssl = new SslStream(stream, false);
                    stream = ssl;
                    ssl.ReadTimeout = ssl.WriteTimeout = TimeoutMilliseconds;
                    ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                    log("HTTPS 握手和证书校验通过。");
                } else log("当前网址为 HTTP，不进行 HTTPS 证书校验。");
                using (stream) {
                    Write(stream, "GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + authority + "\r\nUser-Agent: PortBridge-ConnectionTest/1.1\r\nAccept: */*\r\nConnection: close\r\n\r\n");
                    int response = Status(ReadHeaders(stream)); log("网站返回 HTTP " + response + "。");
                    if (response < 200 || response >= 300) throw new IOException("网站未返回成功状态（2xx）；如为跳转，请填写最终网址后重试。");
                    log("已收到网站成功响应头，双向链路验证完成。");
                }
            }
        }
        static void Write(Stream stream, string text) { byte[] bytes = Encoding.ASCII.GetBytes(text); stream.Write(bytes, 0, bytes.Length); }
        static byte[] ReadExact(Stream stream, int count) { var bytes = new byte[count]; int offset = 0; while (offset < count) { int n = stream.Read(bytes, offset, count - offset); if (n == 0) throw new IOException("连接提前关闭，未收到完整响应。"); offset += n; } return bytes; }
        static string ReadHeaders(Stream stream) {
            var bytes = new MemoryStream(); int matched = 0; byte[] end = { 13, 10, 13, 10 };
            while (bytes.Length < 16384) { int value = stream.ReadByte(); if (value < 0) throw new IOException("连接提前关闭，未收到 HTTP 响应。"); bytes.WriteByte((byte)value); matched = value == end[matched] ? matched + 1 : value == 13 ? 1 : 0; if (matched == 4) return Encoding.ASCII.GetString(bytes.ToArray()); }
            throw new IOException("HTTP 响应头超过 16 KB。");
        }
        static int Status(string headers) { string[] words = headers.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); int code; if (words.Length < 2 || !words[0].StartsWith("HTTP/1.") || !Int32.TryParse(words[1], out code)) throw new IOException("收到的不是有效 HTTP 响应。"); return code; }
    }
    public sealed class ConnectionTestForm : Form {
        readonly TextBox url, output;
        readonly Label result;
        readonly PictureBox resultGlyph;
        readonly Button run, cancel;
        readonly Settings settings;
        readonly bool alreadyRunning;
        CancellationTokenSource cancellation;
        bool busy;
        public ConnectionTestForm(Settings value, bool running) {
            settings = value; alreadyRunning = running;
            Text = "测试连接 · PortBridge"; Font = Theme.Body; BackColor = Theme.Background; ForeColor = Theme.Ink;
            ClientSize = new Size(760, 540); MinimumSize = new Size(680, 500); StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            Icon = IconArtwork.CreateIcon(IconState.App, 32);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.Controls.Add(new Label { Text = "外网测试网址（通过当前中转访问）", AutoSize = true }, 0, 0);
            url = new TextBox { Text = "https://www.gstatic.com/generate_204", Dock = DockStyle.Fill, AccessibleName = "外网测试网址" }; layout.Controls.Add(url, 0, 1);
            output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, AccessibleName = "连接测试日志" }; layout.Controls.Add(output, 0, 2);
            var resultPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            resultPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38)); resultPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            resultGlyph = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "连接结果图标" };
            result = new Label { Text = "正在准备测试……", Dock = DockStyle.Fill, Padding = new Padding(2, 0, 0, 0), AccessibleName = "测试结果", TextAlign = ContentAlignment.MiddleLeft };
            resultPanel.Controls.Add(resultGlyph, 0, 0); resultPanel.Controls.Add(result, 1, 0); layout.Controls.Add(resultPanel, 0, 3);
            SetResultIcon(IconState.Starting);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            run = new Button { Text = "重新测试", Size = new Size(130, 36) }; cancel = new Button { Text = "取消测试", Size = new Size(130, 36) };
            actions.Controls.Add(run); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 4); Controls.Add(layout);
            run.Click += async delegate { await RunTest(); }; cancel.Click += delegate { if (busy) { cancellation.Cancel(); cancel.Enabled = false; } else Close(); };
            Shown += async delegate { await RunTest(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { cancellation.Cancel(); e.Cancel = true; result.Text = "正在取消并释放测试连接……"; } };
        }
        void Append(string line) { if (!IsDisposed) output.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine); }
        async Task RunTest() {
            if (busy) return;
            busy = true; run.Enabled = url.Enabled = false; cancel.Enabled = true; cancel.Text = "取消测试"; output.Clear(); result.Text = "正在测试，请稍候……"; result.ForeColor = Theme.Muted; SetResultIcon(IconState.Starting);
            cancellation = new CancellationTokenSource(); string address = url.Text.Trim();
            var progress = new Progress<string>(Append);
            bool passed = false;
            try { passed = await Task.Run(() => new ConnectionTester().Run(settings, alreadyRunning, address, line => ((IProgress<string>)progress).Report(line), cancellation.Token)); }
            catch (Exception e) { Append("测试不通过：" + e.Message); }
            bool cancelled = cancellation.IsCancellationRequested; cancellation.Dispose(); cancellation = null; busy = false;
            result.Text = cancelled ? "测试已取消" : passed ? "测试通过：所有监听端口均可访问测试网站" : "测试不通过：请查看上方各端口日志";
            result.ForeColor = cancelled ? Theme.Muted : passed ? Theme.Good : Theme.Error;
            SetResultIcon(cancelled ? IconState.Paused : passed ? IconState.Success : IconState.Failure);
            run.Enabled = url.Enabled = cancel.Enabled = true; cancel.Text = "关闭";
        }
        void SetResultIcon(IconState state) { var old = resultGlyph.Image; resultGlyph.Image = IconArtwork.CreateBitmap(state, 32); if (old != null) old.Dispose(); }
    }
}
