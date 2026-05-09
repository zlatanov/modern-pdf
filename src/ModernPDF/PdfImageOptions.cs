namespace ModernPDF;

/// <summary>
/// Controls image placement and scaling when drawing raster images on a page.
/// </summary>
public sealed class PdfImageOptions
{
    /// <summary>The destination X coordinate in user units.</summary>
    public double X { get; init; }

    /// <summary>The destination Y coordinate in user units.</summary>
    public double Y { get; init; }

    /// <summary>The target width in user units. When omitted, image-native width is used.</summary>
    public double? Width { get; init; }

    /// <summary>The target height in user units. When omitted, image-native height is used.</summary>
    public double? Height { get; init; }

    /// <summary>
    /// Preserves source aspect ratio while fitting into the destination box.
    /// </summary>
    public bool PreserveAspectRatio { get; init; } = true;
}
