namespace ModernPDF;

/// <summary>
/// Declares which parts of a matched value should stay visible during soft redaction.
/// </summary>
public sealed class PdfSoftRedactionDirective
{
    /// <summary>Number of text elements kept from the start of the match.</summary>
    public int KeepPrefixCharacters { get; init; }

    /// <summary>Number of text elements kept from the end of the match.</summary>
    public int KeepSuffixCharacters { get; init; }

    /// <summary>Creates a directive that keeps only a trailing suffix.</summary>
    public static PdfSoftRedactionDirective KeepSuffix(int count)
    {
        return new PdfSoftRedactionDirective { KeepSuffixCharacters = count };
    }

    /// <summary>Creates a directive that keeps only a leading prefix.</summary>
    public static PdfSoftRedactionDirective KeepPrefix(int count)
    {
        return new PdfSoftRedactionDirective { KeepPrefixCharacters = count };
    }
}
