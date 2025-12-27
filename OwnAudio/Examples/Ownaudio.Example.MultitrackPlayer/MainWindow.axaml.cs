using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

        // FIX: Sliders in Avalonia often handle PointerPressed/Released internally.
        // We must use AddHandler with handledEventsToo: true to ensure our methods are called.
        
        // Seek Slider
        var seekSlider = this.FindControl<Slider>("SeekSlider");
        if (seekSlider != null)
        {
            seekSlider.AddHandler(PointerPressedEvent, OnSliderSeekStart, RoutingStrategies.Tunnel, true);
            seekSlider.AddHandler(PointerReleasedEvent, OnSliderSeekEnd, RoutingStrategies.Bubble, true);
        }

        // Tempo Slider
        var tempoSlider = this.FindControl<Slider>("TempoSlider");
        if (tempoSlider != null)
        {
            tempoSlider.AddHandler(PointerReleasedEvent, OnTempoSliderDragEnd, RoutingStrategies.Bubble, true);
        }

        // Pitch Slider
        var pitchSlider = this.FindControl<Slider>("PitchSlider");
        if (pitchSlider != null)
        {
            pitchSlider.AddHandler(PointerReleasedEvent, OnPitchSliderDragEnd, RoutingStrategies.Bubble, true);
        }
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

    /// <summary>
    /// Handles the "Save SmartMaster Preset" button click event.
    /// Opens a save file dialog to allow the user to save the current SmartMaster settings.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">Event arguments.</param>
    private async void OnSaveSmartMasterPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        var fileTypes = new FilePickerFileType[]
        {
            new("SmartMaster Preset")
            {
                Patterns = new[] { "*.smartmaster.json" }
            }
        };

        var options = new FilePickerSaveOptions
        {
            Title = "Save SmartMaster Preset",
            FileTypeChoices = fileTypes,
            SuggestedFileName = $"preset_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExtension = ".smartmaster.json"
        };

        var result = await StorageProvider.SaveFilePickerAsync(options);

        if (result != null)
        {
            var path = result.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                // Ensure the path ends with .smartmaster.json
                if (!path.EndsWith(".smartmaster.json", StringComparison.OrdinalIgnoreCase))
                {
                    // Remove any existing extension and add .smartmaster.json
                    var pathWithoutExt = System.IO.Path.ChangeExtension(path, null);
                    path = pathWithoutExt + ".smartmaster.json";
                }

                await ViewModel.SaveCustomPresetAsync(path);
            }
        }
    }

    /// <summary>
    /// Handles the "Load SmartMaster Preset" button click event.
    /// Opens an open file dialog to allow the user to load a SmartMaster preset.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">Event arguments.</param>
    private async void OnLoadSmartMasterPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        var fileTypes = new FilePickerFileType[]
        {
            new("SmartMaster Preset")
            {
                Patterns = new[] { "*.smartmaster.json" }
            }
        };

        var options = new FilePickerOpenOptions
        {
            Title = "Load SmartMaster Preset",
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        };

        var result = await StorageProvider.OpenFilePickerAsync(options);

        if (result != null && result.Count > 0)
        {
            var path = result[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                await ViewModel.LoadCustomPresetAsync(path);
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
    private void OnSliderSeekStart(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.BeginSeekCommand.Execute(null);
    }

    /// <summary>
    /// Handles the pointer release event on the seek slider.
    /// Completes the seek operation by updating the playback position.
    /// </summary>
    /// <param name="sender">The slider that raised the event.</param>
    /// <param name="e">Pointer event arguments.</param>
    private void OnSliderSeekEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel?.EndSeekCommand.Execute(slider.Value);
        }
    }

    #endregion

    #region Event Handlers - Slider Drag Optimization

    /// <summary>
    /// Handles ValueChanged event on the tempo slider.
    /// Updates ONLY the UI display, does NOT update audio engine during drag.
    /// </summary>
    private void OnTempoSliderValueChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is Slider slider)
        { 
            if(TempoValueText != null)
            {
                TempoValueText.Text = $"{slider.Value:F0}%";
            }
        }
    }

    /// <summary>
    /// Handles PointerReleased event on the tempo slider.
    /// Applies the FINAL tempo value to audio engine ONLY when user releases mouse.
    /// </summary>
    private void OnTempoSliderDragEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && ViewModel != null)
        {
            // Apply final value to audio engine
            float finalValue = (float)slider.Value;
            ViewModel.ApplyTempoChange(finalValue);

            // Also update ViewModel property for binding consistency
            ViewModel.TempoPercent = finalValue;
        }
    }

    /// <summary>
    /// Handles ValueChanged event on the pitch slider.
    /// Updates ONLY the UI display, does NOT update audio engine during drag.
    /// </summary>
    private void OnPitchSliderValueChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            // Update UI TextBlock only (visual feedback during drag)
            var textBlock = this.FindControl<TextBlock>("PitchValueText");
            if (textBlock != null)
            {
                int pitchValue = (int)slider.Value;
                textBlock.Text = $"{pitchValue:F0} st";
            }

            // Do NOT update audio engine here - that happens on PointerReleased
        }
    }

    /// <summary>
    /// Handles PointerReleased event on the pitch slider.
    /// Applies the FINAL pitch value to audio engine ONLY when user releases mouse.
    /// </summary>
    private void OnPitchSliderDragEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && ViewModel != null)
        {
            // Apply final value to audio engine
            int finalValue = (int)slider.Value;
            ViewModel.ApplyPitchChange(finalValue);

            // Also update ViewModel property for binding consistency
            ViewModel.PitchSemitones = finalValue;
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
