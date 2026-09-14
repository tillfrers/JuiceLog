using JuiceLog.Options;

namespace JuiceLog.Common;

public class AppConfiguration
{
    public List<LoggerConfigurationOptions> LoggerConfiguration { get; set; } = [];
    
    public FfmpegOptions Ffmpeg { get; set; } = new();
    
    public CalibrationOptions Calibration { get; set; } = new();
}
