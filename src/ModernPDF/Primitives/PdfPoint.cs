namespace ModernPDF.Primitives;

internal readonly record struct PdfPoint(double X, double Y)
{
    public PdfPoint Translate(double dx, double dy)
    {
        return new PdfPoint(X + dx, Y + dy);
    }
}
