namespace ModernPDF;

/// <summary>
/// Fill rule used for path interiors.
/// </summary>
public enum PdfShapeFillRule
{
    /// <summary>Uses non-zero winding number rule.</summary>
    NonZero = 0,
    /// <summary>Uses even-odd crossing rule.</summary>
    EvenOdd = 1,
}
