using CommunityToolkit.Mvvm.ComponentModel;
using OwnaudioNET.Interfaces;

namespace OwnaudioShowcase.Models;

/// <summary>
/// Represents an audio track in the multitrack mixer.
/// Uses CommunityToolkit.Mvvm for property change notifications.
/// </summary>
public partial class AudioTrackModel : ObservableObject
{
    [ObservableProperty]
    private Guid id = Guid.NewGuid();

    [ObservableProperty]
    private string name = "Unnamed Track";

    [ObservableProperty]
    private string filePath = string.Empty;

    [ObservableProperty]
    private float volume = 1.0f;

    [ObservableProperty]
    private bool isMuted;

    [ObservableProperty]
    private bool isSolo;

    [ObservableProperty]
    private double duration;

    // Manual implementation for position to avoid unnecessary notifications (updated 60x/sec)
    private double _position;
    public double Position
    {
        get => _position;
        set
        {
            // Only notify if value actually changed (reduce notification overhead)
            if (Math.Abs(_position - value) > 0.001)
            {
                _position = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private float tempo = 1.0f;

    [ObservableProperty]
    private float pitchShift = 0.0f;

    [ObservableProperty]
    private bool isPlaying;

    /// <summary>
    /// Reference to the actual OwnaudioNET audio source.
    /// This should not be bound to the UI directly.
    /// </summary>
    public IAudioSource? AudioSource { get; set; }

    // Cached formatted strings to avoid allocations on every access
    private string? _durationFormatted;
    private string? _positionFormatted;

    /// <summary>
    /// Formatted duration string for UI display (mm:ss).
    /// Cached to avoid repeated allocations.
    /// </summary>
    public string DurationFormatted
    {
        get
        {
            if (_durationFormatted == null)
            {
                _durationFormatted = TimeSpan.FromSeconds(Duration).ToString(@"mm\:ss");
            }
            return _durationFormatted;
        }
    }

    /// <summary>
    /// Formatted position string for UI display (mm:ss).
    /// Cached to avoid repeated allocations.
    /// NOTE: This is not currently used in the UI, but kept for potential future use.
    /// </summary>
    public string PositionFormatted
    {
        get
        {
            if (_positionFormatted == null || Math.Abs(_position - _lastFormattedPosition) > 1.0)
            {
                _positionFormatted = TimeSpan.FromSeconds(Position).ToString(@"mm\:ss");
                _lastFormattedPosition = _position;
            }
            return _positionFormatted;
        }
    }

    private double _lastFormattedPosition = -1;
}
