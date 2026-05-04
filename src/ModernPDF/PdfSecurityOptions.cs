namespace ModernPDF;

public sealed class PdfSecurityOptions
{
    public string UserPassword { get; init; } = string.Empty;

    public string? OwnerPassword { get; init; }

    public PdfSecurityProfile Profile { get; init; } = PdfSecurityProfile.Standard40BitRc4;

    public PdfPermissions Permissions { get; init; } =
        PdfPermissions.Print | PdfPermissions.Modify | PdfPermissions.Copy | PdfPermissions.Annotate;
}
