using JuiceLog.Common.Enums;
using JuiceLog.Entities;

namespace JuiceLog.Abstractions;

public interface IEnergyRepository
{
    public Task WriteEnergyValueToDbAsync(LoggerType loggerType, EnergyType energyType, double value, bool estimated = false);
    
    public Task<Energy?> GetLastReadingAsync(LoggerType loggerType, EnergyType energyType);
}
