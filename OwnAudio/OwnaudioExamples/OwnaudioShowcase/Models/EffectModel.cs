using CommunityToolkit.Mvvm.ComponentModel;
using OwnaudioNET.Interfaces;
using System.Collections.ObjectModel;

namespace OwnaudioShowcase.Models;

/// <summary>
/// Represents an audio effect with its parameters.
/// Uses CommunityToolkit.Mvvm for property change notifications.
/// </summary>
public partial class EffectModel : ObservableObject
{
    [ObservableProperty]
    private string name = "Unnamed Effect";

    [ObservableProperty]
    private string type = "Unknown";

    [ObservableProperty]
    private bool isEnabled = true;

    [ObservableProperty]
    private ObservableCollection<EffectParameter> parameters = new();

    /// <summary>
    /// Reference to the actual OwnaudioNET effect processor.
    /// This should not be bound to the UI directly.
    /// </summary>
    public IEffectProcessor? EffectProcessor { get; set; }
}

/// <summary>
/// Represents a single parameter of an audio effect.
/// </summary>
public partial class EffectParameter : ObservableObject
{
    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private float value;

    [ObservableProperty]
    private float minimum;

    [ObservableProperty]
    private float maximum = 1.0f;

    [ObservableProperty]
    private float stepSize = 0.01f;

    [ObservableProperty]
    private string unit = string.Empty;

    /// <summary>
    /// Formatted value string for UI display.
    /// </summary>
    public string ValueFormatted =>
        string.IsNullOrEmpty(Unit) ? $"{Value:F2}" : $"{Value:F2} {Unit}";
}
