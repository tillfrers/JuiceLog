using JuiceLog.Entities;

namespace JuiceLog.Abstractions;

public enum ReadingVerdict
{
    Accept,
    
    UseLastValue,
    
    Reject,
}

public interface IMeterReadingValidator
{
    ReadingVerdict Validate(double value, Energy? last, DateTime now,
        double maxIncreasePerHour, double tolerance, out string reason);
    
    int[]? CorrectLeadingDigits(int[] digits, int decimalDigits, Energy last, DateTime now,
        double maxIncreasePerHour, double tolerance, out string reason);
}
