namespace ModernPDF;

public sealed class PdfTextSpan
{
    public string Text { get; init; } = string.Empty;

    public double? FontSize { get; init; }

    public string? TrueTypeFontPath { get; init; }
}
