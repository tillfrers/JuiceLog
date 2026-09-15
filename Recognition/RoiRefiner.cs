using JuiceLog.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JuiceLog.Recognition;

public sealed class RoiRefiner(DigitRecognizer recognizer)
{
    private static readonly float[] Scales = [1.0f, 0.85f, 0.7f];
    private const int Steps = 2;               // offsets -2..2 in both directions
    private const float StepFraction = 0.08f;  // one step = 8 % of the box size

    public List<DigitRoi> Refine(Image<Rgb24> frame, IReadOnlyList<DigitRoi> rois, bool autoContrast)
    {
        // the frame is only read and the model keeps no per-run state, so the digits can be refined in parallel
        var refined = new DigitRoi[rois.Count];
        Parallel.For(0, rois.Count, i => refined[i] = Refine(frame, rois[i], autoContrast));
        return refined.ToList();
    }

    public DigitRoi Refine(Image<Rgb24> frame, DigitRoi roi, bool autoContrast)
    {
        var centreX = roi.X + roi.Width / 2f;
        var centreY = roi.Y + roi.Height / 2f;
        var stepX = Math.Max(2f, roi.Width * StepFraction);
        var stepY = Math.Max(2f, roi.Height * StepFraction);

        var best = roi;
        var bestScore = float.MaxValue;

        foreach (var scale in Scales)
        {
            var width = (int)MathF.Round(roi.Width * scale);
            var height = (int)MathF.Round(roi.Height * scale);

            for (var dy = -Steps; dy <= Steps; dy++)
            {
                for (var dx = -Steps; dx <= Steps; dx++)
                {
                    var candidate = new Rectangle(
                        (int)MathF.Round(centreX + dx * stepX - width / 2f),
                        (int)MathF.Round(centreY + dy * stepY - height / 2f),
                        width, height);

                    if (width < 4 || height < 4 || !frame.Bounds.Contains(candidate))
                    {
                        continue;
                    }

                    var reading = recognizer.ReadDigit(frame, candidate, autoContrast);
                    if (reading.Value < 0)
                    {
                        continue;
                    }

                    // distance to a whole digit (0 = resting digit) plus uncertainty,
                    // slightly favouring variants close to what the user drew
                    var fraction = reading.Value - MathF.Floor(reading.Value);
                    var score = MathF.Min(fraction, 1 - fraction)
                                + (1 - reading.Confidence)
                                + 0.01f * (Math.Abs(dx) + Math.Abs(dy))
                                + 0.02f * (1 - scale) / 0.1f;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = new DigitRoi { X = candidate.X, Y = candidate.Y, Width = candidate.Width, Height = candidate.Height };
                    }
                }
            }
        }

        return best;
    }
}
