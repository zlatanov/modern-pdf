using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Format.Files;

internal sealed class PdfIndirectObject
{
    public PdfIndirectObject(PdfObjectId id, PdfObject value)
    {
        ObjectId = id;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public PdfObjectId ObjectId { get; }

    public PdfObject Value { get; }
}
