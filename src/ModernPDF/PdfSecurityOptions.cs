namespace ModernPDF;

/// <summary>
/// Defines Standard security encryption settings used when saving a PDF.
/// </summary>
public sealed class PdfSecurityOptions
{
    /// <summary>
    /// User password used to open the document and derive encryption keys.
    /// </summary>
    public string UserPassword { get; init; } = string.Empty;

    /// <summary>
    /// Owner password for elevated permissions. Falls back to <see cref="UserPassword"/> when omitted.
    /// </summary>
    public string? OwnerPassword { get; init; }

    /// <summary>The Standard security profile (algorithm and revision).</summary>
    public PdfSecurityProfile Profile { get; init; } = PdfSecurityProfile.Standard40BitRc4;

    /// <summary>Permission flags encoded in the encryption dictionary.</summary>
    public PdfPermissions Permissions { get; init; } =
        PdfPermissions.Print | PdfPermissions.Modify | PdfPermissions.Copy | PdfPermissions.Annotate;
}
