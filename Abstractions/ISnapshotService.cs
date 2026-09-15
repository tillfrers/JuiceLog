namespace JuiceLog.Abstractions;

public interface ISnapshotService
{
    Task<byte[]> CaptureJpegAsync(Uri rtspUri, CancellationToken cancellationToken);
}
