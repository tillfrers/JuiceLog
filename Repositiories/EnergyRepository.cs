using JuiceLog.Abstractions;
using JuiceLog.Common.Enums;
using JuiceLog.Entities;
using JuiceLog.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JuiceLog.Repositiories;

public class EnergyRepository(AppDbContext dbContext, ITimeProvider timeProvider) : IEnergyRepository
{
    public Task WriteEnergyValueToDbAsync(LoggerType loggerType, EnergyType energyType, double value, bool estimated = false)
    {
        var energy = new Energy
        {
            Id = Guid.NewGuid(),
            EnergyType = energyType,
            LoggerType = loggerType,
            Value = value,
            Date = timeProvider.GetBerlinNow,
            Estimated = estimated,
        };

        dbContext.Energy.Add(energy);
        dbContext.SaveChanges();
        
        return Task.CompletedTask;
    }

    public Task<Energy?> GetLastReadingAsync(LoggerType loggerType, EnergyType energyType)
    {
        return dbContext.Energy
            .AsNoTracking()
            .Where(e => e.LoggerType == loggerType && e.EnergyType == energyType && !e.Estimated)
            .OrderByDescending(e => e.Date)
            .FirstOrDefaultAsync();
    }
}
