using ModernPDF.Primitives;

namespace ModernPDF.DocumentModel;

/// <summary>
/// Internal snapshot of resolved document structure and mutation tracking state.
/// </summary>
internal sealed class PdfDocumentModel
{
    public PdfDocumentModel(
        PdfObjectId catalogObjectId,
        PdfObjectId pagesRootObjectId,
        PdfObjectId? metadataObjectId,
        PdfObjectId? infoObjectId,
        IEnumerable<PdfPageModel> pages)
    {
        CatalogObjectId = catalogObjectId;
        PagesRootObjectId = pagesRootObjectId;
        MetadataObjectId = metadataObjectId;
        InfoObjectId = infoObjectId;
        Pages = pages?.ToArray() ?? throw new ArgumentNullException(nameof(pages));
        Mutations = new PdfMutationTracker();
    }

    public PdfObjectId CatalogObjectId { get; }

    public PdfObjectId PagesRootObjectId { get; }

    public PdfObjectId? MetadataObjectId { get; }

    public PdfObjectId? InfoObjectId { get; }

    public IReadOnlyList<PdfPageModel> Pages { get; }

    public PdfMutationTracker Mutations { get; }
}
