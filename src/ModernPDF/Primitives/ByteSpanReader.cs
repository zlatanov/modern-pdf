namespace ModernPDF.Primitives;

internal ref struct ByteSpanReader
{
    private readonly ReadOnlySpan<byte> _buffer;

    public ByteSpanReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        Position = 0;
    }

    public int Position { get; private set; }

    public int Length => _buffer.Length;

    public int Remaining => _buffer.Length - Position;

    public bool End => Position >= _buffer.Length;

    public bool TryPeek(out byte value)
    {
        if (End)
        {
            value = default;
            return false;
        }

        value = _buffer[Position];
        return true;
    }

    public bool TryRead(out byte value)
    {
        if (!TryPeek(out value))
        {
            return false;
        }

        Position++;
        return true;
    }

    public bool TryRead(int length, out ReadOnlySpan<byte> slice)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (Remaining < length)
        {
            slice = default;
            return false;
        }

        slice = _buffer.Slice(Position, length);
        Position += length;
        return true;
    }

    public void Advance(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length > Remaining)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Cannot advance past the end of the buffer.");
        }

        Position += length;
    }

    public void Rewind(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length > Position)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Cannot rewind before the start of the buffer.");
        }

        Position -= length;
    }
}
