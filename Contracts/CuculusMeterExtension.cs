using System.Text.Json.Serialization;

namespace JuiceLog.Contracts;

public static class CuculusMeterExtension
{
    public record GetMeterReading(
        [property: JsonPropertyName("cmd")] string Cmd,
        [property: JsonPropertyName("id")] string Id);
    
    public record MeterEntry(
        string val,
        string ts
    );
    
    public record MeterData(
        string OBIS,
        string scale,
        string unit,
        List<MeterEntry> entry
    );
    
    public record Meter(
        string meterid,
        List<MeterData> data
    );
    
    public record MeterReadingResponse(
        List<Meter> meter,
        string result,
        string id
    );
}
