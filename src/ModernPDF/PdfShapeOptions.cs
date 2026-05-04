namespace ModernPDF;

public sealed class PdfShapeOptions
{
    public PdfRgbColor? StrokeColor { get; init; } = PdfRgbColor.Black;

    public PdfRgbColor? FillColor { get; init; }

    public double StrokeWidth { get; init; } = 1;
}
