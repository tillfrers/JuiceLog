using JuiceLog.Common.Enums;

namespace JuiceLog.Abstractions;

public interface IEnergyRepository
{
    public Task WriteEnergyValueToDbAsync(LoggerType loggerType, EnergyType energyType, double value);
}