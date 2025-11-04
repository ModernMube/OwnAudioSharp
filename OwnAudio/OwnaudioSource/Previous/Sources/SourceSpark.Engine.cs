using Ownaudio.Sources.Extensions;
using Ownaudio.Utilities.Extensions;
using System;
using System.Threading;

namespace Ownaudio.Sources;

/// <summary>
/// A simplified audio source for sound effects and short audio clips.
/// Supports looping, real-time playback, and audio effects.
/// </summary>
public partial class SourceSpark : ISource
{
    /// <summary>
    /// Executes the audio processing engine for the source, performing tasks such as
    /// buffering, applying sound processing, and managing playback state.
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to signal the engine to stop processing and exit gracefully.</param>
    private void RunEngine(CancellationToken cancellationToken)
    {
        Logger?.LogInfo($"Simple source engine thread started: {Name}");

        EnsureProcessedSamplesBuffer();

        soundTouch.Clear();
        soundTouch.SampleRate = SourceManager.OutputEngineOptions.SampleRate;
        soundTouch.Channels = (int)SourceManager.OutputEngineOptions.Channels;

        var channels = CurrentDecoder?.StreamInfo.Channels ?? 2;
        var sampleRate = CurrentDecoder?.StreamInfo.SampleRate ?? 44100;

        while (_isPlaying && !cancellationToken.IsCancellationRequested)
        {
            if (State == SourceState.Paused)
            {
                Thread.Sleep(10);
                continue;
            }

            if (SourceSampleData.Count >= MaxQueueSize) // Limit queue size
            {
                Thread.Sleep(2);
                continue;
            }

            var samplesNeeded = FramesPerBuffer * channels;
            #nullable disable
            var availableSamples = AudioData.Length - CurrentSampleIndex;
            #nullable restore

            if (availableSamples <= 0)
            {
                if (IsLooping)
                {
                    CurrentSampleIndex = 0; // Loop back to start
                    continue;
                }
                else
                {
                    // End of audio, stop playing
                    _isPlaying = false;
                    SetAndRaiseStateChanged(SourceState.Idle);
                    break;
                }
            }

            var samplesToRead = Math.Min(samplesNeeded, availableSamples);
            var samples = SimpleAudioBufferPool.Rent(samplesToRead);

            try
            {
                Array.Copy(AudioData, CurrentSampleIndex, samples, 0, samplesToRead);

                // Apply SoundTouch effects if needed
                bool needsSoundTouch = Math.Abs(Tempo) > 0.01 || Math.Abs(Pitch) > 0.01;
                if (needsSoundTouch)
                {
                    ApplySoundTouchEffects(samples.AsSpan(0, samplesToRead));
                }
                else
                {
                    ProcessSampleProcessors(samples.AsSpan(0, samplesToRead));
                    SourceSampleData.Enqueue(samples);
                    samples = null; // Prevent return to pool
                }

                CurrentSampleIndex += samplesToRead;

                // Update position
                var timeAdvanced = TimeSpan.FromSeconds((double)samplesToRead / (sampleRate * channels));
                SetAndRaisePositionChanged(Position.Add(timeAdvanced));
            }
            finally
            {
                if (samples != null)
                    SimpleAudioBufferPool.Return(samples);
            }

            Thread.Sleep(1); // Small yield
        }

        SetAndRaiseStateChanged(SourceState.Idle);
        Logger?.LogInfo($"Simple source engine thread completed: {Name}");
    }

    /// <summary>
    /// Applies SoundTouch effects, such as pitch and tempo adjustments, to the provided audio samples.
    /// This method processes the input sample data using the SoundTouch library and updates the processed sample buffer.
    /// </summary>
    /// <param name="samples">A span of audio samples to be processed. The samples are modified in place to apply the effects.</param>
    private void ApplySoundTouchEffects(Span<float> samples)
    {
        lock (lockObject)
        {
            soundTouch.PutSamples(samples.ToArray(), samples.Length / soundTouch.Channels);

            EnsureProcessedSamplesBuffer();
            #nullable disable
            FastClear(_processedSamples, FixedBufferSize);
            #nullable restore

            int numSamples = soundTouch.ReceiveSamples(_processedSamples, FixedBufferSize / soundTouch.Channels);

            if (numSamples > 0)
            {
                var processedSpan = _processedSamples.AsSpan(0, numSamples * soundTouch.Channels);
                ProcessSampleProcessors(processedSpan);

                var outputBuffer = SimpleAudioBufferPool.Rent(processedSpan.Length);
                processedSpan.SafeCopyTo(outputBuffer);
                SourceSampleData.Enqueue(outputBuffer);
            }
        }
    }

    /// <summary>
    /// Applies various sample-level processing to an audio sample buffer.
    /// This includes applying a custom sample processor if enabled,
    /// and adjusting the volume if the volume processor indicates a non-default level.
    /// </summary>
    /// <param name="samples">The buffer of audio samples to be processed.</param>
    private void ProcessSampleProcessors(Span<float> samples)
    {
        if (CustomSampleProcessor is { IsEnabled: true })
            CustomSampleProcessor.Process(samples);

        if (VolumeProcessor.Volume != 1.0f)
            VolumeProcessor.Process(samples);

        OutputLevels = SourceManager.OutputEngineOptions.Channels == 2
            ? Extensions.CalculateLevels.CalculateAverageStereoLevelsSpan(samples)
            : Extensions.CalculateLevels.CalculateAverageMonoLevelSpan(samples);
    }

    /// <summary>
    /// Ensures that the buffer used to store processed audio samples is properly initialized and up-to-date.
    /// </summary>
    /// <remarks>
    /// This method verifies that the processed samples buffer is allocated and its size matches the current fixed buffer size.
    /// If the buffer is uninitialized or its size does not match, it creates a new buffer with the correct size.
    /// </remarks>
    private void EnsureProcessedSamplesBuffer()
    {
        if (_processedSamples == null || _lastProcessedSize != FixedBufferSize)
        {
            _processedSamples = new float[FixedBufferSize];
            _lastProcessedSize = FixedBufferSize;
        }
    }

    /// <summary>
    /// Ensures the completion of any running threads related to the audio engine.
    /// </summary>
    /// <remarks>
    /// This method cancels any active cancellation tokens and waits for the engine thread
    /// to complete its execution, ensuring proper cleanup and thread termination.
    /// After execution, the engine thread reference is set to null.
    /// </remarks>
    private void EnsureThreadsDone()
    {
        _cancellationTokenSource?.Cancel();
        EngineThread?.EnsureThreadDone();
        EngineThread = null;
    }
}
