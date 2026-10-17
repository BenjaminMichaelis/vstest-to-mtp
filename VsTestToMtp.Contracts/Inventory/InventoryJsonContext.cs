using System.Text.Json.Serialization;

namespace VsTestToMtp.Inventory;

/// <summary>Source-generated serialization for the CLI to MSBuild host protocol, so it works under Native AOT.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
public sealed partial class InventoryJsonContext : JsonSerializerContext;
