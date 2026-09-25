using System;
using System.Threading;
using PortBridge;
class LiveConnectionTest {
    static int Main() {
        var settings = Settings.Load();
        return new ConnectionTester().Run(settings, true, "https://www.gstatic.com/generate_204", Console.WriteLine, CancellationToken.None) ? 0 : 1;
    }
}
