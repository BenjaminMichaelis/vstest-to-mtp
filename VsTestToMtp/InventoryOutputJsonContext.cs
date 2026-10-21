using System.Text.Json.Serialization;

using VsTestToMtp.Inventory;

namespace VsTestToMtp;

/// <summary>Source-generated serialization of the <c>inventory</c> command's output, so it works under Native AOT.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InventoryResult))]
internal sealed partial class InventoryOutputJsonContext : JsonSerializerContext;
