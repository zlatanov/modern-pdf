using System.CommandLine;

namespace ModernPDF.Samples.Commands;

internal static class SampleCommandHelpers
{
    private const string DefaultOutputDirectoryName = "sample-output";

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
}
