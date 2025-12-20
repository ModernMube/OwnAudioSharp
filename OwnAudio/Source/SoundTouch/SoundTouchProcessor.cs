// License :
//
// SoundTouch audio processing library
// Copyright (c) Olli Parviainen
// C# port Copyright (c) Olaf Woudenberg
//
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

using System;
using System.Diagnostics;
using System.Reflection;

using SoundTouch.Assets;

[assembly: CLSCompliant(false)]

namespace SoundTouch
{
    /// <summary>
    /// SoundTouch - main class for tempo/pitch/rate adjusting routines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// - Initialize the SoundTouch object instance by setting up the sound
    /// stream  parameters with functions <see cref="SampleRate"/> and
    /// <see cref="Channels"/>, then set  desired tempo/pitch/rate settings
    /// with the corresponding functions.
    /// </para>
    /// <para>
    /// - The SoundTouch class behaves like a first-in-first-out pipeline: The
    /// samples that are to be processed are fed into one of the pipe by calling
    /// function <see cref="PutSamples"/>, while the ready processed samples can
    /// be read  from the other end of the pipeline with method
    /// <see cref="FifoProcessor.ReceiveSamples(in Span{float}, int)"/>.
    /// </para>
    /// <para>
    /// - The SoundTouch processing classes require certain sized 'batches' of
    /// samples in order to process the sound. For this reason the classes
    /// buffer  incoming samples until there are enough of samples available for
    /// processing, then they carry out the processing step and consequently
    /// make the processed samples available for outputting.
    /// </para>
    /// <para>
    /// - For the above reason, the processing routines introduce a certain
    /// 'latency' between the input and output, so that the samples input to
    /// SoundTouch may not be immediately available in the output, and neither
    /// the amount of outputtable samples may not immediately be in direct
    /// relationship with the amount of previously input samples.
    /// </para>
    /// <para>
    /// - The tempo/pitch/rate control parameters can be altered during
    /// processing. Please notice though that they aren't currently protected by
    /// semaphores, so in multi-thread application external semaphore protection
    /// may be required.
    /// </para>
    /// <para>
    /// - This class utilizes classes <see cref="TimeStretch"/> for tempo change
    /// (without modifying pitch) and <see cref="RateTransposer"/>
    /// for changing the playback rate (that is, both  tempo and pitch in the
    /// same ratio) of the sound. The third available control  'pitch' (change
    /// pitch but maintain tempo) is produced by a combination of combining the
    /// two other controls.
    /// </para>
    /// </remarks>
    public sealed class SoundTouchProcessor : FifoProcessor
    {
        internal const int SOUNDTOUCH_MAX_CHANNELS = 16;

        // Rate transposer class instance.
        private readonly RateTransposer _rateTransposer;

        // Time-stretch class instance.
        private readonly TimeStretch _stretch;

        // Flag: Has sample rate been set?.
        private bool _isSampleRateSet;

        // Virtual pitch parameter. Effective rate & tempo are calculated from these parameters.
        private double _rate;

        // Virtual pitch parameter. Effective rate & tempo are calculated from these parameters.
        private double _tempo;

        // Virtual pitch parameter. Effective rate & tempo are calculated from these parameters.
        private double _pitch;

        // Accumulator for how many samples in total will be expected as output vs. samples put in,
        // considering current processing settings.
        private double _samplesExpectedOut;

        // Accumulator for how many samples in total have been read out from the processing so far
        private long _samplesOutput;

        // Number of channels
        private int _channels;

        // Effective 'rate' value calculated from 'virtualRate', 'virtualTempo' and 'virtualPitch'
        private double _effectiveRate;

        // Effective 'tempo' value calculated from 'virtualRate', 'virtualTempo' and 'virtualPitch'
        private double _effectiveTempo;

        /// <summary>
        /// Initializes a new instance of the <see cref="SoundTouchProcessor"/> class.
        /// </summary>
        public SoundTouchProcessor()
            : this(new TimeStretch())
        {
        }

        private SoundTouchProcessor(TimeStretch stretch)
            : base(stretch)
        {
            _rateTransposer = new RateTransposer();
            _stretch = stretch;

            _effectiveRate = _effectiveTempo = 0;

            _pitch =
            _rate =
            _tempo = 1.0;

            CalcEffectiveRateAndTempo();

            _samplesExpectedOut = 0;
            _samplesOutput = 0;

            _channels = 0;
            _isSampleRateSet = false;
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            _rateTransposer.Dispose();
            _stretch.Dispose();
            base.Dispose();
        }

        /// <summary>
        /// Gets the <c>SoundTouch</c> library version string.
        /// </summary>
        //public static string VersionString => GitVersionInformation.InformationalVersion;

        /// <summary>
        /// Gets the <c>SoundTouch</c> library version Id.
        /// </summary>
        //public static Version Version => new Version(GitVersionInformation.AssemblySemFileVer);

        /// <summary>
        /// Gets the number of samples currently unprocessed.
        /// </summary>
        public int UnprocessedSampleCount
        {
            get
            {
                if (_stretch != null)
                {
                    var input = _stretch.GetInput();
                    if (input != null)
                    {
                        return input.AvailableSamples;
                    }
                }

                return 0;
            }
        }

        /// <summary>
        /// Gets or sets the number of channels.
        /// </summary>
        /// <value>1 = mono, 2 = stereo, n = multichannel.</value>
        public int Channels
        {
            get => _channels;
            set
            {
                if (!VerifyNumberOfChannels(value))
                    return;

                _channels = value;
                _rateTransposer.SetChannels(value);
                _stretch.SetChannels(value);
            }
        }

        /// <summary>
        /// Gets or sets the new rate control value.
        /// </summary>
        /// <value>Normal rate = 1.0, smaller values represent slower rate,
        /// larger faster rates.</value>
        public double Rate
        {
            get => _rate;
            set
            {
                _rate = value;
                CalcEffectiveRateAndTempo();
            }
        }

        /// <summary>
        /// Gets or sets the new tempo control value.
        /// </summary>
        /// <value>Normal tempo = 1.0, smaller values represent slower tempo, larger faster tempo.</value>
        public double Tempo
        {
            get => _tempo;
            set
            {
                _tempo = value;
                CalcEffectiveRateAndTempo();
            }
        }

        /// <summary>
        /// Gets or sets the rate control value as a difference in percents compared
        /// to the original rate (-50 .. +100 %).
        /// </summary>
        public double RateChange
        {
            get => 100.0 * (_rate - 1.0);
            set
            {
                _rate = 1.0 + (0.01 * value);
                CalcEffectiveRateAndTempo();
            }
        }

        /// <summary>
        /// Gets or sets new tempo control value as a difference in percents compared
        /// to the original tempo (-50 .. +100 %).
        /// </summary>
        public double TempoChange
        {
            get => 100.0 * (_tempo - 1.0);
            set
            {
                _tempo = 1.0 + (0.01 * value);
                CalcEffectiveRateAndTempo();
            }
        }

        /// <summary>
        /// Gets or sets sample rate.
        /// </summary>
        public int SampleRate
        {
            get
            {
                _stretch.GetParameters(out var sampleRate, out _, out _, out _);
                return sampleRate;
            }

            set
            {
                // set sample rate, leave other tempo changer parameters as they are.
                _stretch.SetParameters(value);
                _isSampleRateSet = true;
            }
        }

        /// <summary>
        /// Gets or sets new pitch control value. Original pitch = 1.0, smaller values
        /// represent lower pitches, larger values higher pitch.
        /// </summary>
        public double Pitch
        {
            get => _pitch;
            set
            {
                _pitch = value;
                CalcEffectiveRateAndTempo();
            }
        }

        /// <summary>
        /// Gets or sets pitch change in octaves compared to the original pitch
        /// (-1.00 .. +1.00).
        /// </summary>
        public double PitchOctaves
        {
            get => Math.Log10(_pitch) / 0.301029995664;
            set
            {
                _pitch = Math.Exp(0.69314718056 * value);
                CalcEffectiveRateAndTempo();
            }
        }

        /// <summary>
        /// Gets or sets pitch change in semi-tones compared to the original pitch
        /// (-12 .. +12).
        /// </summary>
        public double PitchSemiTones
        {
            get => PitchOctaves * 12.0;
            set => PitchOctaves = value / 12.0;
        }

        /// <summary>
        /// <para>
        /// Get ratio between input and output audio durations, useful for calculating
        /// processed output duration: if you'll process a stream of N samples, then
        /// you can expect to get out N * getInputOutputSampleRatio() samples.
        /// </para>
        /// <para>
        /// This ratio will give accurate target duration ratio for a full audio track,
        /// given that the the whole track is processed with same processing parameters.
        /// </para>
        /// <para>
        /// If this ratio is applied to calculate intermediate offsets inside a processing
        /// stream, then this ratio is approximate and can deviate +- some tens of milliseconds
        /// from ideal offset, yet by end of the audio stream the duration ratio will become
        /// exact.
        /// </para>
        /// <para>
        /// Example: if processing with parameters "-tempo=15 -pitch=-3", the function
        /// will return value 0.8695652... Now, if processing an audio stream whose duration
        /// is exactly one million audio samples, then you can expect the processed
        /// output duration  be 0.869565 * 1000000 = 869565 samples.
        /// </para>
        /// </summary>
        public double GetInputOutputSampleRatio() => 1.0 / (_effectiveTempo * _effectiveRate);

        /// <summary>
        /// Flushes the last samples from the processing pipeline to the output.
        /// Clears also the internal processing buffers.
        /// </summary>
        /// <remarks>
        /// This function is meant for extracting the last samples of a sound
        /// stream. This function may introduce additional blank samples in the end
        /// of the sound stream, and thus it's not recommended to call this function
        /// in the middle of a sound stream.
        /// </remarks>
        public void Flush()
        {
            Span<float> buff = stackalloc float[128 * _channels];

            // how many samples are still expected to output
            int numStillExpected = (int)((long)(_samplesExpectedOut + 0.5) - _samplesOutput);
            if (numStillExpected < 0)
                numStillExpected = 0;

            // "Push" the last active samples out from the processing pipeline by
            // feeding blank samples into the processing pipeline until new,
            // processed samples appear in the output (not however, more than
            // 24k samples in any case)
            for (int i = 0; (numStillExpected > AvailableSamples) && (i < 200); i++)
            {
                PutSamples(buff, 128);
            }

            AdjustAmountOfSamples(numStillExpected);

            // Clear input buffers; yet leave the output intouched as that's where the
            // flushed samples are!
            _stretch.ClearInput();
        }

        /// <summary>
        /// Adds samples from the <paramref name="samples"/> buffer into
        /// the input of the object. Notice that sample rate _has_to_ be set before
        /// calling this function, otherwise throws a <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <param name="samples">The sample buffer to add as input.</param>
        /// <param name="numSamples">Number of samples to insert.</param>
        /// <exception cref="InvalidOperationException">Sample rate or number of channels not defined.</exception>
        public override void PutSamples(in ReadOnlySpan<float> samples, int numSamples)
        {
            if (!_isSampleRateSet)
            {
                throw new InvalidOperationException(Strings.InvalidOperation_SampleRateUndefined);
            }
            else if (_channels == 0)
            {
                throw new InvalidOperationException(Strings.InvalidOperation_ChannelsUndefined);
            }

            // OPTIMIZATION REMOVED: User requires signal to ALWAYS go through DSP processing
            // even at 1.0x speed/pitch to ensure consistent latency and synchronization.
            // Previous optimization bypassed TimeStretch/RateTransposer which caused latency jumps
            // when toggling between 1.0 and non-1.0 values.

            // accumulate how many samples are expected out from processing, given the current
            // processing setting
            _samplesExpectedOut += numSamples / (_effectiveRate * _effectiveTempo);

#if !SOUNDTOUCH_PREVENT_CLICK_AT_RATE_CROSSOVER
            if (_effectiveRate <= 1.0f)
            {
                // transpose the rate down, output the transposed sound to tempo changer buffer
                Debug.Assert(Output == _stretch, "Output == stretch");
                _rateTransposer.PutSamples(samples, numSamples);
                _stretch.MoveSamples(_rateTransposer);
            }
            else
#endif
            {
                // evaluate the tempo changer, then transpose the rate up.
                Debug.Assert(Output == _rateTransposer, "Output == rateTransposer");
                _stretch.PutSamples(samples, numSamples);
                _rateTransposer.MoveSamples(_stretch);
            }
        }

        /// <summary>
        /// Output samples from beginning of the sample buffer. Copies requested samples to
        /// output buffer and removes them from the sample buffer. If there are less than
        /// requested samples in the buffer, returns all that available.
        /// </summary>
        /// <param name="output">Buffer where to copy output samples.</param>
        /// <param name="maxSamples">Number of samples in buffer.
        /// Note that in case of stereo-sound a single sample contains data for both channels.
        /// </param>
        /// <returns>Returns the number of samples written to <paramref name="output"/>.</returns>
        public override int ReceiveSamples(in Span<float> output, int maxSamples)
        {
            var result = base.ReceiveSamples(output, maxSamples);
            _samplesOutput += result;
            return result;
        }

        /// <summary>
        /// Adjusts book-keeping so that given number of samples are removed from beginning of the
        /// sample buffer without copying them anywhere.
        /// </summary>
        /// <param name="maxSamples">How many samples to receive at max.</param>
        /// <remarks>
        /// Used to reduce the number of samples in the buffer when accessing the sample buffer directly.
        /// </remarks>
        public override int ReceiveSamples(int maxSamples)
        {
            var result = base.ReceiveSamples(maxSamples);
            _samplesOutput += result;
            return result;
        }

        /// <summary>
        /// Clears all the samples in the object's output and internal
        /// processing buffers.
        /// </summary>
        public override void Clear()
        {
            _samplesExpectedOut = 0;
            _samplesOutput = 0;
            _rateTransposer.Clear();
            _stretch.Clear();
        }

        /// <summary>
        /// Changes a setting controlling the processing system behavior. See
        /// the <see cref="SettingId"/> enum for available setting IDs.
        /// </summary>
        /// <param name="settingId">Setting ID number.</param>
        /// <param name="value">New setting value.</param>
        /// <returns><see langword="true"/> if the setting was successfully changed;
        /// otherwise <see langword="false"/>.</returns>
        public bool SetSetting(SettingId settingId, int value)
        {
            // read current tdstretch routine parameters
            _stretch.GetParameters(out var sampleRate, out var sequenceMs, out var seekWindowMs, out var overlapMs);

            switch (settingId)
            {
                case SettingId.UseAntiAliasFilter:
                    // enables / disabless anti-alias filter
                    _rateTransposer.EnableAAFilter(value != 0);
                    return true;

                case SettingId.AntiAliasFilterLength:
                    // sets anti-alias filter length
                    _rateTransposer.GetAAFilter().Length = value;
                    return true;

                case SettingId.UseQuickSeek:
                    // enables / disables tempo routine quick seeking algorithm
                    _stretch.EnableQuickSeek(value != 0);
                    return true;

                case SettingId.SequenceDurationMs:
                    // change time-stretch sequence duration parameter
                    _stretch.SetParameters(sampleRate, value, seekWindowMs, overlapMs);
                    return true;

                case SettingId.SeekWindowDurationMs:
                    // change time-stretch seek window length parameter
                    _stretch.SetParameters(sampleRate, sequenceMs, value, overlapMs);
                    return true;

                case SettingId.OverlapDurationMs:
                    // change time-stretch overlap length parameter
                    _stretch.SetParameters(sampleRate, sequenceMs, seekWindowMs, value);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Reads a setting controlling the processing system behavior. See the
        /// <see cref="SettingId"/> enum for available setting IDs.
        /// </summary>
        /// <param name="settingId">Setting ID number.</param>
        /// <returns>The setting value.</returns>
        public int GetSetting(SettingId settingId)
        {
            switch (settingId)
            {
                case SettingId.UseAntiAliasFilter:
                    return _rateTransposer.IsAAFilterEnabled() ? 1 : 0;

                case SettingId.AntiAliasFilterLength:
                    return _rateTransposer.GetAAFilter().Length;

                case SettingId.UseQuickSeek:
                    return _stretch.IsQuickSeekEnabled() ? 1 : 0;

                case SettingId.SequenceDurationMs:
                    _stretch.GetParameters(out _, out var sequenceMs, out _, out _);
                    return sequenceMs;

                case SettingId.SeekWindowDurationMs:
                    _stretch.GetParameters(out _, out _, out var windowMs, out _);
                    return windowMs;

                case SettingId.OverlapDurationMs:
                    _stretch.GetParameters(out _, out _, out _, out var overlapMs);
                    return overlapMs;

                case SettingId.NominalInputSequence:
                    {
                        int size = _stretch.GetInputSampleReq();

#if !SOUNDTOUCH_PREVENT_CLICK_AT_RATE_CROSSOVER
                        if (_effectiveRate <= 1.0)
                        {
                            // transposing done before timestretch, which impacts latency
                            return (int)((size * _effectiveRate) + 0.5);
                        }
#endif
                        return size;
                    }

                case SettingId.NominalOutputSequence:
                    {
                        int size = _stretch.GetOutputBatchSize();

                        if (_effectiveRate > 1.0)
                        {
                            // transposing done after timestretch, which impacts latency
                            return (int)((size / _effectiveRate) + 0.5);
                        }

                        return size;
                    }

                case SettingId.InitialLatency:
                    {
                        double latency = _stretch.GetLatency();
                        int latency_tr = _rateTransposer.Latency;

#if !SOUNDTOUCH_PREVENT_CLICK_AT_RATE_CROSSOVER
                        if (_effectiveRate <= 1.0)
                        {
                            // transposing done before timestretch, which impacts latency
                            latency = (latency + latency_tr) * _effectiveRate;
                        }
                        else
#endif
                        {
                            latency += latency_tr / _effectiveRate;
                        }

                        return (int)(latency + 0.5);
                    }

                default:
                    return 0;
            }
        }

        private static bool IsDoubleEqual(double a, double b) => Math.Abs(a - b) < double.Epsilon;

        // Calculates effective rate & tempo values from 'virtualRate', 'virtualTempo' and
        // 'virtualPitch' parameters.
        private void CalcEffectiveRateAndTempo()
        {
            double oldTempo = _effectiveTempo;
            double oldRate = _effectiveRate;

            _effectiveTempo = _tempo / _pitch;
            _effectiveRate = _pitch * _rate;

            if (!IsDoubleEqual(_effectiveRate, oldRate))
                _rateTransposer.SetRate(_effectiveRate);
            if (!IsDoubleEqual(_effectiveTempo, oldTempo))
                _stretch.SetTempo(_effectiveTempo);

            if (Output is null)
                throw new InvalidOperationException(Strings.InvalidOperation_OutputUndefined);

#if !SOUNDTOUCH_PREVENT_CLICK_AT_RATE_CROSSOVER
            if (_effectiveRate <= 1.0f)
            {
                if (Output != _stretch)
                {
                    FifoSamplePipe tempoOut;

                    Debug.Assert(Output == _rateTransposer, "Output is RateTransposer");

                    // move samples in the current output buffer to the output of pTDStretch
                    tempoOut = _stretch.GetOutput();
                    tempoOut.MoveSamples(Output);

                    // move samples in pitch transposer's store buffer to tempo changer's input
                    Output = _stretch;
                }
            }
            else
#endif
            {
                if (Output != _rateTransposer)
                {
                    FifoSamplePipe transOut;

                    Debug.Assert(Output == _stretch, "Output is Time Stretch");

                    // move samples in the current output buffer to the output of pRateTransposer
                    transOut = _rateTransposer.GetOutputBuffer();
                    transOut.MoveSamples(Output);

                    // move samples in tempo changer's input to pitch transposer's input
                    _rateTransposer.MoveSamples(_stretch.GetInput());

                    Output = _rateTransposer;
                }
            }
        }
    }
}
