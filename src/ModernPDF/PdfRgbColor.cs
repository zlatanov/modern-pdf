namespace ModernPDF;

/// <summary>
/// Represents an RGB color with normalized channel values in the range [0, 1].
/// </summary>
public readonly struct PdfRgbColor
{
    /// <summary>
    /// Initializes a color from normalized red, green, and blue channels.
    /// </summary>
    public PdfRgbColor(double red, double green, double blue)
    {
        ValidateComponent(red, nameof(red));
        ValidateComponent(green, nameof(green));
        ValidateComponent(blue, nameof(blue));
        Red = red;
        Green = green;
        Blue = blue;
    }

    /// <summary>Pure black (<c>0,0,0</c>).</summary>
    public static PdfRgbColor Black { get; } = new(0, 0, 0);

    /// <summary>The red channel in the range [0, 1].</summary>
    public double Red { get; }

    /// <summary>The green channel in the range [0, 1].</summary>
    public double Green { get; }

    /// <summary>The blue channel in the range [0, 1].</summary>
    public double Blue { get; }

    private static void ValidateComponent(double component, string paramName)
    {
        if (!double.IsFinite(component) || component < 0 || component > 1)
        {
            throw new ArgumentOutOfRangeException(paramName, "RGB components must be finite numbers in the range [0, 1].");
        }
    }
}
