namespace JuiceLog.Recognition;

public static class RollingDigitEvaluator
{
    private const int DigitBand = 3;
    
    private const float TransitionAreaPredecessor = 0.7f;
    
    private const float TransitionAreaForward = 9.7f;
    
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

        // Least significant drum: nothing to the right of it, so round to the nearest digit. Rounding (instead of
        // the truncation a human would use) keeps the noise of a resting drum symmetric: a "7.7" is still a 8 and
        // does not flip to 7 or 6 between readings. A 9.6 becomes 0 and carries into the next drum (see below).
        var previous = ((int)MathF.Round(readings[^1], MidpointRounding.AwayFromZero) + 10) % 10;
        digits[^1] = previous;

        for (var i = readings.Length - 2; i >= 0; i--)
        {
            previous = Evaluate(readings[i], readings[i + 1], previous);
            digits[i] = previous;
        }

        return digits;
    }
    
    public static double ToValue(ReadOnlySpan<int> digits, int decimalDigits)
    {
        var integer = 0L;
        foreach (var digit in digits)
        {
            integer = integer * 10 + digit;
        }

        return integer / Math.Pow(10, decimalDigits);
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
