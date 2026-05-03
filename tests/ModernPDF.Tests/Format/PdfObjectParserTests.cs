using ModernPDF.Format;
using ModernPDF.Format.Objects;
using System.Reflection;

namespace ModernPDF.Tests.Format;

public sealed class PdfObjectParserTests
{
    [Fact]
    public void ParseAsciiRejectsNullText()
    {
        Assert.Throws<ArgumentNullException>(() => PdfObjectParser.ParseAscii(null!));
    }

    [Fact]
    public void ParseRejectsEmptyInput()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.Parse(ReadOnlySpan<byte>.Empty));
    }

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

    [Fact]
    public void ParseThrowsForUnterminatedArray()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("[1 2 3"));
    }

    [Fact]
    public void ParseThrowsForDictionaryKeyThatIsNotName()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("<< 1 /Type >>"));
    }

    [Fact]
    public void ParseThrowsForUnterminatedDictionary()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("<< /Type /Page"));
    }

    [Fact]
    public void ParseThrowsWhenDictionaryValueIsMissing()
    {
        PdfFormatException ex = Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("<< /Type"));

        Assert.Contains("Unexpected end of tokens", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseThrowsForUnsupportedKeywordToken()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("obj"));
    }

    [Fact]
    public void ParseThrowsForUnexpectedToken()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("]"));
    }

    [Fact]
    public void ParseThrowsForInvalidObjectNumberInReference()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("9999999999999999999999 0 R"));
    }

    [Fact]
    public void ParseThrowsForInvalidGenerationNumberInReference()
    {
        Assert.Throws<PdfFormatException>(() => PdfObjectParser.ParseAscii("1 9999999999999999999999 R"));
    }

    [Fact]
    public void ParseParsesRealNumberAndNullObject()
    {
        PdfObject real = PdfObjectParser.ParseAscii("3.14");
        PdfObject nullObject = PdfObjectParser.ParseAscii("null");

        PdfNumberObject number = Assert.IsType<PdfNumberObject>(real);
        Assert.False(number.IsInteger);
        Assert.Equal(3.14, number.Value);
        Assert.Same(PdfNullObject.Instance, nullObject);
    }

    [Fact]
    public void ParseParsesEmptyHexStringToEmptyByteString()
    {
        PdfByteStringObject bytes = Assert.IsType<PdfByteStringObject>(PdfObjectParser.ParseAscii("<>"));

        Assert.True(bytes.Bytes.IsEmpty);
    }

    [Fact]
    public void ParsePrivateNumberParsersRejectInvalidTokenLexemes()
    {
        MethodInfo parseInteger = typeof(PdfObjectParser).GetMethod("ParseInteger", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo parseReal = typeof(PdfObjectParser).GetMethod("ParseReal", BindingFlags.NonPublic | BindingFlags.Static)!;

        TargetInvocationException integerEx = Assert.Throws<TargetInvocationException>(
            () => parseInteger.Invoke(null, [new PdfToken(PdfTokenKind.Integer, "12A")]));
        TargetInvocationException realEx = Assert.Throws<TargetInvocationException>(
            () => parseReal.Invoke(null, [new PdfToken(PdfTokenKind.Real, "1.2.3")]));

        Assert.IsType<PdfFormatException>(integerEx.InnerException);
        Assert.IsType<PdfFormatException>(realEx.InnerException);
    }

    [Fact]
    public void ParsePrivateHexParserRejectsOddAndInvalidPairs()
    {
        MethodInfo parseHexBytes = typeof(PdfObjectParser).GetMethod("ParseHexBytes", BindingFlags.NonPublic | BindingFlags.Static)!;

        TargetInvocationException oddEx = Assert.Throws<TargetInvocationException>(() => parseHexBytes.Invoke(null, ["ABC"]));
        TargetInvocationException invalidPairEx = Assert.Throws<TargetInvocationException>(() => parseHexBytes.Invoke(null, ["FG"]));

        Assert.IsType<PdfFormatException>(oddEx.InnerException);
        Assert.IsType<PdfFormatException>(invalidPairEx.InnerException);
    }
}
