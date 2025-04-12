using System;

namespace Ownaudio.Engines;

/// <summary>
/// An interface to interact with output audio device.
/// <para>Implements: <see cref="IDisposable"/>.</para>
/// </summary>
public interface IAudioEngine : IDisposable
{
    /// <summary>
    /// Stream output pointer
    /// </summary>
    IntPtr GetStream();

    /// <summary>
    /// Engine Frames per Buffer
    /// </summary>
    int FramesPerBuffer { get; }

    /// <summary>
    /// Returns a numeric value about the activity of the audio engine
    /// </summary>
    /// <returns>
    /// 0 - the engine not playing or recording
    /// 1 - the engine plays or records
    /// negative value if there is an error
    /// </returns>
    int OwnAudioEngineActivate();

    /// <summary>
    /// It returns a value whether the motor is stopped or running
    /// </summary>
    /// <returns>
    /// 0 - the engine running
    /// 1 - the engine stopped
    /// negative value if there is an error
    /// </returns>
    int OwnAudioEngineStopped();

    /// <summary>
    /// Audio engine start
    /// </summary>
    /// <returns>Error code</returns>
    int Start();

    /// <summary>
    /// Audio engine stop
    /// </summary>
    /// <returns>Error code</returns>
    int Stop();

    /// <summary>
    /// Sends audio samples to the output device (this is should be a blocking calls).
    /// </summary>
    /// <param name="samples">Audio samples in <c>Float32</c> format.</param>
    void Send(Span<float> samples);

    /// <summary>
    /// Receives audio data from the input
    /// </summary>
    /// <param name="samples"></param>
    void Receives(out float[] samples);
}
