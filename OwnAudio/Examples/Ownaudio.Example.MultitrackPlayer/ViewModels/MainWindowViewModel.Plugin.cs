using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnaudioNET;
using OwnaudioNET.Effects.VST;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// One plugin on the master bus - VST3 anywhere, AudioUnit on macOS too.
/// </summary>
public partial class MainWindowViewModel
{
    private VST3PluginHost? _pluginHost;
    private VST3EffectProcessor? _pluginProcessor;

    public ObservableCollection<AudioPluginInfo> AvailablePlugins { get; } = new ObservableCollection<AudioPluginInfo>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPluginCommand))]
    private AudioPluginInfo? _selectedPlugin;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPluginCommand))]
    private bool _isScanningPlugins;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlugin))]
    [NotifyCanExecuteChangedFor(nameof(OpenPluginEditorCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemovePluginCommand))]
    private string _pluginName = "No plugin loaded";

    /// <summary>
    /// Bypass, not removal: a node pulled out of the chain takes its latency with it and the mix jumps.
    /// </summary>
    [ObservableProperty]
    private bool _isPluginEnabled = true;

    public bool HasPlugin => _pluginHost != null;

    partial void OnIsPluginEnabledChanged(bool value)
    {
        if (_pluginProcessor != null) _pluginProcessor.Enabled = value;
    }

    /// <summary>
    /// The browser's quick scan where the native host has one, a plain VST3 folder walk where it doesn't.
    /// </summary>
    [RelayCommand]
    private async Task ScanPluginsAsync()
    {
        IsScanningPlugins = true;
        StatusMessage = "Scanning for plugins...";

        IReadOnlyList<AudioPluginInfo> found;
        try
        {
            found = AudioPluginBrowser.IsSupported
                ? await AudioPluginBrowser.ScanAsync(AudioPluginFormat.All)
                : await Task.Run(_vst3Folders);
        }
        catch (Exception ex)
        {
            found = Array.Empty<AudioPluginInfo>();
            StatusMessage = $"Plugin scan failed: {ex.Message}";
        }

        AvailablePlugins.Clear();
        foreach (var p in found)
            if (!p.IsInstrument) AvailablePlugins.Add(p);

        IsScanningPlugins = false;
        StatusMessage = $"Found {AvailablePlugins.Count} effect plugin(s)";
    }

    private static IReadOnlyList<AudioPluginInfo> _vst3Folders()
    {
        var list = new List<AudioPluginInfo>();
        foreach (string path in VST3PluginHost.FindPlugins())
            list.Add(new AudioPluginInfo
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Identifier = path,
                FilePath = path,
                Format = AudioPluginFormat.Vst3
            });
        return list;
    }

    /// <summary>
    /// Load, check it really is an effect, prepare its audio, then onto the master chain in
    /// front of the Smart Master.
    /// </summary>
    [RelayCommand(CanExecute = nameof(_canLoadPlugin))]
    private async Task LoadPlugin()
    {
        var mixer = _audio.Mixer;
        if (SelectedPlugin == null || mixer == null) return;

        await RemovePlugin();
        StatusMessage = $"Loading {SelectedPlugin.Name}...";

        VST3PluginHost host;
        try { host = await VST3PluginHost.CreateAsync(SelectedPlugin); }
        catch (Exception ex)
        {
            StatusMessage = $"Plugin load failed: {ex.Message}";
            return;
        }

        if (!host.IsEffect)
        {
            host.Dispose();
            StatusMessage = $"{host.Name} is not an effect";
            return;
        }

        if (!await host.InitializeAudioAsync(OwnaudioNet.Engine!.Config.SampleRate, maxBlockSize: 4096))
        {
            host.Dispose();
            StatusMessage = $"{host.Name} refused to initialize its audio";
            return;
        }

        _pluginHost = host;
        _pluginProcessor = host.GetProcessor();
        _pluginProcessor.Enabled = IsPluginEnabled;
        _pluginProcessor.SetTransportPlaying(IsPlaying);

        if (_smartMaster != null) mixer.RemoveMasterEffect(_smartMaster);
        mixer.AddMasterEffect(_pluginProcessor);
        if (_smartMaster != null) mixer.AddMasterEffect(_smartMaster);

        PluginName = host.Name;
        StatusMessage = $"Loaded {host.Name}";
    }

    private bool _canLoadPlugin() => SelectedPlugin != null && !IsScanningPlugins;

    /// <summary>
    /// Opens the editor, or closes it when it is up. Stays on the UI thread, the OS wants it there.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasPlugin))]
    private async Task OpenPluginEditor()
    {
        if (_pluginHost == null) return;

        if (!_pluginHost.HasEditor)
        {
            StatusMessage = $"{_pluginHost.Name} has no editor";
            return;
        }

        if (_pluginHost.IsEditorOpen) _pluginHost.CloseEditor();
        else await _pluginHost.OpenEditorAsync(_pluginHost.Name);
    }

    /// <summary>
    /// Off the bus first, the dispose comes 100 ms later so the audio thread is surely out of
    /// Process. Processor before host.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasPlugin))]
    private async Task RemovePlugin()
    {
        if (_pluginHost == null) return;

        var host = _pluginHost;
        var processor = _pluginProcessor;
        _pluginHost = null;
        _pluginProcessor = null;

        host.CloseEditor();
        if (processor != null) _audio.Mixer?.RemoveMasterEffect(processor);

        PluginName = "No plugin loaded";

        await Task.Delay(100);
        processor?.Dispose();
        await host.DisposeAsync();
    }
}
