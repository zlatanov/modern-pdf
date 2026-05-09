namespace ModernPDF;

/// <summary>
/// A 2D point used by polygon APIs.
/// </summary>
public readonly struct PdfShapePoint
{
    /// <summary>
    /// Initializes a shape point.
    /// </summary>
    public PdfShapePoint(double x, double y)
    {
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Shape point X must be finite.");
        }

        if (!double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y), "Shape point Y must be finite.");
        }

        X = x;
        Y = y;
    }

    /// <summary>X coordinate in user units.</summary>
    public double X { get; }

    /// <summary>Y coordinate in user units.</summary>
    public double Y { get; }
}
