using JuiceLog.Abstractions;

namespace JuiceLog.Repositiories;

public class SystemTimeProvider : ITimeProvider
{
    private const string BerlinTimeZoneId = "Europe/Berlin";
    private static readonly TimeZoneInfo BerlinTimeZone = TimeZoneInfo.FindSystemTimeZoneById(BerlinTimeZoneId);

    public DateTime GetUtcNow => DateTime.UtcNow;

    public DateTime GetBerlinNow
    {
        get
        {
            var utcNow = DateTime.UtcNow;
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, BerlinTimeZone);
        }
    }
}