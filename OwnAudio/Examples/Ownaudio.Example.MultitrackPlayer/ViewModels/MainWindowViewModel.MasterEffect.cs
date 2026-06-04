using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnaudioNET.Effects.VST;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Master Effect functionality for MainWindowViewModel.
/// Handles VST3 plugin loading, editor window management, and audio processing.
///
/// VST3 lifecycle (required order):
///   1. VST3PluginHost.CreateAsync(path)       – load on plugin thread
///   2. host.InitializeAudioAsync(sr, block)   – audio init on plugin thread
///   3. host.GetProcessor()                    – only when host.IsReady
///   4. mixer.AddMasterEffect(processor)       – validated + buffers allocated
///   5. processor.Dispose() then host.Dispose() on cleanup
/// </summary>
public partial class MainWindowViewModel
{
    #region Master Effect Fields

    /// <summary>
    /// The VST3 plugin host that manages the plugin lifecycle and editor.
    /// Owns the ThreadedVst3Wrapper; must be disposed after the processor.
    /// </summary>
    private VST3PluginHost? _masterEffectHost;

    /// <summary>
    /// The VST3 effect processor for audio processing.
    /// Borrows the ThreadedVst3Wrapper; must be disposed before the host.
    /// </summary>
    private VST3EffectProcessor? _masterEffect;

    #endregion

    #region Master Effect Properties

    [ObservableProperty]
    private bool _isMasterEffectEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMasterEffect))]
    private string _masterEffectName = "No plugin loaded";

    public bool HasMasterEffect => _masterEffectHost != null;

    [ObservableProperty]
    private ObservableCollection<VST3PluginInfo> _availablePlugins = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedPluginCommand))]
    private VST3PluginInfo? _selectedPlugin;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedPluginCommand))]
    private bool _isScanningPlugins;

    #endregion

    #region Master Effect Commands

    /// <summary>
    /// Scans for available VST3 plugins on a background thread.
    /// </summary>
    [RelayCommand]
    private async Task ScanPluginsAsync()
    {
        try
        {
            IsScanningPlugins = true;
            StatusMessage = "Scanning for VST3 plugins...";

            var plugins = await Task.Run(() => VST3PluginHost.ScanPluginsQuick(includeSubdirectories: true));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailablePlugins.Clear();
                foreach (var plugin in plugins)
                    AvailablePlugins.Add(plugin);

                StatusMessage = $"Found {plugins.Count} VST3 plugin(s)";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning plugins: {ex.Message}";
        }
        finally
        {
            IsScanningPlugins = false;
        }
    }

    /// <summary>
    /// Loads the selected plugin as the master effect.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadSelectedPlugin))]
    private async Task LoadSelectedPluginAsync()
    {
        if (SelectedPlugin == null) return;
        await LoadMasterEffectFromPathAsync(SelectedPlugin.Path);
    }

    private bool CanLoadSelectedPlugin() => SelectedPlugin != null && !IsScanningPlugins;

    /// <summary>
    /// Loads a VST3 plugin from <paramref name="pluginPath"/> as the master effect.
    ///
    /// Steps (all non-blocking for the UI thread):
    ///   1. VST3PluginHost.CreateAsync    — loads on the plugin thread
    ///   2. host.InitializeAudioAsync     — initializes audio on the plugin thread
    ///   3. host.GetProcessor()           — safe only after IsReady
    ///   4. mixer.AddMasterEffect         — validates IsReady, allocates buffers
    ///   5. SetTransportPlaying           — syncs current playback state to the plugin
    /// </summary>
    private async Task LoadMasterEffectFromPathAsync(string pluginPath)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = "Loading VST3 plugin...");

            // Clean up any previously loaded plugin; on macOS waits for JUCE timers to drain.
            await UnloadCurrentPluginAsync(updateUI: false);

            // Step 1: Load plugin (plugin thread, non-blocking)
            var newHost = await VST3PluginHost.CreateAsync(pluginPath).ConfigureAwait(false);

            // Step 2: Initialize audio (plugin thread, non-blocking) 
            var engineConfig = OwnaudioNET.OwnaudioNet.Engine?.Config;
            int sampleRate = engineConfig?.SampleRate ?? 48000;
            int blockSize  = engineConfig?.BufferSize ?? 512;

            bool audioReady = await newHost.InitializeAudioAsync(sampleRate, blockSize)
                                           .ConfigureAwait(false);

            if (!audioReady)
            {
                newHost.Dispose();
                throw new InvalidOperationException(
                    $"Audio initialization failed for plugin: {pluginPath}");
            }

            // Step 3: GetProcessor — only valid when host.IsReady
            var newProcessor = newHost.GetProcessor();

            _masterEffectHost = newHost;
            _masterEffect     = newProcessor;

            // Step 4: Add to mixer
            if (IsMasterEffectEnabled && _audioService.Mixer != null)
            {
                _audioService.Mixer.AddMasterEffect(_masterEffect);
            }

            // Step 5: Sync transport state
            _masterEffect.SetTransportPlaying(_audioService.Mixer?.IsRunning ?? false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MasterEffectName = _masterEffectHost.Name;
                OnPropertyChanged(nameof(HasMasterEffect));
                OpenMasterEffectEditorCommand.NotifyCanExecuteChanged();
                RemoveMasterEffectCommand.NotifyCanExecuteChanged();
                StatusMessage = $"Loaded VST plugin: {_masterEffectHost.Name}";
            });
        }
        catch (Exception ex)
        {
            // On error: dispose in correct order (processor first, then host).
            _masterEffect?.Dispose();
            _masterEffectHost?.Dispose();
            _masterEffectHost = null;
            _masterEffect     = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MasterEffectName = "No plugin loaded";
                OnPropertyChanged(nameof(HasMasterEffect));
                OpenMasterEffectEditorCommand.NotifyCanExecuteChanged();
                RemoveMasterEffectCommand.NotifyCanExecuteChanged();
                StatusMessage = $"Error loading VST plugin: {ex.Message}";
            });
        }
    }

    /// <summary>
    /// Opens or closes the VST3 editor window.
    /// Editor open/close must stay on the UI thread (VST3 + OS requirement).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenMasterEffectEditor))]
    private async Task OpenMasterEffectEditor(Window? window)
    {
        if (_masterEffectHost == null || !_masterEffectHost.HasEditor)
        {
            StatusMessage = "Plugin does not have an editor";
            return;
        }

        try
        {
            if (_masterEffectHost.IsEditorOpen)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    _masterEffectHost.CloseEditor());

                ResumeTimersAfterEditor();
                StatusMessage = $"Closed editor for {_masterEffectHost.Name}";
            }
            else
            {
                PauseTimersForEditor();

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    // macOS: give the audio thread a moment to stabilise before creating a Cocoa window.
                    if (OperatingSystem.IsMacOS())
                        await Task.Delay(50);

                    _masterEffectHost.OpenEditor(_masterEffectHost.Name);
                });

                StatusMessage = $"Opened editor for {_masterEffectHost.Name}";
            }
        }
        catch (Exception ex)
        {
            if (!_masterEffectHost.IsEditorOpen)
                ResumeTimersAfterEditor();

            StatusMessage = $"Error with editor: {ex.Message}";
        }
    }

    private bool CanOpenMasterEffectEditor() =>
        _masterEffectHost != null && _masterEffectHost.HasEditor;

    /// <summary>
    /// Removes the current master effect plugin from the mixer and disposes all resources.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasMasterEffect))]
    private async Task RemoveMasterEffect()
    {
        try
        {
            await UnloadCurrentPluginAsync(updateUI: true);
            StatusMessage = "Master effect removed";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error removing master effect: {ex.Message}";
        }
    }

    /// <summary>
    /// Shared teardown logic: removes the plugin from the mixer, disposes the processor,
    /// then calls DisposeAsync on the host (which handles platform-specific timer drain).
    /// </summary>
    private async Task UnloadCurrentPluginAsync(bool updateUI)
    {
        if (_masterEffectHost == null) return;

        bool editorWasOpen = _masterEffectHost.IsEditorOpen;
        _masterEffectHost.CloseEditor();
        if (editorWasOpen)
            ResumeTimersAfterEditor();

        if (_audioService.Mixer != null && _masterEffect != null)
            _audioService.Mixer.RemoveMasterEffect(_masterEffect);

        _masterEffect?.Dispose();
        _masterEffect = null;

        // Capture and null out before the async gap so HasMasterEffect returns false
        var hostToDispose = _masterEffectHost;
        _masterEffectHost = null;

        if (updateUI)
        {
            MasterEffectName = "No plugin loaded";
            OnPropertyChanged(nameof(HasMasterEffect));
            OpenMasterEffectEditorCommand.NotifyCanExecuteChanged();
            RemoveMasterEffectCommand.NotifyCanExecuteChanged();
        }

        // DisposeAsync handles the platform-specific JUCE timer drain internally.
        await hostToDispose.DisposeAsync();
    }

    #endregion

    #region Master Effect Event Handlers

    partial void OnIsMasterEffectEnabledChanged(bool value)
    {
        if (_masterEffectHost == null)
        {
            if (value)
            {
                IsMasterEffectEnabled = false;
                StatusMessage = "Please load a plugin first";
            }
            return;
        }

        if (_audioService.Mixer == null)
            return;

        try
        {
            if (value)
            {
                // Sync current playback state before inserting into the chain.
                _masterEffect?.SetTransportPlaying(_audioService.Mixer.IsRunning);

                // AddMasterEffect validates IsReady, calls Initialize, and adds to chain.
                _audioService.Mixer.AddMasterEffect(_masterEffect!);
                StatusMessage = "Master effect enabled";
            }
            else
            {
                _audioService.Mixer.RemoveMasterEffect(_masterEffect!);
                StatusMessage = "Master effect disabled";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error toggling master effect: {ex.Message}";
            IsMasterEffectEnabled = !value; // Revert toggle on error
        }
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Disposes all master effect resources. Called from the main Dispose method.
    /// Uses the synchronous VST3PluginHost.Dispose() which handles the macOS timer
    /// drain internally via Thread.Sleep.
    /// </summary>
    private void DisposeMasterEffect()
    {
        if (_masterEffectHost != null && _masterEffectHost.IsEditorOpen)
        {
            _masterEffectHost.CloseEditor();
            ResumeTimersAfterEditor();
        }

        _masterEffect?.Dispose();
        _masterEffect = null;

        _masterEffectHost?.Dispose();
        _masterEffectHost = null;
    }

    #endregion
}
