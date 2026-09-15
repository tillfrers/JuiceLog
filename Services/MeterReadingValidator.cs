using JuiceLog.Abstractions;
using JuiceLog.Entities;
using JuiceLog.Recognition;

namespace JuiceLog.Services;

public sealed class MeterReadingValidator : IMeterReadingValidator
{
    private static readonly TimeSpan MinimumElapsed = TimeSpan.FromMinutes(5);

    public ReadingVerdict Validate(double value, Energy? last, DateTime now,
        double maxIncreasePerHour, double tolerance, out string reason)
    {
        if (last is null)
        {
            reason = string.Empty;
            return ReadingVerdict.Accept;
        }

        var elapsed = Elapsed(last, now);
        var allowedIncrease = maxIncreasePerHour * elapsed.TotalHours;
        var delta = value - last.Value;

        if (delta >= -1e-9 && delta <= allowedIncrease)
        {
            reason = string.Empty;
            return ReadingVerdict.Accept;
        }

        if (delta < 0 && -delta <= tolerance)
        {
            // the least significant drum jittered below the stored value, the meter did not move
            reason = $"value {value} is {-delta:0.###} below the stored value {last.Value}, within the tolerance -> keeping {last.Value}";
            return ReadingVerdict.UseLastValue;
        }

        reason = delta < 0
            ? $"value {value} is lower than the last stored value {last.Value}"
            : $"increase of {delta:0.###} within {elapsed.TotalMinutes:0} min exceeds the allowed {allowedIncrease:0.###}";
        return ReadingVerdict.Reject;
    }

    public int[]? CorrectLeadingDigits(int[] digits, int decimalDigits, Energy last, DateTime now,
        double maxIncreasePerHour, double tolerance, out string reason)
    {
        // every value Validate would let through lies in this range, so the leading digits shared by its
        // bounds are known for certain
        var allowedIncrease = maxIncreasePerHour * Elapsed(last, now).TotalHours;
        return LeadingDigitCorrector.Correct(digits, decimalDigits, last.Value - tolerance, last.Value + allowedIncrease, out reason);
    }

    private static TimeSpan Elapsed(Energy last, DateTime now)
    {
        var elapsed = now - last.Date.DateTime;
        return elapsed < MinimumElapsed ? MinimumElapsed : elapsed;
    }
}
