namespace ModernPDF;

/// <summary>
/// Standard security handler profiles supported by the library.
/// </summary>
public enum PdfSecurityProfile
{
    /// <summary>V=1 / R=2 profile using 40-bit RC4.</summary>
    Standard40BitRc4 = 0,
    /// <summary>V=2 / R=3 profile using 128-bit RC4.</summary>
    Standard128BitRc4 = 1,
    /// <summary>V=4 / R=4 profile using 128-bit AES.</summary>
    Standard128BitAes = 2,
    /// <summary>V=5 / R=6 profile using 256-bit AES.</summary>
    Standard256BitAes = 3,
}
