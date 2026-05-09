namespace ModernPDF;

/// <summary>
/// Options that control text layout, font selection, and shaping.
/// </summary>
public sealed class PdfTextOptions
{
    /// <summary>Font size in user units.</summary>
    public double FontSize { get; init; } = 12;

    /// <summary>Text origin X coordinate in user units.</summary>
    public double X { get; init; } = 72;

    /// <summary>Text origin Y coordinate in user units.</summary>
    public double Y { get; init; } = 720;

    /// <summary>
    /// Path to a TrueType/OpenType font file for embedded rendering. When omitted, built-in Helvetica flow is used.
    /// </summary>
    public string? TrueTypeFontPath { get; init; }

    /// <summary>
    /// When embedding, writes only glyphs used by the text rather than full font data.
    /// </summary>
    public bool SubsetFont { get; init; } = true;

    /// <summary>Maximum line width before wrapping. When omitted, no wrapping is applied.</summary>
    public double? MaxWidth { get; init; }

    /// <summary>Line height multiplier applied to <see cref="FontSize"/>.</summary>
    public double LineHeightMultiplier { get; init; } = 1.2;

    /// <summary>Alignment mode for wrapped lines.</summary>
    public PdfTextAlignment Alignment { get; init; } = PdfTextAlignment.Left;

    /// <summary>Direction hint for shaping and fallback selection.</summary>
    public PdfTextDirection Direction { get; init; } = PdfTextDirection.Auto;

    /// <summary>Writing mode for generated text layout.</summary>
    public PdfWritingMode WritingMode { get; init; } = PdfWritingMode.Horizontal;

    /// <summary>Enables simple word-level hyphenation during wrapping.</summary>
    public bool EnableHyphenation { get; init; }

    /// <summary>Additional fallback font paths checked when glyphs are missing.</summary>
    public IReadOnlyList<string>? FallbackTrueTypeFontPaths { get; init; }
}
