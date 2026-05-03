using System.Text;
using ModernPDF.Format;
using ModernPDF.Format.Objects;

namespace ModernPDF.Tests.Format;

public sealed class PdfObjectWriterTests
{
    [Fact]
    public void WriteRejectsNullObject()
    {
        Assert.Throws<ArgumentNullException>(() => PdfObjectWriter.Write(null!));
    }

    [Fact]
    public void WriteHandlesPrimitiveObjectShapes()
    {
        Assert.Equal("null", Encoding.ASCII.GetString(PdfObjectWriter.Write(PdfNullObject.Instance)));
        Assert.Equal("true", Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfBooleanObject(true))));
        Assert.Equal("42", Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfNumberObject(42, isInteger: true))));
        Assert.Equal("3.5", Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfNumberObject(3.5, isInteger: false))));
        Assert.Equal("7 0 R", Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfReferenceObject(new ModernPDF.Primitives.PdfObjectId(7, 0)))));
    }

    [Fact]
    public void WriteEscapesNameCharacters()
    {
        PdfNameObject value = new("A Name/Value#1");
        string text = Encoding.ASCII.GetString(PdfObjectWriter.Write(value));

        Assert.Equal("/A#20Name#2FValue#231", text);
    }

    [Fact]
    public void WriteRejectsNonAsciiNameCharacters()
    {
        PdfNameObject value = new("Náme");

        Assert.Throws<PdfFormatException>(() => PdfObjectWriter.Write(value));
    }

    [Fact]
    public void WriteEscapesControlCharactersInLiteralString()
    {
        PdfStringObject value = new("A\nB\rC\tD");
        string text = Encoding.ASCII.GetString(PdfObjectWriter.Write(value));

        Assert.Equal("(A\\nB\\rC\\tD)", text);
    }

    [Fact]
    public void WriteRejectsNonAsciiLiteralStringCharacters()
    {
        PdfStringObject value = new("smile 😀");

        Assert.Throws<PdfFormatException>(() => PdfObjectWriter.Write(value));
    }

    [Fact]
    public void WriteFormatsEmptyArrayAndDictionary()
    {
        string array = Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfArrayObject([])));
        string dictionary = Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfDictionaryObject([])));

        Assert.Equal("[]", array);
        Assert.Equal("<<>>", dictionary);
    }

    [Fact]
    public void WriteRejectsUnsupportedPdfObjectSubclass()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectWriter.Write(new UnsupportedPdfObject()));
    }

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

    private sealed class UnsupportedPdfObject : PdfObject
    {
    }
}
