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

    /// <summary>
    /// <para>
    /// Base-class for sound processing routines working in FIFO principle. With
    /// this base  class it's easy to implement sound processing stages that can
    /// be chained together, so that samples that are fed into beginning of the
    /// pipe automatically go through  all the processing stages.
    /// </para>
    /// <para>
    /// When samples are input to this class, they're first processed and then
    /// put to  the FIFO pipe that's defined as output of this class. This
    /// output pipe can be either other processing stage or a FIFO sample
    /// buffer.
    /// </para>
    /// </summary>
    public abstract class FifoProcessor : FifoSamplePipe
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FifoProcessor"/> class.
        /// </summary>
        /// <param name="output">The pipe where processed samples are put.</param>
        protected FifoProcessor(FifoSamplePipe output)
        {
            Output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <inheritdoc/>
        public override int AvailableSamples => Output.AvailableSamples;

        /// <inheritdoc/>
        public override bool IsEmpty => Output.IsEmpty;

        /// <summary>
        /// Gets the internal pipe where processed samples are put.
        /// </summary>
        protected FifoSamplePipe Output { get; private protected set; }

        /// <inheritdoc/>
        public override Span<float> PtrBegin() => Output.PtrBegin();

        /// <inheritdoc/>
        public override int ReceiveSamples(in Span<float> output, int maxSamples) => Output.ReceiveSamples(output, maxSamples);

        /// <inheritdoc/>
        public override int ReceiveSamples(int maxSamples) => Output.ReceiveSamples(maxSamples);

        /// <inheritdoc/>
        public override int AdjustAmountOfSamples(int numSamples) => Output.AdjustAmountOfSamples(numSamples);

        /// <summary>
        /// Sets the output pipe.
        /// </summary>
        /// <exception cref="InvalidOperationException">The output pipe is already set.</exception>
        protected void SetOutPipe(FifoSamplePipe output)
        {
            if (output is null)
                throw new ArgumentNullException(nameof(output));

            if (Output != null)
                throw new InvalidOperationException(Strings.InvalidOperation_OutputPipeOverwrite);

            Output = output;
        }
    }
}
