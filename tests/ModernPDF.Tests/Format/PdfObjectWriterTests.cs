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

    [Fact]
    public void WriteEmitsHexStringForByteStringObjects()
    {
        PdfByteStringObject value = new(Encoding.ASCII.GetBytes("Hi"));
        byte[] serialized = PdfObjectWriter.Write(value);
        string text = Encoding.ASCII.GetString(serialized);

        Assert.Equal("<4869>", text);
    }

    [Fact]
    public void WriteEmitsTwoDigitUppercaseHexForBinaryBytes()
    {
        PdfByteStringObject value = new(new byte[] { 0x00, 0xFF, 0x7A });
        byte[] serialized = PdfObjectWriter.Write(value);
        string text = Encoding.ASCII.GetString(serialized);

        Assert.Equal("<00FF7A>", text);
    }
}
