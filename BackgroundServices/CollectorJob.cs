using System.Text;
using System.Text.Json;
using JuiceLog.Abstractions;
using JuiceLog.Common;
using JuiceLog.Common.Enums;
using JuiceLog.Contracts;
using JuiceLog.Entities;
using JuiceLog.Options;
using JuiceLog.Recognition;
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

    private const int MaxReadAttempts = 3;

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

        var last = await energyRepository.GetLastReadingAsync(LoggerType.Camera, logger.EnergyType);
        var tolerance = 2.5 * Math.Pow(10, -logger.Camera.DecimalDigits);
        var values = new List<double>();

        for (var attempt = 1; attempt <= MaxReadAttempts; attempt++)
        {
            var value = await ReadSnapshotAsync(logger, last, attempt, cancellationToken);
            if (value is null)
            {
                continue;
            }

            var agreed = values.Any(v => Math.Abs(v - value.Value) <= tolerance);
            values.Add(value.Value);
            if (agreed)
            {
                break; // two snapshots agree, no need for a third
            }
        }

        if (values.Count == 0)
        {
            if (last is null || !logger.Camera.RepeatLastValueWhenUnreadable)
            {
                Console.WriteLine($"Camera: no usable snapshot in {MaxReadAttempts} attempts, nothing stored {timeProvider.GetBerlinNow}");
                return;
            }

            await energyRepository.WriteEnergyValueToDbAsync(LoggerType.Camera, logger.EnergyType, last.Value, estimated: true);
            Console.WriteLine($"Camera: no usable snapshot in {MaxReadAttempts} attempts, last reading {last.Value} from {last.Date:HH:mm} stored again as estimate {timeProvider.GetBerlinNow}");
            return;
        }

        // the lowest plausible value: too low is caught up by the next reading, too high is stuck forever
        await energyRepository.WriteEnergyValueToDbAsync(LoggerType.Camera, logger.EnergyType, values.Min());

        Console.WriteLine($"Energy data written to DB {timeProvider.GetBerlinNow}");
    }

    private async Task<double?> ReadSnapshotAsync(LoggerConfigurationOptions logger, Energy? last, int attempt,
        CancellationToken cancellationToken)
    {
        double? value;
        string reason;
        try
        {
            var jpeg = await snapshotService.CaptureJpegAsync(logger, cancellationToken);

            var reading = meterImageReader.Read(jpeg, logger.Camera);
            Console.WriteLine($"Camera: raw [{reading.RawReadingsText}] confidence [{reading.ConfidencesText}] -> {reading.DigitsText} {timeProvider.GetBerlinNow}");

            value = meterReadingValidator.Evaluate(reading, last, timeProvider.GetBerlinNow, logger.Camera, out reason);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            value = null;
            reason = e.Message;
        }

        if (value is null)
        {
            Console.WriteLine($"Camera: snapshot {attempt}/{MaxReadAttempts} unusable: {reason} {timeProvider.GetBerlinNow}");
        }
        else if (!string.IsNullOrEmpty(reason))
        {
            Console.WriteLine($"Camera: {reason} {timeProvider.GetBerlinNow}");
        }

        return value;
    }
}
