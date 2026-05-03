namespace ModernPDF;

public sealed class PdfTextOptions
{
    public double FontSize { get; init; } = 12;

    public double X { get; init; } = 72;

    public double Y { get; init; } = 720;

    public string? TrueTypeFontPath { get; init; }

    public bool SubsetFont { get; init; } = true;
}
