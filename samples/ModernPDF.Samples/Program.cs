using ModernPDF.Samples.Commands;
using System.CommandLine;

var doc = ModernPDF.PdfDocument.Open(@"D:\Docs\ЕЦЗ Uchreditelen_Act_.pdf");

doc.HardRedactText("Ирина Стефанова Цакова", new ModernPDF.PdfHardRedactionOptions
{
    HorizontalPadding = 0
});
doc.Save(@"D:\Docs\ЕЦЗ Uchreditelen_Act_redacted.pdf");


//RootCommand rootCommand = new("ModernPDF sample commands.");
//rootCommand.Add(CreateSampleCommand.Build());
//rootCommand.Add(ExtractSampleCommand.Build());
//rootCommand.Add(RedactSampleCommand.Build());
//rootCommand.Add(SecureSampleCommand.Build());

//ParseResult parseResult = rootCommand.Parse(args);
//return parseResult.Invoke(new InvocationConfiguration());
