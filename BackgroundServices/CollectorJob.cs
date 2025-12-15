using System.Text;
using System.Text.Json;
using JuiceLog.Abstractions;
using JuiceLog.Common;
using JuiceLog.Common.Enums;
using JuiceLog.Contracts;
using JuiceLog.Options;
using Microsoft.Extensions.Options;
using Quartz;

namespace JuiceLog.BackgroundServices;

public class CollectorJob(
    IOptions<AppConfiguration> configuration,
    IEnergyRepository energyRepository,
    ITimeProvider timeProvider) : IJob
{
    public static readonly JobKey JobKey = new("Collector", "BackgroundJob");

    public async Task Execute(IJobExecutionContext context)
    {
        Console.WriteLine($"Execute CollectorJob: {timeProvider.GetBerlinNow}");
        
        var loggerConfigurations = configuration.Value.LoggerConfiguration;

        foreach (var loggerConfiguration in loggerConfigurations)
        {
            if (loggerConfiguration.LoggerType == LoggerType.SmartMeter)
            {
                await ProcessSmartMeterAsync(loggerConfiguration);
            }
        }
    }

    private async Task ProcessSmartMeterAsync(LoggerConfigurationOptions logger)
    {
        if (string.IsNullOrEmpty(logger.Url))
        {
            Console.WriteLine($"Missing URL: {timeProvider.GetBerlinNow}");
            return;
        }
        
        using var httpClient = new HttpClient();
        var requestData = new CuculusMeterExtension.GetMeterReading("meter_reading", "123");
        
        var options = new JsonSerializerOptions { WriteIndented = true };
        var jsonPayload = JsonSerializer.Serialize(requestData, options);
        
        var content = new StringContent(
            jsonPayload,
            Encoding.UTF8,
            "application/json"
        );
        
        using var response = await httpClient.PostAsync(logger.Url, content);
        
        if (response.IsSuccessStatusCode)
        {
            var meterReadingResponse = JsonSerializer.Deserialize<CuculusMeterExtension.MeterReadingResponse>(
                await response.Content.ReadAsStringAsync());

            if (meterReadingResponse?.meter.FirstOrDefault()?.data == null) return;
            
            var data = meterReadingResponse.meter.FirstOrDefault()!.data.FirstOrDefault(d => d.OBIS == "1-0:1.8.0.255");

            if (data == null) return;

            await energyRepository.WriteEnergyValueToDbAsync(
                LoggerType.SmartMeter, 
                EnergyType.Electricity, 
                Convert.ToDouble(data.entry[0].val[..^3])
                );
            
            Console.WriteLine($"Energy data written to DB {timeProvider.GetBerlinNow}");
        }
        else
        {
            Console.WriteLine($"Http failed. Code: {response.StatusCode} {timeProvider.GetBerlinNow}");
        }
    }
    
    //ToDo: Logic snippets for camera implementation
    /*
     /*var cameras = configuration.Value.CameraSetups;

        foreach (var camera in cameras)
        {
            if (camera.Url is null) throw new Exception("Camera url is null");
            if (camera.User is null) throw new Exception("Camera user is null");
            if (camera.Password is null) throw new Exception("Camera password is null");

            await GetSnapshot(camera.BuildUri);
        }* /
        
        
        /* "CameraSetups": [
    {
      "CameraType": 0,
      "Url": "192.168.178.135:554/stream1",
      "User": "JuiceLogGas",
      "Password": "12345678"
    }
  ]* /
        
        
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
    }*/
}