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

    using SoundTouch.Assets;
    //using JetBrains.Annotations;

    /// <summary>
    /// An abstract base class for classes that manipulate sound samples by operating like a
    /// first-in-first-out pipe: New samples are fed into one end of the pipe with the
    /// <see cref="PutSamples(in ReadOnlySpan{float}, int)"/> function, and the processed
    /// samples are received from the other end with the <see cref="ReceiveSamples(in Span{float}, int)"/>
    /// method.
    /// </summary>
    public abstract class FifoSamplePipe : IDisposable
    {
        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public virtual void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets the number of samples currently available.
        /// </summary>
        /// <returns>Number of samples currently available.</returns>
        public abstract int AvailableSamples { get; }

        /// <summary>
        /// Gets a value indicating whether there aren't any samples available
        /// for outputting.
        /// </summary>
        /// <value><see langword="true"/> if there aren't any samples available for outputting; otherwise, <see langword="false"/>.</value>
        public abstract bool IsEmpty { get; }

        /// <summary>
        /// <para>
        /// This function if provided for accessing the output samples directly.
        /// Please be careful for not corrupting the book-keeping.
        /// </para>
        /// <para>
        /// When using this function to output samples, also remember to 'remove'
        /// the output samples from the buffer by calling the <see cref="ReceiveSamples(int)"/>
        /// method.
        /// </para>
        /// </summary>
        /// <returns>Memory buffer to the beginning of the output samples.</returns>
        public abstract Span<float> PtrBegin();

        /// <summary>
        /// Adds samples from the <paramref name="samples"/> memory buffer to
        /// the sample buffer.
        /// </summary>
        public abstract void PutSamples(in ReadOnlySpan<float> samples, int numSamples);

        /// <summary>
        /// Moves samples from the <paramref name="other"/> pipe instance to
        /// this instance.
        /// </summary>
        /// <param name="other">Other pipe instance where from the receive the
        /// data.</param>
        public void MoveSamples(in FifoSamplePipe other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));

            int availableSamples = other.AvailableSamples;

            PutSamples(other.PtrBegin(), availableSamples);
            other.ReceiveSamples(availableSamples);
        }

        /// <summary>
        /// Output samples from beginning of the sample buffer. Copies requested samples to
        /// output buffer and removes them from the sample buffer. If there are less than
        /// requested samples in the buffer, returns all that available.
        /// </summary>
        /// <param name="output">Buffer where to copy output samples.</param>
        /// <param name="maxSamples">How many samples to receive at max.</param>
        /// <returns>Returns the number of samples written to <paramref name="output"/>.</returns>
        public abstract int ReceiveSamples(in Span<float> output, int maxSamples);

        /// <summary>
        /// Adjusts book-keeping so that given number of samples are removed from beginning of the
        /// sample buffer without copying them anywhere.
        /// </summary>
        /// <param name="maxSamples">Remove this many samples from the beginning of pipe.</param>
        /// <remarks>
        /// Used to reduce the number of samples in the buffer when accessing the sample buffer directly.
        /// </remarks>
        public abstract int ReceiveSamples(int maxSamples);

        /// <summary>
        /// Clears all the samples.
        /// </summary>
        public abstract void Clear();

        /// <summary>
        /// Allow trimming (downwards) amount of samples in pipeline.
        /// </summary>
        /// <param name="numSamples">The number of samples.</param>
        /// <returns>Returns adjusted amount of samples.</returns>
        public abstract int AdjustAmountOfSamples(int numSamples);

        /// <summary>
        /// Asserts that the correct number of channels are specified.
        /// </summary>
        /// <param name="nChannels">The number of channels to verify.</param>
        /// <returns><see langword="true"/> when the number of <paramref name="nChannels"/> are within bounds;
        /// otherwise an <see cref="InvalidOperationException"/> is thrown.</returns>
        /// <exception cref="InvalidOperationException">Illegal number of channels.</exception>
        protected static bool VerifyNumberOfChannels(int nChannels)
        {
            if ((nChannels > 0) && (nChannels <= SoundTouchProcessor.SOUNDTOUCH_MAX_CHANNELS))
            {
                return true;
            }

            throw new ArgumentException(Strings.Argument_IllegalNumberOfChannels);
        }
    }
}
