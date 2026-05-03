namespace ModernPDF;

public sealed class PdfSecurityOptions
{
    public string UserPassword { get; init; } = string.Empty;

    public string? OwnerPassword { get; init; }

    public PdfPermissions Permissions { get; init; } =
        PdfPermissions.Print | PdfPermissions.Modify | PdfPermissions.Copy | PdfPermissions.Annotate;
}
