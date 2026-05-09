namespace ModernPDF;

/// <summary>
/// Represents one style run in rich text APIs.
/// </summary>
public sealed class PdfTextSpan
{
    /// <summary>Span text content.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Optional font size override for this span.</summary>
    public double? FontSize { get; init; }

    /// <summary>Optional primary font override for this span.</summary>
    public string? TrueTypeFontPath { get; init; }

    /// <summary>Optional fallback fonts used for this span.</summary>
    public IReadOnlyList<string>? FallbackTrueTypeFontPaths { get; init; }
}
