using ModernPDF.Primitives;

namespace ModernPDF.Tests.Primitives;

public sealed class ByteBufferWriterTests
{
    [Fact]
    public void ConstructorRejectsInvalidInitialCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ByteBufferWriter(0));
    }

    [Fact]
    public void WriteMethodsAppendBytesInOrder()
    {
        ByteBufferWriter writer = new();

        writer.WriteByte(0x41);
        writer.Write(new byte[] { 0x42, 0x43 });

        Assert.Equal(3, writer.WrittenCount);
        Assert.True(writer.WrittenSpan.SequenceEqual(new byte[] { 0x41, 0x42, 0x43 }));
        Assert.True(writer.WrittenMemory.Span.SequenceEqual(new byte[] { 0x41, 0x42, 0x43 }));
        Assert.True(writer.ToArray().SequenceEqual(new byte[] { 0x41, 0x42, 0x43 }));
    }

    [Fact]
    public void WriteIgnoresEmptyInput()
    {
        ByteBufferWriter writer = new();

        writer.Write(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void ClearResetsWrittenBuffer()
    {
        ByteBufferWriter writer = new();
        writer.Write(new byte[] { 1, 2, 3 });

        writer.Clear();

        Assert.Equal(0, writer.WrittenCount);
        Assert.True(writer.WrittenSpan.IsEmpty);
    }
}
