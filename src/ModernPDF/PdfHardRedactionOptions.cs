namespace ModernPDF;

/// <summary>
/// Options for hard redaction operations that irreversibly hide matched content.
/// </summary>
public sealed class PdfHardRedactionOptions
{
    /// <summary>
    /// Reserved for API compatibility. Hard redaction always erases matched text operators.
    /// </summary>
    public bool PreserveWhitespace { get; init; } = true;

    /// <summary>
    /// Extra horizontal padding (in user units) added around generated blackout rectangles.
    /// </summary>
    public double HorizontalPadding { get; init; } = 0.5;

    /// <summary>
    /// Extra vertical padding (in user units) added around generated blackout rectangles.
    /// </summary>
    public double VerticalPadding { get; init; } = 1;

    /// <summary>
    /// Fill color used for generated blackout rectangles.
    /// </summary>
    public PdfRgbColor FillColor { get; init; } = PdfRgbColor.Black;
}
