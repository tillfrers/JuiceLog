using System.Text.Json.Serialization;

namespace JuiceLog.Contracts;

public sealed record TasmotaExtension(
    [property: JsonPropertyName("StatusSNS")] StatusSns StatusSns);

public sealed record StatusSns(
    [property: JsonPropertyName("Time")] DateTime Time,
    [property: JsonPropertyName("GS303")] Gs303? Gs303);

public sealed record Gs303(
    [property: JsonPropertyName("Total_in")] decimal TotalIn);