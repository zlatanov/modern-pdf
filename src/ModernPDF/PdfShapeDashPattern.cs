namespace ModernPDF;

public sealed class PdfShapeDashPattern
{
    public IReadOnlyList<double> Segments { get; init; } = Array.Empty<double>();

    public double Phase { get; init; }
}
