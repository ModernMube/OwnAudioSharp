
using System;
using System.Collections.Concurrent; 

using Ownaudio.Engines;
using Ownaudio.Processors;

namespace Ownaudio.Sources
{
    /// <summary>
    /// An input source that processes data from an input
    /// </summary>
    public partial class SourceInput : ISource
    {
        private AudioEngineInputOptions _inputoptions;
        private bool _disposed;
        private float _oldVolume;

        /// <summary>
        /// Initializes <see cref="SourceInput"/>
        /// </summary>
        /// <param name="inOptions"><see cref="AudioEngineInputOptions"/></param>
#nullable disable
        public SourceInput(AudioEngineInputOptions inOptions)
        {
            _inputoptions = inOptions;

            VolumeProcessor = new VolumeProcessor { Volume = 1 };
            SourceSampleData = new ConcurrentQueue<float[]>();
        }

        /// <summary>
        /// Seek
        /// </summary>
        /// <param name="position">Time of position</param>
        public void Seek(TimeSpan position)
        {
        }

        /// <summary>
        /// Changes the status of the given resource.
        /// <see cref="SourceState"/>
        /// </summary>
        /// <param name="state"></param>
        public void ChangeState(SourceState state)
        {            
        }

        /// <summary>
        /// Run <see cref="VolumeProcessor"/> and <see cref="CustomSampleProcessor"/> to the specified samples.
        /// </summary>
        /// <param name="samples">Audio samples to process to.</param>
        protected virtual void ProcessSampleProcessors(Span<float> samples)
        {
            bool useCustomProcessor = CustomSampleProcessor is { IsEnabled: true };
            bool useVolumeProcessor = VolumeProcessor.Volume != 1.0f;

            if (useCustomProcessor || useVolumeProcessor)
            {
                    if (useCustomProcessor && CustomSampleProcessor is not null)
                        CustomSampleProcessor.Process(samples);

                    if (useVolumeProcessor)
                        VolumeProcessor.Process(samples);
            }
        }

        /// <summary>
        /// Stops processing the input data and resets the input.
        /// </summary>
        protected void Stop()
        {
            State = SourceState.Idle;

            while (SourceSampleData.TryDequeue(out _)) { }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Pauses processing of input data.
        /// </summary>
        protected void Pause()
        {
            if (State is SourceState.Playing or SourceState.Buffering)
            {
                while (SourceSampleData.TryDequeue(out _)) { }
                SetAndRaiseStateChanged(SourceState.Paused);
            }
        }

        /// <summary>
        /// Sets <see cref="State"/> value and raise <see cref="StateChanged"/> if value is changed.
        /// </summary>
        /// <param name="state">Playback state.</param>
        protected virtual void SetAndRaiseStateChanged(SourceState state)
        {
            var raise = State != state;
            State = state;

            if (raise && StateChanged != null)
            {
                StateChanged.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Sets <see cref="Position"/> value and raise <see cref="PositionChanged"/> if value is changed.
        /// </summary>
        /// <param name="position">Playback position.</param>
        protected virtual void SetAndRaisePositionChanged(TimeSpan position)
        {
            var raise = position != Position;
            Position = position;

            if (raise && PositionChanged != null)
            {
                PositionChanged.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Dispose input source.
        /// </summary>
        public virtual void Dispose()
        {
            if (_disposed)
                return;

            State = SourceState.Idle;

            while (SourceSampleData.TryDequeue(out _)) { }

            GC.SuppressFinalize(this);

            _disposed = true;
        }

        /// <summary>
        /// Returns the contents of the audio file loaded into the source in a byte array.
        /// </summary>
        /// <returns></returns>
        public byte[] GetByteAudioData(TimeSpan position) { return null; }

        /// <summary>
        /// Returns the contents of the audio file loaded into the source in a float array.
        /// </summary>
        /// <returns></returns>
        public float[] GetFloatAudioData(TimeSpan position) { return null; }
    }
}
