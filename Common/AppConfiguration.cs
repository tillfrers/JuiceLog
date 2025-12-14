using JuiceLog.Options;

namespace JuiceLog.Common;

public class AppConfiguration
{
    public List<LoggerConfigurationOptions> LoggerConfiguration { get; set; } = [];
}