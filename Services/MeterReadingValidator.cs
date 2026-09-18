using JuiceLog.Abstractions;
using JuiceLog.Entities;
using JuiceLog.Options;
using JuiceLog.Recognition;

namespace JuiceLog.Services;

public sealed class MeterReadingValidator : IMeterReadingValidator
{
    private static readonly TimeSpan MinimumElapsed = TimeSpan.FromMinutes(5);

    // The least significant drum is rounded, so even a perfectly read resting drum jitters by a unit or two. That is
    // the room the drums left of it get when deciding which of them cannot have turned since the last reading.
    private const int RoundingUnits = 2;

    // In bad light the decimal drums are misread by several units. Anything below a full step of the second-least
    // significant drum is therefore "the meter did not move" rather than a misread to be corrected: a value stored
    // slightly too high is caught up by the meter within that step, whereas explaining the difference by bending another
    // drum (CorrectAdjacentMisread) would creep upwards with every reading.
    private const int ToleranceUnits = 9;

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
        var lastScaled = (long)Math.Round(last.Value * scale);
        var low = lastScaled - ToleranceUnits;

        // How far the meter can have turned since the last stored reading: this decides which drums cannot have moved.
        var possibleIncrease = camera.MaxIncreasePerHour * elapsed.TotalHours;
        var possibleHigh = lastScaled + ToScaled(possibleIncrease, scale);

        // How much of that is accepted: after a gap of unreadable snapshots the window would otherwise keep growing
        // until a persistent misread fits in (a 9 read for a resting 0 fits after 3 h at 3 m³/h) - and that value
        // sticks, because every correct reading afterwards is "lower than the last stored value".
        var capped = camera.MaxIncreaseAfterGap > 0 && possibleIncrease > camera.MaxIncreaseAfterGap;
        var allowedIncrease = capped ? camera.MaxIncreaseAfterGap : possibleIncrease;
        var high = lastScaled + ToScaled(allowedIncrease, scale);

        var knownLow = lastScaled - RoundingUnits;
        var possibleRange = RangeText(knownLow, possibleHigh, reading.Digits.Length, camera.DecimalDigits);
        var range = RangeText(low, high, reading.Digits.Length, camera.DecimalDigits);

        var digits = reading.Digits.ToArray();
        var known = MeterReadingCorrector.KnownPrefixLength(digits.Length, knownLow, possibleHigh);
        var corrections = new List<string>();

        for (var i = 0; i < known; i++)
        {
            var knownDigit = MeterReadingCorrector.KnownDigit(i, digits.Length, knownLow);
            if (digits[i] != knownDigit)
            {
                corrections.Add($"#{i} {digits[i]}->{knownDigit}");
                digits[i] = knownDigit;
            }
        }

        if (corrections.Count > 1)
        {
            reason = $"digits {string.Join(", ", corrections)} differ from the {possibleRange} the meter must show - is the camera still aligned?";
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
            // Only the integer drums are worth a correction: the decimal drums are read within their noise anyway, and a
            // correction there would merely squeeze a too-high value under the limit or lift a resting meter step by step.
            var correctable = digits.Length - camera.DecimalDigits;
            var corrected = MeterReadingCorrector.CorrectAdjacentMisread(digits, known, correctable, low, high, out var index);
            if (corrected is null)
            {
                var value = scaled / scale;
                reason = scaled < low
                    ? $"value {value} is lower than the last stored value {last.Value}"
                    : $"increase of {value - last.Value:0.###} within {elapsed.TotalMinutes:0} min exceeds the allowed {allowedIncrease:0.###}{(capped ? " (MaxIncreaseAfterGap)" : string.Empty)}";
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
            // the least significant drums jittered below the stored value, the meter did not move
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

    private static long ToScaled(double increase, double scale) => (long)Math.Floor(increase * scale + 1e-6);

    private static string RangeText(long low, long high, int digitCount, int decimalDigits) =>
        $"{ToText(low, digitCount, decimalDigits)}..{ToText(high, digitCount, decimalDigits)}";

    private static string ToText(long scaled, int digitCount, int decimalDigits)
    {
        var text = Math.Max(scaled, 0).ToString().PadLeft(digitCount, '0');
        return decimalDigits == 0 ? text : text[..^decimalDigits] + "." + text[^decimalDigits..];
    }
}
