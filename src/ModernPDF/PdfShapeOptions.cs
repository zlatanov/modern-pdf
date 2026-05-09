namespace ModernPDF;

/// <summary>
/// Style and behavior options applied when drawing vector shapes.
/// </summary>
public sealed class PdfShapeOptions
{
    /// <summary>Stroke color; set to <see langword="null"/> to disable stroke.</summary>
    public PdfRgbColor? StrokeColor { get; init; } = PdfRgbColor.Black;

    /// <summary>Fill color; set to <see langword="null"/> to disable solid fill.</summary>
    public PdfRgbColor? FillColor { get; init; }

    /// <summary>Stroke width in user units.</summary>
    public double StrokeWidth { get; init; } = 1;

    /// <summary>Line-cap style for stroked open paths.</summary>
    public PdfShapeLineCap StrokeLineCap { get; init; } = PdfShapeLineCap.Butt;

    /// <summary>Line-join style for stroked corners.</summary>
    public PdfShapeLineJoin StrokeLineJoin { get; init; } = PdfShapeLineJoin.Miter;

    /// <summary>Miter limit used when <see cref="StrokeLineJoin"/> is <see cref="PdfShapeLineJoin.Miter"/>.</summary>
    public double StrokeMiterLimit { get; init; } = 10;

    /// <summary>Dash pattern for stroke rendering.</summary>
    public PdfShapeDashPattern? StrokeDashPattern { get; init; }

    /// <summary>Fill rule used for closed paths.</summary>
    public PdfShapeFillRule FillRule { get; init; } = PdfShapeFillRule.NonZero;

    /// <summary>Stroke opacity in the range [0, 1].</summary>
    public double? StrokeOpacity { get; init; }

    /// <summary>Fill opacity in the range [0, 1].</summary>
    public double? FillOpacity { get; init; }

    /// <summary>Blend mode used while painting the shape.</summary>
    public PdfBlendMode BlendMode { get; init; } = PdfBlendMode.Normal;

    /// <summary>Optional linear gradient that overrides <see cref="FillColor"/>.</summary>
    public PdfShapeLinearGradient? FillLinearGradient { get; init; }

    /// <summary>Transformation matrix applied to the path before painting.</summary>
    public PdfShapeTransform Transform { get; init; } = PdfShapeTransform.Identity;

    /// <summary>Optional clipping path applied while painting this shape.</summary>
    public IReadOnlyList<PdfPathCommand>? ClipPath { get; init; }

    /// <summary>Optional identifier marker used for later lookup/replacement/removal.</summary>
    public string? ShapeId { get; init; }
}
