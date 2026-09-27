using OwnaudioNET.Mixing;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// The player the network sync reads and steers. The mixer is the real one; tests put a
/// simulated player here so the whole loop runs over sockets without an audio device.
/// </summary>
internal interface ISyncTimeline
{
    /// <summary>
    /// Song position in project seconds and whether it plays. Rendering is false while a start
    /// is still silent — the position isn't moving yet then.
    /// </summary>
    bool Read(out double position, out bool rendering);

    /// <summary>
    /// How far the speaker is behind the rendered position, seconds.
    /// </summary>
    double OutputLatency { get; }

    /// <summary>
    /// Speed trim on top of the tracks' own tempo, 1.0 = none.
    /// </summary>
    float Trim { get; set; }

    void Follow(double position, bool play);
}

/// <summary>
/// ISyncTimeline over a live AudioMixer.
/// </summary>
internal sealed class MixerSyncTimeline : ISyncTimeline
{
    private readonly AudioMixer _mixer;

    public MixerSyncTimeline(AudioMixer mixer) => _mixer = mixer;

    public bool Read(out double position, out bool rendering) => _mixer.ReadSongPosition(out position, out rendering);

    public double OutputLatency => _mixer.OutputLatencySeconds;

    public float Trim
    {
        get => _mixer.MasterClock.TempoTrim;
        set => _mixer.MasterClock.TempoTrim = value;
    }

    public void Follow(double position, bool play) => _mixer.FollowTransport(position, play);
}
