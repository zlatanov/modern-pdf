namespace ModernPDF;

/// <summary>
/// Describes the encryption dictionary metadata discovered in a PDF file.
/// </summary>
public sealed class PdfEncryptionInfo
{
    /// <summary>
    /// Initializes an encryption metadata snapshot.
    /// </summary>
    public PdfEncryptionInfo(
        string? filter,
        string? subFilter,
        int? algorithmVersion,
        int? keyLengthBits)
    {
        Filter = filter;
        SubFilter = subFilter;
        AlgorithmVersion = algorithmVersion;
        KeyLengthBits = keyLengthBits;
    }

    /// <summary>The encryption handler filter name (for example, <c>Standard</c>).</summary>
    public string? Filter { get; }

    /// <summary>The encryption handler sub-filter, when present.</summary>
    public string? SubFilter { get; }

    /// <summary>The <c>/V</c> algorithm version from the encryption dictionary.</summary>
    public int? AlgorithmVersion { get; }

    /// <summary>The effective file-encryption key length in bits.</summary>
    public int? KeyLengthBits { get; }
}
