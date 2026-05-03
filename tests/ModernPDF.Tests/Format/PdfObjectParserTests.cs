using ModernPDF.Format;
using ModernPDF.Format.Objects;

namespace ModernPDF.Tests.Format;

public sealed class PdfObjectParserTests
{
    [Fact]
    public void ParseHandlesNestedDictionaryArrayAndReference()
    {
        const string input = "<< /Type /Example /Flags [ true false null ] /Ref 12 0 R /Message (Hello) >>";

        PdfObject parsed = PdfObjectParser.ParseAscii(input);
        PdfDictionaryObject dictionary = Assert.IsType<PdfDictionaryObject>(parsed);

        Assert.Equal(4, dictionary.Entries.Count);
        Assert.Equal("Type", dictionary.Entries[0].Key);
        Assert.IsType<PdfNameObject>(dictionary.Entries[0].Value);

        PdfArrayObject flags = Assert.IsType<PdfArrayObject>(dictionary.Entries[1].Value);
        Assert.Equal(3, flags.Items.Count);
        Assert.IsType<PdfBooleanObject>(flags.Items[0]);
        Assert.IsType<PdfBooleanObject>(flags.Items[1]);
        Assert.IsType<PdfNullObject>(flags.Items[2]);

        PdfReferenceObject reference = Assert.IsType<PdfReferenceObject>(dictionary.Entries[2].Value);
        Assert.Equal(12, reference.ObjectId.ObjectNumber);
        Assert.Equal((ushort)0, reference.ObjectId.GenerationNumber);

        PdfStringObject message = Assert.IsType<PdfStringObject>(dictionary.Entries[3].Value);
        Assert.Equal("Hello", message.Value);
    }

    [Fact]
    public void ParseRejectsTrailingTokens()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("true false"));
    }

    [Fact]
    public void ParseHandlesHexStringObject()
    {
        PdfObject parsed = PdfObjectParser.ParseAscii("<48656C6C6F>");
        PdfByteStringObject bytes = Assert.IsType<PdfByteStringObject>(parsed);

        Assert.Equal("Hello", System.Text.Encoding.ASCII.GetString(bytes.Bytes.Span));
    }

    [Fact]
    public void ParsePadsOddLengthHexStringObject()
    {
        PdfObject parsed = PdfObjectParser.ParseAscii("<414>");
        PdfByteStringObject bytes = Assert.IsType<PdfByteStringObject>(parsed);

        Assert.Equal(new byte[] { 0x41, 0x40 }, bytes.Bytes.ToArray());
    }

    [Fact]
    public void ParseThrowsForInvalidHexStringCharacter()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("<4G>"));
    }
}
