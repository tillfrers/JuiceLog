using JuiceLog.Common.Enums;

namespace JuiceLog.Abstractions;

public interface ITimeProvider
{
    DateTime GetUtcNow { get; }
    DateTime GetBerlinNow { get; }
}