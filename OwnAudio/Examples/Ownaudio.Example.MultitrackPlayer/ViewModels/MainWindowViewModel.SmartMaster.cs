using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnaudioNET.Effects.SmartMaster;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// SmartMaster effect functionality for MainWindowViewModel.
/// Contains: SmartMaster effect management, presets, measurement wizard, and calibration.
/// </summary>
public partial class MainWindowViewModel
{
    #region Properties

    /// <summary>
    /// Gets or sets whether SmartMaster effect is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _isSmartMasterEnabled;

    /// <summary>
    /// Called when IsSmartMasterEnabled changes.
    /// </summary>
    partial void OnIsSmartMasterEnabledChanged(bool value)
    {
        if (_smartMaster != null)
        {
            _smartMaster.Enabled = value;
        }
    }

    /// <summary>
    /// Gets or sets the selected speaker preset type.
    /// </summary>
    [ObservableProperty]
    private SpeakerType _selectedSpeakerPreset = SpeakerType.Default;

    /// <summary>
    /// Gets the list of available speaker presets.
    /// </summary>
    public List<SpeakerType> AvailableSpeakerPresets { get; } = new()
    {
        SpeakerType.Default,
        SpeakerType.HiFi,
        SpeakerType.Headphone,
        SpeakerType.Studio,
        SpeakerType.Club,
        SpeakerType.Concert
    };

    /// <summary>
    /// Gets or sets whether a measurement is currently in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isMeasuring;

    /// <summary>
    /// Gets or sets the measurement progress (0-100).
    /// </summary>
    [ObservableProperty]
    private float _measurementProgress;

    /// <summary>
    /// Gets or sets the measurement status text.
    /// </summary>
    [ObservableProperty]
    private string _measurementStatusText = "";

    /// <summary>
    /// Gets or sets the microphone input volume (0.0 - 2.0).
    /// </summary>
    [ObservableProperty]
    private float _micVolume = 1.0f;

    /// <summary>
    /// Called when MicVolume changes.
    /// </summary>
    partial void OnMicVolumeChanged(float value)
    {
        if (_smartMaster != null)
        {
            var config = _smartMaster.GetConfiguration();
            config.MicInputGain = value;
        }
    }

    /// <summary>
    /// Gets or sets the current microphone level in dB.
    /// </summary>
    [ObservableProperty]
    private float _micLevel = -100.0f;

    /// <summary>
    /// Gets or sets whether microphone monitoring is active.
    /// </summary>
    [ObservableProperty]
    private bool _isMicMonitoring;

    #endregion

    #region Commands

    /// <summary>
    /// Toggles the SmartMaster effect on/off.
    /// </summary>
    [RelayCommand]
    private void ToggleSmartMaster()
    {
        IsSmartMasterEnabled = !IsSmartMasterEnabled;
        
        if (_smartMaster != null)
        {
            _smartMaster.Enabled = IsSmartMasterEnabled;
            StatusMessage = IsSmartMasterEnabled ? "SmartMaster enabled" : "SmartMaster disabled";
        }
    }

    /// <summary>
    /// Loads the selected factory preset.
    /// </summary>
    [RelayCommand]
    private void LoadFactoryPreset()
    {
        if (_smartMaster == null)
            return;

        try
        {
            _smartMaster.LoadSpeakerPreset(SelectedSpeakerPreset);
            StatusMessage = $"Loaded {SelectedSpeakerPreset} preset";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading preset: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads the measured preset from the last measurement.
    /// </summary>
    [RelayCommand]
    private void LoadMeasuredPreset()
    {
        if (_smartMaster == null)
            return;

        try
        {
            _smartMaster.Load("measured");
            StatusMessage = "Loaded measured preset (from last measurement)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading measured preset: {ex.Message}";
        }
    }

    /// <summary>
    /// Starts the measurement wizard.
    /// </summary>
    [RelayCommand]
    private async Task StartMeasurementAsync()
    {
        if (_smartMaster == null || IsMeasuring)
            return;

        try
        {
            IsMeasuring = true;
            MeasurementProgress = 0;
            MeasurementStatusText = "Starting measurement...";

            // Start measurement timer
            _measurementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _measurementTimer.Tick += MeasurementTimer_Tick;
            _measurementTimer.Start();

            // CRITICAL: Pause the mixer to prevent it from fighting for the output buffer
            bool wasMixerRunning = _audioService.Mixer != null;
            if (wasMixerRunning)
            {
                _audioService.Mixer?.Pause();
                await Task.Delay(100);
            }

            try
            {
                // Start measurement
                await _smartMaster.StartMeasurementAsync();
            }
            finally
            {
                // CRITICAL FIX SEQUENCE for post-measurement stability: 1. Reset SmartMaster components to clear any internal corruption (NaNs/Infs), 2. Clear Engine Buffer to remove residual measurement data, 3. Restart Mixer
                
                try
                {
                    // Step 1: Force reset of all SmartMaster components
                    // This clears any NaN/Inf corruption in filter states that occurred during measurement
                    _smartMaster?.Reset();
                    
                    // Step 2: Clear the audio engine's output buffer
                    // This removes any residual measurement data that could cause buffer overflow
                    if (OwnaudioNET.OwnaudioNet.Engine != null)
                    {
                        OwnaudioNET.OwnaudioNet.Engine.ClearOutputBuffer();
                        // Give the engine a moment to stabilize (flush pending buffers)
                        await Task.Delay(50);
                    }
                }
                catch
                {
                    // Ignore errors during cleanup to prevent crash
                }

                // Step 3: Restart mixer if it was running, regardless of success/failure
                if (wasMixerRunning)
                {
                    _audioService.Mixer?.Start();
                }
            }

            // Stop timer
            _measurementTimer?.Stop();
            _measurementTimer = null;

            IsMeasuring = false;

            // Check the final measurement status
            var finalStatus = _smartMaster.GetMeasurementStatus();
            
            if (finalStatus.Status == OwnaudioNET.Effects.SmartMaster.MeasurementStatus.Error)
            {
                // Measurement failed
                MeasurementProgress = 0;
                MeasurementStatusText = $"Error: {finalStatus.ErrorMessage ?? "Unknown error"}";
                StatusMessage = $"Measurement failed: {finalStatus.ErrorMessage ?? "Unknown error"}";
            }
            else if (finalStatus.Status == OwnaudioNET.Effects.SmartMaster.MeasurementStatus.Completed)
            {
                // Measurement completed successfully
                MeasurementProgress = 100;
                MeasurementStatusText = finalStatus.CurrentStep;
                StatusMessage = finalStatus.Warnings.Length > 0 
                    ? $"Measurement completed with {finalStatus.Warnings.Length} warning(s)" 
                    : "Measurement completed successfully";
            }
            else
            {
                // Unexpected status
                MeasurementProgress = 0;
                MeasurementStatusText = "Measurement ended unexpectedly";
                StatusMessage = "Measurement ended unexpectedly";
            }
        }
        catch (Exception ex)
        {
            _measurementTimer?.Stop();
            _measurementTimer = null;
            IsMeasuring = false;
            MeasurementProgress = 0;
            MeasurementStatusText = $"Error: {ex.Message}";
            StatusMessage = $"Measurement failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Cancels the ongoing measurement.
    /// </summary>
    [RelayCommand]
    private void CancelMeasurement()
    {
        if (_smartMaster == null || !IsMeasuring)
            return;

        _smartMaster.CancelMeasurement();
        _measurementTimer?.Stop();
        _measurementTimer = null;
        IsMeasuring = false;
        MeasurementStatusText = "Measurement cancelled";
        StatusMessage = "Measurement cancelled";
    }

    /// <summary>
    /// Toggles microphone monitoring on/off.
    /// </summary>
    [RelayCommand]
    private void ToggleMicMonitoring()
    {
        if (_smartMaster == null)
            return;

        IsMicMonitoring = !IsMicMonitoring;

        if (IsMicMonitoring)
        {
            // Start monitoring
            _smartMaster.StartMicMonitoring();
            
            // Start timer for UI updates
            _measurementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _measurementTimer.Tick += MicMonitoringTimer_Tick;
            _measurementTimer.Start();
            
            StatusMessage = "Microphone monitoring started";
        }
        else
        {
            // Stop monitoring
            _smartMaster.StopMicMonitoring();
            _measurementTimer?.Stop();
            _measurementTimer = null;
            MicLevel = -100.0f;
            
            StatusMessage = "Microphone monitoring stopped";
        }
    }

    /// <summary>
    /// Saves current settings as a custom preset.
    /// </summary>
    /// <param name="filePath">Full path to the preset file. Extension will be auto-appended if missing.</param>
    public async Task SaveCustomPresetAsync(string filePath)
    {
        if (_smartMaster == null)
            return;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            StatusMessage = "Error: No file path provided";
            return;
        }

        try
        {
            // Ensure the path ends with .smartmaster.json
            if (!filePath.EndsWith(".smartmaster.json", StringComparison.OrdinalIgnoreCase))
            {
                // Remove any existing extension and add .smartmaster.json
                var pathWithoutExt = System.IO.Path.ChangeExtension(filePath, null);
                filePath = pathWithoutExt + ".smartmaster.json";
            }

            // Get the current configuration from SmartMaster
            var configuration = _smartMaster.GetConfiguration();

            // Serialize to JSON
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            };

            string json = System.Text.Json.JsonSerializer.Serialize(configuration, options);
            
            // Save directly to the user-selected path
            System.IO.File.WriteAllText(filePath, json);

            string fileName = System.IO.Path.GetFileName(filePath);
            StatusMessage = $"Saved preset: {fileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error saving preset: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Loads a custom preset.
    /// </summary>
    /// <param name="filePath">Full path to the preset file to load.</param>
    public async Task LoadCustomPresetAsync(string filePath)
    {
        if (_smartMaster == null)
            return;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            StatusMessage = "Error: No file path provided";
            return;
        }

        if (!System.IO.File.Exists(filePath))
        {
            StatusMessage = $"Error: File not found: {System.IO.Path.GetFileName(filePath)}";
            return;
        }

        try
        {
            // Read the JSON file
            string json = System.IO.File.ReadAllText(filePath);

            // Deserialize the configuration
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            };

            var loadedConfig = System.Text.Json.JsonSerializer.Deserialize<OwnaudioNET.Effects.SmartMaster.SmartMasterConfig>(json, options);

            if (loadedConfig != null)
            {
                // Use reflection to set the private _configuration field
                var configField = _smartMaster.GetType().GetField("_configuration", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (configField != null)
                {
                    configField.SetValue(_smartMaster, loadedConfig);
                    
                    // Apply the configuration to components
                    var applyMethod = _smartMaster.GetType().GetMethod("ApplyConfigurationToComponents",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                    if (applyMethod != null)
                    {
                        applyMethod.Invoke(_smartMaster, null);
                    }
                }

                string fileName = System.IO.Path.GetFileName(filePath);
                StatusMessage = $"Loaded preset: {fileName}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading preset: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Updates measurement progress from timer.
    /// </summary>
    private void MeasurementTimer_Tick(object? sender, EventArgs e)
    {
        if (_smartMaster == null)
            return;

        var status = _smartMaster.GetMeasurementStatus();
        MeasurementProgress = status.Progress * 100;
        MeasurementStatusText = status.CurrentStep;
    }

    /// <summary>
    /// Updates microphone level from timer.
    /// </summary>
    private void MicMonitoringTimer_Tick(object? sender, EventArgs e)
    {
        if (_smartMaster == null)
            return;

        MicLevel = _smartMaster.GetLastMicLevel();
    }

    #endregion
}
