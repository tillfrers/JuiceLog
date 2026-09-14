namespace JuiceLog.Abstractions;

public interface ISnapshotService
{
    /// <summary>Grabs a single frame from the given RTSP stream and returns it JPEG encoded.</summary>
    Task<byte[]> CaptureJpegAsync(Uri rtspUri, CancellationToken cancellationToken);
}
