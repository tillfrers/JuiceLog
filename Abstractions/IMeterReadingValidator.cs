using JuiceLog.Entities;
using JuiceLog.Options;
using JuiceLog.Recognition;

namespace JuiceLog.Abstractions;

public interface IMeterReadingValidator
{
    double? Evaluate(MeterImageReading reading, Energy? last, DateTime now, CameraOptions camera, out string reason);
}
