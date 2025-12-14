using JuiceLog.Common.Enums;

namespace JuiceLog.Entities;

public class Energy
{
    public required Guid Id { get; set; }
    public required EnergyType EnergyType { get; set; }
    public required LoggerType LoggerType { get; set; }
    public required double Value { get; set; }
    public required DateTimeOffset Date { get; set; }
}