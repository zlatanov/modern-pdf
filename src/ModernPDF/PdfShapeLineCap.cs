namespace ModernPDF;

/// <summary>
/// Style used to render the ends of open stroked paths.
/// </summary>
public enum PdfShapeLineCap
{
    /// <summary>Flat cap ending exactly at the path endpoint.</summary>
    Butt = 0,
    /// <summary>Rounded cap with radius equal to half stroke width.</summary>
    Round = 1,
    /// <summary>Square cap extended by half stroke width beyond the endpoint.</summary>
    ProjectingSquare = 2,
}
