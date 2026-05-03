namespace ModernPDF;

[Flags]
public enum PdfPermissions
{
    None = 0,
    Print = 1 << 0,
    Modify = 1 << 1,
    Copy = 1 << 2,
    Annotate = 1 << 3,
    FillForms = 1 << 4,
    Accessibility = 1 << 5,
    AssembleDocument = 1 << 6,
    HighQualityPrint = 1 << 7,
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
