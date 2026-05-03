using ModernPDF;
using System.CommandLine;

namespace ModernPDF.Samples.Commands;

internal static class RedactSampleCommand
{
    public static Command Build()
    {
        Command command = new("redact", "Redact target text and save the rewritten document.");
        Option<string> outputOption = SampleCommandHelpers.CreateOutputOption();
        command.Add(outputOption);
        command.SetAction(parseResult =>
        {
            Execute(parseResult.GetRequiredValue(outputOption));
            return 0;
        });
        return command;
    }

    private static void Execute(string outputDirectory)
    {
        string outputRoot = SampleCommandHelpers.EnsureOutputDirectory(outputDirectory);
        string originalPath = Path.Combine(outputRoot, "redact-original.pdf");
        string redactedPath = Path.Combine(outputRoot, "redact-redacted.pdf");

        PdfDocument source = PdfDocument.Create();
        source.AddTextPage("Customer SSN: 111-22-3333");
        source.Save(originalPath);

        PdfDocument editable = PdfDocument.Open(originalPath);
        int replacements = editable.RedactText("111-22-3333", "[REDACTED]");
        editable.Save(redactedPath);

        Console.WriteLine($"Original: {originalPath}");
        Console.WriteLine($"Redacted: {redactedPath}");
        Console.WriteLine($"Replacements applied: {replacements}");
        Console.WriteLine($"Redacted text: {PdfDocument.Open(redactedPath).ExtractText()}");
    }
}
