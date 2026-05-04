using ModernPDF.Format.Objects;

namespace ModernPDF.Format.Files;

internal sealed class PdfFile
{
    public PdfFile(
        string version,
        IEnumerable<PdfIndirectObject> objects,
        PdfDictionaryObject trailer,
        byte[]? sourceBytes = null,
        int? startXrefOffset = null,
        IReadOnlyDictionary<int, PdfXrefEntry>? xrefEntries = null)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("PDF version cannot be empty.", nameof(version));
        }

        Version = version;
        Objects = objects?.ToArray() ?? throw new ArgumentNullException(nameof(objects));
        Trailer = trailer ?? throw new ArgumentNullException(nameof(trailer));
        SourceBytes = sourceBytes;
        StartXrefOffset = startXrefOffset;
        XrefEntries = xrefEntries is null
            ? new Dictionary<int, PdfXrefEntry>()
            : new Dictionary<int, PdfXrefEntry>(xrefEntries);
    }

    public string Version { get; }

    public IReadOnlyList<PdfIndirectObject> Objects { get; }

    public PdfDictionaryObject Trailer { get; }

    public byte[]? SourceBytes { get; }

    public int? StartXrefOffset { get; }

    public IReadOnlyDictionary<int, PdfXrefEntry> XrefEntries { get; }
}
