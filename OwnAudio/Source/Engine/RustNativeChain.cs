using System;

namespace OwnaudioNET.Engine;

/// <summary>
/// Internal gate that reports whether the file-playback DSP chain
/// (decode / tempo / pitch / volume / mix / effect) runs through the native Rust engine.
/// </summary>
/// <remarks>
/// As of 4.0 (plan L — legacy cut-over) the Rust-native chain is the <b>exclusive</b> production
/// path: the legacy managed real-time processing has been removed, so <see cref="Enabled"/> always
/// reports <see langword="true"/> in production. The former opt-out surfaces (the
/// <c>Ownaudio.UseRustNativeChain</c> <see cref="AppContext"/> switch and the
/// <c>OWNAUDIO_RUST_NATIVE_CHAIN</c> environment variable) are no longer consulted. The only
/// remaining knob is <see cref="Override"/>, an internal test hook used during the legacy-removal
/// transition; it is not reachable by consumers, so the frozen A1 contract stays 0-diff.
/// </remarks>
internal static class RustNativeChain
{
    /// <summary>
    /// The resolved value when no <see cref="Override"/> is present. The Rust-native chain is the
    /// exclusive production path as of 4.0, so this is <see langword="true"/>.
    /// </summary>
    internal const bool DefaultEnabled = true;

    /// <summary>
    /// An in-process programmatic override that, when non-<see langword="null"/>, wins over the
    /// <see cref="DefaultEnabled"/> value.
    /// </summary>
    /// <remarks>
    /// This is an internal test hook retained only for the legacy-removal transition: tests that
    /// still exercise a removed managed path set it and restore <see langword="null"/> afterwards.
    /// It is not part of the public API surface, so the frozen A1 contract stays 0-diff. In
    /// production it is always <see langword="null"/>, so <see cref="Enabled"/> is always
    /// <see langword="true"/>.
    /// </remarks>
    internal static bool? Override { get; set; }

    /// <summary>
    /// Gets whether the Rust-native file-playback chain is enabled.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="Override"/> when a test has set it; otherwise
    /// <see cref="DefaultEnabled"/> (<see langword="true"/>). The former <see cref="AppContext"/>
    /// switch and environment-variable opt-outs are no longer consulted.
    /// </remarks>
    internal static bool Enabled => Override ?? DefaultEnabled;
}
