using JuiceLog.Abstractions;
using JuiceLog.Common.Enums;
using JuiceLog.Entities;
using JuiceLog.Persistence;

namespace JuiceLog.Repositiories;

public class EnergyRepository(AppDbContext dbContext, ITimeProvider timeProvider) : IEnergyRepository
{
    public Task WriteEnergyValueToDbAsync(LoggerType loggerType, EnergyType energyType, double value)
    {
        var energy = new Energy
        {
            Id = Guid.NewGuid(),
            EnergyType = energyType,
            LoggerType = loggerType,
            Value = value,
            Date = timeProvider.GetBerlinNow
        };

        dbContext.Energy.Add(energy);
        dbContext.SaveChanges();
        
        return Task.CompletedTask;
    }
}