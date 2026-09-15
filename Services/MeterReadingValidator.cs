using JuiceLog.Abstractions;
using JuiceLog.Entities;

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

        var elapsed = now - last.Date.DateTime;
        if (elapsed < MinimumElapsed) elapsed = MinimumElapsed;

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
}
