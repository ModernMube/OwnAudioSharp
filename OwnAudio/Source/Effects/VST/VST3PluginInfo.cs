using System;

namespace OwnaudioNET.Effects.VST;

/// <summary>
/// Contains metadata information about a VST3 plugin.
/// </summary>
public sealed class VST3PluginInfo
{
    /// <summary>
    /// Gets the full path to the VST3 plugin file or bundle.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the plugin vendor/manufacturer name.
    /// </summary>
    public required string Vendor { get; init; }

    /// <summary>
    /// Gets the plugin version string, if available.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets whether this plugin is an audio effect.
    /// </summary>
    public required bool IsEffect { get; init; }

    /// <summary>
    /// Gets whether this plugin is an instrument (synth).
    /// </summary>
    public required bool IsInstrument { get; init; }

    /// <summary>
    /// Gets the number of parameters exposed by this plugin.
    /// </summary>
    public required int ParameterCount { get; init; }

    /// <summary>
    /// Gets a formatted display name combining plugin name and vendor.
    /// </summary>
    public string DisplayName => $"{Name} ({Vendor})";

    /// <summary>
    /// Returns a string representation of the plugin info.
    /// </summary>
    public override string ToString() => DisplayName;
}
