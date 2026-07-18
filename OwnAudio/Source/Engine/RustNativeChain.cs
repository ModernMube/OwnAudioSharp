using System;

namespace OwnaudioNET.Engine;

/// <summary>
/// Tells whether the file playback DSP chain (decode / tempo / pitch / volume / mix / fx) runs natively in Rust.
/// </summary>
/// <remarks>
/// Since 4.0 the native chain is the only production path, the managed real-time code is gone, so this is always
/// true in the wild. <see cref="Override"/> is just an internal test hook left over from the cut-over.
/// </remarks>
internal static class RustNativeChain
{
    /// <summary>
    /// What we report when nothing overrides it.
    /// </summary>
    internal const bool DefaultEnabled = true;

    /// <summary>
    /// Test only knob, wins over the default when not null. Null in production.
    /// </summary>
    internal static bool? Override { get; set; }

    /// <summary>
    /// True when the Rust-native chain is on. The old AppContext switch and env var are not read anymore.
    /// </summary>
    internal static bool Enabled => Override ?? DefaultEnabled;
}
