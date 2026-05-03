using ModernPDF.Primitives;

namespace ModernPDF.Tests.Primitives;

public sealed class ByteBufferWriterTests
{
    [Fact]
    public void WriteMethodsAppendBytesInOrder()
    {
        ByteBufferWriter writer = new();

        writer.WriteByte(0x41);
        writer.Write(new byte[] { 0x42, 0x43 });

        Assert.Equal(3, writer.WrittenCount);
        Assert.True(writer.WrittenSpan.SequenceEqual(new byte[] { 0x41, 0x42, 0x43 }));
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
