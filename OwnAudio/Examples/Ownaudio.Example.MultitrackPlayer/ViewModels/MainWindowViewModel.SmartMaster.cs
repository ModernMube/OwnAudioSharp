using System;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using OwnaudioNET.Effects.SmartMaster;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Smart Master at the tail of the master chain. Built the first time someone asks for it,
/// an untouched one costs nothing.
/// </summary>
public partial class MainWindowViewModel
{
    private SmartMasterEffect? _smartMaster;

    private static readonly JsonSerializerOptions _presetJson = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SpeakerType[] SpeakerPresets { get; } = Enum.GetValues<SpeakerType>();

    [ObservableProperty]
    private SpeakerType _selectedSpeakerPreset = SpeakerType.Default;

    /// <summary>
    /// The mixer initializes it on AddMasterEffect, nothing else to prepare.
    /// </summary>
    private SmartMasterEffect? _ensureSmartMaster()
    {
        if (_smartMaster != null || _audio.Mixer == null) return _smartMaster;

        _smartMaster = new SmartMasterEffect { Enabled = false };
        _audio.Mixer.AddMasterEffect(_smartMaster);
        return _smartMaster;
    }

    /// <summary>
    /// Bypass toggle - the node stays on the chain either way.
    /// </summary>
    public bool IsSmartMasterEnabled
    {
        get => _smartMaster?.Enabled ?? false;
        set
        {
            var effect = value ? _ensureSmartMaster() : _smartMaster;
            if (effect == null || effect.Enabled == value) return;

            effect.Enabled = value;
            OnPropertyChanged();
            StatusMessage = value ? "Smart Master on" : "Smart Master off";
        }
    }

    partial void OnSelectedSpeakerPresetChanged(SpeakerType value)
    {
        var effect = _ensureSmartMaster();
        if (effect == null) return;

        effect.LoadSpeakerPreset(value);
        StatusMessage = $"Smart Master preset: {value}";
    }

    public void SaveSmartMasterPreset(string filePath)
    {
        var effect = _ensureSmartMaster();
        if (effect == null) return;

        try
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(effect.GetConfiguration(), _presetJson));
            StatusMessage = $"Saved {Path.GetFileName(filePath)}";
        }
        catch (Exception ex) { StatusMessage = $"Could not save the preset: {ex.Message}"; }
    }

    public void LoadSmartMasterPreset(string filePath)
    {
        var effect = _ensureSmartMaster();
        if (effect == null) return;

        try
        {
            var config = JsonSerializer.Deserialize<SmartMasterConfig>(File.ReadAllText(filePath), _presetJson);
            if (config == null) return;

            effect.ApplyConfiguration(config);
            StatusMessage = $"Loaded {Path.GetFileName(filePath)}";
        }
        catch (Exception ex) { StatusMessage = $"Could not load the preset: {ex.Message}"; }
    }
}
