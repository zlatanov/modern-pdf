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
        document.AddRichTextPage(
        [
            new PdfTextSpan { Text = "Rich paragraph: " },
            new PdfTextSpan { Text = "office fi ffi ", FontSize = 18 },
            new PdfTextSpan { Text = "Arabic مرحبا بالعالم ", FontSize = 14 },
            new PdfTextSpan { Text = "Emoji 👩‍💻 fallback.", FontSize = 14 },
        ],
            textOptions: new PdfTextOptions
            {
                FontSize = 14,
                X = 72,
                Y = 740,
                TrueTypeFontPath = defaultText.TrueTypeFontPath,
                FallbackTrueTypeFontPaths = defaultText.FallbackTrueTypeFontPaths,
                SubsetFont = defaultText.SubsetFont,
                MaxWidth = 468,
                LineHeightMultiplier = 1.35,
                Alignment = PdfTextAlignment.Justify,
                Direction = PdfTextDirection.Auto,
            });

        document.AddTextPage(
            "Fallback demo 👩‍💻 with shaping and fallback chain.",
            textOptions: new PdfTextOptions
            {
                FontSize = 14,
                X = 72,
                Y = 720,
                TrueTypeFontPath = defaultText.TrueTypeFontPath,
                FallbackTrueTypeFontPaths = defaultText.FallbackTrueTypeFontPaths,
                SubsetFont = defaultText.SubsetFont,
                MaxWidth = 468,
                Alignment = PdfTextAlignment.Justify,
                Direction = PdfTextDirection.Auto,
            });

        document.AddTextPage(
            "Vertical sample",
            textOptions: new PdfTextOptions
            {
                FontSize = 16,
                X = 72,
                Y = 720,
                WritingMode = PdfWritingMode.Vertical,
                Direction = PdfTextDirection.RightToLeft,
                MaxWidth = 200,
                LineHeightMultiplier = 1.1,
            });
        document.SetInfoProducer("ModernPDF Samples");
        document.Save(outputPath);

        Console.WriteLine($"Created: {outputPath}");
    }
}
