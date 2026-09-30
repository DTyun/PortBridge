using Microsoft.Maui.Hosting;

namespace PortBridge.iOS;

public static class MauiProgram {
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<App>().Build();
}
