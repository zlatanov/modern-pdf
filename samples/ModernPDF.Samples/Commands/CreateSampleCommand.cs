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
        SampleCommandHelpers.ConfigureDefaultTextOptions(document, fontSize: 24, x: 72, y: 720);
        document.AddTextPage(
            "Hello from ModernPDF!",
            new PdfPageOptions { Width = 612, Height = 792 });
        PdfTextOptions defaultText = document.DefaultTextOptions;
        document.DefaultTextOptions = new PdfTextOptions
        {
            FontSize = 14,
            X = 72,
            Y = 740,
            TrueTypeFontPath = defaultText.TrueTypeFontPath,
            SubsetFont = defaultText.SubsetFont,
            MaxWidth = 468,
            LineHeightMultiplier = 1.4,
            Alignment = PdfTextAlignment.Left,
            Direction = PdfTextDirection.Auto,
        };
        document.AddTextPage("Paragraph demo: office fi ffi, Arabic مرحبا بالعالم, and Emoji 👩‍💻 with automatic shaping, wrapping, and alignment.");
        document.SetInfoProducer("ModernPDF Samples");
        document.Save(outputPath);

        Console.WriteLine($"Created: {outputPath}");
    }
}
