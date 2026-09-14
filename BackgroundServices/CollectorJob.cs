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
    IOptionsSnapshot<AppConfiguration> configuration,
    IEnergyRepository energyRepository,
    ITimeProvider timeProvider,
    ISnapshotService snapshotService,
    IMeterImageReader meterImageReader,
    IMeterReadingValidator meterReadingValidator) : IJob
{
    public static readonly JobKey JobKey = new("Collector", "BackgroundJob");

    public async Task Execute(IJobExecutionContext context)
    {
        Console.WriteLine($"Execute CollectorJob: {timeProvider.GetBerlinNow}");
        
        var loggerConfigurations = configuration.Value.LoggerConfiguration;

        foreach (var loggerConfiguration in loggerConfigurations)
        {
            try
            {
                switch (loggerConfiguration.LoggerType)
                {
                    case LoggerType.SmartMeter :
                        await ProcessSmartMeterAsync(loggerConfiguration);
                        break;
                    case LoggerType.Camera :
                        await ProcessCameraAsync(loggerConfiguration, context.CancellationToken);
                        break;
                    default: throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // one failing logger must not prevent the others from being collected
                Console.WriteLine($"{loggerConfiguration.LoggerType} logger failed: {e.Message} {timeProvider.GetBerlinNow}");
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

    private async Task ProcessCameraAsync(LoggerConfigurationOptions logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(logger.Url)) throw new Exception("Camera url is null");
        if (string.IsNullOrEmpty(logger.User)) throw new Exception("Camera user is null");
        if (string.IsNullOrEmpty(logger.Password)) throw new Exception("Camera password is null");

        if (logger.Camera.DigitRois.Count == 0)
        {
            Console.WriteLine($"Camera: no digit positions configured - open http://<host>:{configuration.Value.Calibration.Port}/ to draw them {timeProvider.GetBerlinNow}");
            return;
        }

        var jpeg = await snapshotService.CaptureJpegAsync(logger.BuildRtspUri, cancellationToken);

        var reading = meterImageReader.Read(jpeg, logger.Camera);
        Console.WriteLine($"Camera: raw [{reading.RawReadingsText}] confidence [{reading.ConfidencesText}] -> {reading.DigitsText} {timeProvider.GetBerlinNow}");

        if (reading.Value is not { } value)
        {
            Console.WriteLine($"Camera: snapshot skipped: {reading.Problem} {timeProvider.GetBerlinNow}");
            return;
        }

        var last = await energyRepository.GetLastEnergyValueAsync(LoggerType.Camera, logger.EnergyType);
        // the least significant drum may jitter by one unit while the meter stands still
        var tolerance = 1.5 * Math.Pow(10, -logger.Camera.DecimalDigits);
        var verdict = meterReadingValidator.Validate(logger.EnergyType, value, last, timeProvider.GetBerlinNow,
            logger.Camera.MaxIncreasePerHour, tolerance, out var reason);

        if (verdict != ReadingVerdict.Accept)
        {
            Console.WriteLine($"Camera: reading {value} {(verdict == ReadingVerdict.Reject ? "rejected" : "skipped")}: {reason} {timeProvider.GetBerlinNow}");
            return;
        }

        if (!string.IsNullOrEmpty(reason))
        {
            Console.WriteLine($"Camera: {reason} {timeProvider.GetBerlinNow}");
        }

        await energyRepository.WriteEnergyValueToDbAsync(LoggerType.Camera, logger.EnergyType, value);

        Console.WriteLine($"Energy data written to DB {timeProvider.GetBerlinNow}");
    }
}
