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
    /// <summary>
    /// Default values for sound processing parameters.
    /// </summary>
    /// <remarks>
    /// Notice that the default parameters are tuned for contemporary popular music
    /// processing.
    /// For speech processing applications these parameters suit better:
    /// <code>
    ///     SEQUENCE_MS     = 40
    ///     SEEKWINDOW_MS   = 15
    ///     OVERLAP_MS      = 8
    /// </code>
    /// </remarks>
    internal static class Defaults
    {
        /// <summary>
        /// <para>
        /// Default length of a single processing sequence, in milliseconds.
        /// This determines to how  long sequences the original sound is chopped
        /// in the time-stretch algorithm.
        /// </para>
        /// <para>
        /// The larger this value is, the lesser sequences are used in
        /// processing. In principle a bigger value sounds better when slowing
        /// down tempo, but worse when increasing tempo and vice versa.
        /// </para>
        /// <para>Increasing this value reduces computational burden &amp; vice versa.</para>
        /// </summary>
        public const int SEQUENCE_MS = USE_AUTO_SEQUENCE_LEN;

        /// <summary>
        /// Giving this value for the sequence length sets automatic parameter
        /// value according to tempo setting (recommended).
        /// </summary>
        public const int USE_AUTO_SEQUENCE_LEN = 0;

        /// <summary>
        /// <para>
        /// Seeking window default length in milliseconds for algorithm that
        /// finds the best possible  overlapping location. This determines from
        /// how wide window the algorithm may look for an  optimal joining
        /// location when mixing the sound sequences back together.
        /// </para>
        /// <para>
        /// The bigger this window setting is, the higher the possibility to
        /// find a better mixing position will become, but at the same time
        /// large values may cause a "drifting" artifact because consequent
        /// sequences will be taken at more uneven intervals.
        /// </para>
        /// <para>
        /// If there's a disturbing artifact that sounds as if a constant
        /// frequency was drifting  around, try reducing this setting.
        /// </para>
        /// <para>
        /// Increasing this value increases computational burden &amp; vice versa.
        /// <code>
        /// // public const int SEEKWINDOW_MS       15
        /// </code>
        /// </para>
        /// </summary>
        public const int SEEKWINDOW_MS = USE_AUTO_SEEKWINDOW_LEN;

        /// <summary>
        /// Giving this value for the seek window length sets automatic
        /// parameter value according to tempo setting (recommended).
        /// </summary>
        public const int USE_AUTO_SEEKWINDOW_LEN = 0;

        /// <summary>
        /// <para>
        /// Overlap length in milliseconds. When the chopped sound sequences are
        /// mixed back together,  to form a continuous sound stream, this
        /// parameter defines over how long period the two  consecutive
        /// sequences are let to overlap each other.
        /// </para>
        /// <para>
        /// This shouldn't be that critical parameter. If you reduce the
        /// DEFAULT_SEQUENCE_MS setting  by a large amount, you might wish to
        /// try a smaller value on this.
        /// </para>
        /// <para>Increasing this value increases computational burden &amp; vice versa.</para>
        /// </summary>
        public const int OVERLAP_MS = 8;
    }
}
