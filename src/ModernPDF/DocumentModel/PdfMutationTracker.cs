using ModernPDF.Primitives;

namespace ModernPDF.DocumentModel;

internal sealed class PdfMutationTracker
{
    private readonly HashSet<PdfObjectId> _dirtyObjects = [];

    public IReadOnlyCollection<PdfObjectId> DirtyObjectIds => _dirtyObjects;

    public bool IsDirty(PdfObjectId objectId)
    {
        return _dirtyObjects.Contains(objectId);
    }

    public void MarkDirty(PdfObjectId objectId)
    {
        _dirtyObjects.Add(objectId);
    }

    public void MarkClean(PdfObjectId objectId)
    {
        _dirtyObjects.Remove(objectId);
    }

    public void Clear()
    {
        _dirtyObjects.Clear();
    }
}
