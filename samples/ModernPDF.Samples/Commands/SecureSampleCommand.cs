using ModernPDF;
using System.CommandLine;
using System.Globalization;

namespace ModernPDF.Samples.Commands;

internal static class SecureSampleCommand
{
    public static Command Build()
    {
        Command command = new(
            "secure",
            "Save an encrypted PDF, inspect encryption info, and open it with a password.");
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
        const string userPassword = "user-pass";
        const string ownerPassword = "owner-pass";

        string outputRoot = SampleCommandHelpers.EnsureOutputDirectory(outputDirectory);
        string encryptedPath = Path.Combine(outputRoot, "secure.pdf");

        PdfDocument source = PdfDocument.Create();
        SampleCommandHelpers.ConfigureDefaultTextOptions(source);
        source.AddTextPage("Confidential: internal-only text.");
        source.Save(
            encryptedPath,
            new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = userPassword,
                    OwnerPassword = ownerPassword,
                    Permissions = PdfPermissions.Print | PdfPermissions.Copy,
                },
            });

        PdfEncryptionInfo? info = PdfDocument.InspectEncryption(encryptedPath);
        PdfDocument opened = PdfDocument.Open(encryptedPath, userPassword);

        Console.WriteLine($"Encrypted PDF: {encryptedPath}");
        Console.WriteLine(
            $"Encryption: Filter={info?.Filter ?? "n/a"}, V={info?.AlgorithmVersion?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}, KeyLength={info?.KeyLengthBits?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
        Console.WriteLine($"Extracted text with password: {opened.ExtractText()}");
    }
}
