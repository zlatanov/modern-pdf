namespace ModernPDF;

/// <summary>
/// Represents one text search match with page-relative bounds and an internal redaction anchor.
/// </summary>
public sealed class PdfTextMatch
{
    internal PdfTextMatch(
        int pageIndex,
        string text,
        double x,
        double y,
        double width,
        double height,
        int streamObjectNumber,
        int streamObjectGeneration,
        int stringTokenIndex,
        int startIndex,
        int length)
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
        StartIndex = startIndex;
        Length = length;
    }

    /// <summary>Zero-based page index containing this match.</summary>
    public int PageIndex { get; }

    /// <summary>Matched text value.</summary>
    public string Text { get; }

    /// <summary>Left X coordinate in user units.</summary>
    public double X { get; }

    /// <summary>Bottom Y coordinate in user units.</summary>
    public double Y { get; }

    /// <summary>Match width in user units.</summary>
    public double Width { get; }

    /// <summary>Match height in user units.</summary>
    public double Height { get; }

    internal int StreamObjectNumber { get; }

    internal int StreamObjectGeneration { get; }

    internal int StringTokenIndex { get; }

    internal int StartIndex { get; }

    internal int Length { get; }
}
