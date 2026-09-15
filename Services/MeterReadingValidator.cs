using JuiceLog.Abstractions;
using JuiceLog.Common.Enums;
using JuiceLog.Entities;

namespace JuiceLog.Services;

public sealed class MeterReadingValidator : IMeterReadingValidator
{
    private const int ConsistentRejectionsToAccept = 3;
    private static readonly TimeSpan MinimumElapsed = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Dictionary<EnergyType, List<double>> _rejected = new();

    public ReadingVerdict Validate(EnergyType energyType, double value, Energy? last, DateTime now,
        double maxIncreasePerHour, double tolerance, out string reason)
    {
        lock (_lock)
        {
            if (last is null)
            {
                reason = string.Empty;
                _rejected.Remove(energyType);
                return ReadingVerdict.Accept;
            }

            var elapsed = now - last.Date.DateTime;
            if (elapsed < MinimumElapsed) elapsed = MinimumElapsed;

            var allowedIncrease = maxIncreasePerHour * elapsed.TotalHours;
            var delta = value - last.Value;

            if (delta >= -1e-9 && delta <= allowedIncrease)
            {
                reason = string.Empty;
                _rejected.Remove(energyType);
                return ReadingVerdict.Accept;
            }

            if (delta < 0 && -delta <= tolerance)
            {
                // the least significant drum jittered below the stored value, the meter did not move
                reason = $"value {value} is {-delta:0.###} below the stored value {last.Value}, within the tolerance -> keeping {last.Value}";
                _rejected.Remove(energyType);
                return ReadingVerdict.UseLastValue;
            }

            if (!_rejected.TryGetValue(energyType, out var rejected))
            {
                rejected = [];
                _rejected[energyType] = rejected;
            }

            rejected.Add(value);
            if (rejected.Count > ConsistentRejectionsToAccept)
            {
                rejected.RemoveAt(0); // only the most recent rejections matter
            }

            if (rejected.Count == ConsistentRejectionsToAccept && AreConsistent(rejected, maxIncreasePerHour))
            {
                reason = $"accepted after {rejected.Count} consistent readings that contradict the stored value {last.Value}";
                _rejected.Remove(energyType);
                return ReadingVerdict.Accept;
            }

            reason = delta < 0
                ? $"value {value} is lower than the last stored value {last.Value}"
                : $"increase of {delta:0.###} within {elapsed.TotalMinutes:0} min exceeds the allowed {allowedIncrease:0.###}";
            reason += $" (rejected {rejected.Count}x in a row)";
            return ReadingVerdict.Reject;
        }
    }

    private static bool AreConsistent(List<double> values, double maxIncreasePerHour)
    {
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] < values[i - 1]) return false;
        }

        // the readings are usually 5 minutes apart, be generous and allow the hourly maximum per reading
        return values[^1] - values[0] <= maxIncreasePerHour * values.Count;
    }
}
