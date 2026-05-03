using ModernPDF.Primitives;

namespace ModernPDF.DocumentModel;

internal sealed class PdfPageModel
{
    public PdfPageModel(PdfObjectId objectId, PdfRectangle? mediaBox)
    {
        ObjectId = objectId;
        MediaBox = mediaBox;
    }

    public PdfObjectId ObjectId { get; }

    public PdfRectangle? MediaBox { get; }
}
