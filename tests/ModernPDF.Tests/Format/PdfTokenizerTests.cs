using ModernPDF.Format;

namespace ModernPDF.Tests.Format;

public sealed class PdfTokenizerTests
{
    [Fact]
    public void TokenizeReturnsEmptyForWhitespaceAndComments()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(" \r\n\t%comment\n%more\n ");

        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(bytes);

        Assert.Empty(tokens);
    }

    [Fact]
    public void TokenizeReadsCoreLiteralTokens()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("[ true false null 12 -4.5 /Name ]");

        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(bytes);

        Assert.Equal(PdfTokenKind.StartArray, tokens[0].Kind);
        Assert.Equal(PdfTokenKind.BooleanTrue, tokens[1].Kind);
        Assert.Equal(PdfTokenKind.BooleanFalse, tokens[2].Kind);
        Assert.Equal(PdfTokenKind.Null, tokens[3].Kind);
        Assert.Equal(PdfTokenKind.Integer, tokens[4].Kind);
        Assert.Equal("12", tokens[4].Lexeme);
        Assert.Equal(PdfTokenKind.Real, tokens[5].Kind);
        Assert.Equal("-4.5", tokens[5].Lexeme);
        Assert.Equal(PdfTokenKind.Name, tokens[6].Kind);
        Assert.Equal("Name", tokens[6].Lexeme);
        Assert.Equal(PdfTokenKind.EndArray, tokens[7].Kind);
    }

    [Fact]
    public void TokenizeReadsLiteralStringEscapesAndNestedParens()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("(A\\n\\r\\t\\b\\f\\(\\)\\\\(B))");

        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(bytes);

        PdfToken token = Assert.Single(tokens);
        Assert.Equal(PdfTokenKind.String, token.Kind);
        Assert.Equal("A\n\r\t\b\f()\\(B)", token.Lexeme);
    }

    [Fact]
    public void TokenizeReadsHexStringAndPadsOddNibble()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("<4F A>");

        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(bytes);

        PdfToken token = Assert.Single(tokens);
        Assert.Equal(PdfTokenKind.HexString, token.Kind);
        Assert.Equal("4FA0", token.Lexeme);
    }

    [Fact]
    public void TokenizeReadsDictionaryTokens()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("<< /Type /Page >>");

        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(bytes);

        Assert.Equal(PdfTokenKind.StartDictionary, tokens[0].Kind);
        Assert.Equal(PdfTokenKind.Name, tokens[1].Kind);
        Assert.Equal("Type", tokens[1].Lexeme);
        Assert.Equal(PdfTokenKind.Name, tokens[2].Kind);
        Assert.Equal("Page", tokens[2].Lexeme);
        Assert.Equal(PdfTokenKind.EndDictionary, tokens[3].Kind);
    }

    [Fact]
    public void TokenizeThrowsForInvalidNumericToken()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("-");

        Assert.Throws<PdfFormatException>(() => PdfTokenizer.Tokenize(bytes));
    }

    [Fact]
    public void TokenizeThrowsForUnterminatedLiteralStringEscape()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("(A\\");

        Assert.Throws<PdfFormatException>(() => PdfTokenizer.Tokenize(bytes));
    }

    [Fact]
    public void TokenizeThrowsForUnterminatedLiteralString()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("(ABC");

        Assert.Throws<PdfFormatException>(() => PdfTokenizer.Tokenize(bytes));
    }

    [Fact]
    public void TokenizeKeepsUnknownEscapedCharactersAsIs()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("(A\\zB)");

        PdfToken token = Assert.Single(PdfTokenizer.Tokenize(bytes));

        Assert.Equal(PdfTokenKind.String, token.Kind);
        Assert.Equal("AzB", token.Lexeme);
    }

    [Fact]
    public void TokenizeThrowsForUnterminatedHexString()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("<AB");

        Assert.Throws<PdfFormatException>(() => PdfTokenizer.Tokenize(bytes));
    }

    [Fact]
    public void TokenizeThrowsForInvalidHexCharacter()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("<AG>");

        Assert.Throws<PdfFormatException>(() => PdfTokenizer.Tokenize(bytes));
    }

    [Fact]
    public void TokenizeThrowsForUnexpectedGreaterThanToken()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(">");

        Assert.Throws<PdfFormatException>(() => PdfTokenizer.Tokenize(bytes));
    }

    [Fact]
    public void TokenizeThrowsWhenKeywordTokenCannotBeRead()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(")");

        Assert.Throws<PdfFormatException>(() => PdfTokenizer.Tokenize(bytes));
    }
}
