namespace JuiceLog.Recognition;

public static class MeterReadingCorrector
{
    public static int KnownPrefixLength(int digitCount, long low, long high)
    {
        var weight = Weight(digitCount - 1);
        for (var i = 0; i < digitCount; i++, weight /= 10)
        {
            if (low / weight != high / weight)
            {
                return i;
            }
        }

        return digitCount;
    }

    public static int KnownDigit(int index, int digitCount, long low) => (int)(low / Weight(digitCount - 1 - index) % 10);

    public static int[]? CorrectAdjacentMisread(ReadOnlySpan<int> digits, int knownDigits, long low, long high, out int correctedIndex)
    {
        int[]? best = null;
        var bestValue = long.MaxValue;
        correctedIndex = -1;

        for (var i = knownDigits; i < digits.Length; i++)
        {
            foreach (var delta in new[] { -1, 1 })
            {
                var candidate = digits.ToArray();
                candidate[i] = (candidate[i] + delta + 10) % 10;

                var value = RollingDigitEvaluator.ToScaled(candidate);
                if (value >= low && value <= high && value < bestValue)
                {
                    best = candidate;
                    bestValue = value;
                    correctedIndex = i;
                }
            }
        }

        return best;
    }

    private static long Weight(int exponent)
    {
        var weight = 1L;
        for (var i = 0; i < exponent; i++)
        {
            weight *= 10;
        }

        return weight;
    }
}
