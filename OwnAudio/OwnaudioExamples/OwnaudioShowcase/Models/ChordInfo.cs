using CommunityToolkit.Mvvm.ComponentModel;

namespace OwnaudioShowcase.Models;

/// <summary>
/// Represents a detected chord at a specific time in an audio track.
/// </summary>
public partial class ChordInfo : ObservableObject
{
    [ObservableProperty]
    private double startTime;

    [ObservableProperty]
    private double endTime;

    [ObservableProperty]
    private string chordName = string.Empty;

    [ObservableProperty]
    private float confidence;

    /// <summary>
    /// Duration of the chord in seconds.
    /// </summary>
    public double Duration => EndTime - StartTime;

    /// <summary>
    /// Formatted time range string for UI display (mm:ss - mm:ss).
    /// </summary>
    public string TimeRange =>
        $"{TimeSpan.FromSeconds(StartTime):mm\\:ss} - {TimeSpan.FromSeconds(EndTime):mm\\:ss}";

    /// <summary>
    /// Formatted confidence string for UI display (percentage).
    /// </summary>
    public string ConfidenceFormatted => $"{Confidence * 100:F1}%";
}
