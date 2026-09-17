using System.Globalization;

namespace JuiceLog.Recognition;

public sealed record MeterImageReading(double? Value, int[]? Digits, DigitReading[] DigitReadings, string? Problem)
{
    public string RawReadingsText => string.Join(" ",
        DigitReadings.Select(r => r.Value.ToString("0.0", CultureInfo.InvariantCulture)));

    public string ConfidencesText => string.Join(" ",
        DigitReadings.Select(r => r.Confidence.ToString("0.00", CultureInfo.InvariantCulture)));

    public string DigitsText => Digits is null ? "?" : string.Concat(Digits);

    public int[] UncertainDigits(double minConfidence) => DigitReadings
        .Select((reading, index) => (reading.Confidence, index))
        .Where(r => r.Confidence < minConfidence)
        .Select(r => r.index)
        .ToArray();
}
