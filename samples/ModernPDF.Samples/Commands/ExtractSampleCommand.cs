using ModernPDF;
using System.CommandLine;

namespace ModernPDF.Samples.Commands;

internal static class ExtractSampleCommand
{
    public static Command Build()
    {
        Command command = new("extract", "Open a PDF and extract text from all pages.");
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
        string inputPath = Path.Combine(outputRoot, "extract-input.pdf");
        string extractedPath = Path.Combine(outputRoot, "extract-output.txt");

        PdfDocument source = PdfDocument.Create();
        source.AddTextPage("Page one text.");
        source.AddTextPage("Page two text.");
        source.Save(inputPath);

        PdfDocument opened = PdfDocument.Open(inputPath);
        string text = opened.ExtractText();
        File.WriteAllText(extractedPath, text);

        Console.WriteLine($"Input PDF: {inputPath}");
        Console.WriteLine($"Extracted text file: {extractedPath}");
        Console.WriteLine("Extracted text:");
        Console.WriteLine(text);
    }
}
