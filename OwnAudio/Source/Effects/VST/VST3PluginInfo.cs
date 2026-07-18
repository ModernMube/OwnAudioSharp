using System;

namespace OwnaudioNET.Effects.VST;

/// <summary>
/// What we know about a VST3 plugin without hosting it.
/// </summary>
public sealed class VST3PluginInfo
{
    /// <summary>
    /// Full path of the .vst3 file or bundle.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Plugin name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Vendor / manufacturer.
    /// </summary>
    public required string Vendor { get; init; }

    /// <summary>
    /// Version string, when the plugin bothers to report one.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// True for audio effects.
    /// </summary>
    public required bool IsEffect { get; init; }

    /// <summary>
    /// True for instruments / synths.
    /// </summary>
    public required bool IsInstrument { get; init; }

    /// <summary>
    /// How many params the plugin exposes.
    /// </summary>
    public required int ParameterCount { get; init; }

    /// <summary>
    /// Name + vendor, ready for a combo box.
    /// </summary>
    public string DisplayName => $"{Name} ({Vendor})";

    /// <summary>
    /// Same as DisplayName.
    /// </summary>
    public override string ToString() => DisplayName;
}
