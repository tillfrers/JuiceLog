using JuiceLog.Common.Enums;

namespace JuiceLog.Options;

public class LoggerConfigurationOptions
{ 
    public LoggerType LoggerType { get; set; }
    
    public EnergyType EnergyType { get; set; }
    
    // camera: an http(s) URL that returns a JPEG (IP Webcam /photo.jpg, ESP32-CAM /capture) or host[:port][/path]
    // of an RTSP stream that ffmpeg grabs a frame from
    public string Url { get; set; } = string.Empty;
    
    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    // accept the self-signed certificate of an https camera (IP Webcam brings its own)
    public bool AllowUntrustedCertificate { get; set; }
    
    public CameraOptions Camera { get; set; } = new();

    public bool IsHttpCamera =>
        Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    
    public Uri BuildRtspUri => new Uri($"rtsp://{User}:{Password}@{Url}");
}
