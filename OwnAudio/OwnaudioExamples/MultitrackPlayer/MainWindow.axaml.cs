using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MultitrackPlayer.ViewModels;

namespace MultitrackPlayer;

/// <summary>
/// Main window for the multitrack audio player application.
/// Provides UI for loading audio files, controlling playback, and managing tracks.
/// </summary>
public partial class MainWindow : Window
{
    #region Properties

    /// <summary>
    /// Gets the ViewModel associated with this window.
    /// </summary>
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// Sets up the UI components and creates the ViewModel.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

    #endregion

    #region Event Handlers - File Operations

    /// <summary>
    /// Handles the "Add Tracks" button click event.
    /// Opens a file picker dialog to allow the user to select audio files.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">Event arguments.</param>
    private async void OnAddTracksClicked(object? sender, RoutedEventArgs e)
    {
        var fileTypes = new FilePickerFileType[]
        {
            new("Audio Files")
            {
                Patterns = new[] { "*.mp3", "*.wav", "*.flac" },
                MimeTypes = new[] { "audio/mpeg", "audio/wav", "audio/flac" }
            },
            new("All Files")
            {
                Patterns = new[] { "*.*" }
            }
        };

        var options = new FilePickerOpenOptions
        {
            Title = "Select Audio Tracks",
            AllowMultiple = true,
            FileTypeFilter = fileTypes
        };

        var result = await StorageProvider.OpenFilePickerAsync(options);

        if (result != null && result.Count > 0)
        {
            foreach (var file in result)
            {
                var path = file.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path) && ViewModel != null)
                {
                    await ViewModel.AddTrackAsync(path);
                }
            }
        }
    }

    #endregion

    #region Event Handlers - Seek Operations

    /// <summary>
    /// Handles the pointer press event on the seek slider.
    /// Begins a seek operation by pausing the playback timer.
    /// </summary>
    /// <param name="sender">The slider that raised the event.</param>
    /// <param name="e">Pointer event arguments.</param>
    private void OnSliderSeekStart(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        ViewModel?.BeginSeekCommand.Execute(null);
    }

    /// <summary>
    /// Handles the pointer release event on the seek slider.
    /// Completes the seek operation by updating the playback position.
    /// </summary>
    /// <param name="sender">The slider that raised the event.</param>
    /// <param name="e">Pointer event arguments.</param>
    private void OnSliderSeekEnd(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel?.EndSeekCommand.Execute(slider.Value);
        }
    }

    #endregion

    #region Lifecycle Methods

    /// <summary>
    /// Called when the window is closed.
    /// Disposes the ViewModel to release audio resources.
    /// </summary>
    /// <param name="e">Event arguments.</param>
    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Dispose();
        base.OnClosed(e);
    }

    #endregion
}