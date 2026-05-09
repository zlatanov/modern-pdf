namespace ModernPDF;

/// <summary>
/// Horizontal alignment mode used when wrapping text.
/// </summary>
public enum PdfTextAlignment
{
    /// <summary>Aligns lines to the left edge.</summary>
    Left = 0,
    /// <summary>Centers each line between left and right bounds.</summary>
    Center = 1,
    /// <summary>Aligns lines to the right edge.</summary>
    Right = 2,
    /// <summary>Expands inter-word spacing so non-final lines fill the width.</summary>
    Justify = 3,
}
