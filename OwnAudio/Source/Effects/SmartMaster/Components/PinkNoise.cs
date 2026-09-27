using System;
using System.Numerics;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Voss-McCartney pink noise for the room measurement. Cheap, and the octave balance
    /// is all we need from it. Streams, so it can sit behind a StreamingSource.
    /// </summary>
    internal sealed class PinkNoise
    {
        private readonly Random _random = new Random();
        private readonly double[] _rows = new double[7];
        private double _runningSum;
        private int _counter;

        public void Reset()
        {
            Array.Clear(_rows);
            _runningSum = 0;
            _counter = 0;
        }

        /// <summary>
        /// Fills an interleaved block. channelMask below zero feeds L and R, otherwise only
        /// that one channel gets the noise and the rest stay silent.
        /// </summary>
        public void Render(Span<float> buffer, int channels, int channelMask)
        {
            for (int i = 0; i + channels <= buffer.Length; i += channels)
            {
                _counter++;
                int _row = BitOperations.TrailingZeroCount(_counter) % _rows.Length;

                _runningSum -= _rows[_row];
                _rows[_row] = _random.NextDouble() * 2.0 - 1.0;
                _runningSum += _rows[_row];

                float _sample = (float)(_runningSum / _rows.Length);
                for (int c = 0; c < channels; c++)
                    buffer[i + c] = channelMask < 0 ? (c < 2 ? _sample : 0f) : (channelMask == c ? _sample : 0f);
            }
        }
    }
}
