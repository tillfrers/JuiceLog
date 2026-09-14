using System.Globalization;

namespace JuiceLog.Recognition;

/// <param name="Value">The meter value in the meter's unit (e.g. m³), <c>null</c> if the snapshot could not be read.</param>
/// <param name="Digits">Resolved digits, left to right (<c>null</c> if the snapshot could not be read).</param>
/// <param name="DigitReadings">Fractional reading and confidence of every drum as returned by the network, left to right.</param>
/// <param name="Problem">Why the snapshot could not be read (<c>null</c> when <paramref name="Value"/> is set).</param>
public sealed record MeterImageReading(double? Value, int[]? Digits, DigitReading[] DigitReadings, string? Problem)
{
    public string RawReadingsText => string.Join(" ",
        DigitReadings.Select(r => r.Value.ToString("0.0", CultureInfo.InvariantCulture)));

    public string ConfidencesText => string.Join(" ",
        DigitReadings.Select(r => r.Confidence.ToString("0.00", CultureInfo.InvariantCulture)));

    public string DigitsText => Digits is null ? "?" : string.Concat(Digits);
}
