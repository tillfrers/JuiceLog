namespace JuiceLog.Options;

public class CalibrationOptions
{
    public bool Enabled { get; set; } = true;
    
    public int Port { get; set; } = 47311;
    
    public bool OpenBrowser { get; set; } = true;
}
