using JuiceLog.Options;
using JuiceLog.Recognition.TfLite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace JuiceLog.Recognition;

public sealed class DigitRecognizer(TfLiteModel model)
{
    public const float NotANumber = -1f;
    
    public DigitReading[] Recognize(Image<Rgb24> frame, CameraOptions options)
    {
        var rois = options.DigitRois;
        if (rois.Count == 0)
        {
            throw new ArgumentException("At least one digit ROI must be configured.", nameof(options));
        }

        var debugDirectory = ResolveDebugDirectory(options.DebugDirectory);

        var input = new float[model.InputHeight * model.InputWidth * model.InputChannels];
        var readings = new DigitReading[rois.Count];

        for (var i = 0; i < rois.Count; i++)
        {
            var roi = rois[i];
            var rect = new Rectangle(roi.X, roi.Y, roi.Width, roi.Height);
            if (rect.Width < 4 || rect.Height < 4 || !frame.Bounds.Contains(rect))
            {
                throw new ArgumentException(
                    $"Digit ROI #{i} ({rect}) lies outside the frame ({frame.Width}x{frame.Height}).", nameof(options));
            }

            using var crop = PrepareCrop(frame, rect, options.AutoContrast);
            readings[i] = ReadDigit(crop, input);

            if (debugDirectory is not null)
            {
                crop.SaveAsPng(Path.Combine(debugDirectory, $"digit_{i}.png"));
            }
        }

        if (debugDirectory is not null)
        {
            SaveAnnotatedFrame(frame, rois, Path.Combine(debugDirectory, "last_frame.jpg"));
        }

        return readings;
    }

    public DigitReading ReadDigit(Image<Rgb24> frame, Rectangle roi, bool autoContrast)
    {
        using var crop = PrepareCrop(frame, roi, autoContrast);
        return ReadDigit(crop, new float[model.InputHeight * model.InputWidth * model.InputChannels]);
    }

    private DigitReading ReadDigit(Image<Rgb24> crop, float[] input)
    {
        FillInput(crop, input);
        return Interpret(model.Run(input));
    }
    
    public Image<Rgb24> PrepareCrop(Image<Rgb24> frame, Rectangle roi, bool autoContrast)
    {
        // copy only the ROI (frame.Clone(...).Crop would duplicate the whole 3 MP frame first)
        var crop = new Image<Rgb24>(roi.Width, roi.Height);
        frame.ProcessPixelRows(crop, (source, target) =>
        {
            for (var y = 0; y < roi.Height; y++)
            {
                source.GetRowSpan(roi.Y + y).Slice(roi.X, roi.Width).CopyTo(target.GetRowSpan(y));
            }
        });

        crop.Mutate(ctx => ctx.Resize(model.InputWidth, model.InputHeight));
        if (autoContrast)
        {
            StretchContrast(crop);
        }

        return crop;
    }

    private static string? ResolveDebugDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var directory = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void FillInput(Image<Rgb24> crop, float[] input)
    {
        var channels = model.InputChannels;
        crop.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var offset = (y * accessor.Width + x) * channels;
                    var pixel = row[x];
                    if (channels == 1)
                    {
                        input[offset] = (pixel.R + pixel.G + pixel.B) / 3f;
                    }
                    else
                    {
                        input[offset] = pixel.R;
                        input[offset + 1] = pixel.G;
                        input[offset + 2] = pixel.B;
                    }
                }
            }
        });
    }

    private static void StretchContrast(Image<Rgb24> crop)
    {
        var histogram = new int[256];
        crop.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                foreach (var pixel in accessor.GetRowSpan(y))
                {
                    histogram[(pixel.R + pixel.G + pixel.B) / 3]++;
                }
            }
        });

        var total = crop.Width * crop.Height;
        var low = Percentile(histogram, total, 0.02);
        var high = Percentile(histogram, total, 0.98);
        if (high - low < 16)
        {
            return; // (almost) uniform crop, stretching would only amplify noise
        }

        var scale = 255f / (high - low);
        crop.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    row[x] = new Rgb24(Stretch(p.R), Stretch(p.G), Stretch(p.B));
                }
            }
        });

        byte Stretch(byte value) => (byte)Math.Clamp((value - low) * scale, 0f, 255f);
    }

    private static int Percentile(int[] histogram, int total, double fraction)
    {
        var target = (int)(total * fraction);
        var seen = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen > target) return i;
        }

        return histogram.Length - 1;
    }

    private static DigitReading Interpret(float[] output)
    {
        switch (output.Length)
        {
            case 100: // dig-class100: one logit per tenth (0.0 ... 9.9)
            {
                var probabilities = Softmax(output);
                var best = ArgMax(probabilities);

                // probability mass within half a digit of the best class (the logits spread over neighbouring tenths)
                var confidence = 0f;
                for (var k = -5; k <= 5; k++) confidence += probabilities[(best + k + 100) % 100];

                return new DigitReading(best / 10f, confidence);
            }

            case 10: // dig-cont (newer): softmax over the digits, transition interpolated from the neighbours
            {
                var digit = ArgMax(output);
                var next = output[(digit + 1) % 10];
                var prev = output[(digit + 9) % 10];
                var value = next > prev
                    ? digit + next / (next + output[digit])
                    : digit - prev / (prev + output[digit]);

                return new DigitReading((value + 10f) % 10f, output[digit] + MathF.Max(next, prev));
            }

            case 2: // dig-cont (older): angle encoded as (sin, cos)
            {
                var turn = (MathF.Atan2(output[0], output[1]) / (2 * MathF.PI) + 2) % 1;
                var magnitude = MathF.Sqrt(output[0] * output[0] + output[1] * output[1]);
                return new DigitReading(turn * 10f, Math.Clamp(magnitude, 0f, 1f));
            }

            case 11: // dig-class11: digits 0-9 plus "not a number"
            {
                var cls = ArgMax(output);
                return cls == 10 ? new DigitReading(NotANumber, output[cls]) : new DigitReading(cls, output[cls]);
            }

            default:
                throw new NotSupportedException($"Unknown digit model with {output.Length} outputs.");
        }
    }

    private static int ArgMax(float[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }

        return best;
    }

    private static float[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var result = new float[logits.Length];
        var sum = 0f;
        for (var i = 0; i < logits.Length; i++)
        {
            result[i] = MathF.Exp(logits[i] - max);
            sum += result[i];
        }

        for (var i = 0; i < result.Length; i++) result[i] /= sum;
        return result;
    }

    private static void SaveAnnotatedFrame(Image<Rgb24> frame, IReadOnlyList<DigitRoi> rois, string path)
    {
        using var annotated = frame.Clone();
        var color = new Rgb24(255, 0, 0);

        foreach (var roi in rois)
        {
            for (var x = roi.X; x < roi.X + roi.Width; x++)
            {
                annotated[x, roi.Y] = color;
                annotated[x, roi.Y + 1] = color;
                annotated[x, roi.Y + roi.Height - 1] = color;
                annotated[x, roi.Y + roi.Height - 2] = color;
            }

            for (var y = roi.Y; y < roi.Y + roi.Height; y++)
            {
                annotated[roi.X, y] = color;
                annotated[roi.X + 1, y] = color;
                annotated[roi.X + roi.Width - 1, y] = color;
                annotated[roi.X + roi.Width - 2, y] = color;
            }
        }

        annotated.SaveAsJpeg(path);
    }
}

/// <param name="Value">Fractional drum position 0.0 ≤ value &lt; 10.0, or <see cref="DigitRecognizer.NotANumber"/>.</param>
/// <param name="Confidence">0..1, how sure the network is about the digit (and its neighbour during a transition).</param>
public readonly record struct DigitReading(float Value, float Confidence);
