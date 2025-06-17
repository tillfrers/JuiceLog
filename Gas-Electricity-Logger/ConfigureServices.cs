using System.Reflection;
using Gas_Electricity_Logger.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Gas_Electricity_Logger;

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
