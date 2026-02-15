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
/// </summary>
public partial class MainWindowViewModel
{
    #region Master Effect Fields

    /// <summary>
    /// The VST3 plugin host that manages the plugin lifecycle and editor.
    /// </summary>
    private VST3PluginHost? _masterEffectHost;

    /// <summary>
    /// The VST3 effect processor instance for audio processing.
    /// </summary>
    private VST3EffectProcessor? _masterEffect;

    #endregion

    #region Master Effect Properties

    /// <summary>
    /// Gets or sets whether the master effect is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _isMasterEffectEnabled;

    /// <summary>
    /// Gets or sets the name of the loaded master effect plugin.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMasterEffect))]
    private string _masterEffectName = "No plugin loaded";

    /// <summary>
    /// Gets or sets whether a master effect plugin is currently loaded.
    /// </summary>
    public bool HasMasterEffect => _masterEffectHost != null;

    /// <summary>
    /// Gets the list of available VST3 plugins.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<VST3PluginInfo> _availablePlugins = new();

    /// <summary>
    /// Gets or sets the selected plugin from the browser.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedPluginCommand))]
    private VST3PluginInfo? _selectedPlugin;

    /// <summary>
    /// Gets or sets whether plugins are being scanned.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedPluginCommand))]
    private bool _isScanningPlugins;

    #endregion

    #region Master Effect Commands

    /// <summary>
    /// Command to scan for available VST3 plugins.
    /// </summary>
    [RelayCommand]
    private async Task ScanPluginsAsync()
    {
        try
        {
            IsScanningPlugins = true;
            StatusMessage = "Scanning for VST3 plugins...";

            // Scan for plugins on a background thread
            var plugins = await Task.Run(() => VST3PluginHost.ScanPlugins(includeSubdirectories: true));

            // Filter to only effects (not instruments)
            var effectPlugins = plugins.Where(p => p.IsEffect).ToList();

            // Update UI on main thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailablePlugins.Clear();
                foreach (var plugin in effectPlugins)
                {
                    AvailablePlugins.Add(plugin);
                }

                StatusMessage = $"Found {effectPlugins.Count} VST3 effect plugin(s)";
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
    /// Command to load the selected VST3 plugin as the master effect.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadSelectedPlugin))]
    private async Task LoadSelectedPluginAsync()
    {
        if (SelectedPlugin == null)
            return;

        await LoadMasterEffectFromPathAsync(SelectedPlugin.Path);
    }

    /// <summary>
    /// Determines whether a plugin can be loaded.
    /// </summary>
    private bool CanLoadSelectedPlugin()
    {
        return SelectedPlugin != null && !IsScanningPlugins;
    }

    /// <summary>
    /// Loads a VST3 plugin from the specified path.
    /// </summary>
    private Task LoadMasterEffectFromPathAsync(string pluginPath)
    {
        try
        {
            StatusMessage = "Loading VST3 plugin...";

            // Remove existing master effect if any
            RemoveMasterEffect();

            // Create new VST3 plugin host
            _masterEffectHost = new VST3PluginHost(pluginPath);

            // Get processor from host
            _masterEffect = _masterEffectHost.GetProcessor();

            // Initialize with current audio config
            if (OwnaudioNET.OwnaudioNet.Engine != null)
            {
                _masterEffect.Initialize(OwnaudioNET.OwnaudioNet.Engine.Config);
            }

            // Update UI
            MasterEffectName = _masterEffectHost.Name;
            OnPropertyChanged(nameof(HasMasterEffect));

            // Notify commands to re-evaluate their CanExecute
            OpenMasterEffectEditorCommand.NotifyCanExecuteChanged();
            RemoveMasterEffectCommand.NotifyCanExecuteChanged();

            // Add to mixer if enabled
            if (IsMasterEffectEnabled && _audioService.Mixer != null)
            {
                _audioService.Mixer.AddMasterEffect(_masterEffect);
            }

            StatusMessage = $"Loaded VST plugin: {_masterEffectHost.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading VST plugin: {ex.Message}";
            _masterEffectHost?.Dispose();
            _masterEffectHost = null;
            _masterEffect = null;
            MasterEffectName = "No plugin loaded";
            OnPropertyChanged(nameof(HasMasterEffect));

            // Notify commands to re-evaluate their CanExecute
            OpenMasterEffectEditorCommand.NotifyCanExecuteChanged();
            RemoveMasterEffectCommand.NotifyCanExecuteChanged();
        }

        return Task.CompletedTask;
    }


    /// <summary>
    /// Command to open the VST3 editor window.
    /// </summary>
    /// <param name="window">The parent window (not used anymore, kept for compatibility).</param>
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
                // Closing editor - do it synchronously on UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _masterEffectHost.CloseEditor();
                });
                ResumeTimersAfterEditor();
                StatusMessage = $"Closed editor for {_masterEffectHost.Name}";
            }
            else
            {
                // Opening editor - use async pattern for macOS compatibility
                PauseTimersForEditor();

                // On macOS, native window creation must happen on the main thread with proper timing
                // Wrap in InvokeAsync to ensure we're on the correct dispatcher context
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    // Small delay on macOS to let audio thread stabilize
                    if (OperatingSystem.IsMacOS())
                    {
                        await Task.Delay(50);
                    }

                    _masterEffectHost.OpenEditor(_masterEffectHost.Name);
                });

                StatusMessage = $"Opened editor for {_masterEffectHost.Name}";
            }
        }
        catch (Exception ex)
        {
            // If opening failed, make sure to resume timers
            if (!_masterEffectHost.IsEditorOpen)
            {
                ResumeTimersAfterEditor();
            }
            StatusMessage = $"Error with editor: {ex.Message}";
        }
    }

    /// <summary>
    /// Determines whether the master effect editor can be opened.
    /// </summary>
    private bool CanOpenMasterEffectEditor()
    {
        return _masterEffectHost != null && _masterEffectHost.HasEditor;
    }

    /// <summary>
    /// Command to remove the current master effect plugin.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasMasterEffect))]
    private void RemoveMasterEffect()
    {
        try
        {
            if (_masterEffectHost == null)
                return;

            // Resume timers if editor was open
            bool editorWasOpen = _masterEffectHost.IsEditorOpen;

            // Close native editor window if open
            _masterEffectHost.CloseEditor();

            if (editorWasOpen)
            {
                ResumeTimersAfterEditor();
            }

            // Remove from mixer
            if (_audioService.Mixer != null && _masterEffect != null)
            {
                _audioService.Mixer.RemoveMasterEffect(_masterEffect);
            }

            // Dispose the host (which also disposes the processor)
            _masterEffectHost.Dispose();
            _masterEffectHost = null;
            _masterEffect = null;

            // Update UI
            MasterEffectName = "No plugin loaded";
            OnPropertyChanged(nameof(HasMasterEffect));

            // Notify commands to re-evaluate their CanExecute
            OpenMasterEffectEditorCommand.NotifyCanExecuteChanged();
            RemoveMasterEffectCommand.NotifyCanExecuteChanged();

            StatusMessage = "Master effect removed";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error removing master effect: {ex.Message}";
        }
    }

    #endregion

    #region Master Effect Event Handlers

    /// <summary>
    /// Handles changes to the IsMasterEffectEnabled property.
    /// </summary>
    partial void OnIsMasterEffectEnabledChanged(bool value)
    {
        // If no plugin is loaded, automatically turn off the toggle
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
                // Add master effect to mixer
                _audioService.Mixer.AddMasterEffect(_masterEffect);
                StatusMessage = "Master effect enabled";
            }
            else
            {
                // Remove master effect from mixer
                _audioService.Mixer.RemoveMasterEffect(_masterEffect);
                StatusMessage = "Master effect disabled";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error toggling master effect: {ex.Message}";
            IsMasterEffectEnabled = !value; // Revert the change
        }
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Disposes master effect resources (called from main Dispose method).
    /// </summary>
    private void DisposeMasterEffect()
    {
        if (_masterEffectHost != null && _masterEffectHost.IsEditorOpen)
        {
            _masterEffectHost.CloseEditor();
            ResumeTimersAfterEditor();
        }
        _masterEffectHost?.Dispose();
        _masterEffectHost = null;
        _masterEffect = null;
    }

    #endregion
}
