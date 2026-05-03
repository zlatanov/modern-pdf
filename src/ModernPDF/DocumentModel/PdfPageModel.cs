using ModernPDF.Primitives;
using ModernPDF.Format.Objects;

namespace ModernPDF.DocumentModel;

internal sealed class PdfPageModel
{
    public PdfPageModel(PdfObjectId objectId, PdfRectangle? mediaBox, PdfObject? resources, PdfObject? contents)
    {
        ObjectId = objectId;
        MediaBox = mediaBox;
        Resources = resources;
        Contents = contents;
    }

    public PdfObjectId ObjectId { get; }

    public PdfRectangle? MediaBox { get; }

    public PdfObject? Resources { get; }

    public PdfObject? Contents { get; }
}
