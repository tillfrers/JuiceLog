namespace JuiceLog.Options;

public class FfmpegOptions
{
    /// <summary>Folder containing the ffmpeg binary. Empty = resolve "ffmpeg" via PATH / application directory.</summary>
    public string BinaryFolder { get; set; } = string.Empty;

    /// <summary>Maximum time to wait for a snapshot from the RTSP stream.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
