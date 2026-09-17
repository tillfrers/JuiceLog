namespace JuiceLog.Options;

public class CameraOptions
{
    public string ModelPath { get; set; } = "Models/dig-cont_0900_s3_q.tflite";
    
    public List<DigitRoi> DigitRois { get; set; } = [];
    
    public int DecimalDigits { get; set; } = 3;
    
    public double MinConfidence { get; set; } = 0.6;
    
    public bool AutoContrast { get; set; } = true;
    
    public double MaxIncreasePerHour { get; set; } = 3.5;
    
    public bool RepeatLastValueWhenUnreadable { get; set; } = true;
    
    public string? DebugDirectory { get; set; }
}

public class DigitRoi
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
