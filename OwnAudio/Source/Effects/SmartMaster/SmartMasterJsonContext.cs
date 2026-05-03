using System.Text.Json.Serialization;

namespace OwnaudioNET.Effects.SmartMaster;

/// <summary>
/// Source-generated JSON serialization context for SmartMaster types.
/// Eliminates reflection-based serialization, making the SmartMaster preset
/// system compatible with Native AOT and trimming.
/// </summary>
[JsonSerializable(typeof(SmartMasterConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SmartMasterJsonContext : JsonSerializerContext
{
}
