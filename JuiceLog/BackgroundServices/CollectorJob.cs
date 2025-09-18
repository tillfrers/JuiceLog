using FFMpegCore;
using Quartz;
namespace JuiceLog.BackgroundServices;

public class CollectorJob : IJob
{
    public static readonly JobKey JobKey = new("Collector", "BackgroundJob");

    public async Task Execute(IJobExecutionContext context)
    {
        string userName = "JuiceLogGas";
        string password = "12345678";

        var snapshotUri = new Uri("rtsp://" + userName + ":" + password + "@192.168.178.135:554/stream1");
        string outputFileName = "C:\\Users\\tillf\\Downloads\\" + DateTime.UtcNow.ToString("yyy-MM-dd-hh-mm-ss") + ".jpeg";
        
        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = @"C:\Users\tillf\Downloads" });
        FFMpegArguments
            .FromUrlInput(snapshotUri)
            .OutputToFile(outputFileName, false, options => options
                .WithFrameOutputCount(1)
                )
            .ProcessSynchronously();

        Console.WriteLine($"Snapshot captured and saved to: {outputFileName}");
    }
}