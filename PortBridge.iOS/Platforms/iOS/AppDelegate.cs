using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using UIKit;

namespace PortBridge.iOS;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate {
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    public override void DidEnterBackground(UIApplication application) {
        (Application.Current?.MainPage as MainPage)?.Stop();
        base.DidEnterBackground(application);
    }
}
