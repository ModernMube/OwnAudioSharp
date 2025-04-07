using Avalonia.Controls;
using Avalonia.Input;

using OwnaAvalonia.ViewModels;

namespace OwnaAvalonia.Views
{
    public partial class MainWindow : Window
    {
        public static MainWindow? Instance;

        public MainWindow()
        {
            Instance = this;

            InitializeComponent();

            Progress.PointerReleased += PointerReleasedHandler;
        }

        private void PointerReleasedHandler(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            if (sender?.GetType() == typeof(ProgressBar))
            {
                 ProgressBar progressBar = (ProgressBar)sender;

                var ratio = e.GetPosition(progressBar).X / progressBar.Bounds.Width;
                var value = ratio * progressBar.Maximum;

                progressBar.Value = value;
                vm.Seek(value);
            }
        }
    }
}
