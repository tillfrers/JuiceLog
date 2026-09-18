using JuiceLog.Options;

namespace JuiceLog.Abstractions;

public interface ISnapshotService
{
    Task<byte[]> CaptureJpegAsync(LoggerConfigurationOptions camera, CancellationToken cancellationToken);
}
