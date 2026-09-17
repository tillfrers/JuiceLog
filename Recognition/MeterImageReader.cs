using JuiceLog.Abstractions;
using JuiceLog.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JuiceLog.Recognition;

public sealed class MeterImageReader(DigitRecognizer recognizer) : IMeterImageReader
{
    public MeterImageReading Read(byte[] jpeg, CameraOptions options)
    {
        if (options.DigitRois.Count == 0)
        {
            throw new InvalidOperationException("No digit ROIs configured for the camera logger (Camera:DigitRois).");
        }

        if (options.DecimalDigits < 0 || options.DecimalDigits > options.DigitRois.Count)
        {
            throw new InvalidOperationException("Camera:DecimalDigits must be between 0 and the number of digit ROIs.");
        }

        using var frame = Image.Load<Rgb24>(jpeg);

        var digitReadings = recognizer.Recognize(frame, options);

        var rawValues = digitReadings.Select(r => r.Value).ToArray();
        var digits = RollingDigitEvaluator.ResolveDigits(rawValues);
        if (digits is null)
        {
            return new MeterImageReading(null, null, digitReadings, "at least one digit is not a number");
        }

        var value = RollingDigitEvaluator.ToValue(digits, options.DecimalDigits);

        return new MeterImageReading(value, digits, digitReadings, null);
    }
}
