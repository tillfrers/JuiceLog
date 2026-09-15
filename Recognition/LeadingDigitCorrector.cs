namespace JuiceLog.Recognition;

public static class LeadingDigitCorrector
{
    public static int[]? Correct(ReadOnlySpan<int> digits, int decimalDigits, double minValue, double maxValue, out string reason)
    {
        reason = string.Empty;

        // readings are whole numbers in units of the last drum, so compare in that scale
        var scale = Math.Pow(10, decimalDigits);
        var low = (long)Math.Ceiling(minValue * scale - 1e-6);
        var high = (long)Math.Floor(maxValue * scale + 1e-6);
        if (low > high || low < 0)
        {
            return null;
        }

        int[]? corrected = null;
        var changes = new List<string>();
        var weight = (long)Math.Pow(10, digits.Length - 1);

        for (var i = 0; i < digits.Length && weight > 0; i++, weight /= 10)
        {
            if (low / weight != high / weight)
            {
                // from this drum on the meter may have moved since the last value, nothing is known for certain
                break;
            }

            var known = (int)(low / weight % 10);
            if (digits[i] == known)
            {
                continue;
            }

            corrected ??= digits.ToArray();
            corrected[i] = known;
            changes.Add($"#{i} {digits[i]}->{known}");
        }

        if (corrected is null)
        {
            return null;
        }

        reason = $"digit {string.Join(", ", changes)}: the meter cannot have left {ToText(low, digits.Length, decimalDigits)}" +
                 $"..{ToText(high, digits.Length, decimalDigits)} since the last stored value";
        return corrected;
    }

    private static string ToText(long scaled, int digitCount, int decimalDigits)
    {
        var text = scaled.ToString().PadLeft(digitCount, '0');
        return decimalDigits == 0 ? text : text[..^decimalDigits] + "." + text[^decimalDigits..];
    }
}
