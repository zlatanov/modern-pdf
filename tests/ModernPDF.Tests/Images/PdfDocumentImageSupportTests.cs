using System.Text;

namespace ModernPDF.Tests;

public sealed class PdfDocumentImageSupportTests
{
    [Fact]
    public void AddImagePageEmbedsJpegImageXObject()
    {
        PdfDocument document = PdfDocument.Create();
        int pageIndex = document.AddImagePage(GetSampleJpegBytes());

        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);

        Assert.Equal(0, pageIndex);
        Assert.Equal(1, document.PageCount);
        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
        Assert.Contains("/Filter /DCTDecode", text, StringComparison.Ordinal);
        Assert.Contains("/Im1 Do", text, StringComparison.Ordinal);
        Assert.Equal(1, PdfDocument.Open(saved).PageCount);
    }

    [Fact]
    public void AddImagePageEmbedsPngImageXObject()
    {
        PdfDocument document = PdfDocument.Create();
        int pageIndex = document.AddImagePage(GetSamplePngBytes());

        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);

        Assert.Equal(0, pageIndex);
        Assert.Equal(1, document.PageCount);
        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
        Assert.Contains("/Filter /FlateDecode", text, StringComparison.Ordinal);
        Assert.Contains("/Predictor 15", text, StringComparison.Ordinal);
        Assert.Contains("/Im1 Do", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacePageImageReplacesExistingPageContent()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("before");

        document.ReplacePageImage(0, GetSampleJpegBytes());
        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);

        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
        Assert.Contains("/Im1 Do", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, PdfDocument.Open(saved).ExtractText());
    }

    [Fact]
    public void AddAndReplaceImageFromPathOverloadsWork()
    {
        string path = Path.Combine(Path.GetTempPath(), $"modernpdf-image-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, GetSampleJpegBytes());
            PdfDocument document = PdfDocument.Create();

            int pageIndex = document.AddImagePage(path);
            document.ReplacePageImage(pageIndex, path);
            document.AddPageImage(pageIndex, path, new PdfImageOptions { X = 8, Y = 8 });

            byte[] saved = document.Save();
            Assert.Equal(1, PdfDocument.Open(saved).PageCount);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void AddImagePageThrowsForUnsupportedImageFormat()
    {
        PdfDocument document = PdfDocument.Create();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => document.AddImagePage([0x00, 0x01, 0x02, 0x03]));
        Assert.Contains("JPEG", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddImagePageEmbedsPngImageWithAlphaUsingSoftMask()
    {
        PdfDocument document = PdfDocument.Create();
        int pageIndex = document.AddImagePage(GetSamplePngWithAlphaBytes());

        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);

        Assert.Equal(0, pageIndex);
        Assert.Contains("/SMask", text, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(text, "/Subtype /Image"));
        Assert.Contains("/Filter /FlateDecode", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacePageImagePreservesAspectRatioInsideBounds()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage(new PdfPageOptions { Width = 200, Height = 200 });
        document.ReplacePageImage(
            0,
            GetSampleJpegBytes(),
            new PdfImageOptions
            {
                X = 0,
                Y = 0,
                Width = 200,
                Height = 100,
                PreserveAspectRatio = true,
            });

        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);
        Assert.Contains("100 0 0 100 50 0 cm /Im1 Do", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageImagePreservesExistingPageText()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("before");
        document.AddPageImage(0, GetSampleJpegBytes(), new PdfImageOptions { X = 10, Y = 10 });

        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);

        Assert.Equal("before", PdfDocument.Open(saved).ExtractText());
        Assert.Contains("/Im1 Do", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPageImageSupportsMultipleImagesOnOnePage()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage(new PdfPageOptions { Width = 240, Height = 160 });
        byte[] sampleJpeg = GetSampleJpegBytes();

        document.AddPageImage(0, sampleJpeg, new PdfImageOptions { X = 0, Y = 0, Width = 80, Height = 80 });
        document.AddPageImage(0, sampleJpeg, new PdfImageOptions { X = 120, Y = 40, Width = 80, Height = 80 });

        byte[] saved = document.Save();
        string text = Encoding.ASCII.GetString(saved);

        Assert.Equal(2, CountOccurrences(text, "/Subtype /Image"));
        Assert.Contains("/Im1 Do", text, StringComparison.Ordinal);
        Assert.Contains("/Im2 Do", text, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string token)
    {
        int count = 0;
        int index = 0;
        while (true)
        {
            int found = source.IndexOf(token, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            index = found + token.Length;
        }
    }

    private static byte[] GetSampleJpegBytes()
    {
        return Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAAQABADASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAVEAEBAAAAAAAAAAAAAAAAAAABAP/aAAwDAQACEAMQAAAAhA//xAAVEAEBAAAAAAAAAAAAAAAAAAABAP/aAAgBAQABBQJf/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJf/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyFf/9k=");
    }

    private static byte[] GetSamplePngBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR42mP8z/C/HwAFgwJ/lvIprwAAAABJRU5ErkJggg==");
    }

    private static byte[] GetSamplePngWithAlphaBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO7Zk9sAAAAASUVORK5CYII=");
    }
}
