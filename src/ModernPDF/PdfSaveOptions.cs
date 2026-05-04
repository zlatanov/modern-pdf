namespace ModernPDF;

public sealed class PdfSaveOptions
{
    public PdfSaveMode Mode { get; init; } = PdfSaveMode.Full;

    public PdfCrossReferenceStyle CrossReferenceStyle { get; init; } = PdfCrossReferenceStyle.Classic;

    public PdfSecurityOptions? Security { get; init; }
}
