using System;

namespace Ownaudio.Sources.Extensions
{
    /// <summary>
    /// Thread-safe circular buffer implementation for audio sample storage.
    /// Provides efficient FIFO (First In, First Out) operations optimized for audio processing.
    /// Uses power-of-2 sizing for performance optimization with bitwise operations.
    /// </summary>
    public class CircularAudioBuffer
    {
        /// <summary>
        /// Internal buffer array that stores the audio samples.
        /// </summary>
        private readonly float[] _buffer;

        /// <summary>
        /// The total capacity of the buffer (always a power of 2).
        /// </summary>
        private readonly int _capacity;

        /// <summary>
        /// Current write position in the buffer.
        /// </summary>
        private int _writePos = 0;

        /// <summary>
        /// Current read position in the buffer.
        /// </summary>
        private int _readPos = 0;

        /// <summary>
        /// Number of samples currently stored in the buffer.
        /// </summary>
        private int _count = 0;

        /// <summary>
        /// Synchronization object to ensure thread-safe operations.
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// Initializes a new instance of the CircularAudioBuffer class with the specified capacity.
        /// The actual capacity will be rounded up to the next power of 2 for performance optimization.
        /// </summary>
        /// <param name="capacity">The desired minimum capacity of the buffer.</param>
        public CircularAudioBuffer(int capacity)
        {
            // Ensure capacity is power of 2 for efficient modulo operations
            _capacity = GetNextPowerOf2(capacity);
            _buffer = new float[_capacity];
        }

        /// <summary>
        /// Gets the number of samples currently stored in the buffer.
        /// This property is thread-safe.
        /// </summary>
        /// <value>The number of audio samples available for reading.</value>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        /// <summary>
        /// Gets the number of samples that can still be written to the buffer.
        /// This property is thread-safe.
        /// </summary>
        /// <value>The number of additional samples that can be stored without overwriting existing data.</value>
        public int AvailableSpace
        {
            get
            {
                lock (_lock)
                {
                    return _capacity - _count;
                }
            }
        }

        /// <summary>
        /// Adds audio samples to the buffer. If the buffer doesn't have enough space,
        /// only the samples that fit will be added. This method is thread-safe.
        /// </summary>
        /// <param name="samples">The audio samples to add to the buffer.</param>
        public void AddSamples(ReadOnlySpan<float> samples)
        {
            lock (_lock)
            {
                for (int i = 0; i < samples.Length && _count < _capacity; i++)
                {
                    _buffer[_writePos] = samples[i];
                    _writePos = (_writePos + 1) & (_capacity - 1); // Efficient modulo for power of 2
                    _count++;
                }
            }
        }

        /// <summary>
        /// Extracts audio samples from the buffer into the provided output span.
        /// This method is thread-safe and removes the extracted samples from the buffer.
        /// </summary>
        /// <param name="output">The span to write the extracted samples to.</param>
        /// <returns>The actual number of samples extracted and written to the output span.</returns>
        public int ExtractSamples(Span<float> output)
        {
            lock (_lock)
            {
                int samplesToRead = Math.Min(output.Length, _count);

                for (int i = 0; i < samplesToRead; i++)
                {
                    output[i] = _buffer[_readPos];
                    _readPos = (_readPos + 1) & (_capacity - 1); // Efficient modulo
                    _count--;
                }

                return samplesToRead;
            }
        }

        /// <summary>
        /// Clears all samples from the buffer and resets read/write positions.
        /// This method is thread-safe.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _writePos = 0;
                _readPos = 0;
                _count = 0;
                Array.Clear(_buffer, 0, _buffer.Length);
            }
        }

        /// <summary>
        /// Calculates the next power of 2 that is greater than or equal to the specified value.
        /// This optimization enables efficient modulo operations using bitwise AND operations.
        /// </summary>
        /// <param name="value">The input value.</param>
        /// <returns>The next power of 2 greater than or equal to the input value.</returns>
        private static int GetNextPowerOf2(int value)
        {
            if (value <= 0) return 1;

            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }
    }
}
