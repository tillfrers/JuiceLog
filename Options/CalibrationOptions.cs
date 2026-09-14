namespace JuiceLog.Options;

/// <summary>Settings of the built-in web page that is used to draw the digit ROIs on a live snapshot.</summary>
public class CalibrationOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>TCP port of the calibration page (http://&lt;host&gt;:&lt;port&gt;/).</summary>
    public int Port { get; set; } = 47311;

    /// <summary>
    /// Open http://localhost:&lt;port&gt;/ in the default browser at start-up when a camera has no digit ROIs yet.
    /// Only possible where a desktop session exists (ignored on a headless Raspberry Pi).
    /// </summary>
    public bool OpenBrowser { get; set; } = true;
}
