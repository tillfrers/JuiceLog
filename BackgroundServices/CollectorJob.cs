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

    // a rejected reading is retried with a fresh snapshot; only when every attempt is rejected the leading digits
    // that cannot have changed since the last stored value are corrected (a blurry 4 read as a 7)
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

        var last = await energyRepository.GetLastEnergyValueAsync(LoggerType.Camera, logger.EnergyType);
        // the least significant drum may jitter by a unit or two while the meter stands still
        var tolerance = 2.5 * Math.Pow(10, -logger.Camera.DecimalDigits);
        var rejected = new List<MeterImageReading>();

        for (var attempt = 1; attempt <= MaxReadAttempts; attempt++)
        {
            var jpeg = await snapshotService.CaptureJpegAsync(logger.BuildRtspUri, cancellationToken);

            var reading = meterImageReader.Read(jpeg, logger.Camera);
            Console.WriteLine($"Camera: raw [{reading.RawReadingsText}] confidence [{reading.ConfidencesText}] -> {reading.DigitsText} {timeProvider.GetBerlinNow}");

            if (reading.Value is not { } value)
            {
                Console.WriteLine($"Camera: snapshot skipped: {reading.Problem} {timeProvider.GetBerlinNow}");
                return;
            }

            var verdict = meterReadingValidator.Validate(value, last, timeProvider.GetBerlinNow,
                logger.Camera.MaxIncreasePerHour, tolerance, out var reason);

            if (verdict == ReadingVerdict.Reject)
            {
                Console.WriteLine($"Camera: reading {value} rejected ({attempt}/{MaxReadAttempts}): {reason} {timeProvider.GetBerlinNow}");
                rejected.Add(reading);
                continue;
            }

            if (!string.IsNullOrEmpty(reason))
            {
                Console.WriteLine($"Camera: {reason} {timeProvider.GetBerlinNow}");
            }

            if (verdict == ReadingVerdict.UseLastValue)
            {
                value = last!.Value; // a meter never runs backwards, the drum only jittered
            }

            await energyRepository.WriteEnergyValueToDbAsync(LoggerType.Camera, logger.EnergyType, value);

            Console.WriteLine($"Energy data written to DB {timeProvider.GetBerlinNow}");
            return;
        }

        if (last is not null && await TryStoreCorrectedAsync(logger, rejected, last, tolerance))
        {
            return;
        }

        Console.WriteLine($"Camera: {MaxReadAttempts} readings in a row rejected, nothing stored {timeProvider.GetBerlinNow}");
    }

    /// <summary>
    /// Every attempt was rejected: if the readings only differ from the stored value in leading drums that cannot
    /// have turned in the meantime, those drums were misread and the known digits are used instead.
    /// </summary>
    private async Task<bool> TryStoreCorrectedAsync(LoggerConfigurationOptions logger, List<MeterImageReading> rejected,
        Energy last, double tolerance)
    {
        foreach (var reading in rejected)
        {
            var digits = meterReadingValidator.CorrectLeadingDigits(reading.Digits!, logger.Camera.DecimalDigits, last,
                timeProvider.GetBerlinNow, logger.Camera.MaxIncreasePerHour, tolerance, out var correction);
            if (digits is null)
            {
                continue;
            }

            var value = RollingDigitEvaluator.ToValue(digits, logger.Camera.DecimalDigits);
            var verdict = meterReadingValidator.Validate(value, last, timeProvider.GetBerlinNow,
                logger.Camera.MaxIncreasePerHour, tolerance, out var reason);

            if (verdict == ReadingVerdict.Reject)
            {
                Console.WriteLine($"Camera: corrected reading {value} rejected as well: {reason} {timeProvider.GetBerlinNow}");
                continue;
            }

            Console.WriteLine($"Camera: {reading.DigitsText} corrected to {string.Concat(digits)} - {correction} {timeProvider.GetBerlinNow}");
            if (!string.IsNullOrEmpty(reason))
            {
                Console.WriteLine($"Camera: {reason} {timeProvider.GetBerlinNow}");
            }

            if (verdict == ReadingVerdict.UseLastValue)
            {
                value = last.Value;
            }

            await energyRepository.WriteEnergyValueToDbAsync(LoggerType.Camera, logger.EnergyType, value);

            Console.WriteLine($"Energy data written to DB {timeProvider.GetBerlinNow}");
            return true;
        }

        return false;
    }
}
