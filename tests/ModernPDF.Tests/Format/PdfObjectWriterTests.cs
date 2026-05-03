using System.Text;
using ModernPDF.Format;
using ModernPDF.Format.Objects;

namespace ModernPDF.Tests.Format;

public sealed class PdfObjectWriterTests
{
    [Fact]
    public void WriteRoundTripsDictionaryObject()
    {
        const string input = "<< /Type /Example /Flags [ true false 12 ] /Message (Hello) >>";

        PdfObject parsed = PdfObjectParser.ParseAscii(input);
        byte[] serialized = PdfObjectWriter.Write(parsed);
        PdfObject reparsed = PdfObjectParser.Parse(serialized);
        byte[] reserialized = PdfObjectWriter.Write(reparsed);

        Assert.True(serialized.SequenceEqual(reserialized));
    }

    [Fact]
    public void WriteEscapesLiteralStringCharacters()
    {
        PdfStringObject value = new("A (test) \\ sample");
        byte[] serialized = PdfObjectWriter.Write(value);
        string text = Encoding.ASCII.GetString(serialized);

        Assert.Equal("(A \\(test\\) \\\\ sample)", text);
    }
}
