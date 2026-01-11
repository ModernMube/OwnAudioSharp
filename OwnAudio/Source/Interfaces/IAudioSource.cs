using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;

namespace OwnaudioNET.Interfaces;

/// <summary>
/// Represents a source of audio data that can be played, paused, and controlled.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>
    /// Gets the unique identifier for this audio source.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Gets the current playback state.
    /// </summary>
    AudioState State { get; }

    /// <summary>
    /// Gets the audio configuration (sample rate, channels, buffer size).
    /// </summary>
    AudioConfig Config { get; }

    /// <summary>
    /// Gets the stream information (duration, channels, sample rate).
    /// </summary>
    AudioStreamInfo StreamInfo { get; }

    /// <summary>
    /// Gets or sets the volume (0.0 to 1.0).
    /// </summary>
    float Volume { get; set; }

    /// <summary>
    /// Gets or sets whether the source should loop when reaching the end.
    /// </summary>
    bool Loop { get; set; }

    /// <summary>
    /// Gets the current playback position in seconds.
    /// </summary>
    double Position { get; }

    /// <summary>
    /// Gets the duration of the audio in seconds.
    /// </summary>
    double Duration { get; }

    /// <summary>
    /// Gets whether the source has reached the end of the audio.
    /// </summary>
    bool IsEndOfStream { get; }

    /// <summary>
    /// Gets or sets the playback speed multiplier (1.0 = normal speed).
    /// Only available if SoundTouch processing is enabled.
    /// </summary>
    float Tempo { get; set; }

    /// <summary>
    /// Gets or sets the pitch shift in semitones (0 = no shift).
    /// Only available if SoundTouch processing is enabled.
    /// </summary>
    float PitchShift { get; set; }

    /// <summary>
    /// Reads audio samples into the provided buffer.
    /// </summary>
    /// <param name="buffer">The buffer to fill with audio data.</param>
    /// <param name="frameCount">The number of frames to read.</param>
    /// <returns>The actual number of frames read.</returns>
    int ReadSamples(Span<float> buffer, int frameCount);

    /// <summary>
    /// Seeks to a specific position in the audio.
    /// </summary>
    /// <param name="positionInSeconds">The target position in seconds.</param>
    /// <returns>True if seek was successful, false otherwise.</returns>
    bool Seek(double positionInSeconds);

    /// <summary>
    /// Starts or resumes playback.
    /// </summary>
    void Play();

    /// <summary>
    /// Pauses playback.
    /// </summary>
    void Pause();

    /// <summary>
    /// Stops playback and resets position to the beginning.
    /// </summary>
    void Stop();

    /// <summary>
    /// Occurs when the playback state changes.
    /// </summary>
    event EventHandler<AudioStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Occurs when a buffer underrun is detected.
    /// </summary>
    event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>
    /// Occurs when an error occurs during playback.
    /// </summary>
    event EventHandler<AudioErrorEventArgs>? Error;
}
