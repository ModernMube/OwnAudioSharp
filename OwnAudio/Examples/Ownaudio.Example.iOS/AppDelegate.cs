using Foundation;
using UIKit;

namespace OwnaudioIosExample
{
    /// <summary>
    /// Builds the window by hand — no storyboard, the whole demo is one view controller.
    /// </summary>
    [Register("AppDelegate")]
    public class AppDelegate : UIApplicationDelegate
    {
        public override UIWindow? Window { get; set; }

        public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
        {
            Window = new UIWindow(UIScreen.MainScreen.Bounds);
            Window.RootViewController = new MainViewController();
            Window.MakeKeyAndVisible();

            return true;
        }
    }
}
