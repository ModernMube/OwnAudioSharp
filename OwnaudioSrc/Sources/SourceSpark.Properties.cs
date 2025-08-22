using Ownaudio.Common;
using Ownaudio.Decoders;
using Ownaudio.Processors;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using System;
using System.Collections.Concurrent;

namespace Ownaudio.Sources
{
    public partial class SourceSpark : ISource
    {
        #region Properties
        /// <summary>
        /// Occurs when the state of the object changes.
        /// </summary>
        /// <remarks>Subscribe to this event to be notified whenever the state changes.  The event provides no
        /// additional data beyond the sender and event arguments.</remarks>
        public event EventHandler? StateChanged;
        /// <summary>
        /// Occurs when the position of the object changes.
        /// </summary>
        /// <remarks>Subscribe to this event to be notified whenever the position changes.  The event handler
        /// receives an <see cref="EventArgs"/> object, which does not contain additional data.</remarks>
        public event EventHandler? PositionChanged;

        /// <summary>
        /// Gets a value indicating whether the object has been successfully loaded.
        /// </summary>
        public bool IsLoaded { get; private set; }

        /// <summary>
        /// Gets the duration of the event or operation.
        /// </summary>
        public TimeSpan Duration { get; private set; }

        /// <summary>
        /// Gets the current playback position within the media.
        /// </summary>
        public TimeSpan Position { get; private set; }

        /// <summary>
        /// Gets the current state of the source.
        /// </summary>
        public SourceState State { get; private set; } = SourceState.Idle;

        /// <summary>
        /// Gets or sets the name associated with the object.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the system is currently in a seeking state.
        /// </summary>
        public bool IsSeeking { get; set; }

        /// <summary>
        /// Gets or sets the volume level for audio playback.
        /// </summary>
        public float Volume { get => VolumeProcessor.Volume; set => VolumeProcessor.Volume = value.VerifyVolume(); }

        /// <summary>
        /// Gets or sets the tempo adjustment factor for audio processing.
        /// </summary>
        public double Tempo
        {
            get => soundTouch.TempoChange;
            set
            {
                lock (lockObject)
                {
                    if (!DoubleUtil.AreClose(soundTouch.TempoChange, value))
                        soundTouch.TempoChange = value.VerifyTempo();
                }
            }
        }

        /// <summary>
        /// Gets or sets the pitch adjustment in semitones.
        /// </summary>
        public double Pitch
        {
            get => soundTouch.PitchSemiTones;
            set
            {
                lock (lockObject)
                {
                    if (!DoubleUtil.AreClose(soundTouch.PitchSemiTones, value))
                        soundTouch.PitchSemiTones = value.VerifyPitch();
                }
            }
        }

        /// <summary>
        /// Gets or sets the custom sample processor used to handle sample processing operations.
        /// </summary>
        public SampleProcessorBase? CustomSampleProcessor { get; set; }

        /// <summary>
        /// Gets or sets the logger instance used for logging messages and events.
        /// </summary>
        public ILogger? Logger { get; set; }

        /// <summary>
        /// Gets the queue containing source sample data as arrays of floating-point values.
        /// </summary>
        public ConcurrentQueue<float[]> SourceSampleData { get; }

        /// <summary>
        /// Gets the URL of the current page or resource being accessed.
        /// </summary>
        public string? CurrentUrl { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether the playback is set to loop.
        /// </summary>
        public bool IsLooping
        {
            get => _isLooping;
            set => _isLooping = value;
        }

        /// <summary>
        /// Gets a value indicating whether the source is currently playing.
        /// </summary>
        public bool IsPlaying => _isPlaying && State == SourceState.Playing;

        /// <summary>
        /// Gets a value indicating whether the source has finished processing.
        /// </summary>
        public bool HasFinished => State == SourceState.Idle && !_isPlaying;

        /// <summary>
        /// Gets the <see cref="VolumeProcessor"/> instance used to process volume-related operations.
        /// </summary>
        protected VolumeProcessor VolumeProcessor { get; }

        /// <summary>
        /// Gets or sets the current audio decoder used for processing audio data.
        /// </summary>
        protected IAudioDecoder? CurrentDecoder { get; set; }

        /// <summary>
        /// Gets or sets the audio data represented as an array of floating-point values.
        /// </summary>
        protected float[]? AudioData { get; set; }

        /// <summary>
        /// Gets or sets the index of the current sample being processed.
        /// </summary>
        protected int CurrentSampleIndex { get; set; }

        /// <summary>
        /// Stereo audio output level. 
        /// In the case of mono signal, only the left channel value changes. 
        /// </summary>
        public (float, float)? OutputLevels { get; private set; }
        #endregion
    }
}
