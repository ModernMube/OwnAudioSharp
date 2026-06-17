using Ownaudio.Audio.Streams;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Contract for a real-time audio effect that processes samples in-place.
/// </summary>
/// <remarks>
/// <para>
/// This interface is declared for future extensibility and is not yet wired into
/// <see cref="Playback.AudioPlayer"/> or <see cref="Capture.AudioRecorder"/>.
/// A future release will expose an <c>Effects</c> collection on both types.
/// </para>
/// <para>
/// <b>Implementation requirements:</b>
/// <list type="bullet">
///   <item>Must not allocate on the heap inside <see cref="Process"/> — it runs on the real-time audio thread.</item>
///   <item>Must not acquire locks or perform blocking I/O inside <see cref="Process"/>.</item>
///   <item><see cref="Enabled"/> may be read from the audio thread; writes from any thread must be atomic (plain <see langword="bool"/> field is sufficient on .NET).</item>
/// </list>
/// </para>
/// </remarks>
public interface IAudioEffect
{
    /// <summary>Human-readable name of this effect (e.g. "Low-Pass Filter").</summary>
    string Name { get; }

    /// <summary>
    /// When <see langword="false"/>, the effect is bypassed and <see cref="Process"/> is not called.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Processes the given buffer in-place.
    /// </summary>
    /// <param name="buffer">
    /// Interleaved audio samples.  Modify <see cref="AudioBuffer.Samples"/> directly.
    /// </param>
    /// <param name="format">
    /// The sample rate, channel count, and format of the data in <paramref name="buffer"/>.
    /// </param>
    void Process(AudioBuffer buffer, AudioFormat format);
}
