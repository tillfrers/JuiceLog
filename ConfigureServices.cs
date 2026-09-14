using JuiceLog.Abstractions;
using JuiceLog.BackgroundServices;
using JuiceLog.Common;
using JuiceLog.Common.Enums;
using JuiceLog.Options;
using JuiceLog.Persistence;
using JuiceLog.Recognition;
using JuiceLog.Recognition.TfLite;
using JuiceLog.Repositiories;
using JuiceLog.Services;
using Microsoft.EntityFrameworkCore;

namespace JuiceLog;

public static class ConfigureServices
{
    public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        
        services.AddScoped<CollectorJob>();
        
        services.Configure<AppConfiguration>(configuration);
        
        services.AddSingleton<ITimeProvider, SystemTimeProvider>();
        services.AddSingleton<IEnergyRepository, EnergyRepository>();

        services.AddCameraMeterReading(configuration);

        return services;
    }

    private static IServiceCollection AddCameraMeterReading(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ISnapshotService, FfmpegSnapshotService>();
        services.AddSingleton<IMeterReadingValidator, MeterReadingValidator>();
        services.AddSingleton<IMeterImageReader, MeterImageReader>();
        services.AddSingleton<DigitRecognizer>();

        // web page to draw the digit ROIs on a live snapshot
        services.AddSingleton<RoiRefiner>();
        services.AddSingleton<CameraSettingsWriter>();
        services.AddHostedService<CalibrationServer>();

        // The model is loaded once; all camera loggers share it.
        services.AddSingleton(_ =>
        {
            var appConfiguration = configuration.Get<AppConfiguration>() ?? new AppConfiguration();
            var camera = appConfiguration.LoggerConfiguration.FirstOrDefault(l => l.LoggerType == LoggerType.Camera)?.Camera
                         ?? new CameraOptions();

            var modelPath = Path.IsPathRooted(camera.ModelPath)
                ? camera.ModelPath
                : Path.Combine(AppContext.BaseDirectory, camera.ModelPath);

            return TfLiteModel.Load(modelPath);
        });

        return services;
    }
}
