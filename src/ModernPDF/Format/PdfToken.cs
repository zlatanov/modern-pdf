namespace ModernPDF.Format;

internal readonly struct PdfToken
{
    public PdfToken(PdfTokenKind kind, string lexeme = "")
    {
        Kind = kind;
        Lexeme = lexeme ?? string.Empty;
    }

    public PdfTokenKind Kind { get; }

    public string Lexeme { get; }

    public override string ToString()
    {
        return string.IsNullOrEmpty(Lexeme) ? Kind.ToString() : $"{Kind}({Lexeme})";
    }
}
