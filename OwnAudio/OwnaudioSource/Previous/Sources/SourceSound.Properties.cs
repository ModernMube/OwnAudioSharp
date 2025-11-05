using System;
using System.Collections.Concurrent;

using OwnaudioLegacy.Common;
using OwnaudioLegacy.Processors;
using OwnaudioLegacy.Utilities.Extensions;


namespace OwnaudioLegacy.Sources
{
    public partial class SourceSound
    {
        /// <summary>
        /// Occurs when the playback state of the source changes.
        /// </summary>
        public event EventHandler? StateChanged;

        /// <summary>
        /// Occurs when the playback position of the source changes.
        /// </summary>
        public event EventHandler? PositionChanged;

        /// <summary>
        /// Gets the total duration of the audio source.
        /// </summary>
        public TimeSpan Duration { get; private set; } = TimeSpan.Zero;

        /// <summary>
        /// Gets the current playback position of the audio source.
        /// </summary>
        public TimeSpan Position { get; private set; } = TimeSpan.Zero;

        /// <summary>
        /// Gets the current state of the audio source (Idle, Playing, Paused, Buffering).
        /// </summary>
        public SourceState State { get; private set; } = SourceState.Idle;

        /// <summary>
        /// Gets or sets a value indicating whether a seek operation is in progress.
        /// Note: Seeking is a no-op for real-time streams.
        /// </summary>
        public bool IsSeeking { get; set; }

        /// <summary>
        /// Source name, which is used to identify the source.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the playback volume multiplier. Validated between 0.0 and 1.0.
        /// </summary>
        public float Volume
        {
            get => VolumeProcessor.Volume;
            set => VolumeProcessor.Volume = value.VerifyVolume();
        }

        /// <summary>
        /// Gets or sets the playback pitch adjustment. A value of 0 means no change.
        /// </summary>
        public double Pitch { get; set; } = 0;

        /// <summary>
        /// Gets or sets the playback tempo adjustment. A value of 0 means no change.
        /// </summary>
        public double Tempo { get; set; } = 0;

        /// <summary>
        /// Gets or sets a custom sample processor that can modify audio samples in real time.
        /// </summary>
        public SampleProcessorBase? CustomSampleProcessor { get; set; }

        /// <summary>
        /// Gets or sets an optional logger for diagnostic output.
        /// </summary>
        public ILogger? Logger { get; set; }

        /// <summary>
        /// Gets the queue that holds submitted audio sample arrays for processing.
        /// </summary>
        public ConcurrentQueue<float[]> SourceSampleData { get; } = new();

        /// <summary>
        /// Gets or sets the current URL of the source sound.
        /// </summary>
        public string? CurrentUrl { get; private set; }

        /// <summary>
        /// Number of channels in the incoming audio (1 = mono, 2 = stereo)
        /// </summary>
        public int InputDataChannels { get; }

        /// <summary>
        /// Gets the internal volume processor used to adjust sample amplitudes.
        /// </summary>
        private VolumeProcessor VolumeProcessor { get; } = new();

        /// <summary>
        /// Stereo audio output level. 
        /// In the case of mono signal, only the left channel value changes. 
        /// </summary>
        public (float, float)? OutputLevels { get; private set; }
    }
}
