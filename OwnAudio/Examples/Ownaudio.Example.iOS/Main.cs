using UIKit;

namespace OwnaudioIosExample
{
    /// <summary>
    /// Entry point. Hands control to UIKit with our own AppDelegate.
    /// </summary>
    public static class Application
    {
        static void Main(string[] args)
        {
            UIApplication.Main(args, null, typeof(AppDelegate));
        }
    }
}
