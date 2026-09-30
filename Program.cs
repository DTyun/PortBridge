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
[assembly: System.Reflection.AssemblyVersion("1.4.1.0")]
namespace PortBridge {
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
        PictureBox statusGlyph;
        NotifyIcon tray;
        ToolStripMenuItem trayToggle;
        RelayGroup engine;
        Settings settings;
        readonly string settingsPath;
        bool exiting, loading = true;
        readonly bool startHidden;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        public MainForm(bool hidden) : this(hidden, null, null) { }
        public MainForm(bool hidden, Settings initialSettings) : this(hidden, initialSettings, null) { }
        public MainForm(bool hidden, Settings initialSettings, string savePath) {
            startHidden = hidden;
            settingsPath = savePath ?? Settings.FilePath;
            string loadError = null;
            try {
                string bundled = Path.Combine(Application.StartupPath, "PortBridge.config.xml");
                settings = initialSettings ?? (File.Exists(Settings.FilePath) ? Settings.Load() : File.Exists(bundled) ? Settings.Read(bundled) : new Settings());
            } catch (Exception e) { settings = new Settings(); loadError = "读取配置失败，已使用默认值：" + e.Message; }

            Text = "PortBridge · 端口中转"; ClientSize = new Size(820, 720); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            Font = Theme.Body; BackColor = Theme.Background; ForeColor = Theme.Ink; AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen; Icon = IconArtwork.CreateIcon(IconState.Paused, 32);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 12), ColumnCount = 1, RowCount = 8 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 164));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.Transparent };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var title = new Label { Text = "端口中转", Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold), AutoSize = true, Margin = new Padding(0) };
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(new Label { Text = "本机入口 → 目标服务", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(2, 0, 0, 0) }, 0, 1);
            status = new Label { Text = "已暂停", ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill, AccessibleName = "转发状态" };
            header.Controls.Add(status, 1, 0); header.SetRowSpan(status, 2);
            statusGlyph = new PictureBox { Size = new Size(26, 26), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 2, 0, 0), AccessibleName = "转发状态图标" };
            header.Controls.Add(statusGlyph, 2, 0); header.SetRowSpan(statusGlyph, 2); root.Controls.Add(header, 0, 0);

            var guide = new Panel { Dock = DockStyle.Fill, BackColor = Theme.AccentSoft, Padding = new Padding(12, 5, 12, 5) };
            guide.Controls.Add(new Label { Text = "填写两端地址，选择协议，再启动转发；测试连接可检查网页链路。", ForeColor = Theme.Ink, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            root.Controls.Add(guide, 0, 1);

            var route = Card(); route.Padding = new Padding(14, 10, 14, 12);
            var routeGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = new Padding(0) };
            routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46)); routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64)); routeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
            routeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); routeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); routeGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var fromTitle = new Label { Text = "本机入口", Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), AutoSize = true, ForeColor = Theme.Accent };
            var toTitle = new Label { Text = "目标服务", Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), AutoSize = true, ForeColor = Theme.Accent };
            routeGrid.Controls.Add(fromTitle, 0, 0); routeGrid.Controls.Add(toTitle, 2, 0);
            listen = Field(settings.ListenAddress, "监听 IP 地址"); target = Field(settings.TargetAddress, "目标 IP 地址");
            listenPort = Field(settings.PortsText(), "监听端口，多个用逗号分隔，例如 7890,7891"); listenPort.MaxLength = 512; targetPort = Port(settings.TargetPort, "目标端口");
            routeGrid.Controls.Add(LabeledField("地址", listen), 0, 1); routeGrid.Controls.Add(LabeledField("地址", target), 2, 1);
            routeGrid.Controls.Add(LabeledField("端口", listenPort), 0, 2); routeGrid.Controls.Add(LabeledField("端口", targetPort), 2, 2);
            var arrow = new Label { Text = "→", Font = new Font("Segoe UI", 26F, FontStyle.Bold), ForeColor = Theme.Accent, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            routeGrid.Controls.Add(arrow, 1, 1); routeGrid.SetRowSpan(arrow, 2); route.Controls.Add(routeGrid); root.Controls.Add(route, 0, 2);

            var options = Card(); options.Padding = new Padding(14, 8, 14, 6);
            var optionGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            optionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); optionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            optionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); optionGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            optionGrid.Controls.Add(new Label { Text = "传输方式", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), AutoSize = true }, 0, 0);
            optionGrid.Controls.Add(new Label { Text = "启动选项", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), AutoSize = true }, 1, 0);
            var protocolStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            protocolStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); protocolStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var protocols = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false, Margin = new Padding(0) };
            tcp = Check("TCP", settings.Tcp); udp = Check("UDP", settings.Udp); protocols.Controls.Add(tcp); protocols.Controls.Add(udp);
            protocolStack.Controls.Add(protocols, 0, 0);
            protocolStack.Controls.Add(new Label { Text = "多个入口端口用逗号分隔", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 3, 0, 0) }, 0, 1); optionGrid.Controls.Add(protocolStack, 0, 1);
            var startupOptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            startupOptions.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); startupOptions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            startup = Check("登录 Windows 后自动打开", false); auto = Check("打开后自动启动转发", settings.AutoRelay);
            try { startup.Checked = Startup.Enabled; } catch (Exception e) { loadError = "读取开机启动设置失败：" + e.Message; }
            startupOptions.Controls.Add(startup); startupOptions.Controls.Add(auto); optionGrid.Controls.Add(startupOptions, 1, 1); options.Controls.Add(optionGrid); root.Controls.Add(options, 0, 3);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
            for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            toggle = ActionButton("▶  启动转发", true); save = ActionButton("保存设置", false); var test = ActionButton("测试连接", false);
            var hide = ActionButton("收起到托盘", false); import = ActionButton("导入配置", false); var export = ActionButton("导出配置", false);
            actions.Controls.Add(toggle, 0, 0); actions.Controls.Add(save, 1, 0); actions.Controls.Add(test, 2, 0);
            actions.Controls.Add(import, 0, 1); actions.Controls.Add(export, 1, 1); actions.Controls.Add(hide, 2, 1);
            foreach (Control button in actions.Controls) { button.Dock = DockStyle.Fill; button.Margin = new Padding(3, 3, 3, 3); }
            root.Controls.Add(actions, 0, 4);
            import.Click += delegate { ImportSettings(); }; export.Click += delegate { ExportSettings(); };
            test.Click += delegate { try { var snapshot = Snapshot(); using (var dialog = new ConnectionTestForm(snapshot, engine != null && engine.Running)) dialog.ShowDialog(this); } catch (Exception e) { Feedback("测试前检查失败：" + e.Message, true); } };

            var feedbackPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            feedbackPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70)); feedbackPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            feedback = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Text = "准备就绪。修改设置后先保存，再启动转发。", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            stats = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "0 个会话\r\n↑ 0 B   ↓ 0 B", ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleRight };
            feedbackPanel.Controls.Add(feedback, 0, 0); feedbackPanel.Controls.Add(stats, 1, 0); root.Controls.Add(feedbackPanel, 0, 5);

            var logPanel = Card(); logPanel.Padding = new Padding(14, 10, 14, 12);
            var logGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; logGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); logGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logGrid.Controls.Add(new Label { Text = "运行日志", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), ForeColor = Theme.Ink, AutoSize = true }, 0, 0);
            log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "运行日志", Font = new Font("Microsoft YaHei UI", 9F) }; logGrid.Controls.Add(log, 0, 1); logPanel.Controls.Add(logGrid); root.Controls.Add(logPanel, 0, 6);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            string version = typeof(MainForm).Assembly.GetName().Version.ToString(3);
            footer.Controls.Add(ProjectLink("PortBridge v" + version, "https://github.com/DTyun/PortBridge/releases", ContentAlignment.MiddleLeft, "当前版本 " + version + "，查看 GitHub 发布版本"), 0, 0);
            footer.Controls.Add(ProjectLink("GitHub · github.com/DTyun/PortBridge", "https://github.com/DTyun/PortBridge", ContentAlignment.MiddleRight, "打开 PortBridge 的 GitHub 开源仓库"), 1, 0);
            root.Controls.Add(footer, 0, 7);
            var menu = new ContextMenuStrip(); menu.Items.Add("打开主窗口", null, delegate { Restore(); });
            trayToggle = new ToolStripMenuItem("启动转发", null, delegate { Toggle(); }); menu.Items.Add(trayToggle); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出软件", null, delegate { exiting = true; Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "端口中转 · 已暂停", ContextMenuStrip = menu, Visible = true };
            SetStatusVisual(IconState.Paused);
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
        LinkLabel ProjectLink(string text, string address, ContentAlignment alignment, string accessibleName) {
            var link = new LinkLabel { Text = text, Dock = DockStyle.Fill, TextAlign = alignment, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Ink, VisitedLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, AccessibleName = accessibleName, TabStop = true, Margin = new Padding(0) };
            link.LinkClicked += delegate {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true }); }
                catch (Exception e) { Feedback("无法打开浏览器，请手动访问 " + address + "。" + e.Message, true); }
            };
            return link;
        }
        static Panel Card() { return new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0) }; }
        static Control LabeledField(string text, Control field) {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0), ForeColor = Theme.Muted }, 0, 0);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right; field.Dock = DockStyle.None;
            row.Controls.Add(field, 1, 0); return row;
        }
        static TextBox Field(string value, string name) { return new TextBox { Text = value, Dock = DockStyle.Fill, Font = Theme.Data, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AccessibleName = name, MaxLength = 64, Margin = new Padding(0, 3, 0, 3), Padding = new Padding(7, 4, 7, 4) }; }
        static NumericUpDown Port(int value, string name) { return new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Max(1, Math.Min(65535, value)), Dock = DockStyle.Fill, Font = Theme.Data, BackColor = Color.White, AccessibleName = name, Margin = new Padding(0, 3, 0, 3) }; }
        static CheckBox Check(string text, bool value) { return new CheckBox { Text = text, Checked = value, AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(0, 2, 15, 2) }; }
        static Button ActionButton(string text, bool primary) {
            var button = new Button { Text = text, Size = new Size(140, 40), FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme.Accent : Theme.Surface, ForeColor = primary ? Color.White : Theme.Ink, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 0), TabStop = true };
            button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Line; button.FlatAppearance.MouseOverBackColor = primary ? ColorTranslator.FromHtml("#2859B8") : Theme.AccentSoft; button.FlatAppearance.MouseDownBackColor = primary ? ColorTranslator.FromHtml("#204B9D") : ColorTranslator.FromHtml("#DCE7FF");
            return button;
        }
        static IPAddress Parse(string value) { if (value.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback; IPAddress ip; if (!IPAddress.TryParse(value.Trim(), out ip)) throw new ArgumentException("地址请输入有效 IP（如 127.0.0.1 或 ::1），也可输入 localhost。"); return ip; }
        bool SaveSettings() {
            try {
                Parse(listen.Text); var ip = Parse(target.Text);
                if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) throw new ArgumentException("目标地址不能为 0.0.0.0 或 ::。");
                if (!tcp.Checked && !udp.Checked) throw new ArgumentException("请至少选择 TCP 或 UDP。");
                var next = Snapshot();
                next.SaveTo(settingsPath); settings = next; listenPort.Text = next.PortsText(); Feedback("设置已保存。", false); return true;
            } catch (Exception e) { Feedback("保存失败：" + e.Message, true); return false; }
        }
        Settings Snapshot() {
            var value = new Settings { ListenAddress = listen.Text.Trim(), ListenPorts = listenPort.Text.Trim(), TargetAddress = target.Text.Trim(), TargetPort = (int)targetPort.Value, Tcp = tcp.Checked, Udp = udp.Checked, AutoRelay = auto.Checked };
            value.Validate(); value.ListenPorts = value.PortsText(); return value;
        }
        void ImportSettings() {
            if (engine != null && engine.Running) { Feedback("请先暂停转发，再导入配置。", true); return; }
            using (var picker = new OpenFileDialog { Title = "导入配置文件", Filter = "XML 配置文件 (*.xml)|*.xml", CheckFileExists = true, Multiselect = false }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try {
                    var value = Settings.Read(picker.FileName);
                    value.SaveTo(settingsPath); settings = value;
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
            SetStatusVisual(IconState.Starting);
            status.Text = "正在启动"; status.ForeColor = Theme.Accent;
            var next = new RelayGroup();
            next.Log = delegate(string message) { if (messages.Count < 100) messages.Enqueue(message); };
            try {
                next.Start(settings);
                engine = next; SetRunning(true); Feedback(String.Format("正在转发  {0}:[{1}] → {2}:{3}", settings.ListenAddress, settings.PortsText(), settings.TargetAddress, settings.TargetPort), false);
            } catch (Exception e) { next.Dispose(); SetRunning(false); Feedback("启动失败：" + e.Message + " 请检查监听地址或端口占用。", true); if (!Visible) tray.ShowBalloonTip(5000, "端口中转启动失败", e.Message, ToolTipIcon.Error); }
        }
        void SetRunning(bool value) {
            toggle.Text = trayToggle.Text = value ? "■  暂停转发" : "▶  启动转发";
            status.Text = value ? "正在转发" : "已暂停"; status.ForeColor = value ? Theme.Good : Theme.Muted;
            SetStatusVisual(value ? IconState.Running : IconState.Paused);
            tray.Text = value ? "端口中转 · 正在转发" : "端口中转 · 已暂停";
            foreach (TextBox field in new[] { listen, target, listenPort }) {
                field.ReadOnly = value;
                if (value) { field.SelectionStart = 0; field.SelectionLength = 0; }
            }
            foreach (Control control in new Control[] { targetPort, tcp, udp }) control.Enabled = !value;
            import.Enabled = !value;
        }
        void SetStatusVisual(IconState state) {
            var old = Icon;
            Icon = IconArtwork.CreateIcon(state, 32);
            if (tray != null) tray.Icon = Icon;
            if (statusGlyph != null) { var image = statusGlyph.Image; statusGlyph.Image = IconArtwork.CreateBitmap(state, 28); if (image != null) image.Dispose(); }
            if (old != null) old.Dispose();
        }
        void Feedback(string text, bool error) { feedback.Text = text; feedback.ForeColor = error ? Theme.Error : Theme.Muted; if (error) SetStatusVisual(IconState.Failure); AppendLog(text); }
        void AppendLog(string text) { if (log.TextLength > 16000) log.Text = log.Text.Substring(log.TextLength - 10000); log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); }
        void HideToTray() { Hide(); ShowInTaskbar = false; }
        public void Restore() {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            if (engine != null && engine.Running) {
                foreach (TextBox field in new[] { listen, target, listenPort }) {
                    field.SelectionStart = 0;
                    field.SelectionLength = 0;
                }
            }
            Activate();
        }
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
