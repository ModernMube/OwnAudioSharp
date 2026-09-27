using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MultitrackPlayer.ViewModels;

namespace MultitrackPlayer;

/// <summary>
/// File pickers and the seek slider's mouse handling. Everything else is bound to the VM.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly FilePickerFileType _audioFiles = new FilePickerFileType("Audio Files")
    {
        Patterns = new[] { "*.wav", "*.mp3", "*.flac" }
    };

    private static readonly FilePickerFileType _presetFiles = new FilePickerFileType("Smart Master Preset")
    {
        Patterns = new[] { "*.smartmaster.json" }
    };

    private readonly MainWindowViewModel _vm = new MainWindowViewModel();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        //The thumb swallows the pointer events, so we listen for handled ones too
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => _vm.BeginSeekCommand.Execute(null), RoutingStrategies.Tunnel, true);
        SeekSlider.AddHandler(PointerReleasedEvent, (_, _) => _vm.EndSeekCommand.Execute(SeekSlider.Value), RoutingStrategies.Bubble, true);
    }

    private async void OnAddTracksClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Audio Tracks",
            AllowMultiple = true,
            FileTypeFilter = new[] { _audioFiles, FilePickerFileTypes.All }
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path) await _vm.AddTrackAsync(path);
        }
    }

    private async void OnSaveSmartMasterClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Smart Master Preset",
            FileTypeChoices = new[] { _presetFiles },
            SuggestedFileName = $"preset_{DateTime.Now:yyyyMMdd_HHmmss}.smartmaster.json"
        });

        if (file?.TryGetLocalPath() is { } path) _vm.SaveSmartMasterPreset(path);
    }

    private async void OnLoadSmartMasterClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Smart Master Preset",
            FileTypeFilter = new[] { _presetFiles }
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) _vm.LoadSmartMasterPreset(path);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
