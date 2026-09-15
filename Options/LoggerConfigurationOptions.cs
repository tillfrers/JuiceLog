using JuiceLog.Common.Enums;

namespace JuiceLog.Options;

public class LoggerConfigurationOptions
{ 
    public LoggerType LoggerType { get; set; }
    
    public EnergyType EnergyType { get; set; }
    
    public string Url { get; set; } = string.Empty;
    
    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
    
    public CameraOptions Camera { get; set; } = new();
    
    public Uri BuildRtspUri => new Uri($"rtsp://{User}:{Password}@{Url}");
}
