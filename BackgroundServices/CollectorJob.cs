using System;
using System.Threading.Tasks;
using FFMpegCore;
using JuiceLog.Common;
using JuiceLog.Common.Enums;
using Microsoft.Extensions.Options;
using Quartz;
namespace JuiceLog.BackgroundServices;

public class CollectorJob(IOptions<AppConfiguration> configuration) : IJob
{
    public static readonly JobKey JobKey = new("Collector", "BackgroundJob");

    public async Task Execute(IJobExecutionContext context)
    {
        var cameras = configuration.Value.CameraSetups;

        foreach (var camera in cameras)
        {
            if (camera.Url is null) throw new Exception("Camera url is null");
            if (camera.User is null) throw new Exception("Camera user is null");
            if (camera.Password is null) throw new Exception("Camera password is null");

            await GetSnapshot(camera.BuildUri);
        }
        
        
       
    }

    private Task GetSnapshot(Uri uri)
    {
        string outputFileName = "C:\\Users\\tillf\\Downloads\\" + DateTime.UtcNow.ToString("yyy-MM-dd-hh-mm-ss") + ".jpeg";

        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = @"C:\Users\tillf\Downloads" });
        FFMpegArguments
            .FromUrlInput(uri)
            .OutputToFile(outputFileName, false, options => options
                .WithFrameOutputCount(1)
                )
            .ProcessSynchronously();

        Console.WriteLine($"Snapshot captured and saved to: {outputFileName}");
        
        return Task.CompletedTask;
    }
}