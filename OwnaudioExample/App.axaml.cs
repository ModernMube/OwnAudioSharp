using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using OwnaAvalonia.ViewModels;
using OwnaAvalonia.Views;
using SukiUI;

namespace OwnaAvalonia
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            SukiTheme.GetInstance().ChangeBaseTheme(ThemeVariant.Dark);
            SukiTheme.GetInstance().ChangeColorTheme(SukiUI.Enums.SukiColor.Red);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
