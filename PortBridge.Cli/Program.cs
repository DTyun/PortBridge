using System;
using System.IO;
using PortBridge;

if (args.Length > 2 || (args.Length > 0 && args[0] != "--config" && args[0] != "--help") || (args.Length == 1 && args[0] == "--config")) {
    Console.Error.WriteLine("用法：PortBridge.Cli [--config 配置文件路径]");
    return 2;
}
if (args.Length == 1 && args[0] == "--help") {
    Console.WriteLine("PortBridge.Cli [--config 配置文件路径]\n按 Ctrl+C 或发送 SIGTERM 停止转发。默认读取用户配置，若不存在则使用默认值。");
    return 0;
}
try {
    string path = args.Length == 2 ? args[1] : Settings.FilePath;
    Settings settings = File.Exists(path) ? Settings.Read(path) : args.Length == 2 ? throw new FileNotFoundException("配置文件不存在。", path) : new Settings();
    using var relay = new RelayGroup();
    using var stopped = new System.Threading.ManualResetEventSlim(false);
    relay.Log = message => Console.WriteLine("[" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message);
    relay.Start(settings);
    Console.WriteLine("正在监听 " + settings.ListenAddress + ":" + settings.PortsText() + " → " + settings.TargetAddress + ":" + settings.TargetPort);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.Set(); };
    System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += _ => stopped.Set();
    stopped.Wait();
    return 0;
} catch (Exception e) {
    Console.Error.WriteLine("启动失败：" + e.Message);
    return 1;
}
