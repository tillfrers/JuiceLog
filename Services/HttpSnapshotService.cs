using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using JuiceLog.Abstractions;
using JuiceLog.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;

namespace JuiceLog.Services;

// Fetches one JPEG from a still-image URL: IP Webcam (/photo.jpg, /photoaf.jpg, /shot.jpg), an ESP32-CAM (/capture)
// or any IP camera with a snapshot endpoint. One request per reading - no stream to keep alive and no session the
// camera could lose, which is what made the RTSP path of the phone apps flaky.
public sealed class HttpSnapshotService : ISnapshotService, IDisposable
{
    // Generous for a phone that still has to focus and take the photo; a stalled request is retried by the collector.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client = CreateClient(trustAnyCertificate: false);
    private readonly HttpClient _trustingClient = CreateClient(trustAnyCertificate: true);

    public async Task<byte[]> CaptureJpegAsync(LoggerConfigurationOptions camera, CancellationToken cancellationToken)
    {
        var uri = new Uri(camera.Url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(camera.User))
        {
            // IP Webcam and the ESP32 examples use basic auth; sending it right away saves the 401 round trip
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{camera.User}:{camera.Password}")));
        }

        var client = camera.AllowUntrustedCertificate ? _trustingClient : _client;
        byte[] picture;
        string? contentType;
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException($"{uri.Host} rejected the login (401) - check User and Password.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"{uri.Host} answered {(int)response.StatusCode} {response.ReasonPhrase} for {uri.AbsolutePath}.");
            }

            picture = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            contentType = response.Content.Headers.ContentType?.MediaType;
        }
        catch (HttpRequestException e) when (e.InnerException is AuthenticationException)
        {
            throw new InvalidOperationException(
                $"{uri.Host} uses a certificate that is not trusted - set AllowUntrustedCertificate for a self-signed one.", e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation, just like ffmpeg - keep shutdown and timeout apart
            throw new TimeoutException($"{uri.Host} delivered no picture within {RequestTimeout.TotalSeconds:0}s.", e);
        }

        if (picture.Length < 2 || picture[0] != 0xFF || picture[1] != 0xD8)
        {
            throw new InvalidOperationException(
                $"{uri.Host} returned {contentType ?? "unknown content"} ({picture.Length} bytes) instead of a JPEG - is {uri.AbsolutePath} the picture endpoint?");
        }

        return ApplyExifOrientation(picture);
    }

    // A phone photo is the sensor image plus an EXIF orientation tag. Browsers apply the tag, ImageSharp does not,
    // so the ROIs drawn on the calibration page would miss the digits - bake the rotation into the pixels instead.
    // Frames without the tag (ffmpeg, video snapshots) pass through untouched.
    private static byte[] ApplyExifOrientation(byte[] jpeg)
    {
        var exif = Image.Identify(jpeg).Metadata.ExifProfile;
        if (exif is null || !exif.TryGetValue(ExifTag.Orientation, out var orientation) || orientation?.Value is null or 1)
        {
            return jpeg;
        }

        using var image = Image.Load(jpeg);
        image.Mutate(x => x.AutoOrient());
        using var upright = new MemoryStream();
        image.SaveAsJpeg(upright, new JpegEncoder { Quality = 95 });
        return upright.ToArray();
    }

    private static HttpClient CreateClient(bool trustAnyCertificate)
    {
        var handler = new SocketsHttpHandler();
        if (trustAnyCertificate)
        {
            // The https of IP Webcam comes with a self-signed certificate; the camera sits in the LAN and its login
            // is in appsettings anyway, so there is nothing a proper certificate would protect here.
            handler.SslOptions.RemoteCertificateValidationCallback = delegate { return true; };
        }

        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    public void Dispose()
    {
        _client.Dispose();
        _trustingClient.Dispose();
    }
}
