using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;

namespace OwnaudioNET.Interfaces;

/// <summary>
/// One playable audio source: play, pause, seek, volume/pan.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>
    /// Unique id for this source.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Current playback state.
    /// </summary>
    AudioState State { get; }

    /// <summary>
    /// Sample rate, channels, buffer size.
    /// </summary>
    AudioConfig Config { get; }

    /// <summary>
    /// Duration, channels, sample rate.
    /// </summary>
    AudioStreamInfo StreamInfo { get; }

    /// <summary>
    /// Volume, 0..1.
    /// </summary>
    float Volume { get; set; }

    /// <summary>
    /// Stereo pan, -1 left .. +1 right. Equal-power, so centered is untouched.
    /// </summary>
    float Pan { get; set; }

    /// <summary>
    /// Loop back to the start at the end.
    /// </summary>
    bool Loop { get; set; }

    /// <summary>
    /// Playback position in seconds.
    /// </summary>
    double Position { get; }

    /// <summary>
    /// Length in seconds.
    /// </summary>
    double Duration { get; }

    /// <summary>
    /// True once we ran out of audio.
    /// </summary>
    bool IsEndOfStream { get; }

    /// <summary>
    /// Speed multiplier, 1.0 = normal. Needs SoundTouch.
    /// </summary>
    float Tempo { get; set; }

    /// <summary>
    /// Pitch shift in semitones, 0 = none. Needs SoundTouch.
    /// </summary>
    float PitchShift { get; set; }

    /// <summary>
    /// Fills buffer with up to frameCount frames, returns frames actually read.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    int ReadSamples(Span<float> buffer, int frameCount);

    /// <summary>
    /// Jump to a position (seconds), false if it didn't take.
    /// </summary>
    /// <param name="positionInSeconds"></param>
    bool Seek(double positionInSeconds);

    /// <summary>
    /// Start or resume.
    /// </summary>
    void Play();

    /// <summary>
    /// Pause.
    /// </summary>
    void Pause();

    /// <summary>
    /// Stop and rewind to the start.
    /// </summary>
    void Stop();

    /// <summary>
    /// Fires when the playback state flips.
    /// </summary>
    event EventHandler<AudioStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Fires on a buffer underrun.
    /// </summary>
    event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>
    /// Fires when playback blows up.
    /// </summary>
    event EventHandler<AudioErrorEventArgs>? Error;
}
