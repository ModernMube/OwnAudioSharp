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
    //using JetBrains.Annotations;

    /// <summary>
    /// <para>
    /// Sample rate transposer. Changes sample rate by using linear interpolation
    /// together with anti-alias filtering (first order interpolation with anti-
    /// alias filtering should be quite adequate for this application).
    /// </para>
    /// <para>
    /// Use either of the derived classes of 'RateTransposerInteger' or
    /// 'RateTransposerFloat' for corresponding integer/floating point tranposing
    /// algorithm implementation.
    /// </para>
    /// </summary>
    internal class RateTransposer : FifoProcessor
    {
        // Anti-alias filter object
        private readonly AntiAliasFilter _pAAFilter;
        private readonly TransposerBase _transposer;

        // Buffer for collecting samples to feed the anti-alias filter between two batches
        private readonly FifoSampleBuffer _inputBuffer;

        // Buffer for keeping samples between transposing & anti-alias filter
        private readonly FifoSampleBuffer _midBuffer;

        // Output sample buffer
        private readonly FifoSampleBuffer _outputBuffer;

        private bool _useAAFilter;

        /// <summary>
        /// Initializes a new instance of the <see cref="RateTransposer"/> class.
        /// </summary>
        public RateTransposer()
            : this(new FifoSampleBuffer())
        {
        }

        private RateTransposer(FifoSampleBuffer outputBuffer)
            : base(outputBuffer)
        {
            _useAAFilter =
#if !SOUNDTOUCH_PREVENT_CLICK_AT_RATE_CROSSOVER
        true;
#else
        // Disable Anti-alias filter if desirable to avoid click at rate change zero value crossover
        false;
#endif

            _inputBuffer = new FifoSampleBuffer();
            _midBuffer = new FifoSampleBuffer();
            _outputBuffer = outputBuffer;

            // Instantiates the anti-alias filter
            _pAAFilter = new AntiAliasFilter(64);
            _transposer = TransposerBase.CreateInstance();
            Clear();
        }

        public override void Dispose()
        {
            _inputBuffer.Dispose();
            _midBuffer.Dispose();
            _pAAFilter.Dispose();
            base.Dispose();
        }

        /// <summary>
        /// Gets a value indicating whether there aren't any samples available for outputting.
        /// </summary>
        public override bool IsEmpty => base.IsEmpty && _inputBuffer.IsEmpty;

        /// <summary>
        /// Gets the approximate initial input-output latency.
        /// </summary>
        public int Latency => _transposer.Latency + (_useAAFilter ? _pAAFilter.Length / 2 : 0);

        /// <summary>
        /// Returns the output buffer object.
        /// </summary>
        public FifoSamplePipe GetOutputBuffer() => _outputBuffer;

        /// <summary>
        /// Return anti-alias filter object.
        /// </summary>
        public AntiAliasFilter GetAAFilter() => _pAAFilter;

        /// <summary>
        /// Enables/disables the anti-alias filter. Zero to disable, nonzero to enable.
        /// </summary>
        public void EnableAAFilter(bool newMode)
        {
#if !SOUNDTOUCH_PREVENT_CLICK_AT_RATE_CROSSOVER
            // Disable Anti-alias filter if desirable to avoid click at rate change zero value crossover
            _useAAFilter = newMode;
            Clear();
#endif
        }

        /// <summary>
        /// Returns nonzero if anti-alias filter is enabled.
        /// </summary>
        public bool IsAAFilterEnabled() => _useAAFilter;

        /// <summary>
        /// Sets new target rate. Normal rate = 1.0, smaller values represent slower
        /// rate, larger faster rates.
        /// </summary>
        public virtual void SetRate(double newRate)
        {
            double fCutoff;

            _transposer.SetRate(newRate);

            // design a new anti-alias filter
            fCutoff = newRate > 1.0 ? 0.5 / newRate : 0.5 * newRate;

            _pAAFilter.SetCutoffFreq(fCutoff);
        }

        /// <summary>
        /// Sets the number of channels, 1 = mono, 2 = stereo.
        /// </summary>
        public void SetChannels(int channels)
        {
            if (!VerifyNumberOfChannels(channels) || (_transposer.NumberOfChannels == channels))
                return;

            _transposer.SetChannels(channels);
            _inputBuffer.Channels = channels;
            _midBuffer.Channels = channels;
            _outputBuffer.Channels = channels;
        }

        /// <summary>
        /// Adds 'numSamples' pcs of samples from the 'samples' memory position into
        /// the input of the object.
        /// </summary>
        public override void PutSamples(in ReadOnlySpan<float> samples, int numSamples) => ProcessSamples(samples, numSamples);

        /// <summary>
        /// Clears all the samples in the object.
        /// </summary>
        public override void Clear()
        {
            _outputBuffer.Clear();
            _midBuffer.Clear();
            _inputBuffer.Clear();
            _transposer.ResetRegisters();

            // prefill buffer to avoid losing first samples at beginning of stream
            int prefil = Latency;
            _inputBuffer.AddSilent(prefil);
        }

        /// <summary>
        /// Transposes sample rate by applying anti-alias filter to prevent folding.
        /// </summary>
        private void ProcessSamples(in ReadOnlySpan<float> src, int numSamples)
        {
            if (numSamples == 0)
                return;

            // Store samples to input buffer
            _inputBuffer.PutSamples(src, numSamples);

            // If anti-alias filter is turned off, simply transpose without applying
            // the filter
            if (!_useAAFilter)
            {
                _transposer.Transpose(_outputBuffer, _inputBuffer);
                return;
            }

            // Transpose with anti-alias filter
            if (_transposer.Rate < 1.0f)
            {
                // If the parameter 'Rate' value is smaller than 1, first transpose
                // the samples and then apply the anti-alias filter to remove aliasing.

                // Transpose the samples, store the result to end of "midBuffer"
                _transposer.Transpose(_midBuffer, _inputBuffer);

                // Apply the anti-alias filter for transposed samples in midBuffer
                _pAAFilter.Evaluate(_outputBuffer, _midBuffer);
            }
            else
            {
                // If the parameter 'Rate' value is larger than 1, first apply the
                // anti-alias filter to remove high frequencies (prevent them from folding
                // over the lover frequencies), then transpose.

                // Apply the anti-alias filter for samples in inputBuffer
                _pAAFilter.Evaluate(_midBuffer, _inputBuffer);

                // Transpose the AA-filtered samples in "midBuffer"
                _transposer.Transpose(_outputBuffer, _midBuffer);
            }
        }
    }
}
