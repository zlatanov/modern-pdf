using System.Buffers;

namespace ModernPDF.Primitives;

internal sealed class ByteBufferWriter
{
    private readonly ArrayBufferWriter<byte> _writer;

    public ByteBufferWriter(int initialCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _writer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int WrittenCount => _writer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _writer.WrittenSpan;

    public ReadOnlyMemory<byte> WrittenMemory => _writer.WrittenMemory;

    public void WriteByte(byte value)
    {
        Span<byte> span = _writer.GetSpan(1);
        span[0] = value;
        _writer.Advance(1);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        bytes.CopyTo(_writer.GetSpan(bytes.Length));
        _writer.Advance(bytes.Length);
    }

    public byte[] ToArray()
    {
        return _writer.WrittenSpan.ToArray();
    }

    public void Clear()
    {
        _writer.Clear();
    }
}
