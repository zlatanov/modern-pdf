namespace ModernPDF;

/// <summary>
/// Represents an extracted text segment with page-relative bounds.
/// </summary>
public sealed class PdfTextRegion
{
    internal PdfTextRegion(
        int pageIndex,
        string text,
        double x,
        double y,
        double width,
        double height,
        int streamObjectNumber,
        int streamObjectGeneration,
        int stringTokenIndex,
        double fontSize)
    {
        PageIndex = pageIndex;
        Text = text;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        StreamObjectNumber = streamObjectNumber;
        StreamObjectGeneration = streamObjectGeneration;
        StringTokenIndex = stringTokenIndex;
        FontSize = fontSize;
    }

    /// <summary>Zero-based page index containing this text region.</summary>
    public int PageIndex { get; }

    /// <summary>Decoded text content represented by this region.</summary>
    public string Text { get; }

    /// <summary>Left X coordinate in user units.</summary>
    public double X { get; }

    /// <summary>Bottom Y coordinate in user units.</summary>
    public double Y { get; }

    /// <summary>Region width in user units.</summary>
    public double Width { get; }

    /// <summary>Region height in user units.</summary>
    public double Height { get; }

    internal int StreamObjectNumber { get; }

    internal int StreamObjectGeneration { get; }

    internal int StringTokenIndex { get; }

    internal double FontSize { get; }
}
