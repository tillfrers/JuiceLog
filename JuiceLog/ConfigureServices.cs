using JuiceLog.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JuiceLog;

public static class ConfigureServices
{
    public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
    {
        /*services.AddDbContext<A1DbContext>((serviceProvider, options) =>
        {
            var secretConnectionStringService = serviceProvider.GetRequiredService<ISecretConnectionStringService>();

            options.UseNpgsql(
                secretConnectionStringService.GetConnectionString(),
                o => o.MigrationsHistoryTable(
                    tableName: HistoryRepository.DefaultTableName,
                    schema: A1DbContext.Schema));
        });

        services.AddScoped<IMeldedateiRepository, MeldedateiRepository>();
*/
        services.AddScoped<CollectorJob>();

        return services;
    }
}
