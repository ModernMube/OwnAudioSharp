using System;
using System.Collections.Concurrent;
using MathNet.Numerics.Interpolation;
using Ownaudio.Common;
using Ownaudio.Processors;

namespace Ownaudio.Sources;

/// <summary>
/// An interface for loading and controlling audio playback.
/// <para>Implements: <see cref="IDisposable"/>.</para>
/// </summary>
public interface ISource : IDisposable
{
    /// <summary>
    /// Event that is raised when player state has been changed.
    /// </summary>
    event EventHandler StateChanged;

    /// <summary>
    /// Event that is raised when player position has been changed.
    /// </summary>
    event EventHandler PositionChanged;

    /// <summary>
    /// Gets total duration from loaded audio file.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Gets current player position.
    /// </summary>
    TimeSpan Position { get; }

    /// <summary>
    /// Gets current playback state.
    /// </summary>
    SourceState State { get; }

    /// <summary>
    /// Gets whether or not the player is currently seeking an audio stream.
    /// </summary>
    bool IsSeeking { get; set; }

    /// <summary>
    /// Gets or sets audio volume.
    /// </summary>
    float Volume { get; set; }

    /// <summary>
    /// Gets or sets the pitch of the source in semitones. 
    /// It can be a positive or negative value.
    /// </summary>
    double Pitch { get; set; }

    /// <summary>
    /// Gets or set the tempo of the source as a percentage. 
    /// This can be a positive or negative value.
    /// </summary>
    double Tempo { get; set; }

    /// <summary>
    /// Gets or sets the name associated with the source.
    /// </summary>
    string? Name { get; set; }

    /// <summary>
    /// Gets or sets custom sample processor.
    /// </summary>
    SampleProcessorBase? CustomSampleProcessor { get; set; }

    /// <summary>
    /// Gets or sets logger instance.
    /// </summary>
    ILogger? Logger { get; set; }

    /// <summary>
    /// Gets queue object that holds queued audio frames.
    /// </summary>
    ConcurrentQueue<float[]> SourceSampleData { get; }

    /// <summary>
    /// Seeks loaded audio to the specified position.
    /// </summary>
    /// <param name="position">Desired seek position.</param>
    void Seek(TimeSpan position);

    /// <summary>
    /// Changes the status of the given resource
    /// </summary>
    void ChangeState(SourceState state);

    /// <summary>
    /// Returns the contents of the audio file loaded into the source in a byte array.
    /// </summary>
    /// <returns></returns>
    byte[] GetByteAudioData(TimeSpan position);

    /// <summary>
    /// Returns the contents of the audio file loaded into the source in a float array.
    /// </summary>
    /// <returns></returns>
    float[] GetFloatAudioData(TimeSpan position);
}
