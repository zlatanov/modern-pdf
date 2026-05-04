namespace ModernPDF;

public sealed class PdfImageOptions
{
    public double X { get; init; }

    public double Y { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }

    public bool PreserveAspectRatio { get; init; } = true;
}
