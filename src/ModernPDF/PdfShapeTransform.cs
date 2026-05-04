namespace ModernPDF;

public readonly struct PdfShapeTransform
{
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

    public static PdfShapeTransform Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public double A { get; }

    public double B { get; }

    public double C { get; }

    public double D { get; }

    public double E { get; }

    public double F { get; }

    public static PdfShapeTransform Translate(double tx, double ty)
    {
        ValidateFinite(tx, nameof(tx));
        ValidateFinite(ty, nameof(ty));
        return new PdfShapeTransform(1, 0, 0, 1, tx, ty);
    }

    public static PdfShapeTransform Scale(double sx, double sy)
    {
        ValidateFinite(sx, nameof(sx));
        ValidateFinite(sy, nameof(sy));
        return new PdfShapeTransform(sx, 0, 0, sy, 0, 0);
    }

    public static PdfShapeTransform Rotate(double degrees)
    {
        ValidateFinite(degrees, nameof(degrees));
        double radians = degrees * Math.PI / 180d;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new PdfShapeTransform(cos, sin, -sin, cos, 0, 0);
    }

    public static PdfShapeTransform RotateAt(double degrees, double centerX, double centerY)
    {
        ValidateFinite(centerX, nameof(centerX));
        ValidateFinite(centerY, nameof(centerY));
        return Translate(centerX, centerY)
            .Multiply(Rotate(degrees))
            .Multiply(Translate(-centerX, -centerY));
    }

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
