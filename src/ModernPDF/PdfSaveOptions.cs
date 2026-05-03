namespace ModernPDF;

public sealed class PdfSaveOptions
{
    public PdfSaveMode Mode { get; init; } = PdfSaveMode.Full;

    public PdfSecurityOptions? Security { get; init; }
}
