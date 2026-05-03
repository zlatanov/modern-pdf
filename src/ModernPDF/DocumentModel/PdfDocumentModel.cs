using ModernPDF.Primitives;

namespace ModernPDF.DocumentModel;

internal sealed class PdfDocumentModel
{
    public PdfDocumentModel(PdfObjectId catalogObjectId, PdfObjectId pagesRootObjectId, IEnumerable<PdfPageModel> pages)
    {
        CatalogObjectId = catalogObjectId;
        PagesRootObjectId = pagesRootObjectId;
        Pages = pages?.ToArray() ?? throw new ArgumentNullException(nameof(pages));
    }

    public PdfObjectId CatalogObjectId { get; }

    public PdfObjectId PagesRootObjectId { get; }

    public IReadOnlyList<PdfPageModel> Pages { get; }
}
