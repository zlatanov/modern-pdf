namespace ModernPDF;

/// <summary>
/// Chooses whether serialization rewrites the full file or appends an incremental update.
/// </summary>
public enum PdfSaveMode
{
    /// <summary>Rewrites the full document into a new PDF byte stream.</summary>
    Full = 0,
    /// <summary>Appends only changed objects as an incremental section.</summary>
    Incremental = 1,
}
