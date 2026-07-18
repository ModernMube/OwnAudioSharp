using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Per-channel delay and polarity flip for L / R / Sub.
    /// </summary>
    public class PhaseAlignment
    {
        private readonly float _sampleRate;

        /// <summary>
        /// Delay in ms per channel, and the same thing in samples.
        /// </summary>
        private readonly float[] _delays = new float[3];
        private readonly int[] _delaySamples = new int[3];

        private readonly bool[] _phaseInvert = new bool[3];
        private readonly float[][] _delayBuffers = new float[3][];
        private readonly int[] _delayBufferIndices = new int[3];

        /// <summary>
        /// Sample rate in Hz. We reserve 50ms worth of delay per channel.
        /// </summary>
        public PhaseAlignment(float sampleRate)
        {
            _sampleRate = sampleRate;

            int maxDelaySamples = (int)(sampleRate * 0.05f);
            for (int i = 0; i < 3; i++)
                _delayBuffers[i] = new float[maxDelaySamples];
        }

        /// <summary>
        /// Delays in milliseconds, L / R / Sub order. Buffers get flushed
        /// whenever a value actually moves.
        /// </summary>
        /// <param name="delays"></param>
        public void SetDelays(float[] delays)
        {
            if (delays.Length < 3) return;

            bool changed = false;
            for (int i = 0; i < 3; i++)
                if (Math.Abs(_delays[i] - delays[i]) > 0.01f) changed = true;

            if (!changed) return;

            Array.Copy(delays, _delays, 3);
            for (int i = 0; i < 3; i++)
            {
                int _smp = (int)(_delays[i] * _sampleRate / 1000.0f);
                _delaySamples[i] = Math.Clamp(_smp, 0, _delayBuffers[i].Length - 1);
            }

            Reset();
        }

        /// <summary>
        /// Polarity flip flags, L / R / Sub order.
        /// </summary>
        /// <param name="inversions"></param>
        public void SetPhaseInversions(bool[] inversions)
        {
            if (inversions.Length >= 3) { Array.Copy(inversions, _phaseInvert, 3); }
        }

        /// <summary>
        /// Delays and/or flips one channel in place. channel is 0 = L, 1 = R, 2 = Sub.
        /// </summary>
        public void Process(Span<float> buffer, int channel, int frameCount)
        {
            if (channel < 0 || channel >= 3) return;

            int delaySamples = _delaySamples[channel];
            bool invert = _phaseInvert[channel];
            if (delaySamples == 0 && !invert) return;

            float[] delayBuffer = _delayBuffers[channel];
            int writeIndex = _delayBufferIndices[channel];
            int bufferSize = delayBuffer.Length;

            for (int i = 0; i < buffer.Length; i++)
            {
                delayBuffer[writeIndex] = buffer[i];

                int readIndex = writeIndex - delaySamples;
                if (readIndex < 0) readIndex += bufferSize;

                float output = delayBuffer[readIndex];
                if (invert) output = -output;
                buffer[i] = output;

                writeIndex++;
                if (writeIndex >= bufferSize) writeIndex = 0;
            }

            _delayBufferIndices[channel] = writeIndex;
        }

        /// <summary>
        /// Empties every delay line.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < 3; i++)
            {
                Array.Clear(_delayBuffers[i]);
                _delayBufferIndices[i] = 0;
            }
        }
    }
}
