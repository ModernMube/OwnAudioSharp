using CommunityToolkit.Mvvm.ComponentModel;

namespace OwnaudioShowcase.ViewModels;

/// <summary>
/// Main window ViewModel that manages tab navigation and child ViewModels.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private int selectedTabIndex;

    [ObservableProperty]
    private string title = "OwnAudioSharp Showcase";

    // Child ViewModels for each tab
    public AudioPlayerViewModel AudioPlayerViewModel { get; }

    public MainWindowViewModel()
    {
        // Initialize child ViewModels
        AudioPlayerViewModel = new AudioPlayerViewModel();
    }
}
