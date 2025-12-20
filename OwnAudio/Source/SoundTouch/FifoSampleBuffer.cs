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
    using System.Diagnostics;
    using System.Buffers;

    /// <summary>
    /// Sample buffer working in FIFO (first-in-first-out) principle. The class
    /// takes care of storage size adjustment and data moving during
    /// input/output operations.
    /// </summary>
    /// <remarks>
    /// Notice that in case of stereo audio, one sample is considered to consist
    /// of  both channel data.
    /// </remarks>
    public sealed class FifoSampleBuffer : FifoSamplePipe
    {
        private float[]? _buffer;

        // How many samples are currently in buffer.
        private int _samplesInBuffer;

        // Channels, 1=mono, 2=stereo.
        private int _channels;

        // Current position pointer to the buffer. This pointer is increased when samples are
        // removed from the pipe so that it's necessary to actually rewind buffer (move data)
        // only new data when is put to the pipe.
        private int _bufferPos;

        /// <summary>
        /// Initializes a new instance of the <see cref="FifoSampleBuffer"/> class.
        /// </summary>
        /// <param name="numberOfChannels">Number of channels, 1=mono, 2=stereo; Default is stereo.</param>
        public FifoSampleBuffer(int numberOfChannels = 2)
        {
            if (numberOfChannels <= 0)
                throw new ArgumentOutOfRangeException(nameof(numberOfChannels));

            _buffer = null;
            _samplesInBuffer = 0;
            _bufferPos = 0;
            _channels = numberOfChannels;
            EnsureCapacity(32);     // allocate initial capacity.
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (_buffer != null)
            {
                ArrayPool<float>.Shared.Return(_buffer);
                _buffer = null;
            }
            base.Dispose();
        }

        /// <inheritdoc />
        public override int AvailableSamples => _samplesInBuffer;

        /// <inheritdoc />
        public override bool IsEmpty => _samplesInBuffer == 0;

        /// <summary>
        /// Gets or sets the number of channels.
        /// </summary>
        /// <remarks>1 = mono, 2 = stereo.</remarks>
        public int Channels
        {
            get => _channels;
            set
            {
                if (!VerifyNumberOfChannels(value))
                    return;

                if (_channels == value)
                    return;

                // If channels change, we need to re-interpret the data or clear it.
                // Usually changing channels on the fly with existing data is invalid for audio
                // unless we are just setting it up.
                // Assuming we preserve the total sample count (frames), the byte size changes.
                // But _samplesInBuffer represents frames.
                
                // If we have data, this is tricky. The original code did:
                // var usedBytes = _channels * _samplesInBuffer;
                // _channels = value;
                // _samplesInBuffer = usedBytes / _channels;
                // It tried to preserve total bytes? That seems weird for audio frames.
                // But let's stick to original logic to be safe, although it's likely broken for actual audio content.
                
                var totalFloats = _channels * _samplesInBuffer;
                _channels = value;
                _samplesInBuffer = totalFloats / _channels;
            }
        }

        /// <summary>
        /// Gets the current capacity.
        /// </summary>
        private int Capacity => _buffer?.Length / _channels ?? 0;

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">The buffer isn't initialized.</exception>
        public override Span<float> PtrBegin()
        {
            if (_buffer is null)
                throw new InvalidOperationException();

            return _buffer.AsSpan(_bufferPos * _channels);
        }

        /// <summary>
        /// <para>
        /// Returns a pointer to the end of the used part of the sample buffer
        /// (i.e.  where the new samples are to be inserted). This function may
        /// be used for  inserting new samples into the sample buffer directly.
        /// Please be careful not corrupt the book-keeping.
        /// </para>
        /// <para>
        /// When using this function as means for inserting new samples, also
        /// remember  to increase the sample count afterwards, by calling the
        /// <see cref="PutSamples(int)"/> method.
        /// </para>
        /// </summary>
        /// <param name="slackCapacity">How much free capacity (in samples)
        /// there _at least_ should be so that the caller can successfully
        /// insert the desired samples to the buffer. If necessary, the
        /// function grows the buffer size to comply with this requirement.
        /// </param>
        /// <returns>Pointer to the end of the used part of the sample buffer.</returns>
        public Span<float> PtrEnd(int slackCapacity)
        {
            EnsureCapacity(_samplesInBuffer + slackCapacity);
            return _buffer.AsSpan().Slice((_bufferPos + _samplesInBuffer) * _channels);
        }

        /// <summary>
        /// Adds samples from the <paramref name="samples"/> memory position to
        /// the sample buffer.
        /// </summary>
        /// <param name="samples">Pointer to samples.</param>
        /// <param name="numSamples">Number of samples to insert.</param>
        public override void PutSamples(in ReadOnlySpan<float> samples, int numSamples)
        {
            var dest = PtrEnd(numSamples);
            samples.Slice(0, numSamples * _channels).CopyTo(dest);

            _samplesInBuffer += numSamples;
        }

        /// <summary>
        /// <para>
        /// Adjusts the book-keeping to increase number of samples in the buffer
        /// without  copying any actual samples.
        /// </para>
        /// <para>
        /// This function is used to update the number of samples in the sample
        /// buffer when accessing the buffer directly with <see cref="PtrEnd"/>
        /// function. Please be  careful though.
        /// </para>
        /// </summary>
        /// <param name="numSamples">Number of samples been inserted.</param>
        public void PutSamples(int numSamples)
        {
            var req = _samplesInBuffer + numSamples;
            EnsureCapacity(req);
            _samplesInBuffer += numSamples;
        }

        /// <inheritdoc />
        public override int ReceiveSamples(in Span<float> output, int maxSamples)
        {
            var num = (maxSamples > _samplesInBuffer) ? _samplesInBuffer : maxSamples;

            PtrBegin().Slice(0, _channels * num).CopyTo(output);
            return ReceiveSamples(num);
        }

        /// <inheritdoc />
        public override int ReceiveSamples(int maxSamples)
        {
            if (maxSamples >= _samplesInBuffer)
            {
                var temp = _samplesInBuffer;
                _samplesInBuffer = 0;
                _bufferPos = 0; // Reset pos when empty to avoid unnecessary rewind
                return temp;
            }

            _samplesInBuffer -= maxSamples;
            _bufferPos += maxSamples;

            return maxSamples;
        }

        /// <inheritdoc />
        public override void Clear()
        {
            _samplesInBuffer = 0;
            _bufferPos = 0;
        }

        /// <inheritdoc />
        public override int AdjustAmountOfSamples(int numSamples)
        {
            if (numSamples < _samplesInBuffer)
                _samplesInBuffer = numSamples;

            return _samplesInBuffer;
        }

        /// <summary>
        /// Add silence to the end of the buffer.
        /// </summary>
        /// <param name="numSamples">Number of samples of silence.</param>
        public void AddSilent(int numSamples)
        {
            PtrEnd(numSamples).Slice(0, numSamples * _channels).Clear();
            _samplesInBuffer += numSamples;
        }

        /// <summary>
        /// Rewind the buffer by moving data from position pointed by 'bufferPos' to real
        /// beginning of the buffer.
        /// </summary>
        private void Rewind()
        {
            if (_buffer is null || _bufferPos == 0)
                return;

            if (_samplesInBuffer > 0)
            {
                var src = _buffer.AsSpan(_bufferPos * _channels, _samplesInBuffer * _channels);
                src.CopyTo(_buffer);
            }

            _bufferPos = 0;
        }

        /// <summary>
        /// Ensures that the buffer has capacity for at least this many samples.
        /// </summary>
        private void EnsureCapacity(int capacityRequirement)
        {
            int currentTotalCapacity = _buffer?.Length / _channels ?? 0;
            int availableAtEnd = currentTotalCapacity - (_bufferPos + _samplesInBuffer);

            // If we have enough space at the end, do nothing
            if (availableAtEnd >= (capacityRequirement - _samplesInBuffer))
                return;

            // If we have enough total space but it's fragmented (start is unused), rewind
            if (currentTotalCapacity >= capacityRequirement)
            {
                Rewind();
                return;
            }

            // We need to grow
            int requiredTotalFloats = capacityRequirement * _channels;
            
            // Round up to next power of 2 for efficiency, or at least 4096 floats
            int newSize = Math.Max(4096, requiredTotalFloats); 
            // Ensure newSize is somewhat grown to avoid frequent re-alloc
            if (newSize < _buffer?.Length * 2) newSize = (_buffer?.Length ?? 0) * 2;
            if (newSize < requiredTotalFloats) newSize = requiredTotalFloats;

            var newBuffer = ArrayPool<float>.Shared.Rent(newSize);

            if (_buffer != null)
            {
                if (_samplesInBuffer > 0)
                {
                    // Copy existing data to new buffer, rewinding effectively
                    var src = _buffer.AsSpan(_bufferPos * _channels, _samplesInBuffer * _channels);
                    src.CopyTo(newBuffer);
                }
                
                ArrayPool<float>.Shared.Return(_buffer);
            }

            _buffer = newBuffer;
            _bufferPos = 0;
        }
    }
}
