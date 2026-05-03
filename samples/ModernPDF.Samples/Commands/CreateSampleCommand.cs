using ModernPDF;
using System.CommandLine;

namespace ModernPDF.Samples.Commands;

internal static class CreateSampleCommand
{
    public static Command Build()
    {
        Command command = new("create", "Create a PDF with text, metadata, and custom page sizing.");
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
        string outputPath = Path.Combine(outputRoot, "create.pdf");

        PdfDocument document = PdfDocument.Create();
        document.AddTextPage(
            "Hello from ModernPDF!",
            new PdfPageOptions { Width = 612, Height = 792 },
            new PdfTextOptions { FontSize = 24, X = 72, Y = 720 });
        document.SetInfoProducer("ModernPDF Samples");
        document.Save(outputPath);

        Console.WriteLine($"Created: {outputPath}");
    }
}
