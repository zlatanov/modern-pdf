using ModernPDF.Format;

namespace ModernPDF.Tests.Format;

public sealed class PdfTokenTests
{
    [Fact]
    public void ConstructorNormalizesNullLexemeToEmptyString()
    {
        PdfToken token = new(PdfTokenKind.Keyword, lexeme: null!);

        Assert.Equal(PdfTokenKind.Keyword, token.Kind);
        Assert.Equal(string.Empty, token.Lexeme);
    }

    [Fact]
    public void ToStringReturnsKindWhenLexemeIsEmpty()
    {
        PdfToken token = new(PdfTokenKind.StartDictionary);

        Assert.Equal("StartDictionary", token.ToString());
    }

    [Fact]
    public void ToStringReturnsKindAndLexemeWhenLexemeExists()
    {
        PdfToken token = new(PdfTokenKind.Name, "Type");

        Assert.Equal("Name(Type)", token.ToString());
    }
}
