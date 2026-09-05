using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// How wide a net the detector casts.
    /// </summary>
    public enum DetectionMode
    {
        /// <summary>
        /// Triads and 7ths only.
        /// </summary>
        Basic,

        /// <summary>
        /// Adds 9ths, 11ths, 13ths and the altered stuff.
        /// </summary>
        Extended,

        /// <summary>
        /// Extended set, names spelled to fit the key.
        /// </summary>
        KeyAware,

        /// <summary>
        /// Extended set plus ambiguity reporting.
        /// </summary>
        Optimized
    }
}
