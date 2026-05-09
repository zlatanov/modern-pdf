namespace ModernPDF;

/// <summary>
/// Selects the compositing operator used when painting shape fill or stroke content.
/// </summary>
public enum PdfBlendMode
{
    /// <summary>Paints source colors directly over destination colors.</summary>
    Normal = 0,
    /// <summary>Multiplies source and destination colors to produce darker output.</summary>
    Multiply = 1,
    /// <summary>Inverts, multiplies, and inverts again to produce lighter output.</summary>
    Screen = 2,
    /// <summary>Combines multiply and screen depending on destination luminosity.</summary>
    Overlay = 3,
    /// <summary>Chooses the darker channel value from source and destination.</summary>
    Darken = 4,
    /// <summary>Chooses the lighter channel value from source and destination.</summary>
    Lighten = 5,
    /// <summary>Brightens destination colors to reflect source colors.</summary>
    ColorDodge = 6,
    /// <summary>Darkens destination colors to reflect source colors.</summary>
    ColorBurn = 7,
    /// <summary>Applies overlay using source as the controlling layer.</summary>
    HardLight = 8,
    /// <summary>Applies a softer contrast adjustment than <see cref="HardLight"/>.</summary>
    SoftLight = 9,
    /// <summary>Subtracts darker channels from lighter channels for high-contrast output.</summary>
    Difference = 10,
    /// <summary>Produces a low-contrast difference blend.</summary>
    Exclusion = 11,
}
