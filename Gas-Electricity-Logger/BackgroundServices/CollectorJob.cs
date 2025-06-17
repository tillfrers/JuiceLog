using Quartz;

namespace Gas_Electricity_Logger.BackgroundServices;

public class CollectorJob : IJob
{
    public static readonly JobKey JobKey = new("Collector", "BackgroundJob");

    public async Task Execute(IJobExecutionContext context)
    {

        Console.WriteLine("Test");
        
    }
}