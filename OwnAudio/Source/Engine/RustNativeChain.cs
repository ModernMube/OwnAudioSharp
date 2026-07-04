using System;

namespace OwnaudioNET.Engine;

/// <summary>
/// Internal gate that decides whether the file-playback DSP chain
/// (decode / tempo / pitch / volume / mix / effect) is routed through the native
/// Rust engine or the legacy managed path.
/// </summary>
/// <remarks>
/// As of 4.0 the Rust-native chain is the <b>default</b> (plan 14 / D.4): with nothing set,
/// <see cref="Enabled"/> is <see langword="true"/>. Consumers can opt back into the legacy managed
/// path by setting the <see cref="AppContext"/> switch (<see cref="AppContextSwitchName"/>) to
/// <see langword="false"/>, or the environment variable (<see cref="EnvironmentVariableName"/>) to a
/// falsy value (<c>0</c> / <c>false</c> / <c>off</c> / <c>no</c>). No public API surface is added, so
/// the frozen A1 contract stays 0-diff. The value is read at <c>FileSource</c> / <c>AudioMixer</c>
/// construction time, so toggling it affects only the instances created afterwards.
/// </remarks>
internal static class RustNativeChain
{
    /// <summary>
    /// The resolved value when neither an override, an <see cref="AppContext"/> switch, nor a
    /// recognized environment-variable value is present. As of 4.0 this is <see langword="true"/>
    /// (the Rust-native chain is the default).
    /// </summary>
    internal const bool DefaultEnabled = true;

    /// <summary>
    /// The <see cref="AppContext"/> switch name that enables the Rust-native chain when set to
    /// <see langword="true"/>.
    /// </summary>
    internal const string AppContextSwitchName = "Ownaudio.UseRustNativeChain";

    /// <summary>
    /// The environment variable consulted when neither <see cref="Override"/> nor the
    /// <see cref="AppContext"/> switch is set. A falsy value (<c>0</c> / <c>false</c> / <c>off</c> /
    /// <c>no</c>) opts back into the legacy managed path; any other (or unset) value keeps the
    /// Rust-native default.
    /// </summary>
    internal const string EnvironmentVariableName = "OWNAUDIO_RUST_NATIVE_CHAIN";

    /// <summary>
    /// An in-process programmatic override that, when non-<see langword="null"/>, wins over the
    /// <see cref="AppContext"/> switch and the environment variable.
    /// </summary>
    /// <remarks>
    /// This is the resettable counterpart of the <see cref="AppContext"/> switch (which cannot be
    /// unset once set): tests set it to force a mode and restore <see langword="null"/> afterwards.
    /// It is not part of the public API surface, so the frozen A1 contract stays 0-diff.
    /// </remarks>
    internal static bool? Override { get; set; }

    /// <summary>
    /// Gets whether the Rust-native file-playback chain is currently enabled.
    /// </summary>
    /// <remarks>
    /// Resolution order: <see cref="Override"/> wins when set; otherwise an explicitly set
    /// <see cref="AppContext"/> switch; otherwise the environment variable (truthy <c>1</c> /
    /// <c>true</c> / <c>on</c> / <c>yes</c> enables, falsy <c>0</c> / <c>false</c> / <c>off</c> /
    /// <c>no</c> disables, case-insensitive); otherwise <see cref="DefaultEnabled"/> (the
    /// Rust-native chain, as of 4.0).
    /// </remarks>
    internal static bool Enabled
    {
        get
        {
            if (Override is bool overridden)
                return overridden;

            if (AppContext.TryGetSwitch(AppContextSwitchName, out bool enabled))
                return enabled;

            string? env = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            return ResolveEnvironment(env);
        }
    }

    /// <summary>
    /// Interprets an environment-variable value as an enable/disable flag, falling back to
    /// <see cref="DefaultEnabled"/> when unset or unrecognized.
    /// </summary>
    /// <param name="value">The raw environment-variable value (may be <see langword="null"/>).</param>
    /// <returns>
    /// <see langword="true"/> for <c>1</c> / <c>true</c> / <c>on</c> / <c>yes</c>;
    /// <see langword="false"/> for <c>0</c> / <c>false</c> / <c>off</c> / <c>no</c>
    /// (case-insensitive, surrounding whitespace ignored); otherwise <see cref="DefaultEnabled"/>.
    /// </returns>
    private static bool ResolveEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultEnabled;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => DefaultEnabled
        };
    }
}
