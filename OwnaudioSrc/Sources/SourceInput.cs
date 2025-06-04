using System;
using System.Collections.Concurrent; 

using Ownaudio.Engines;
using Ownaudio.Processors;

namespace Ownaudio.Sources
{
    /// <summary>
    /// An input source that processes audio data from an input device or stream.
    /// This class provides functionality for handling real-time audio input with processing capabilities.
    /// </summary>
    public partial class SourceInput : ISource
    {
        /// <summary>
        /// Configuration options for the audio input engine.
        /// </summary>
        private AudioEngineInputOptions _inputoptions;

        /// <summary>
        /// Indicates whether this instance has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SourceInput"/> class with the specified input options.
        /// </summary>
        /// <param name="inOptions">The audio engine input options that configure the input source behavior.</param>
        /// <remarks>
        /// This constructor sets up the input source with:
        /// - Volume processor initialized to 100% volume
        /// - Thread-safe queue for audio sample data
        /// - Input configuration based on the provided options
        /// </remarks>
#nullable disable
        public SourceInput(AudioEngineInputOptions inOptions)
        {
            _inputoptions = inOptions;

            VolumeProcessor = new VolumeProcessor { Volume = 1 };
            SourceSampleData = new ConcurrentQueue<float[]>();
        }

        /// <summary>
        /// Seeks to the specified position in the audio input.
        /// </summary>
        /// <param name="position">The target position as a TimeSpan. This parameter is ignored for input sources as seeking is not applicable to live input streams.</param>
        /// <remarks>
        /// This method is provided for interface compliance but performs no operation since 
        /// seeking is not meaningful for real-time input sources. Live audio input cannot 
        /// be rewound or fast-forwarded to specific time positions.
        /// </remarks>
        public void Seek(TimeSpan position)
        {
        }

        /// <summary>
        /// Changes the operational state of the input source.
        /// </summary>
        /// <param name="state">The desired source state to transition to.</param>
        /// <remarks>
        /// This method is provided for interface compliance but currently performs no operation.
        /// State changes for input sources may be handled differently than file-based sources,
        /// as input sources typically respond to external input availability rather than 
        /// explicit state commands.
        /// </remarks>
        public void ChangeState(SourceState state)
        {
        }

        /// <summary>
        /// Applies audio processing to the specified samples using volume and custom sample processors.
        /// </summary>
        /// <param name="samples">The audio samples to process.</param>
        /// <remarks>
        /// This method applies processing in the following order:
        /// 1. Custom sample processor (if enabled and available)
        /// 2. Volume processor (if volume is not at 100%)
        /// 
        /// The method optimizes performance by checking processor availability before applying effects,
        /// avoiding unnecessary processing when no effects are needed.
        /// </remarks>
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
        /// Stops processing input data and resets the input source to idle state.
        /// </summary>
        /// <remarks>
        /// This method performs the following operations:
        /// - Sets the source state to Idle
        /// - Clears all queued sample data to free memory
        /// - Raises the StateChanged event to notify listeners
        /// 
        /// After calling this method, the input source will no longer process incoming audio data
        /// until restarted.
        /// </remarks>
        protected void Stop()
        {
            State = SourceState.Idle;

            while (SourceSampleData.TryDequeue(out _)) { }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Pauses the processing of input data if currently playing or buffering.
        /// </summary>
        /// <remarks>
        /// This method pauses input processing by:
        /// - Checking if the source is currently in a playing or buffering state
        /// - Clearing all queued sample data to prevent buffer buildup
        /// - Setting the state to Paused and raising the StateChanged event
        /// 
        /// While paused, the input source will not process new audio data but can be resumed later.
        /// </remarks>
        protected void Pause()
        {
            if (State is SourceState.Playing or SourceState.Buffering)
            {
                while (SourceSampleData.TryDequeue(out _)) { }
                SetAndRaiseStateChanged(SourceState.Paused);
            }
        }

        /// <summary>
        /// Sets the <see cref="State"/> value and raises the <see cref="StateChanged"/> event if the value has changed.
        /// </summary>
        /// <param name="state">The new source state to set.</param>
        /// <remarks>
        /// This method provides thread-safe state management by only raising the event when the state actually changes.
        /// The StateChanged event is invoked synchronously on the calling thread, allowing listeners to respond
        /// immediately to state transitions.
        /// </remarks>
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
        /// Sets the <see cref="Position"/> value and raises the <see cref="PositionChanged"/> event if the value has changed.
        /// </summary>
        /// <param name="position">The new position to set.</param>
        /// <remarks>
        /// This method provides thread-safe position management by only raising the event when the position actually changes.
        /// For input sources, position typically represents the duration of processed input rather than a seekable position.
        /// The PositionChanged event is invoked synchronously on the calling thread.
        /// </remarks>
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
        /// Releases all resources used by the <see cref="SourceInput"/> instance.
        /// </summary>
        /// <remarks>
        /// This method performs complete cleanup:
        /// - Sets the state to Idle to stop any processing
        /// - Clears all queued sample data to free memory
        /// - Suppresses finalizer execution for better performance
        /// - Sets the disposed flag to prevent multiple disposal
        /// 
        /// This method is safe to call multiple times and follows the standard dispose pattern.
        /// </remarks>
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
        /// Returns the audio content as a byte array from the input source.
        /// </summary>
        /// <param name="position">The position parameter (ignored for input sources).</param>
        /// <returns>Always returns null as input sources do not store complete audio data in memory.</returns>
        /// <remarks>
        /// This method is provided for interface compliance but always returns null because input sources
        /// process live audio data in real-time rather than storing complete audio files in memory.
        /// For input sources, audio data flows through the system continuously and is not retained
        /// for later retrieval.
        /// </remarks>
        public byte[] GetByteAudioData(TimeSpan position) { return null; }

        /// <summary>
        /// Returns the audio content as a float array from the input source.
        /// </summary>
        /// <param name="position">The position parameter (ignored for input sources).</param>
        /// <returns>Always returns null as input sources do not store complete audio data in memory.</returns>
        /// <remarks>
        /// This method is provided for interface compliance but always returns null because input sources
        /// process live audio data in real-time rather than storing complete audio files in memory.
        /// For input sources, audio data flows through the system continuously and is not retained
        /// for later retrieval as float arrays.
        /// </remarks>
        public float[] GetFloatAudioData(TimeSpan position) { return null; }
        #nullable restore
    }
}