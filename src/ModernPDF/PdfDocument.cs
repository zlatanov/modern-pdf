namespace ModernPDF;

public sealed class PdfDocument
{
    private PdfDocument()
    {
    }

    public static PdfDocument Create()
    {
        return new PdfDocument();
    }
}
