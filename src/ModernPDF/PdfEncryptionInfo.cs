namespace ModernPDF;

public sealed class PdfEncryptionInfo
{
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

    public string? Filter { get; }

    public string? SubFilter { get; }

    public int? AlgorithmVersion { get; }

    public int? KeyLengthBits { get; }
}
