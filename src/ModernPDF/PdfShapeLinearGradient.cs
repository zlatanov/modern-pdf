namespace ModernPDF;

public sealed class PdfShapeLinearGradient
{
    public double StartX { get; init; }

    public double StartY { get; init; }

    public double EndX { get; init; } = 1;

    public double EndY { get; init; }

    public PdfRgbColor StartColor { get; init; } = new(0, 0, 0);

    public PdfRgbColor EndColor { get; init; } = new(1, 1, 1);
}
