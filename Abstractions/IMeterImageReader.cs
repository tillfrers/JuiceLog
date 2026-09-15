using JuiceLog.Options;
using JuiceLog.Recognition;

namespace JuiceLog.Abstractions;

public interface IMeterImageReader
{
    MeterImageReading Read(byte[] jpeg, CameraOptions options);
}
