using JuiceLog.BackgroundServices;
using JuiceLog.Persistence;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace JuiceLog;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
        }
        
        await host.RunAsync();
    }

    private static IHostBuilder CreateHostBuilder(string[] args) => Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((_, config) =>
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? "Production";

            config
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();
        })
        .ConfigureServices((hostContext, services) =>
        {
            services.AddServices(hostContext.Configuration);

            services.AddQuartz(q =>
            {
                var jobKey = new JobKey("CollectorJob");
                var interval = hostContext.Configuration.GetValue<int>("Intervals:Collector");

                q.AddJob<CollectorJob>(opts => opts.WithIdentity(jobKey));

                q.AddTrigger(trigger => trigger
                    .ForJob(jobKey)
                    .WithIdentity("CollectorJob-Trigger")
                    .StartAt(DateTime.UtcNow.Add(TimeSpan.FromSeconds(1)))
                    .WithSimpleSchedule(schedule => schedule
                        .WithIntervalInMinutes(interval)
                        .RepeatForever()));
            });

            services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
        });
}