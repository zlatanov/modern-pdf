namespace ModernPDF;

/// <summary>
/// Affine transform matrix used for shape rendering (PDF <c>cm</c> semantics).
/// </summary>
public readonly struct PdfShapeTransform
{
    /// <summary>
    /// Initializes a transform from raw matrix components.
    /// </summary>
    public PdfShapeTransform(double a, double b, double c, double d, double e, double f)
    {
        ValidateFinite(a, nameof(a));
        ValidateFinite(b, nameof(b));
        ValidateFinite(c, nameof(c));
        ValidateFinite(d, nameof(d));
        ValidateFinite(e, nameof(e));
        ValidateFinite(f, nameof(f));

        A = a;
        B = b;
        C = c;
        D = d;
        E = e;
        F = f;
    }

    /// <summary>The identity transform.</summary>
    public static PdfShapeTransform Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>Matrix component <c>a</c>.</summary>
    public double A { get; }

    /// <summary>Matrix component <c>b</c>.</summary>
    public double B { get; }

    /// <summary>Matrix component <c>c</c>.</summary>
    public double C { get; }

    /// <summary>Matrix component <c>d</c>.</summary>
    public double D { get; }

    /// <summary>Matrix component <c>e</c> (translation X).</summary>
    public double E { get; }

    /// <summary>Matrix component <c>f</c> (translation Y).</summary>
    public double F { get; }

    /// <summary>Creates a translation transform.</summary>
    public static PdfShapeTransform Translate(double tx, double ty)
    {
        ValidateFinite(tx, nameof(tx));
        ValidateFinite(ty, nameof(ty));
        return new PdfShapeTransform(1, 0, 0, 1, tx, ty);
    }

    /// <summary>Creates a scaling transform.</summary>
    public static PdfShapeTransform Scale(double sx, double sy)
    {
        ValidateFinite(sx, nameof(sx));
        ValidateFinite(sy, nameof(sy));
        return new PdfShapeTransform(sx, 0, 0, sy, 0, 0);
    }

    /// <summary>Creates a rotation transform around the origin.</summary>
    public static PdfShapeTransform Rotate(double degrees)
    {
        ValidateFinite(degrees, nameof(degrees));
        double radians = degrees * Math.PI / 180d;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new PdfShapeTransform(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>Creates a rotation transform around a specific point.</summary>
    public static PdfShapeTransform RotateAt(double degrees, double centerX, double centerY)
    {
        ValidateFinite(centerX, nameof(centerX));
        ValidateFinite(centerY, nameof(centerY));
        return Translate(centerX, centerY)
            .Multiply(Rotate(degrees))
            .Multiply(Translate(-centerX, -centerY));
    }

    /// <summary>
    /// Multiplies this matrix by <paramref name="other"/> using PDF transform order.
    /// </summary>
    public PdfShapeTransform Multiply(PdfShapeTransform other)
    {
        double a = (A * other.A) + (B * other.C);
        double b = (A * other.B) + (B * other.D);
        double c = (C * other.A) + (D * other.C);
        double d = (C * other.B) + (D * other.D);
        double e = (E * other.A) + (F * other.C) + other.E;
        double f = (E * other.B) + (F * other.D) + other.F;
        return new PdfShapeTransform(a, b, c, d, e, f);
    }

    private static void ValidateFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(paramName, "Transform components must be finite.");
        }
    }
}
