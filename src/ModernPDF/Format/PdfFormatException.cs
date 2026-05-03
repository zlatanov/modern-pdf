namespace ModernPDF.Format;

internal sealed class PdfFormatException : Exception
{
    public PdfFormatException(string message)
        : base(message)
    {
    }
}
