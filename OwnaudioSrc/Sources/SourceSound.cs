using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Ownaudio.Common;
using Ownaudio.Processors;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Sources
{
    /// <summary>
    /// Represents a real-time audio source that allows external sample data to be pushed into the engine.
    /// </summary>
    public partial class SourceSound : ISource
    {
        /// <summary>
        /// Indicates whether the instance has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SourceSound"/> class and sets default volume.
        /// </summary>
        public SourceSound(int inputDataChannels = 1)
        {
            VolumeProcessor.Volume = 1.0f;
            InputDataChannels = inputDataChannels;
        }

        /// <summary>
        /// Submits an array of audio samples for real-time processing and playback.
        /// </summary>
        /// <param name="samples">The array of floating-point audio samples to process and enqueue.</param>
        public void SubmitSamples(float[] samples)
        {
            if (_disposed || samples == null || samples.Length == 0)
                return;

            int engineChannels = (int)SourceManager.OutputEngineOptions.Channels;
            int framesPerBuffer = SourceManager.EngineFramesPerBuffer;

            int inputFrames = samples.Length / InputDataChannels;

            for (int i = 0; i + framesPerBuffer <= inputFrames; i += framesPerBuffer)
            {
                float[] buffer = new float[framesPerBuffer * engineChannels];

                for (int frame = 0; frame < framesPerBuffer; frame++)
                {
                    int inputIndex = (i + frame) * InputDataChannels;
                    int outputIndex = frame * engineChannels;

                    if (InputDataChannels == 1 && engineChannels == 2) // Mono to Stereo
                    {
                        float monoSample = samples[inputIndex];
                        buffer[outputIndex] = monoSample;
                        buffer[outputIndex + 1] = monoSample;
                    }
                    else if (InputDataChannels == 2 && engineChannels == 1) // Stereo to Mono
                    {                        
                        float left = samples[inputIndex];
                        float right = samples[inputIndex + 1];
                        buffer[outputIndex] = (left + right) / 2;
                    }
                    else if (InputDataChannels == engineChannels) // Same format
                    {
                        Array.Copy(samples, inputIndex, buffer, outputIndex, engineChannels);
                    }
                    else
                    {
                        Logger?.LogWarning($"Unsupported channel conversion: Input={InputDataChannels}, Output={engineChannels}");
                    }
                }

                ProcessSampleProcessors(buffer.AsSpan());
                SourceSampleData.Enqueue(buffer);
            }
        }

        /// <summary>
        /// Seeks to the specified position in the audio source. No operation for real-time streams.
        /// </summary>
        /// <param name="position">The target playback position.</param>
        public void Seek(TimeSpan position)
        {
            // No-op for real-time stream
        }

        /// <summary>
        /// Changes the playback state of the source to Idle, Playing, or Paused.
        /// </summary>
        /// <param name="state">The desired <see cref="SourceState"/>.</param>
        public void ChangeState(SourceState state)
        {
            if (_disposed)
                return;

            switch (state)
            {
                case SourceState.Idle:
                    Stop();
                    break;
                case SourceState.Playing:
                    Play();
                    break;
                case SourceState.Paused:
                    Pause();
                    break;
            }
        }

        /// <summary>
        /// Starts playback if not already playing.
        /// </summary>
        private void Play()
        {
            if (State == SourceState.Playing)
                return;

            SetAndRaiseStateChanged(SourceState.Playing);
        }

        /// <summary>
        /// Pauses playback if currently playing or buffering.
        /// </summary>
        private void Pause()
        {
            if (State == SourceState.Playing || State == SourceState.Buffering)
                SetAndRaiseStateChanged(SourceState.Paused);
        }

        /// <summary>
        /// Stops playback, clears queued samples, and sets state to Idle.
        /// </summary>
        private void Stop()
        {
            if (State == SourceState.Idle)
                return;

            State = SourceState.Idle;
            while (SourceSampleData.TryDequeue(out _)) { }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Sets the internal state and raises the <see cref="StateChanged"/> event if the state changed.
        /// </summary>
        /// <param name="state">The new <see cref="SourceState"/> to apply.</param>
        private void SetAndRaiseStateChanged(SourceState state)
        {
            bool raise = State != state;
            State = state;
            if (raise)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Sets the internal playback position and raises the <see cref="PositionChanged"/> event if it changed.
        /// </summary>
        /// <param name="position">The new playback position.</param>
        private void SetAndRaisePositionChanged(TimeSpan position)
        {
            bool raise = Position != position;
            Position = position;
            if (raise)
                PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Processes the provided audio samples through custom and volume processors.
        /// </summary>
        /// <param name="samples">The span of audio samples to process.</param>
        private void ProcessSampleProcessors(Span<float> samples)
        {
            if (CustomSampleProcessor is { IsEnabled: true })
                CustomSampleProcessor.Process(samples);

            if (VolumeProcessor.Volume != 1.0f)
                VolumeProcessor.Process(samples);
        }

        /// <summary>
        /// Retrieves raw byte audio data for a given position. (Not implemented)
        /// </summary>
        /// <param name="position">The playback position for which to retrieve data.</param>
        /// <returns>A byte array of audio data.</returns>
        public byte[] GetByteAudioData(TimeSpan position) => null!;

        /// <summary>
        /// Retrieves floating-point audio data for a given position. (Not implemented)
        /// </summary>
        /// <param name="position">The playback position for which to retrieve data.</param>
        /// <returns>An array of floats representing audio samples.</returns>
        public float[] GetFloatAudioData(TimeSpan position) => null!;

        /// <summary>
        /// Disposes the source, stops playback, clears queued samples, and suppresses finalization.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            State = SourceState.Idle;
            while (SourceSampleData.TryDequeue(out _)) { }
            GC.SuppressFinalize(this);
            _disposed = true;
        }
    }
}
