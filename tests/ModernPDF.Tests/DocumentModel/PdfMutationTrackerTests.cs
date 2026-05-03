using ModernPDF.DocumentModel;
using ModernPDF.Primitives;

namespace ModernPDF.Tests.DocumentModel;

public sealed class PdfMutationTrackerTests
{
    [Fact]
    public void MarkDirtyTracksObjectAndIsDirtyReturnsTrue()
    {
        PdfMutationTracker tracker = new();
        PdfObjectId objectId = new(7, 0);

        tracker.MarkDirty(objectId);

        Assert.True(tracker.IsDirty(objectId));
        Assert.Contains(objectId, tracker.DirtyObjectIds);
    }

    [Fact]
    public void MarkCleanRemovesObjectFromDirtySet()
    {
        PdfMutationTracker tracker = new();
        PdfObjectId objectId = new(8, 0);
        tracker.MarkDirty(objectId);

        tracker.MarkClean(objectId);

        Assert.False(tracker.IsDirty(objectId));
        Assert.DoesNotContain(objectId, tracker.DirtyObjectIds);
    }

    [Fact]
    public void ClearRemovesAllDirtyObjects()
    {
        PdfMutationTracker tracker = new();
        tracker.MarkDirty(new PdfObjectId(1, 0));
        tracker.MarkDirty(new PdfObjectId(2, 0));

        tracker.Clear();

        Assert.Empty(tracker.DirtyObjectIds);
    }
}
