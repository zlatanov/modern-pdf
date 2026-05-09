namespace ModernPDF;

/// <summary>
/// Defines the dimensions of a new page.
/// </summary>
public sealed class PdfPageOptions
{
    /// <summary>Page width in user units.</summary>
    public double Width { get; init; } = 595;

    /// <summary>Page height in user units.</summary>
    public double Height { get; init; } = 842;
}
