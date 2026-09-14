using JuiceLog.Common.Enums;
using JuiceLog.Entities;

namespace JuiceLog.Abstractions;

public enum ReadingVerdict
{
    /// <summary>Plausible, write it to the database.</summary>
    Accept,

    /// <summary>Plausible but not worth storing (e.g. the last digit jittered one unit below the stored value).</summary>
    Skip,

    /// <summary>Most likely a misread, do not store.</summary>
    Reject,
}

public interface IMeterReadingValidator
{
    /// <summary>Checks a new reading against the last stored value.</summary>
    /// <param name="maxIncreasePerHour">Upper bound for a plausible consumption per hour.</param>
    /// <param name="tolerance">
    /// How far a reading may fall below the stored value and still count as "unchanged" (noise of the
    /// least significant digit); larger decreases are misreads.
    /// </param>
    ReadingVerdict Validate(EnergyType energyType, double value, Energy? last, DateTime now,
        double maxIncreasePerHour, double tolerance, out string reason);
}
