namespace ModernPDF;

public sealed class PdfShapeOptions
{
    public PdfRgbColor? StrokeColor { get; init; } = PdfRgbColor.Black;

    public PdfRgbColor? FillColor { get; init; }

    public double StrokeWidth { get; init; } = 1;

    public PdfShapeLineCap StrokeLineCap { get; init; } = PdfShapeLineCap.Butt;

    public PdfShapeLineJoin StrokeLineJoin { get; init; } = PdfShapeLineJoin.Miter;

    public double StrokeMiterLimit { get; init; } = 10;

    public PdfShapeDashPattern? StrokeDashPattern { get; init; }

    public PdfShapeFillRule FillRule { get; init; } = PdfShapeFillRule.NonZero;

    public double? StrokeOpacity { get; init; }

    public double? FillOpacity { get; init; }

    public PdfBlendMode BlendMode { get; init; } = PdfBlendMode.Normal;

    public PdfShapeLinearGradient? FillLinearGradient { get; init; }

    public PdfShapeTransform Transform { get; init; } = PdfShapeTransform.Identity;

    public IReadOnlyList<PdfPathCommand>? ClipPath { get; init; }

    public string? ShapeId { get; init; }
}
