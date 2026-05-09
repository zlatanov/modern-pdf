namespace ModernPDF;

/// <summary>
/// Configures how a document is serialized.
/// </summary>
public sealed class PdfSaveOptions
{
    /// <summary>Determines whether save is full or incremental.</summary>
    public PdfSaveMode Mode { get; init; } = PdfSaveMode.Full;

    /// <summary>Chooses the cross-reference encoding style.</summary>
    public PdfCrossReferenceStyle CrossReferenceStyle { get; init; } = PdfCrossReferenceStyle.Classic;

    /// <summary>Applies or replaces Standard security encryption at save time.</summary>
    public PdfSecurityOptions? Security { get; init; }
}
