using ModernPDF.Format.Objects;

namespace ModernPDF.Format.Files;

internal sealed class PdfFile
{
    public PdfFile(string version, IEnumerable<PdfIndirectObject> objects, PdfDictionaryObject trailer)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("PDF version cannot be empty.", nameof(version));
        }

        Version = version;
        Objects = objects?.ToArray() ?? throw new ArgumentNullException(nameof(objects));
        Trailer = trailer ?? throw new ArgumentNullException(nameof(trailer));
    }

    public string Version { get; }

    public IReadOnlyList<PdfIndirectObject> Objects { get; }

    public PdfDictionaryObject Trailer { get; }
}
