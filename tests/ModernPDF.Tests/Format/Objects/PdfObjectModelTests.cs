using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests.Format.Objects;

public sealed class PdfObjectModelTests
{
    [Fact]
    public void NameObjectRejectsEmptyValues()
    {
        Assert.Throws<ArgumentException>(() => _ = new PdfNameObject(""));
        Assert.Throws<ArgumentException>(() => _ = new PdfNameObject("   "));
    }

    [Fact]
    public void StringObjectRejectsNullValue()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new PdfStringObject(null!));
    }

    [Fact]
    public void ArrayAndDictionaryRejectNullCollections()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new PdfArrayObject(null!));
        Assert.Throws<ArgumentNullException>(() => _ = new PdfDictionaryObject(null!));
    }

    [Fact]
    public void DictionaryEntryRejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => _ = new PdfDictionaryEntry("", PdfNullObject.Instance));
        Assert.Throws<ArgumentNullException>(() => _ = new PdfDictionaryEntry("Type", null!));
    }

    [Fact]
    public void StreamObjectRejectsNullDictionary()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new PdfStreamObject(null!, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void ObjectConstructorsStoreProvidedValues()
    {
        PdfBooleanObject booleanObject = new(true);
        PdfNumberObject numberObject = new(42, isInteger: true);
        PdfNameObject nameObject = new("Type");
        PdfStringObject stringObject = new("Hello");
        PdfByteStringObject byteStringObject = new(new byte[] { 1, 2, 3 });
        PdfReferenceObject referenceObject = new(new PdfObjectId(4, 0));
        PdfArrayObject arrayObject = new([nameObject, stringObject]);
        PdfDictionaryEntry entry = new("Type", nameObject);
        PdfDictionaryObject dictionaryObject = new([entry]);
        PdfStreamObject streamObject = new(dictionaryObject, new byte[] { 9, 8 });

        Assert.True(booleanObject.Value);
        Assert.Equal(42, numberObject.Value);
        Assert.True(numberObject.IsInteger);
        Assert.Equal("Type", nameObject.Value);
        Assert.Equal("Hello", stringObject.Value);
        Assert.True(byteStringObject.Bytes.Span.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Equal(new PdfObjectId(4, 0), referenceObject.ObjectId);
        Assert.Equal(2, arrayObject.Items.Count);
        Assert.Equal("Type", entry.Key);
        Assert.Same(nameObject, entry.Value);
        Assert.Single(dictionaryObject.Entries);
        Assert.Same(dictionaryObject, streamObject.Dictionary);
        Assert.True(streamObject.Data.Span.SequenceEqual(new byte[] { 9, 8 }));
    }

}
