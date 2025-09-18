using JuiceLog.BackgroundServices;
using Quartz;

namespace JuiceLog;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();
        await host.RunAsync();
    }

    private static IHostBuilder CreateHostBuilder(string[] args) => Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((_, config) =>
        {
            config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        })
        .ConfigureServices((hostContext, services) =>
        {
            //services.AddServices(hostContext.Configuration);

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
                        .WithIntervalInSeconds(interval)
                        .RepeatForever()));
            });

            services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
        });
}