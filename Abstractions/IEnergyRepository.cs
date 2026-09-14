using JuiceLog.Common.Enums;
using JuiceLog.Entities;

namespace JuiceLog.Abstractions;

public interface IEnergyRepository
{
    public Task WriteEnergyValueToDbAsync(LoggerType loggerType, EnergyType energyType, double value);
    
    public Task<Energy?> GetLastEnergyValueAsync(LoggerType loggerType, EnergyType energyType);
}
