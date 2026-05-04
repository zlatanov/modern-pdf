namespace ModernPDF;

public readonly struct PdfRgbColor
{
    public PdfRgbColor(double red, double green, double blue)
    {
        ValidateComponent(red, nameof(red));
        ValidateComponent(green, nameof(green));
        ValidateComponent(blue, nameof(blue));
        Red = red;
        Green = green;
        Blue = blue;
    }

    public static PdfRgbColor Black { get; } = new(0, 0, 0);

    public double Red { get; }

    public double Green { get; }

    public double Blue { get; }

    private static void ValidateComponent(double component, string paramName)
    {
        if (!double.IsFinite(component) || component < 0 || component > 1)
        {
            throw new ArgumentOutOfRangeException(paramName, "RGB components must be finite numbers in the range [0, 1].");
        }
    }
}
