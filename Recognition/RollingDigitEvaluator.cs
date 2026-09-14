namespace JuiceLog.Recognition;

/// <summary>
/// Turns the fractional per-drum readings (e.g. 4.0, 3.0, 7.9, 0.2) of a mechanical counter into whole digits.
///
/// On a rolling counter the next higher drum starts moving while the lower drum passes from 9 to 0, so a drum
/// may be read as "7.6" although the meter still shows 7. The rules below (ported from the AI-on-the-edge
/// firmware, <c>ClassFlowCNNGeneral::PointerEvalHybridNew</c>) resolve that by looking at the drum to the right.
/// </summary>
public static class RollingDigitEvaluator
{
    /// <summary>Readings this close to a whole digit (in tenths) are rounded when the predecessor is stable.</summary>
    private const int DigitBand = 3;

    /// <summary>Predecessor readings in [0.7, 9.3] mean "no zero crossing anywhere near".</summary>
    private const float TransitionAreaPredecessor = 0.7f;

    /// <summary>The current drum only runs ahead of its predecessor once the predecessor passed ~9.7.</summary>
    private const float TransitionAreaForward = 9.7f;

    /// <summary>
    /// Resolves the readings (most significant digit first) to digits 0-9.
    /// Returns <c>null</c> when a reading is not a number.
    /// </summary>
    public static int[]? ResolveDigits(ReadOnlySpan<float> readings)
    {
        if (readings.Length == 0)
        {
            return null;
        }

        foreach (var reading in readings)
        {
            if (reading < 0 || reading >= 10 || float.IsNaN(reading))
            {
                return null;
            }
        }

        var digits = new int[readings.Length];

        // Least significant drum: nothing to the right of it, take it as is (a meter is read by truncation).
        var previous = ((int)MathF.Floor(readings[^1] + 0.001f) + 10) % 10;
        digits[^1] = previous;

        for (var i = readings.Length - 2; i >= 0; i--)
        {
            previous = Evaluate(readings[i], readings[i + 1], previous);
            digits[i] = previous;
        }

        return digits;
    }

    /// <param name="number">Raw reading of the current drum.</param>
    /// <param name="predecessor">Raw reading of the drum to the right.</param>
    /// <param name="evaluatedPredecessor">Already resolved digit of the drum to the right.</param>
    private static int Evaluate(float number, float predecessor, int evaluatedPredecessor)
    {
        var tenths = (int)MathF.Floor(number * 10) % 10;
        var whole = ((int)MathF.Floor(number) + 10) % 10;

        if (evaluatedPredecessor <= 1 && predecessor > 5)
        {
            // The predecessor still reads 9.x but was resolved to 0 because the drums below it passed zero
            // (e.g. ...8 | 9.9 | 0.3 -> ...9.000). The carry has to cascade into this drum as well,
            // otherwise the result would be a full unit too low.
            return (whole + 1) % 10;
        }

        if (predecessor >= TransitionAreaPredecessor && predecessor <= 10 - TransitionAreaPredecessor)
        {
            // The predecessor is far away from a zero crossing, so this drum must be at rest.
            // Readings close to a whole digit are rounded (the ROI is never perfectly centred),
            // everything else is truncated.
            if (tenths <= DigitBand || tenths >= 10 - DigitBand)
            {
                return ((int)MathF.Round(number) + 10) % 10;
            }

            return whole;
        }

        if (evaluatedPredecessor <= 1)
        {
            // The predecessor has already passed zero. This drum is at least half way through its
            // transition, so x.6 and above is already the next digit.
            return tenths > 5 ? (whole + 1) % 10 : whole;
        }

        // The predecessor is at 9.x and has not passed zero yet.
        if (TransitionAreaForward >= predecessor || tenths >= 4)
        {
            // This drum is (at most) in transition as well: keep the current digit.
            return whole;
        }

        // This drum already shows the next whole digit although the predecessor did not pass zero yet
        // (the drum runs slightly ahead) -> take the previous digit.
        return (whole - 1 + 10) % 10;
    }
}
