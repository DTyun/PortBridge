using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Xml;
using System.Linq;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("端口中转 PortBridge")]
[assembly: System.Reflection.AssemblyVersion("1.4.0.0")]
namespace PortBridge {
    public class Settings {
        public string ListenAddress = "127.0.0.1";
        public int ListenPort = 7890;
        public string ListenPorts;
        public bool ShouldSerializeListenPort() { return ListenPorts == null; }
        public int[] GetListenPorts() {
            string text = ListenPorts ?? ListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string[] parts = text.Replace('，', ',').Split(',');
            if (parts.Length > 64) throw new ArgumentException("最多同时监听 64 个端口。");
            var ports = new System.Collections.Generic.List<int>();
            foreach (string part in parts) {
                int port;
                if (!Int32.TryParse(part.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                    throw new ArgumentException("监听端口须为 1–65535，用逗号分隔，例如 7890,7891,7892；不能有空项。");
                if (ports.Contains(port)) throw new ArgumentException("监听端口重复：" + port + "。");
                ports.Add(port);
            }
            return ports.ToArray();
        }
        public string PortsText() { return String.Join(",", GetListenPorts().Select(p => p.ToString()).ToArray()); }
        public string TargetAddress = "127.0.0.1";
        public int TargetPort = 23578;
        public bool Tcp = true;
        public bool Udp = true;
        public bool AutoRelay = false;
        public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortBridge", "settings.xml"); } }
        public static Settings Load() { return Read(FilePath); }
        public static Settings Read(string path) {
            if (new FileInfo(path).Length > 65536) throw new ArgumentException("配置文件不能超过 64 KB。");
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) {
                var value = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(reader); value.Validate(); return value;
            }
        }
        public void Validate() {
            int[] ports = GetListenPorts();
            if (TargetPort < 1 || TargetPort > 65535) throw new ArgumentException("端口范围为 1–65535。");
            IPAddress bind = ParseAddress(ListenAddress), target = ParseAddress(TargetAddress);
            if (target.Equals(IPAddress.Any) || target.Equals(IPAddress.IPv6Any)) throw new ArgumentException("目标地址不能为 0.0.0.0 或 ::。");
            if (!Tcp && !Udp) throw new ArgumentException("请至少选择 TCP 或 UDP。");
            if (ports.Contains(TargetPort) && (bind.Equals(target) || ((bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any)) && IPAddress.IsLoopback(target)))) throw new ArgumentException("监听和目标不能指向同一端口。");
        }
        static IPAddress ParseAddress(string text) { IPAddress value; if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("IP 地址不能为空。"); if (text.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback; if (!IPAddress.TryParse(text.Trim(), out value)) throw new ArgumentException("请输入有效 IP 地址或 localhost。"); return value; }
        public void Save() {
            SaveTo(FilePath);
        }
        public void SaveTo(string path) {
            Validate(); path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var f = File.Create(temp)) new XmlSerializer(typeof(Settings)).Serialize(f, this);
                if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public static class Startup {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "PortBridge";
        public static bool Enabled { get { using (var key = Registry.CurrentUser.OpenSubKey(Key)) return key != null && key.GetValue(Name) != null; } }
        public static void Set(bool enabled) {
            using (var key = Registry.CurrentUser.CreateSubKey(Key)) {
                if (enabled) key.SetValue(Name, "\"" + Application.ExecutablePath + "\" --tray");
                else key.DeleteValue(Name, false);
            }
        }
    }
    static class Theme {
        public static readonly Color Background = ColorTranslator.FromHtml("#F5F7FB");
        public static readonly Color Surface = Color.White;
        public static readonly Color Line = ColorTranslator.FromHtml("#DDE4EE");
        public static readonly Color Ink = ColorTranslator.FromHtml("#172B4D");
        public static readonly Color Muted = ColorTranslator.FromHtml("#64748B");
        public static readonly Color Accent = ColorTranslator.FromHtml("#3366CC");
        public static readonly Color AccentSoft = ColorTranslator.FromHtml("#EAF0FF");
        public static readonly Color Good = ColorTranslator.FromHtml("#087F5B");
        public static readonly Color Error = ColorTranslator.FromHtml("#C2413B");
        public static readonly Font Body = new Font("Microsoft YaHei UI", 10F);
        public static readonly Font Data = new Font("Consolas", 12F);
    }
    public sealed class MainForm : Form {
        TextBox listen, target, log, listenPort;
        NumericUpDown targetPort;
        CheckBox tcp, udp, startup, auto;
        Button toggle, save, import;
        Label status, stats, feedback;
        NotifyIcon tray;
        ToolStripMenuItem trayToggle;
        RelayGroup engine;
        Settings settings;
        bool exiting, loading = true;
        readonly bool startHidden;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        public MainForm(bool hidden) : this(hidden, null) { }
        public MainForm(bool hidden, Settings initialSettings) {
            startHidden = hidden;
            string loadError = null;
            try {
                string bundled = Path.Combine(Application.StartupPath, "PortBridge.config.xml");
                settings = initialSettings ?? (File.Exists(Settings.FilePath) ? Settings.Load() : File.Exists(bundled) ? Settings.Read(bundled) : new Settings());
            } catch (Exception e) { settings = new Settings(); loadError = "读取配置失败，已使用默认值：" + e.Message; }

            Text = "PortBridge · 端口中转"; ClientSize = new Size(820, 860); MinimumSize = new Size(760, 800);
            Font = Theme.Body; BackColor = Theme.Background; ForeColor = Theme.Ink; AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen; Icon = MakeIcon();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 24), ColumnCount = 1, RowCount = 7 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 238));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 37)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var title = new Label { Text = "端口中转", Font = new Font("Microsoft YaHei UI", 24F, FontStyle.Bold), AutoSize = true, Margin = new Padding(0) };
            header.Controls.Add(title, 0, 0); header.SetColumnSpan(title, 1);
            header.Controls.Add(new Label { Text = "把本机端口的流量转发到另一个服务", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(2, 2, 0, 0) }, 0, 1);
            status = new Label { Text = "●  已暂停", ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AccessibleName = "转发状态" };
            header.Controls.Add(status, 1, 0); header.SetRowSpan(status, 2); root.Controls.Add(header, 0, 0);

            var guide = new Panel { Dock = DockStyle.Fill, BackColor = Theme.AccentSoft, Padding = new Padding(14, 9, 14, 8) };
            guide.Controls.Add(new Label { Text = "使用方法：填写入口和目标 → 先保存设置 → 点击启动。第一次使用建议点击“测试连接”。", ForeColor = Theme.Ink, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            root.Controls.Add(guide, 0, 1);

            var route = Card(); route.Padding = new Padding(18, 14, 18, 16);
            var routeGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4 };
            routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46)); routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64)); routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
            routeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); routeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 31)); routeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); routeGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var fromTitle = new Label { Text = "01  本机入口", Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold), AutoSize = true, ForeColor = Theme.Accent };
            var toTitle = new Label { Text = "02  目标服务", Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold), AutoSize = true, ForeColor = Theme.Accent };
            routeGrid.Controls.Add(fromTitle, 0, 0); routeGrid.Controls.Add(toTitle, 2, 0);
            routeGrid.Controls.Add(new Label { Text = "用户连接这里，软件接收流量", ForeColor = Theme.Muted, AutoSize = true }, 0, 1);
            routeGrid.Controls.Add(new Label { Text = "软件把流量送到这里", ForeColor = Theme.Muted, AutoSize = true }, 2, 1);
            listen = Field(settings.ListenAddress, "监听 IP 地址"); target = Field(settings.TargetAddress, "目标 IP 地址");
            listenPort = Field(settings.PortsText(), "监听端口，多个用逗号分隔，例如 7890,7891"); listenPort.MaxLength = 512; targetPort = Port(settings.TargetPort, "目标端口");
            routeGrid.Controls.Add(listen, 0, 2); routeGrid.Controls.Add(target, 2, 2); routeGrid.Controls.Add(listenPort, 0, 3); routeGrid.Controls.Add(targetPort, 2, 3);
            var arrow = new Label { Text = "→", Font = new Font("Segoe UI", 26F, FontStyle.Bold), ForeColor = Theme.Accent, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            routeGrid.Controls.Add(arrow, 1, 2); routeGrid.SetRowSpan(arrow, 2); route.Controls.Add(routeGrid); root.Controls.Add(route, 0, 2);

            var options = Card(); options.Padding = new Padding(18, 12, 18, 10);
            var optionGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            optionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); optionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            optionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 31)); optionGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            optionGrid.Controls.Add(new Label { Text = "传输方式", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), AutoSize = true }, 0, 0);
            optionGrid.Controls.Add(new Label { Text = "启动选项", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), AutoSize = true }, 1, 0);
            var protocols = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false };
            tcp = Check("TCP", settings.Tcp); udp = Check("UDP", settings.Udp); protocols.Controls.Add(tcp); protocols.Controls.Add(udp);
            protocols.Controls.Add(new Label { Text = "常用代理一般选 TCP + UDP", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(10, 3, 0, 0) }); optionGrid.Controls.Add(protocols, 0, 1);
            var startupOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            startup = Check("登录 Windows 后自动打开", false); auto = Check("打开后自动启动转发", settings.AutoRelay);
            try { startup.Checked = Startup.Enabled; } catch (Exception e) { loadError = "读取开机启动设置失败：" + e.Message; }
            startupOptions.Controls.Add(startup); startupOptions.Controls.Add(auto); optionGrid.Controls.Add(startupOptions, 1, 1); options.Controls.Add(optionGrid); root.Controls.Add(options, 0, 3);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            var mainActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            toggle = ActionButton("▶  启动转发", true); save = ActionButton("保存设置", false); var test = ActionButton("测试连接", false); mainActions.Controls.Add(toggle); mainActions.Controls.Add(save); mainActions.Controls.Add(test);
            actions.Controls.Add(mainActions, 0, 0); actions.SetColumnSpan(mainActions, 2);
            var fileActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            var hide = ActionButton("收起到托盘", false); import = ActionButton("导入配置", false); var export = ActionButton("导出配置", false);
            fileActions.Controls.Add(hide); fileActions.Controls.Add(export); fileActions.Controls.Add(import); actions.Controls.Add(fileActions, 0, 1); actions.SetColumnSpan(fileActions, 2);
            root.Controls.Add(actions, 0, 4);
            import.Click += delegate { ImportSettings(); }; export.Click += delegate { ExportSettings(); };
            test.Click += delegate { try { var snapshot = Snapshot(); using (var dialog = new ConnectionTestForm(snapshot, engine != null && engine.Running)) dialog.ShowDialog(this); } catch (Exception e) { Feedback("测试前检查失败：" + e.Message, true); } };

            var feedbackPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            feedbackPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70)); feedbackPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            feedback = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Text = "准备就绪。修改设置后先保存，再启动转发。", TextAlign = ContentAlignment.MiddleLeft };
            stats = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "0 个会话\r\n↑ 0 B   ↓ 0 B", ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleRight };
            feedbackPanel.Controls.Add(feedback, 0, 0); feedbackPanel.Controls.Add(stats, 1, 0); root.Controls.Add(feedbackPanel, 0, 5);

            var logPanel = Card(); logPanel.Padding = new Padding(14, 10, 14, 12);
            var logGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; logGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); logGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logGrid.Controls.Add(new Label { Text = "运行日志", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), ForeColor = Theme.Ink, AutoSize = true }, 0, 0);
            log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "运行日志", Font = new Font("Microsoft YaHei UI", 9F) }; logGrid.Controls.Add(log, 0, 1); logPanel.Controls.Add(logGrid); root.Controls.Add(logPanel, 0, 6);
            var menu = new ContextMenuStrip(); menu.Items.Add("打开主窗口", null, delegate { Restore(); });
            trayToggle = new ToolStripMenuItem("启动转发", null, delegate { Toggle(); }); menu.Items.Add(trayToggle); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出软件", null, delegate { exiting = true; Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "端口中转 · 已暂停", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { Restore(); };
            toggle.Click += delegate { Toggle(); }; save.Click += delegate { SaveSettings(); }; hide.Click += delegate { HideToTray(); };
            startup.CheckedChanged += delegate {
                if (loading) return;
                try { Startup.Set(startup.Checked); Feedback(startup.Checked ? "已开启开机启动。请保留 exe 所在位置。" : "已关闭开机启动。", false); }
                catch (Exception e) { loading = true; startup.Checked = !startup.Checked; loading = false; Feedback("设置开机启动失败：" + e.Message, true); }
            };
            auto.CheckedChanged += delegate { if (!loading) SaveSettings(); };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) HideToTray(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideToTray(); return; }
                timer.Stop(); if (engine != null) engine.Dispose(); tray.Visible = false; tray.Dispose();
            };
            timer.Interval = 500; timer.Tick += delegate {
                if (engine != null) stats.Text = String.Format("{0} 个会话\r\n↑ {1}   ↓ {2}", engine.Connections, SizeText(engine.Sent), SizeText(engine.Received));
                string line; int count = 0; while (count++ < 30 && messages.TryDequeue(out line)) AppendLog(line);
            }; timer.Start(); loading = false;
            Shown += delegate {
                if (loadError != null) Feedback(loadError, true);
                if (settings.AutoRelay && loadError == null) Toggle();
                if (startHidden) HideToTray();
            };
        }
        static Panel Card() { return new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0) }; }
        static TextBox Field(string value, string name) { return new TextBox { Text = value, Dock = DockStyle.Fill, Font = Theme.Data, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AccessibleName = name, MaxLength = 64, Margin = new Padding(0, 3, 0, 3), Padding = new Padding(7, 4, 7, 4) }; }
        static NumericUpDown Port(int value, string name) { return new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Max(1, Math.Min(65535, value)), Dock = DockStyle.Fill, Font = Theme.Data, BackColor = Color.White, AccessibleName = name, Margin = new Padding(0, 3, 0, 3) }; }
        static CheckBox Check(string text, bool value) { return new CheckBox { Text = text, Checked = value, AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(0, 2, 15, 2) }; }
        static Button ActionButton(string text, bool primary) {
            var button = new Button { Text = text, Size = new Size(140, 40), FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme.Accent : Theme.Surface, ForeColor = primary ? Color.White : Theme.Ink, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 0), TabStop = true };
            button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Line; button.FlatAppearance.MouseOverBackColor = primary ? ColorTranslator.FromHtml("#2859B8") : Theme.AccentSoft; button.FlatAppearance.MouseDownBackColor = primary ? ColorTranslator.FromHtml("#204B9D") : ColorTranslator.FromHtml("#DCE7FF");
            return button;
        }
        public static Icon MakeIcon() {
            using (var bitmap = new Bitmap(32, 32)) using (var g = Graphics.FromImage(bitmap)) {
                g.Clear(Theme.Accent); using (var pen = new Pen(Color.White, 3)) {
                    g.DrawLines(pen, new Point[] { new Point(5, 11), new Point(26, 11), new Point(21, 6) });
                    g.DrawLines(pen, new Point[] { new Point(27, 22), new Point(6, 22), new Point(11, 27) });
                }
                IntPtr handle = bitmap.GetHicon(); try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
            }
        }
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        static IPAddress Parse(string value) { if (value.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback; IPAddress ip; if (!IPAddress.TryParse(value.Trim(), out ip)) throw new ArgumentException("地址请输入有效 IP（如 127.0.0.1 或 ::1），也可输入 localhost。"); return ip; }
        bool SaveSettings() {
            try {
                Parse(listen.Text); var ip = Parse(target.Text);
                if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) throw new ArgumentException("目标地址不能为 0.0.0.0 或 ::。");
                if (!tcp.Checked && !udp.Checked) throw new ArgumentException("请至少选择 TCP 或 UDP。");
                var next = Snapshot();
                next.Save(); settings = next; Feedback("设置已保存。", false); return true;
            } catch (Exception e) { Feedback("保存失败：" + e.Message, true); return false; }
        }
        Settings Snapshot() {
            var value = new Settings { ListenAddress = listen.Text.Trim(), ListenPorts = listenPort.Text.Trim(), TargetAddress = target.Text.Trim(), TargetPort = (int)targetPort.Value, Tcp = tcp.Checked, Udp = udp.Checked, AutoRelay = auto.Checked };
            value.Validate(); return value;
        }
        void ImportSettings() {
            if (engine != null && engine.Running) { Feedback("请先暂停转发，再导入配置。", true); return; }
            using (var picker = new OpenFileDialog { Title = "导入配置文件", Filter = "XML 配置文件 (*.xml)|*.xml", CheckFileExists = true, Multiselect = false }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try {
                    var value = Settings.Read(picker.FileName);
                    value.Save(); settings = value;
                    loading = true;
                    listen.Text = value.ListenAddress; listenPort.Text = value.PortsText(); target.Text = value.TargetAddress; targetPort.Value = value.TargetPort;
                    tcp.Checked = value.Tcp; udp.Checked = value.Udp; auto.Checked = value.AutoRelay;
                    loading = false;
                    Feedback("配置已导入并保存。点击「启动转发」应用；开机启动选项保持原设置。", false);
                } catch (Exception e) { loading = false; Feedback("导入失败，原配置未更改：" + e.GetBaseException().Message, true); }
            }
        }
        void ExportSettings() {
            try {
                var value = Snapshot();
                using (var picker = new SaveFileDialog { Title = "导出配置文件", Filter = "XML 配置文件 (*.xml)|*.xml", FileName = "PortBridge.config.xml", DefaultExt = "xml", AddExtension = true, OverwritePrompt = true }) {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    value.SaveTo(picker.FileName); Feedback("配置已导出：" + picker.FileName, false);
                }
            } catch (Exception e) { Feedback("导出失败：" + e.GetBaseException().Message, true); }
        }
        void Toggle() {
            if (engine != null && engine.Running) {
                engine.Dispose(); SetRunning(false); Feedback("已暂停，现有连接已关闭，监听端口已释放。", false); return;
            }
            if (!SaveSettings()) return;
            var next = new RelayGroup();
            next.Log = delegate(string message) { if (messages.Count < 100) messages.Enqueue(message); };
            try {
                next.Start(settings);
                engine = next; SetRunning(true); Feedback(String.Format("正在转发  {0}:[{1}] → {2}:{3}", settings.ListenAddress, settings.PortsText(), settings.TargetAddress, settings.TargetPort), false);
            } catch (Exception e) { next.Dispose(); SetRunning(false); Feedback("启动失败：" + e.Message + " 请检查监听地址或端口占用。", true); if (!Visible) tray.ShowBalloonTip(5000, "端口中转启动失败", e.Message, ToolTipIcon.Error); }
        }
        void SetRunning(bool value) {
            toggle.Text = trayToggle.Text = value ? "■  暂停转发" : "▶  启动转发";
            status.Text = value ? "●  正在转发" : "●  已暂停"; status.ForeColor = value ? Theme.Good : Theme.Muted;
            tray.Text = value ? "端口中转 · 正在转发" : "端口中转 · 已暂停";
            foreach (Control control in new Control[] { listen, target, listenPort, targetPort, tcp, udp }) control.Enabled = !value;
            import.Enabled = !value;
        }
        void Feedback(string text, bool error) { feedback.Text = text; feedback.ForeColor = error ? Theme.Error : Theme.Muted; AppendLog(text); }
        void AppendLog(string text) { if (log.TextLength > 16000) log.Text = log.Text.Substring(log.TextLength - 10000); log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); }
        void HideToTray() { Hide(); ShowInTaskbar = false; }
        public void Restore() { ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
        protected override void WndProc(ref Message m) { if (m.Msg == Program.ShowMessage) Restore(); base.WndProc(ref m); }
        static string SizeText(long bytes) { if (bytes < 1024) return bytes + " B"; if (bytes < 1048576) return (bytes / 1024.0).ToString("F1") + " KB"; return (bytes / 1048576.0).ToString("F1") + " MB"; }
    }
    static class Program {
        public static readonly int ShowMessage = RegisterWindowMessage("PortBridge.Show.1A761A4F");
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string value);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
        [STAThread] static void Main(string[] args) {
            bool first;
            using (var mutex = new Mutex(true, @"Local\PortBridge.1A761A4F", out first)) {
                if (!first) { PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero); return; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(Array.IndexOf(args, "--tray") >= 0));
                mutex.ReleaseMutex();
            }
        }
    }
}
