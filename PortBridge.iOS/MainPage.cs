using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using PortBridge;
using System;
using System.IO;

namespace PortBridge.iOS;

public sealed class MainPage : ContentPage {
    readonly Entry listenAddress = new() { Placeholder = "监听地址，例如 127.0.0.1" };
    readonly Entry listenPorts = new() { Placeholder = "监听端口，例如 7890,7891" };
    readonly Entry targetAddress = new() { Placeholder = "目标地址，例如 127.0.0.1" };
    readonly Entry targetPort = new() { Keyboard = Keyboard.Numeric, Placeholder = "目标端口" };
    readonly Switch tcp = new();
    readonly Switch udp = new();
    readonly Label status = new() { Text = "未启动" };
    readonly Button toggle = new() { Text = "启动转发" };
    RelayGroup relay;

    public MainPage() {
        Title = "PortBridge";
        Settings initial;
        try { initial = File.Exists(Settings.FilePath) ? Settings.Load() : new Settings(); }
        catch { initial = new Settings(); }
        listenAddress.Text = initial.ListenAddress;
        listenPorts.Text = initial.PortsText();
        targetAddress.Text = initial.TargetAddress;
        targetPort.Text = initial.TargetPort.ToString();
        tcp.IsToggled = initial.Tcp;
        udp.IsToggled = initial.Udp;
        toggle.Clicked += Toggle;
        Content = new ScrollView { Content = new VerticalStackLayout {
            Padding = new Thickness(24), Spacing = 12,
            Children = {
                new Label { Text = "本机入口 → 目标服务", FontSize = 24, FontAttributes = FontAttributes.Bold },
                new Label { Text = "仅在应用保持前台时转发。切换到后台后 iOS 可能暂停网络活动。", TextColor = Colors.DarkOrange },
                new Label { Text = "监听地址" }, listenAddress,
                new Label { Text = "监听端口（最多 64 个，逗号分隔）" }, listenPorts,
                new Label { Text = "目标地址" }, targetAddress,
                new Label { Text = "目标端口" }, targetPort,
                new HorizontalStackLayout { Spacing = 12, Children = { new Label { Text = "TCP" }, tcp, new Label { Text = "UDP" }, udp } },
                toggle, status
            }
        }};
    }

    void Toggle(object sender, EventArgs args) {
        if (relay != null) { Stop(); return; }
        try {
            if (!int.TryParse(targetPort.Text, out int port)) throw new ArgumentException("请输入有效目标端口。");
            var settings = new Settings {
                ListenAddress = listenAddress.Text?.Trim(), ListenPorts = listenPorts.Text?.Trim(),
                TargetAddress = targetAddress.Text?.Trim(), TargetPort = port,
                Tcp = tcp.IsToggled, Udp = udp.IsToggled
            };
            settings.Validate();
            var next = new RelayGroup();
            try { next.Start(settings); settings.Save(); relay = next; }
            catch { next.Dispose(); throw; }
            toggle.Text = "暂停转发";
            status.Text = "正在监听 " + settings.ListenAddress + ":" + settings.PortsText();
        } catch (Exception e) { status.Text = "启动失败：" + e.Message; }
    }

    public void Stop() {
        relay?.Dispose(); relay = null;
        toggle.Text = "启动转发";
        status.Text = "已暂停";
    }

    protected override void OnDisappearing() { Stop(); base.OnDisappearing(); }
}
