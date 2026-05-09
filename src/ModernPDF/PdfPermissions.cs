namespace ModernPDF;

/// <summary>
/// Permission flags encoded into Standard security encryption settings.
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>No permissions granted.</summary>
    None = 0,
    /// <summary>Allows document printing.</summary>
    Print = 1 << 0,
    /// <summary>Allows content modification.</summary>
    Modify = 1 << 1,
    /// <summary>Allows text and graphics extraction.</summary>
    Copy = 1 << 2,
    /// <summary>Allows annotation and form comment operations.</summary>
    Annotate = 1 << 3,
    /// <summary>Allows interactive form filling.</summary>
    FillForms = 1 << 4,
    /// <summary>Allows accessibility extraction operations.</summary>
    Accessibility = 1 << 5,
    /// <summary>Allows page insertion, rotation, and assembly operations.</summary>
    AssembleDocument = 1 << 6,
    /// <summary>Allows high-quality printing when supported by the profile.</summary>
    HighQualityPrint = 1 << 7,
    /// <summary>Grants all supported permissions.</summary>
    All =
        Print
        | Modify
        | Copy
        | Annotate
        | FillForms
        | Accessibility
        | AssembleDocument
        | HighQualityPrint,
}
