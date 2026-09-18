using FFMpegCore;
using FFMpegCore.Pipes;
using JuiceLog.Abstractions;
using JuiceLog.Common;
using Microsoft.Extensions.Options;

namespace JuiceLog.Services;

public sealed class FfmpegSnapshotService : ISnapshotService
{
    private readonly TimeSpan _timeout;

    public FfmpegSnapshotService(IOptions<AppConfiguration> configuration)
    {
        var options = configuration.Value.Ffmpeg;
        _timeout = TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, 1));

        var binaryFolder = options.BinaryFolder;
        if (string.IsNullOrWhiteSpace(binaryFolder) && FfmpegExistsIn(AppContext.BaseDirectory))
        {
            // An ffmpeg next to the application wins over PATH. Windows only searches the application folder
            // when the apphost (JuiceLog.exe) is used, not for "dotnet JuiceLog.dll", so resolve it explicitly.
            binaryFolder = AppContext.BaseDirectory;
        }

        if (!string.IsNullOrWhiteSpace(binaryFolder))
        {
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = binaryFolder });
        }
    }

    private static bool FfmpegExistsIn(string folder) =>
        File.Exists(Path.Combine(folder, "ffmpeg.exe")) || File.Exists(Path.Combine(folder, "ffmpeg"));

    public async Task<byte[]> CaptureJpegAsync(Uri rtspUri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        await using var buffer = new MemoryStream();

        try
        {
            await FFMpegArguments
                .FromUrlInput(rtspUri, input => input.WithCustomArgument("-rtsp_transport tcp"))
                .OutputToPipe(new StreamPipeSink(buffer), output => output
                    .WithFrameOutputCount(1)
                    .WithVideoCodec("mjpeg")
                    .WithCustomArgument("-q:v 2")
                    .ForceFormat("image2pipe"))
                .CancellableThrough(timeout.Token)
                .ProcessAsynchronously();
        }
        catch (Exception e) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // FFMpegCore reports our own timeout as an OperationCanceledException, which the collector treats as
            // "shutting down". A stalled stream is just one failed attempt, so surface it as an ordinary error.
            throw new TimeoutException($"ffmpeg delivered no frame from {rtspUri.Host} within {_timeout.TotalSeconds:0}s.", e);
        }

        if (buffer.Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg returned no frame from {rtspUri.Host}.");
        }

        return buffer.ToArray();
    }
}
