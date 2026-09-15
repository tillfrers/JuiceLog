using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JuiceLog.Options;

namespace JuiceLog.Services;

public sealed class CameraSettingsWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <param name="loggerIndex">Index of the camera entry in the "LoggerConfiguration" array.</param>
    /// <returns>The paths of the files that were updated.</returns>
    public IReadOnlyList<string> Save(int loggerIndex, IReadOnlyList<DigitRoi> rois, int decimalDigits)
    {
        var written = new List<string>();

        foreach (var directory in TargetDirectories())
        {
            var file = CandidateFiles(directory).FirstOrDefault(path => TryUpdate(path, loggerIndex, rois, decimalDigits));
            if (file is not null)
            {
                written.Add(file);
            }
        }

        if (written.Count == 0)
        {
            throw new InvalidOperationException(
                "No appsettings file with a matching LoggerConfiguration entry found in " + AppContext.BaseDirectory);
        }

        return written;
    }

    private static bool TryUpdate(string path, int loggerIndex, IReadOnlyList<DigitRoi> rois, int decimalDigits)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject;
        if (root?["LoggerConfiguration"] is not JsonArray loggers || loggerIndex >= loggers.Count ||
            loggers[loggerIndex] is not JsonObject logger)
        {
            return false; // this file does not define the camera logger
        }

        if (logger["Camera"] is not JsonObject camera)
        {
            camera = new JsonObject();
            logger["Camera"] = camera;
        }

        camera["DecimalDigits"] = decimalDigits;
        camera["DigitRois"] = new JsonArray(rois.Select(r => (JsonNode)new JsonObject
        {
            ["X"] = r.X,
            ["Y"] = r.Y,
            ["Width"] = r.Width,
            ["Height"] = r.Height,
        }).ToArray());

        // keep the file's BOM convention so that the change does not show up as a full-file diff
        var hadBom = File.ReadAllBytes(path) is [0xEF, 0xBB, 0xBF, ..];
        File.WriteAllText(path, root.ToJsonString(WriteOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: hadBom));
        return true;
    }
    
    private static IEnumerable<string> TargetDirectories()
    {
        var runtime = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        yield return runtime;

        var directory = Directory.GetParent(runtime);
        for (var level = 0; level < 4 && directory is not null; level++, directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.csproj").Any())
            {
                yield return directory.FullName;
                yield break;
            }
        }
    }
    
    private static IEnumerable<string> CandidateFiles(string directory)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                          ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                          ?? "Production";

        yield return Path.Combine(directory, $"appsettings.{environment}.json");
        yield return Path.Combine(directory, "appsettings.json");
    }
}
