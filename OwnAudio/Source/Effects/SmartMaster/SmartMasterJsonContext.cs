using System.Text.Json.Serialization;

namespace OwnaudioNET.Effects.SmartMaster;

/// <summary>
/// Source-generated JSON serialization context for SmartMaster types.
/// Eliminates reflection-based serialization, making the SmartMaster preset
/// system compatible with Native AOT and trimming.
/// </summary>
/// <remarks>
/// The preset files on disk use camelCase property names (e.g. <c>graphicEQGains</c>), matching the
/// custom-preset convention used by the sample applications. The naming policy here must therefore be
/// camelCase, and case-insensitive matching is enabled so any legacy PascalCase preset still loads —
/// without it, a mismatched key silently deserializes to the type default, which would flatten every
/// EQ band and disable each stage, making a loaded preset inaudible.
/// </remarks>
[JsonSerializable(typeof(SmartMasterConfig))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
internal partial class SmartMasterRustNextJsonContext : JsonSerializerContext
{
}
