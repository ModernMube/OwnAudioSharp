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
    //using JetBrains.Annotations;

    /// <summary>
    /// Available setting IDs for the <see cref="SoundTouchProcessor.SetSetting(SettingId, int)"/>
    /// &amp; <see cref="SoundTouchProcessor.GetSetting(SettingId)"/> methods.
    /// </summary>
    public enum SettingId
    {
        /// <summary>
        /// Enable/disable anti-alias filter in pitch transposer (0 = disable).
        /// </summary>
        UseAntiAliasFilter = 0,

        /// <summary>
        /// Pitch transposer anti-alias filter length (8 .. 128 taps, default = 32).
        /// </summary>
        AntiAliasFilterLength = 1,

        /// <summary>
        /// Enable/disable quick seeking algorithm in tempo changer routine
        /// (enabling quick seeking lowers CPU utilization but causes a minor sound
        ///  quality compromising).
        /// </summary>
        UseQuickSeek = 2,

        /// <summary>
        /// Time-stretch algorithm single processing sequence length in milliseconds. This determines
        /// to how long sequences the original sound is chopped in the time-stretch algorithm.
        /// </summary>
        SequenceDurationMs = 3,

        /// <summary>
        /// Time-stretch algorithm seeking window length in milliseconds for algorithm that finds the
        /// best possible overlapping location. This determines from how wide window the algorithm
        /// may look for an optimal joining location when mixing the sound sequences back together.
        /// </summary>
        SeekWindowDurationMs = 4,

        /// <summary>
        /// Time-stretch algorithm overlap length in milliseconds. When the chopped sound sequences
        /// are mixed back together, to form a continuous sound stream, this parameter defines over
        /// how long period the two consecutive sequences are let to overlap each other.
        /// </summary>
        OverlapDurationMs = 5,

        /// <summary>
        /// <para>
        /// Call <see cref="SoundTouchProcessor.GetSetting(SettingId)"/> with this ID to query processing
        /// sequence size in samples.
        /// This value gives approximate value of how many input samples you'll need to
        /// feed into SoundTouch after initial buffering to get out a new batch of
        /// output samples.
        /// </para>
        /// <para>
        /// This value does not include initial buffering at beginning of a new processing
        /// stream, use <see cref="InitialLatency"/> to get the initial buffering size.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item>This is read-only parameter, i.e. <see cref="SoundTouchProcessor.SetSetting(SettingId, int)"/>
        /// ignores this parameter</item>
        /// <item>This parameter value is not constant but change depending on the
        /// tempo/pitch/rate/sample-rate settings.</item>
        /// </list>
        /// </remarks>
        NominalInputSequence = 6,

        /// <summary>
        /// Call <see cref="SoundTouchProcessor.GetSetting(SettingId)"/> with this ID to query nominal
        /// average processing output size in samples. This value tells approximate value
        /// how many output samples SoundTouch outputs once it does DSP processing run for a
        /// batch of input samples.
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item>This is read-only parameter, i.e. <see cref="SoundTouchProcessor.SetSetting(SettingId, int)"/>
        /// ignores this parameter.</item>
        /// <item>This parameter value is not constant but change depending on the
        /// tempo/pitch/rate/sample-rate settings.</item>
        /// </list>
        /// </remarks>
        NominalOutputSequence = 7,

        /// <summary>
        /// <para>
        /// Call <see cref="SoundTouchProcessor.GetSetting(SettingId)"/> with this ID to query
        /// initial processing latency, i.e. approx. how many samples you'll need to enter
        /// to SoundTouch pipeline before you can expect to get first batch of ready output
        /// samples out.
        /// </para>
        /// <para>
        /// After the first output batch, you can then expect to get approx.
        /// <see cref="NominalOutputSequence"/> ready samples out for every
        /// <see cref="NominalInputSequence"/> samples that you enter into SoundTouch.
        /// </para>
        /// <para>
        /// Example:
        /// <example>
        ///     processing with parameter -tempo=5
        ///     => initial latency = 5509 samples
        ///        input sequence  = 4167 samples
        ///        output sequence = 3969 samples
        /// </example>
        /// </para>
        /// <para>
        /// Accordingly, you can expect to feed in approx. 5509 samples at beginning of
        /// the stream, and then you'll get out the first 3969 samples. After that, for
        /// every approx. 4167 samples that you'll put in, you'll receive again approx.
        /// 3969 samples out.
        /// </para>
        /// <para>
        /// This also means that average latency during stream processing is
        /// <see cref="InitialLatency"/>-<see cref="NominalOutputSequence"/>/2, in the
        /// above example case 5509-3969/2 = 3524 samples.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item>This is read-only parameter, i.e. <see cref="SoundTouchProcessor.SetSetting(SettingId, int)"/>
        /// ignores this parameter.</item>
        /// <item>This parameter value is not constant but change depending on the
        /// tempo/pitch/rate/sample-rate settings.</item>
        /// </list>
        /// </remarks>
        InitialLatency = 8,
    }
}
