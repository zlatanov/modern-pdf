namespace ModernPDF;

/// <summary>
/// Defines a stroke dash pattern.
/// </summary>
public sealed class PdfShapeDashPattern
{
    /// <summary>
    /// Alternating dash and gap lengths in user units.
    /// </summary>
    public IReadOnlyList<double> Segments { get; init; } = Array.Empty<double>();

    /// <summary>
    /// Dash pattern phase offset in user units.
    /// </summary>
    public double Phase { get; init; }
}
