namespace JuiceLog.Options;

/// <summary>
/// Describes how the meter value is read from a camera snapshot.
/// </summary>
public class CameraOptions
{
    /// <summary>
    /// Path to the TFLite digit model (relative to the application directory).
    /// Any "dig-class100", "dig-cont" or "dig-class11" model of the AI-on-the-edge project can be used.
    /// </summary>
    public string ModelPath { get; set; } = "Models/dig-cont_0900_s3_q.tflite";

    /// <summary>
    /// One region of interest per digit drum, ordered from the most significant (left) to the
    /// least significant (right) digit. Coordinates are pixels in the camera frame.
    /// </summary>
    public List<DigitRoi> DigitRois { get; set; } = [];

    /// <summary>How many of the trailing digits are behind the decimal point (red drums on a gas meter).</summary>
    public int DecimalDigits { get; set; } = 3;

    /// <summary>
    /// Minimum confidence (0..1) the network must have for every digit; otherwise the snapshot is skipped.
    /// </summary>
    public double MinConfidence { get; set; } = 0.6;

    /// <summary>
    /// Stretch the contrast of every digit crop before classification. Helps a lot with the low-contrast
    /// coloured drums in infrared images; disable if the camera delivers crisp, high-contrast colour images.
    /// </summary>
    public bool AutoContrast { get; set; } = true;

    /// <summary>
    /// Upper bound for a plausible consumption in the meter's unit per hour. Readings that increase faster
    /// than this (or decrease at all) are treated as misreads and not stored.
    /// Rule of thumb for a gas boiler: rated power [kW] / ~10 kWh per m³, plus some headroom
    /// (a 26 kW condensing boiler draws ~2.4-2.8 m³/h at full load).
    /// </summary>
    public double MaxIncreasePerHour { get; set; } = 3.5;

    /// <summary>
    /// Optional directory in which the last snapshot (with the ROIs drawn) and the digit crops are stored.
    /// Useful to calibrate the <see cref="DigitRois"/>. Leave empty to disable.
    /// </summary>
    public string? DebugDirectory { get; set; }
}

public class DigitRoi
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
