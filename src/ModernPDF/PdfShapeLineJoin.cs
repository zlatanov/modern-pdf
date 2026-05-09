namespace ModernPDF;

/// <summary>
/// Style used at corners where stroked path segments join.
/// </summary>
public enum PdfShapeLineJoin
{
    /// <summary>Sharp miter corner limited by miter limit.</summary>
    Miter = 0,
    /// <summary>Rounded corner join.</summary>
    Round = 1,
    /// <summary>Beveled corner join.</summary>
    Bevel = 2,
}
