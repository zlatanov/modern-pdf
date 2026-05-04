using ModernPDF;
using System.CommandLine;

namespace ModernPDF.Samples.Commands;

internal static class SampleCommandHelpers
{
    private const string DefaultOutputDirectoryName = "sample-output";
    private static readonly string[] WindowsTrueTypeCandidates = ["segoeui.ttf", "arial.ttf", "calibri.ttf", "times.ttf"];

    public static Option<string> CreateOutputOption()
    {
        Option<string> outputOption = new("--output", "-o")
        {
            Description = "Writes generated files to this directory.",
            DefaultValueFactory = static _ => Path.Combine(Environment.CurrentDirectory, DefaultOutputDirectoryName),
        };
        return outputOption;
    }

    public static string EnsureOutputDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string fullPath = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public static void ConfigureDefaultTextOptions(PdfDocument document, double fontSize = 12, double x = 72, double y = 720)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<string> availableFonts = GetAvailableWindowsTrueTypeFonts();
        string? trueTypeFontPath = availableFonts.Count > 0 ? availableFonts[0] : null;
        document.DefaultTextOptions = trueTypeFontPath is null
            ? new PdfTextOptions { FontSize = fontSize, X = x, Y = y }
            : new PdfTextOptions
            {
                FontSize = fontSize,
                X = x,
                Y = y,
                TrueTypeFontPath = trueTypeFontPath,
                FallbackTrueTypeFontPaths = availableFonts.Skip(1).ToArray(),
            };
    }

    private static List<string> GetAvailableWindowsTrueTypeFonts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return [];
        }

        string fontsDirectory = Path.Combine(windowsDirectory, "Fonts");
        if (!Directory.Exists(fontsDirectory))
        {
            return [];
        }

        List<string> available = [];
        foreach (string candidate in WindowsTrueTypeCandidates)
        {
            string candidatePath = Path.Combine(fontsDirectory, candidate);
            if (File.Exists(candidatePath))
            {
                available.Add(candidatePath);
            }
        }

        return available;
    }
}
