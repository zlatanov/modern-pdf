namespace ModernPDF;

public abstract class PdfPathCommand
{
}

public sealed class PdfPathMoveTo : PdfPathCommand
{
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

    public double X { get; }

    public double Y { get; }
}

public sealed class PdfPathLineTo : PdfPathCommand
{
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

    public double X { get; }

    public double Y { get; }
}

public sealed class PdfPathCurveTo : PdfPathCommand
{
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

    public double Control1X { get; }

    public double Control1Y { get; }

    public double Control2X { get; }

    public double Control2Y { get; }

    public double EndX { get; }

    public double EndY { get; }
}

public sealed class PdfPathClosePath : PdfPathCommand
{
}
