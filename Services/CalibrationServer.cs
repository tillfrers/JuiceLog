using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JuiceLog.Abstractions;
using JuiceLog.Common;
using JuiceLog.Common.Enums;
using JuiceLog.Options;
using JuiceLog.Recognition;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JuiceLog.Services;

/// <summary>
/// Tiny web page (http://&lt;host&gt;:&lt;port&gt;/) that shows a live snapshot of the camera and lets the user draw
/// a box around every digit drum. The boxes can be tested against the recognizer and are saved as
/// <see cref="CameraOptions.DigitRois"/>. Runs on plain <see cref="HttpListener"/>, so it works headless on the Pi.
/// </summary>
public sealed class CalibrationServer(
    IOptionsMonitor<AppConfiguration> configuration,
    ISnapshotService snapshotService,
    IMeterImageReader meterImageReader,
    DigitRecognizer digitRecognizer,
    RoiRefiner roiRefiner,
    CameraSettingsWriter settingsWriter,
    ILogger<CalibrationServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private byte[]? _lastSnapshot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.CurrentValue.Calibration;
        if (!options.Enabled || FindCamera() is null)
        {
            return;
        }

        var listener = StartListener(options.Port);
        if (listener is null)
        {
            return;
        }

        await using var stopRegistration = stoppingToken.Register(listener.Stop);

        if (options.OpenBrowser && FindCamera() is { logger.Camera.DigitRois.Count: 0 })
        {
            TryOpenBrowser($"http://localhost:{options.Port}/");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException e)
            {
                logger.LogWarning("Calibration server: {Message}", e.Message);
                continue;
            }

            _ = HandleSafeAsync(context, stoppingToken);
        }
    }

    private HttpListener? StartListener(int port)
    {
        // "*" makes the page reachable from other devices in the LAN. On Windows this needs an URL ACL
        // (or admin rights) - fall back to localhost there so that the logger itself keeps running.
        foreach (var host in new[] { "*", "localhost" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            try
            {
                listener.Start();
                logger.LogInformation("Calibration page available at http://{Host}:{Port}/", host == "*" ? "<host>" : host, port);
                if (host == "localhost")
                {
                    logger.LogInformation("To reach the page from other devices on Windows run once as admin: netsh http add urlacl url=http://*:{Port}/ user=Everyone", port);
                }

                return listener;
            }
            catch (HttpListenerException e)
            {
                listener.Close();
                logger.LogWarning("Calibration page could not listen on http://{Host}:{Port}/: {Message}", host, port, e.Message);
            }
        }

        return null;
    }

    /// <summary>Opens the page in the default browser - only where a desktop session exists.</summary>
    private void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                     !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                Process.Start("xdg-open", url);
            }
            else
            {
                logger.LogInformation("No desktop session, open {Url} manually to draw the digit positions", url);
                return;
            }

            logger.LogInformation("Opened {Url} in the browser to draw the digit positions", url);
        }
        catch (Exception e)
        {
            logger.LogWarning("Could not open the browser for {Url}: {Message}", url, e.Message);
        }
    }

    private async Task HandleSafeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(context, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Calibration request {Method} {Path} failed", context.Request.HttpMethod, context.Request.Url?.AbsolutePath);
            try
            {
                await WriteJsonAsync(context.Response, new { error = e.Message }, HttpStatusCode.InternalServerError);
            }
            catch
            {
                // response already gone
            }
        }
        finally
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";

        switch (request.HttpMethod, path)
        {
            case ("GET", "/"):
                await WriteBytesAsync(response, LoadPage(), "text/html; charset=utf-8");
                return;

            case ("GET", "/api/config"):
            {
                var (_, camera) = RequireCamera();
                await WriteJsonAsync(response, new
                {
                    url = camera.Url,
                    modelPath = camera.Camera.ModelPath,
                    decimalDigits = camera.Camera.DecimalDigits,
                    digitRois = camera.Camera.DigitRois,
                });
                return;
            }

            case ("GET", "/api/snapshot"):
            {
                var fresh = request.QueryString["fresh"] is not null;
                var jpeg = await GetSnapshotAsync(fresh, cancellationToken);
                response.Headers["Cache-Control"] = "no-store";
                await WriteBytesAsync(response, jpeg, "image/jpeg");
                return;
            }

            case ("POST", "/api/test"):
            {
                var body = await ReadJsonAsync<RoiRequest>(request);
                var (_, camera) = RequireCamera();
                var jpeg = await GetSnapshotAsync(false, cancellationToken);

                var options = new CameraOptions
                {
                    ModelPath = camera.Camera.ModelPath,
                    AutoContrast = camera.Camera.AutoContrast,
                    MinConfidence = camera.Camera.MinConfidence,
                    MaxIncreasePerHour = camera.Camera.MaxIncreasePerHour,
                    DecimalDigits = body.DecimalDigits,
                    DigitRois = body.DigitRois,
                };

                var reading = meterImageReader.Read(jpeg, options);
                var crops = RenderCrops(jpeg, body.DigitRois, camera.Camera.AutoContrast);

                await WriteJsonAsync(response, new
                {
                    value = reading.Value,
                    digits = reading.DigitsText,
                    problem = reading.Problem,
                    readings = reading.DigitReadings.Select((r, i) => new { value = r.Value, confidence = r.Confidence, crop = crops[i] }),
                });
                return;
            }

            case ("POST", "/api/refine"):
            {
                var body = await ReadJsonAsync<RoiRequest>(request);
                var (_, camera) = RequireCamera();
                var jpeg = await GetSnapshotAsync(false, cancellationToken);

                using var frame = Image.Load<Rgb24>(jpeg);
                var refined = roiRefiner.Refine(frame, body.DigitRois, camera.Camera.AutoContrast);

                await WriteJsonAsync(response, new { digitRois = refined });
                return;
            }

            case ("POST", "/api/rois"):
            {
                var body = await ReadJsonAsync<RoiRequest>(request);
                var (loggerIndex, _) = RequireCamera();

                var jpeg = await GetSnapshotAsync(false, cancellationToken);
                var frameSize = Image.Identify(jpeg).Size;
                foreach (var roi in body.DigitRois)
                {
                    var rect = new Rectangle(roi.X, roi.Y, roi.Width, roi.Height);
                    if (rect.Width < 4 || rect.Height < 4 || !new Rectangle(Point.Empty, frameSize).Contains(rect))
                    {
                        throw new InvalidOperationException($"ROI {rect} lies outside the frame {frameSize.Width}x{frameSize.Height}.");
                    }
                }

                var files = settingsWriter.Save(loggerIndex, body.DigitRois, body.DecimalDigits);
                logger.LogInformation("Saved {Count} digit ROIs to {Files}", body.DigitRois.Count, string.Join(", ", files));
                await WriteJsonAsync(response, new { files, count = body.DigitRois.Count });
                return;
            }

            default:
                await WriteJsonAsync(response, new { error = "not found" }, HttpStatusCode.NotFound);
                return;
        }
    }

    private async Task<byte[]> GetSnapshotAsync(bool fresh, CancellationToken cancellationToken)
    {
        await _snapshotLock.WaitAsync(cancellationToken);
        try
        {
            if (fresh || _lastSnapshot is null)
            {
                var (_, camera) = RequireCamera();
                _lastSnapshot = await snapshotService.CaptureJpegAsync(camera.BuildRtspUri, cancellationToken);
            }

            return _lastSnapshot;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    /// <summary>The 20x32 crops as data URLs, so the page can show what the network actually sees.</summary>
    private string[] RenderCrops(byte[] jpeg, IReadOnlyList<DigitRoi> rois, bool autoContrast)
    {
        using var frame = Image.Load<Rgb24>(jpeg);
        var result = new string[rois.Count];
        for (var i = 0; i < rois.Count; i++)
        {
            var roi = rois[i];
            using var crop = digitRecognizer.PrepareCrop(frame, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height), autoContrast);
            using var stream = new MemoryStream();
            crop.SaveAsPng(stream);
            result[i] = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }

        return result;
    }

    private (int loggerIndex, LoggerConfigurationOptions logger)? FindCamera()
    {
        var loggers = configuration.CurrentValue.LoggerConfiguration;
        for (var i = 0; i < loggers.Count; i++)
        {
            if (loggers[i].LoggerType == LoggerType.Camera)
            {
                return (i, loggers[i]);
            }
        }

        return null;
    }

    private (int loggerIndex, LoggerConfigurationOptions logger) RequireCamera() =>
        FindCamera() ?? throw new InvalidOperationException("No camera logger configured.");

    private static byte[] LoadPage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("calibration.html", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task<T> ReadJsonAsync<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("Empty request body.");
    }

    private static Task WriteJsonAsync(HttpListenerResponse response, object payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode = (int)status;
        return WriteBytesAsync(response, JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions), "application/json; charset=utf-8");
    }

    private static async Task WriteBytesAsync(HttpListenerResponse response, byte[] bytes, string contentType)
    {
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private sealed class RoiRequest
    {
        public List<DigitRoi> DigitRois { get; set; } = [];
        public int DecimalDigits { get; set; }
    }
}
