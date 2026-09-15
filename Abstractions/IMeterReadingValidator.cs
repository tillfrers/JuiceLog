using JuiceLog.Common.Enums;
using JuiceLog.Entities;

namespace JuiceLog.Abstractions;

public enum ReadingVerdict
{
    Accept,
    
    UseLastValue,
    
    Reject,
}

public interface IMeterReadingValidator
{
    ReadingVerdict Validate(EnergyType energyType, double value, Energy? last, DateTime now,
        double maxIncreasePerHour, double tolerance, out string reason);
}
