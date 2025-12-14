using JuiceLog.Abstractions;
using JuiceLog.BackgroundServices;
using JuiceLog.Common;
using JuiceLog.Persistence;
using JuiceLog.Repositiories;
using Microsoft.EntityFrameworkCore;

namespace JuiceLog;

public static class ConfigureServices
{
    public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        
        services.AddScoped<CollectorJob>();
        
        services.Configure<AppConfiguration>(configuration);
        
        services.AddSingleton<ITimeProvider, SystemTimeProvider>();
        services.AddSingleton<IEnergyRepository, EnergyRepository>();

        return services;
    }
}
