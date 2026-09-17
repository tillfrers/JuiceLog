using JuiceLog.Abstractions;
using JuiceLog.Entities;
using JuiceLog.Options;
using JuiceLog.Recognition;

namespace JuiceLog.Services;

public sealed class MeterReadingValidator : IMeterReadingValidator
{
    private static readonly TimeSpan MinimumElapsed = TimeSpan.FromMinutes(5);

    private const int ToleranceUnits = 2;

    public double? Evaluate(MeterImageReading reading, Energy? last, DateTime now, CameraOptions camera, out string reason)
    {
        if (reading.Digits is null)
        {
            reason = reading.Problem ?? "snapshot could not be read";
            return null;
        }

        var uncertain = reading.UncertainDigits(camera.MinConfidence);

        if (last is null)
        {
            if (uncertain.Length > 0)
            {
                reason = UncertainText(reading, uncertain[0], camera);
                return null;
            }

            reason = string.Empty;
            return reading.Value;
        }

        var scale = Math.Pow(10, camera.DecimalDigits);
        var elapsed = Elapsed(last, now);
        var allowedIncrease = camera.MaxIncreasePerHour * elapsed.TotalHours;
        var lastScaled = (long)Math.Round(last.Value * scale);
        var low = lastScaled - ToleranceUnits;
        var high = lastScaled + (long)Math.Floor(allowedIncrease * scale + 1e-6);
        var range = $"{ToText(low, reading.Digits.Length, camera.DecimalDigits)}..{ToText(high, reading.Digits.Length, camera.DecimalDigits)}";

        var digits = reading.Digits.ToArray();
        var known = MeterReadingCorrector.KnownPrefixLength(digits.Length, low, high);
        var corrections = new List<string>();

        for (var i = 0; i < known; i++)
        {
            var knownDigit = MeterReadingCorrector.KnownDigit(i, digits.Length, low);
            if (digits[i] != knownDigit)
            {
                corrections.Add($"#{i} {digits[i]}->{knownDigit}");
                digits[i] = knownDigit;
            }
        }

        if (corrections.Count > 1)
        {
            reason = $"digits {string.Join(", ", corrections)} differ from the {range} the meter must show - is the camera still aligned?";
            return null;
        }

        foreach (var index in uncertain)
        {
            if (index >= known)
            {
                reason = UncertainText(reading, index, camera);
                return null;
            }
        }

        var scaled = RollingDigitEvaluator.ToScaled(digits);

        if (scaled < low || scaled > high)
        {
            var corrected = MeterReadingCorrector.CorrectAdjacentMisread(digits, known, low, high, out var index);
            if (corrected is null)
            {
                var value = scaled / scale;
                reason = scaled < low
                    ? $"value {value} is lower than the last stored value {last.Value}"
                    : $"increase of {value - last.Value:0.###} within {elapsed.TotalMinutes:0} min exceeds the allowed {allowedIncrease:0.###}";
                return null;
            }

            corrections.Add($"#{index} {digits[index]}->{corrected[index]}");
            digits = corrected;
            scaled = RollingDigitEvaluator.ToScaled(digits);
        }

        var result = scaled / scale;
        reason = corrections.Count == 0
            ? string.Empty
            : $"{reading.DigitsText} corrected to {string.Concat(digits)} (digit {string.Join(", ", corrections)}): the meter must show {range} now";

        if (scaled < lastScaled)
        {
            // the least significant drum jittered below the stored value, the meter did not move
            var jitter = $"value {result} is {last.Value - result:0.###} below the stored value {last.Value}, within the tolerance -> keeping {last.Value}";
            reason = reason.Length == 0 ? jitter : $"{reason}; {jitter}";
            return last.Value;
        }

        return result;
    }

    private static string UncertainText(MeterImageReading reading, int index, CameraOptions camera) =>
        $"digit #{index} is uncertain (confidence {reading.DigitReadings[index].Confidence:0.00} < {camera.MinConfidence:0.00})";

    private static TimeSpan Elapsed(Energy last, DateTime now)
    {
        var elapsed = now - last.Date.DateTime;
        return elapsed < MinimumElapsed ? MinimumElapsed : elapsed;
    }

    private static string ToText(long scaled, int digitCount, int decimalDigits)
    {
        var text = Math.Max(scaled, 0).ToString().PadLeft(digitCount, '0');
        return decimalDigits == 0 ? text : text[..^decimalDigits] + "." + text[^decimalDigits..];
    }
}
