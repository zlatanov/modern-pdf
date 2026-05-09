namespace ModernPDF;

/// <summary>
/// Base type for vector path commands consumed by shape APIs.
/// </summary>
public abstract class PdfPathCommand
{
}

/// <summary>
/// Moves the current path point without drawing.
/// </summary>
public sealed class PdfPathMoveTo : PdfPathCommand
{
    /// <summary>
    /// Initializes a move-to command.
    /// </summary>
    public PdfPathMoveTo(double x, double y)
    {
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Path move X must be finite.");
        }

        if (!double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y), "Path move Y must be finite.");
        }

        X = x;
        Y = y;
    }

    /// <summary>Destination X coordinate.</summary>
    public double X { get; }

    /// <summary>Destination Y coordinate.</summary>
    public double Y { get; }
}

/// <summary>
/// Draws a straight line from the current point to a new point.
/// </summary>
public sealed class PdfPathLineTo : PdfPathCommand
{
    /// <summary>
    /// Initializes a line-to command.
    /// </summary>
    public PdfPathLineTo(double x, double y)
    {
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Path line X must be finite.");
        }

        if (!double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y), "Path line Y must be finite.");
        }

        X = x;
        Y = y;
    }

    /// <summary>Line end-point X coordinate.</summary>
    public double X { get; }

    /// <summary>Line end-point Y coordinate.</summary>
    public double Y { get; }
}

/// <summary>
/// Draws a cubic Bezier segment from the current point.
/// </summary>
public sealed class PdfPathCurveTo : PdfPathCommand
{
    /// <summary>
    /// Initializes a cubic Bezier curve command.
    /// </summary>
    public PdfPathCurveTo(
        double control1X,
        double control1Y,
        double control2X,
        double control2Y,
        double endX,
        double endY)
    {
        if (!double.IsFinite(control1X))
        {
            throw new ArgumentOutOfRangeException(nameof(control1X), "Path first control point X must be finite.");
        }

        if (!double.IsFinite(control1Y))
        {
            throw new ArgumentOutOfRangeException(nameof(control1Y), "Path first control point Y must be finite.");
        }

        if (!double.IsFinite(control2X))
        {
            throw new ArgumentOutOfRangeException(nameof(control2X), "Path second control point X must be finite.");
        }

        if (!double.IsFinite(control2Y))
        {
            throw new ArgumentOutOfRangeException(nameof(control2Y), "Path second control point Y must be finite.");
        }

        if (!double.IsFinite(endX))
        {
            throw new ArgumentOutOfRangeException(nameof(endX), "Path end point X must be finite.");
        }

        if (!double.IsFinite(endY))
        {
            throw new ArgumentOutOfRangeException(nameof(endY), "Path end point Y must be finite.");
        }

        Control1X = control1X;
        Control1Y = control1Y;
        Control2X = control2X;
        Control2Y = control2Y;
        EndX = endX;
        EndY = endY;
    }

    /// <summary>First control point X coordinate.</summary>
    public double Control1X { get; }

    /// <summary>First control point Y coordinate.</summary>
    public double Control1Y { get; }

    /// <summary>Second control point X coordinate.</summary>
    public double Control2X { get; }

    /// <summary>Second control point Y coordinate.</summary>
    public double Control2Y { get; }

    /// <summary>Curve end-point X coordinate.</summary>
    public double EndX { get; }

    /// <summary>Curve end-point Y coordinate.</summary>
    public double EndY { get; }
}

/// <summary>
/// Closes the current subpath by connecting the endpoint to the starting point.
/// </summary>
public sealed class PdfPathClosePath : PdfPathCommand
{
}
