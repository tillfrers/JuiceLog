using JuiceLog.Common.Enums;

namespace JuiceLog.Options;

public class CameraSetupOptions
{ 
    public CameraType CameraType { get; set; }
    public string Url { get; set; }
    public string User { get; set; }
    public string Password { get; set; }
    
    public Uri BuildUri => new Uri($"rtsp://{User}:{Password}@{Url}");
}