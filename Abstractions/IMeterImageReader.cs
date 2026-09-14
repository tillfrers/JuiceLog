using JuiceLog.Options;
using JuiceLog.Recognition;

namespace JuiceLog.Abstractions;

public interface IMeterImageReader
{
    /// <summary>
    /// Reads the meter value from a JPEG snapshot. <see cref="MeterImageReading.Value"/> is <c>null</c>
    /// when at least one digit could not be recognized reliably.
    /// </summary>
    MeterImageReading Read(byte[] jpeg, CameraOptions options);
}
