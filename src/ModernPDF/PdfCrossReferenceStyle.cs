namespace ModernPDF;

/// <summary>
/// Controls how cross-reference data is encoded when serializing a PDF file.
/// </summary>
public enum PdfCrossReferenceStyle
{
    /// <summary>Writes classic xref tables and trailers.</summary>
    Classic = 0,
    /// <summary>Writes cross-reference streams introduced in PDF 1.5.</summary>
    Stream = 1,
}
