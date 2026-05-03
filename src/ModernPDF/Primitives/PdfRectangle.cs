namespace ModernPDF.Primitives;

internal readonly struct PdfRectangle : IEquatable<PdfRectangle>
{
    public PdfRectangle(double left, double bottom, double right, double top)
    {
        EnsureFinite(left, nameof(left));
        EnsureFinite(bottom, nameof(bottom));
        EnsureFinite(right, nameof(right));
        EnsureFinite(top, nameof(top));

        if (right < left)
        {
            throw new ArgumentOutOfRangeException(nameof(right), "Right edge cannot be less than left edge.");
        }

        if (top < bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(top), "Top edge cannot be less than bottom edge.");
        }

        Left = left;
        Bottom = bottom;
        Right = right;
        Top = top;
    }

    public double Left { get; }

    public double Bottom { get; }

    public double Right { get; }

    public double Top { get; }

    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool Contains(PdfPoint point)
    {
        return point.X >= Left && point.X <= Right && point.Y >= Bottom && point.Y <= Top;
    }

    public static PdfRectangle FromDimensions(double x, double y, double width, double height)
    {
        EnsureFinite(x, nameof(x));
        EnsureFinite(y, nameof(y));
        EnsureFinite(width, nameof(width));
        EnsureFinite(height, nameof(height));

        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        return new PdfRectangle(x, y, x + width, y + height);
    }

    public bool Equals(PdfRectangle other)
    {
        return Left.Equals(other.Left)
            && Bottom.Equals(other.Bottom)
            && Right.Equals(other.Right)
            && Top.Equals(other.Top);
    }

    public override bool Equals(object? obj)
    {
        return obj is PdfRectangle other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Left, Bottom, Right, Top);
    }

    public static bool operator ==(PdfRectangle left, PdfRectangle right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(PdfRectangle left, PdfRectangle right)
    {
        return !(left == right);
    }

    private static void EnsureFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(paramName, "Value must be a finite number.");
        }
    }
}
