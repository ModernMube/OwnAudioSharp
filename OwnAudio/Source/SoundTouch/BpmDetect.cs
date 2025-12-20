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

namespace SoundTouch
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
#if !NETSTANDARD1_1
    using System.IO;
#endif

    using SoundTouch.Assets;

    /// <summary>
    /// <para>Beats-per-minute (BPM) detection routine.</para>
    /// <para>
    /// The beat detection algorithm works as follows:
    /// <list type="bullet">
    /// <item>
    /// Use function 'inputSamples' to input a chunks of samples to the class for
    /// analysis. It's a good idea to enter a large sound file or stream in smallish
    /// chunks of around few kilo-samples in order not to extinguish too much RAM memory.
    /// </item>
    /// <item>
    /// Input sound data is decimated to approx 500 Hz to reduce calculation burden,
    /// which is basically ok as low (bass) frequencies mostly determine the beat rate.
    /// Simple averaging is used for anti-alias filtering because the resulting signal
    /// quality isn't of that high importance.
    /// </item>
    /// <item>
    /// Decimated sound data is enveloped, i.e. the amplitude shape is detected by
    /// taking absolute value that's smoothed by sliding average. Signal levels that
    /// are below a couple of times the general RMS amplitude level are cut away to
    /// leave only notable peaks there.
    /// </item>
    /// <item>
    /// Repeating sound patterns (e.g. beats) are detected by calculating short-term
    /// auto-correlation function of the enveloped signal.
    /// </item>
    /// <item>
    /// After whole sound data file has been analyzed as above, the bpm level is
    /// detected by function 'getBpm' that finds the highest peak of the auto-correlation
    /// function, calculates it's precise location and converts this reading to bpm's.
    /// </item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class BpmDetect
    {
        // Minimum allowed BPM rate. Used to restrict accepted result above a reasonable limit.
        private const int MIN_BPM = 45;

        // Maximum allowed BPM rate range. Used for calculating algorithm parameters.
        private const int MAX_BPM_RANGE = 200;

        // Maximum allowed BPM rate range. Used to restrict accepted result below a reasonable limit.
        private const int MAX_BPM_VALID = 190;

        // algorithm input sample block size
        private const int INPUT_BLOCK_SIZE = 2048;

        // decimated sample block size
        private const int DECIMATED_BLOCK_SIZE = 256;

        // Target sample rate after decimation
        private const int TARGET_SRATE = 1000;

        // XCorr update sequence size, update in about 200msec chunks
        private const int XCORR_UPDATE_SEQUENCE = TARGET_SRATE / 5;

        // Moving average N size
        private const int MOVING_AVERAGE_N = 15;

        // XCorr decay time constant, decay to half in 30 seconds
        // If it's desired to have the system adapt quicker to beat rate
        // changes within a continuing music stream, then the
        // 'xcorr_decay_time_constant' value can be reduced, yet that
        // can increase possibility of glitches in bpm detection.
        private const double XCORR_DECAY_TIME_CONSTANT = 30.0;

        // Data overlap factor for beat detection algorithm
        private const int OVERLAP_FACTOR = 4;

        private const double TWOPI = 2 * Math.PI;

        private static readonly double[] _LPF_coeffs = new double[5] { 0.00996655391939, -0.01944529148401, 0.00996655391939, 1.96867605796247, -0.96916387431724 };

        // Auto-correlation accumulator bins.
        private readonly float[] _xcorr;

        // Decimate sound by this coefficient to reach approx. 500 Hz.
        private readonly int _decimateBy;

        // Auto-correlation window length
        private readonly int _windowLen;

        // Number of channels (1 = mono, 2 = stereo)
        private readonly int _channels;

        // sample rate
        private readonly int _sampleRate;

        // Beginning of auto-correlation window: Autocorrelation isn't being updated for
        // the first these many correlation bins.
        private readonly int _windowStart;

        // window functions for data preconditioning
        private readonly float[] _hamw;
        private readonly float[] _hamw2;

        // beat detection variable
        private readonly float[] _beatcorr_ringbuff;

        // FIFO-buffer for decimated processing samples.
        private readonly FifoSampleBuffer _buffer;

        // Collection of detected beat positions BeatCollection beats
        private readonly List<Beat> _beats;

        // 2nd order low-pass-filter
        private readonly IIR2Filter _beat_lpf;

        // Sample average counter.
        private int _decimateCount;

        // Sample average accumulator for FIFO-like decimation.
        private double _decimateSum;

        // beat detection variables
        private int _pos;
        private int _peakPos;
        private int _beatcorr_ringbuffpos;
        private int _init_scaler;
        private float _peakVal;

        /// <summary>
        /// Initializes a new instance of the <see cref="BpmDetect"/> class.
        /// </summary>
        /// <param name="numChannels">Number of channels in sample data.</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        public BpmDetect(int numChannels, int sampleRate)
        {
            _beat_lpf = new IIR2Filter(_LPF_coeffs);

            _beats = new List<Beat>(250);

            _sampleRate = sampleRate;
            _channels = numChannels;

            _decimateSum = 0;
            _decimateCount = 0;

            // choose decimation factor so that result is approx. 1000 Hz
            _decimateBy = sampleRate / TARGET_SRATE;
            if ((_decimateBy <= 0) || (_decimateBy * DECIMATED_BLOCK_SIZE < INPUT_BLOCK_SIZE))
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate), Strings.Argument_SampleRateTooSmall);
            }

            // Calculate window length & starting item according to desired min & max bpms
            _windowLen = (60 * sampleRate) / (_decimateBy * MIN_BPM);
            _windowStart = (60 * sampleRate) / (_decimateBy * MAX_BPM_RANGE);

            Debug.Assert(_windowLen > _windowStart, "Window length exceeds window start");

            // allocate new working objects
            _xcorr = new float[_windowLen];

            _pos = 0;
            _peakPos = 0;
            _peakVal = 0;
            _init_scaler = 1;
            _beatcorr_ringbuffpos = 0;
            _beatcorr_ringbuff = new float[_windowLen];

            // allocate processing buffer
            _buffer = new FifoSampleBuffer
            {
                // we do processing in mono mode
                Channels = 1
            };

            _buffer.Clear();

            // calculate hamming windows
            _hamw = new float[XCORR_UPDATE_SEQUENCE];
            Hamming(_hamw);
            _hamw2 = new float[XCORR_UPDATE_SEQUENCE / 2];
            Hamming(_hamw2);
        }

        /// <summary>
        /// Inputs a block of samples for analyzing: Envelopes the samples and then
        /// updates the auto-correlation estimation. When whole song data has been input
        /// in smaller blocks using this function, read the resulting bpm with 'getBpm'
        /// method.
        /// </summary>
        /// <param name="samples">Pointer to input/working data buffer.</param>
        /// <param name="numSamples">Number of samples to insert.</param>
        /// <remarks>
        /// Notice that data in 'samples' array can be disrupted in processing.
        /// </remarks>
        public void InputSamples(ReadOnlySpan<float> samples, int numSamples)
        {
            Span<float> decimated = stackalloc float[DECIMATED_BLOCK_SIZE];

            // iterate so that max INPUT_BLOCK_SAMPLES processed per iteration
            while (numSamples > 0)
            {
                var block = (numSamples > INPUT_BLOCK_SIZE) ? INPUT_BLOCK_SIZE : numSamples;

                // decimate. note that converts to mono at the same time
                var decSamples = Decimate(in decimated, samples, block);
                samples = samples.Slice(block * _channels);
                numSamples -= block;

                _buffer.PutSamples(decimated, decSamples);
            }

            // when the buffer has enough samples for processing...
            int req = Math.Max(_windowLen + XCORR_UPDATE_SEQUENCE, 2 * XCORR_UPDATE_SEQUENCE);
            while (_buffer.AvailableSamples >= req)
            {
                // ... update auto-correlations...
                UpdateXCorr(XCORR_UPDATE_SEQUENCE);

                // ...update beat position calculation...
                UpdateBeatPos(XCORR_UPDATE_SEQUENCE / 2);

                // ... and remove processed samples from the buffer
                const int NUM_SAMPLES = XCORR_UPDATE_SEQUENCE / OVERLAP_FACTOR;
                _buffer.ReceiveSamples(NUM_SAMPLES);
            }
        }

        /// <summary>
        /// Analyzes the results and returns the BPM rate. Use this method to read result
        /// after whole song data has been input to the class by consecutive calls of
        /// <see cref="InputSamples"/>.
        /// </summary>
        /// <returns>The beats-per-minute rate, or zero if detection failed.</returns>
        public float GetBpm()
        {
            var peakFinder = new PeakFinder();

            // remove bias from xcorr data
            RemoveBias();

            var coeff = 60.0 * (_sampleRate / (double)_decimateBy);

            // save bpm debug data if debug data writing enabled
            SaveDebugData("soundtouch-bpm-xcorr.txt", _xcorr, _windowStart, _windowLen, coeff);

            // Smoothen by N-point moving-average
            Span<float> data = stackalloc float[_windowLen];
            MAFilter(data, _xcorr, _windowStart, _windowLen, MOVING_AVERAGE_N);

            // find peak position
            double peakPos = peakFinder.DetectPeak(data, _windowStart, _windowLen);

            // save bpm debug data if debug data writing enabled
            SaveDebugData("soundtouch-bpm-smoothed.txt", data, _windowStart, _windowLen, coeff);

            if (peakPos < 1e-9)
                return 0; // detection failed.

            SaveDebugBeatPos("soundtouch-detected-beats.txt", _beats);

            // calculate BPM
            float bpm = (float)(coeff / peakPos);
            return (bpm >= MIN_BPM && bpm <= MAX_BPM_VALID) ? bpm : 0;
        }

        /// <summary>
        /// Get beat position arrays.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The array includes also really low beat detection values
        /// in absence of clear strong beats. Consumer may wish to filter low values away.
        /// </para>
        /// <para>You can query a suitable array sized by calling this with NULL in "pos" &amp; "values".</para>
        /// </remarks>
        /// <param name="pos">receive array of beat positions.</param>
        /// <param name="strength">receive array of beat detection strengths.</param>
        /// <returns>The number of beats in the arrays.</returns>
        public int GetBeats(Span<float> pos, Span<float> strength)
        {
            int num = _beats.Count;
            if ((pos.Length == 0) || (strength.Length == 0))
                return num;    // pos or values NULL, return just size

            int max_num = Math.Min(pos.Length, strength.Length);
            for (int i = 0; (i < num) && (i < max_num); i++)
            {
                pos[i] = _beats[i].Position;
                strength[i] = _beats[i].Strength;
            }

            return num;
        }

        private static void Hamming(in Span<float> w)
        {
            var length = w.Length;
            for (var i = 0; i < length; i++)
            {
                w[i] = (float)(0.54 - (0.46 * Math.Cos(TWOPI * i / (length - 1))));
            }
        }

        // Calculate N-point moving average for "source" values
        private static void MAFilter(in Span<float> dest, in ReadOnlySpan<float> source, int start, int end, int n)
        {
            for (int i = start; i < end; i++)
            {
                int i1 = i - (n / 2);
                int i2 = i + (n / 2) + 1;
                if (i1 < start)
                    i1 = start;
                if (i2 > end)
                    i2 = end;

                double sum = 0;
                for (int j = i1; j < i2; j++)
                {
                    sum += source[j];
                }

                dest[i] = (float)(sum / (i2 - i1));
            }
        }

        [Conditional("_CREATE_BPM_DEBUG_FILE")]
        private static void SaveDebugData(in string name, in ReadOnlySpan<float> data, int minpos, int maxpos, double coeff)
        {
#if !NETSTANDARD1_1
            using var writer = new StreamWriter(name, false);

            Console.Error.WriteLine();
            Console.Error.WriteLine($"Writing BPM debug data into file {name}.");
            for (int i = minpos; i < maxpos; i++)
            {
                writer.WriteLine("{0}\t{1:.0}\t{2}", i, coeff / i, data[i]);
            }
#else
            Debug.WriteLine($"Writing BPM debug data ({name}).");
            for (int i = minpos; i < maxpos; i++)
            {
                Debug.WriteLine("{0}\t{1:.0}\t{2}", i, coeff / i, data[i]);
            }
#endif

        }

        [Conditional("_CREATE_BPM_DEBUG_FILE")]
        private static void SaveDebugBeatPos(in string name, in List<Beat> beats)
        {
#if !NETSTANDARD1_1
            using var writer = new StreamWriter(name, false);

            Console.Error.WriteLine();
            Console.Error.WriteLine($"Writing BPM debug data into file {name}.");
            foreach (var b in beats)
            {
                writer.WriteLine("{0}\t{1}", b.Position, b.Strength);
            }
#else
            Debug.WriteLine($"Writing BPM debug data ({name}).");
            foreach (var b in beats)
            {
                Debug.WriteLine("{0}\t{1}", b.Position, b.Strength);
            }
#endif
        }

        /// <summary>
        /// Updates auto-correlation function for given number of decimated samples that
        /// are read from the internal 'buffer' pipe (samples aren't removed from the pipe
        /// though).
        /// </summary>
        /// <param name="process_samples">How many samples are processed.</param>
        private void UpdateXCorr(int process_samples)
        {
            int offs;

            Debug.Assert(_buffer.AvailableSamples >= (process_samples + _windowLen), "Buffer should have enough samples");
            Debug.Assert(process_samples == XCORR_UPDATE_SEQUENCE, "Argument process_samples should always be XCORR_UPDATE_SEQUENCE");

            ReadOnlySpan<float> pBuffer = _buffer.PtrBegin();

            // calculate decay factor for xcorr filtering
            float xcorr_decay = (float)Math.Pow(0.5, 1.0 / (XCORR_DECAY_TIME_CONSTANT * TARGET_SRATE / process_samples));

            // prescale pbuffer
            Span<float> tmp = stackalloc float[XCORR_UPDATE_SEQUENCE];

            for (int i = 0; i < process_samples; i++)
            {
                tmp[i] = _hamw[i] * _hamw[i] * pBuffer[i];
            }

            for (offs = _windowStart; offs < _windowLen; offs++)
            {
                float sum = 0;
                for (int i = 0; i < process_samples; i++)
                {
                    sum += tmp[i] * pBuffer[i + offs]; // scaling the sub-result shouldn't be necessary
                }

                _xcorr[offs] *= xcorr_decay; // decay 'xcorr' here with suitable time constant.

                _xcorr[offs] += Math.Abs(sum);
            }
        }

        /// <summary>
        /// Decimates samples to approx. 500 Hz.
        /// </summary>
        /// <param name="dest">Destination buffer.</param>
        /// <param name="src">Source sample buffer.</param>
        /// <param name="numSamples">Number of samples to process.</param>
        /// <returns>Number of output samples.</returns>
        private int Decimate(in Span<float> dest, ReadOnlySpan<float> src, int numSamples)
        {
            Debug.Assert(_channels > 0, "_channels > 0");
            Debug.Assert(_decimateBy > 0, "_decimateBy > 0");

            var outcount = 0;
            for (int count = 0; count < numSamples; count++)
            {
                int j;

                // convert to mono and accumulate
                for (j = 0; j < _channels; j++)
                {
                    _decimateSum += src[j];
                }

                src = src.Slice(j);

                _decimateCount++;
                if (_decimateCount >= _decimateBy)
                {
                    // Store every Nth sample only
                    var nthSample = _decimateSum / (_decimateBy * _channels);
                    _decimateSum = 0;
                    _decimateCount = 0;

                    dest[outcount] = (float)nthSample;
                    outcount++;
                }
            }

            return outcount;
        }

        // remove constant bias from xcorr data
        private void RemoveBias()
        {
            // Remove linear bias: calculate linear regression coefficient
            // 1. calc mean of 'xcorr' and 'i'
            double mean_x = 0;
            for (int i = _windowStart; i < _windowLen; i++)
            {
                mean_x += _xcorr[i];
            }

            mean_x /= _windowLen - _windowStart;
            double mean_i = 0.5 * (_windowLen - 1 + _windowStart);

            // 2. calculate linear regression coefficient
            double b = 0;
            double div = 0;
            for (int i = _windowStart; i < _windowLen; i++)
            {
                double xt = _xcorr[i] - mean_x;
                double xi = i - mean_i;
                b += xt * xi;
                div += xi * xi;
            }

            b /= div;

            // subtract linear regression and resolve min. value bias
            float minval = float.MaxValue;   // arbitrary large number
            for (int i = _windowStart; i < _windowLen; i++)
            {
                _xcorr[i] -= (float)(b * i);
                if (_xcorr[i] < minval)
                {
                    minval = _xcorr[i];
                }
            }

            // subtract min.value
            for (int i = _windowStart; i < _windowLen; i++)
            {
                _xcorr[i] -= minval;
            }
        }

        // Detect individual beat positions
        private void UpdateBeatPos(int process_samples)
        {
            Debug.Assert(_buffer.AvailableSamples >= (uint)(process_samples + _windowLen), "Buffer should have enough samples.");
            Debug.Assert(process_samples == XCORR_UPDATE_SEQUENCE / 2, "Argument process_samples should always be XCORR_UPDATE_SEQUENCE / 2");

            ReadOnlySpan<float> pBuffer = _buffer.PtrBegin();

            double posScale = _decimateBy / (double)_sampleRate;
            int resetDur = (int)((0.12 / posScale) + 0.5);

            // prescale pbuffer
            Span<float> tmp = stackalloc float[XCORR_UPDATE_SEQUENCE / 2];
            for (int i = 0; i < process_samples; i++)
            {
                tmp[i] = _hamw2[i] * _hamw2[i] * pBuffer[i];
            }

            for (int offs = _windowStart; offs < _windowLen; offs++)
            {
                float sum = 0;
                for (int i = 0; i < process_samples; i++)
                {
                    sum += tmp[i] * pBuffer[offs + i];
                }

                _beatcorr_ringbuff[(_beatcorr_ringbuffpos + offs) % _windowLen] += (sum > 0) ? sum : 0; // accumulate only positive correlations
            }

            const int SKIP_STEP = XCORR_UPDATE_SEQUENCE / OVERLAP_FACTOR;

            // compensate empty buffer at beginning by scaling coefficient
            float scale = _windowLen / (float)(SKIP_STEP * _init_scaler);
            if (scale > 1.0f)
            {
                _init_scaler++;
            }
            else
            {
                scale = 1.0f;
            }

            // detect beats
            for (int i = 0; i < SKIP_STEP; i++)
            {
                float sum = _beatcorr_ringbuff[_beatcorr_ringbuffpos];
                sum -= _beat_lpf.Update(sum);

                if (sum > _peakVal)
                {
                    // found new local largest value
                    _peakVal = sum;
                    _peakPos = _pos;
                }

                if (_pos > _peakPos + resetDur)
                {
                    // largest value not updated for 200msec => accept as beat
                    _peakPos += SKIP_STEP;
                    if (_peakVal > 0)
                    {
                        // add detected beat to end of "beats" vector.
                        _beats.Add(new Beat((float)(_peakPos * posScale), _peakVal * scale));
                    }

                    _peakVal = 0;
                    _peakPos = _pos;
                }

                _beatcorr_ringbuff[_beatcorr_ringbuffpos] = 0;
                _pos++;
                _beatcorr_ringbuffpos = (_beatcorr_ringbuffpos + 1) % _windowLen;
            }
        }
    }
}
