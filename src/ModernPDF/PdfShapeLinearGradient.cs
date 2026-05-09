namespace ModernPDF;

/// <summary>
/// Describes a two-stop linear gradient for shape filling.
/// </summary>
public sealed class PdfShapeLinearGradient
{
    /// <summary>Gradient start point X coordinate.</summary>
    public double StartX { get; init; }

    /// <summary>Gradient start point Y coordinate.</summary>
    public double StartY { get; init; }

    /// <summary>Gradient end point X coordinate.</summary>
    public double EndX { get; init; } = 1;

    /// <summary>Gradient end point Y coordinate.</summary>
    public double EndY { get; init; }

    /// <summary>Color at the gradient start point.</summary>
    public PdfRgbColor StartColor { get; init; } = new(0, 0, 0);

    /// <summary>Color at the gradient end point.</summary>
    public PdfRgbColor EndColor { get; init; } = new(1, 1, 1);
}
