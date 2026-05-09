namespace ModernPDF;

/// <summary>
/// Text direction hint used for shaping and fallback behavior.
/// </summary>
public enum PdfTextDirection
{
    /// <summary>Automatically detects direction from text content.</summary>
    Auto = 0,
    /// <summary>Forces left-to-right shaping.</summary>
    LeftToRight = 1,
    /// <summary>Forces right-to-left shaping.</summary>
    RightToLeft = 2,
}
