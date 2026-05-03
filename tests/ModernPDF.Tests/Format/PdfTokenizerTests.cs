using System.Text;
using ModernPDF.Format;

namespace ModernPDF.Tests.Format;

public sealed class PdfTokenizerTests
{
    [Fact]
    public void TokenizeHandlesCommentsAndDictionaryTokens()
    {
        byte[] data = Encoding.ASCII.GetBytes("% Comment\n<< /Type /Example /Count 2 >>");

        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(data);

        Assert.Collection(
            tokens,
            token => Assert.Equal(PdfTokenKind.StartDictionary, token.Kind),
            token =>
            {
                Assert.Equal(PdfTokenKind.Name, token.Kind);
                Assert.Equal("Type", token.Lexeme);
            },
            token =>
            {
                Assert.Equal(PdfTokenKind.Name, token.Kind);
                Assert.Equal("Example", token.Lexeme);
            },
            token =>
            {
                Assert.Equal(PdfTokenKind.Name, token.Kind);
                Assert.Equal("Count", token.Lexeme);
            },
            token =>
            {
                Assert.Equal(PdfTokenKind.Integer, token.Kind);
                Assert.Equal("2", token.Lexeme);
            },
            token => Assert.Equal(PdfTokenKind.EndDictionary, token.Kind));
    }

    [Fact]
    public void TokenizeParsesEscapedLiteralString()
    {
        byte[] data = Encoding.ASCII.GetBytes("(Hello \\(PDF\\))");

        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(data);

        Assert.Single(tokens);
        Assert.Equal(PdfTokenKind.String, tokens[0].Kind);
        Assert.Equal("Hello (PDF)", tokens[0].Lexeme);
    }
}
