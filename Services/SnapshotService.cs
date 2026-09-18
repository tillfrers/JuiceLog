using JuiceLog.Abstractions;
using JuiceLog.Options;

namespace JuiceLog.Services;

// picks the transport from the camera URL: http(s) fetches a still image, anything else is RTSP via ffmpeg
public sealed class SnapshotService(HttpSnapshotService http, FfmpegSnapshotService ffmpeg) : ISnapshotService
{
    public Task<byte[]> CaptureJpegAsync(LoggerConfigurationOptions camera, CancellationToken cancellationToken) =>
        camera.IsHttpCamera
            ? http.CaptureJpegAsync(camera, cancellationToken)
            : ffmpeg.CaptureJpegAsync(camera, cancellationToken);
}
