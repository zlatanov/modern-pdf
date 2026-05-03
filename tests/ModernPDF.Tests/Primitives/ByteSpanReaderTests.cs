using ModernPDF.Primitives;

namespace ModernPDF.Tests.Primitives;

public sealed class ByteSpanReaderTests
{
    [Fact]
    public void TryReadAndTryPeekAdvanceCorrectly()
    {
        ByteSpanReader reader = new(new byte[] { 10, 20, 30 });

        Assert.True(reader.TryPeek(out byte firstPeek));
        Assert.Equal((byte)10, firstPeek);
        Assert.Equal(0, reader.Position);
        Assert.Equal(3, reader.Length);

        Assert.True(reader.TryRead(out byte firstRead));
        Assert.Equal((byte)10, firstRead);
        Assert.Equal(1, reader.Position);
        Assert.Equal(2, reader.Remaining);
    }

    [Fact]
    public void EndStateReturnsFalseForPeekAndRead()
    {
        ByteSpanReader reader = new(new byte[] { 1 });
        _ = reader.TryRead(out _);

        Assert.True(reader.End);
        Assert.False(reader.TryPeek(out byte peeked));
        Assert.False(reader.TryRead(out byte read));
        Assert.Equal(default, peeked);
        Assert.Equal(default, read);
    }

    [Fact]
    public void TryReadSliceReturnsSpanAndAdvances()
    {
        ByteSpanReader reader = new(new byte[] { 1, 2, 3, 4 });

        Assert.True(reader.TryRead(3, out ReadOnlySpan<byte> slice));
        Assert.True(slice.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Equal(3, reader.Position);
    }

    [Fact]
    public void TryReadSliceReturnsFalseWhenNotEnoughRemaining()
    {
        ByteSpanReader reader = new(new byte[] { 1, 2 });

        bool result = reader.TryRead(3, out ReadOnlySpan<byte> slice);

        Assert.False(result);
        Assert.True(slice.IsEmpty);
        Assert.Equal(0, reader.Position);
    }

    [Fact]
    public void AdvanceAndRewindRespectBounds()
    {
        ByteSpanReader reader = new(new byte[] { 1, 2, 3, 4 });

        reader.Advance(2);
        Assert.Equal(2, reader.Position);

        reader.Rewind(1);
        Assert.Equal(1, reader.Position);

        bool advanceFailed = false;
        try
        {
            reader.Advance(10);
        }
        catch (ArgumentOutOfRangeException)
        {
            advanceFailed = true;
        }

        bool rewindFailed = false;
        try
        {
            reader.Rewind(2);
        }
        catch (ArgumentOutOfRangeException)
        {
            rewindFailed = true;
        }

        Assert.True(advanceFailed);
        Assert.True(rewindFailed);
    }

    [Fact]
    public void NegativeLengthsThrowForAdvanceRewindAndRead()
    {
        ByteSpanReader reader = new(new byte[] { 1, 2, 3 });

        bool advanceThrew = false;
        try
        {
            reader.Advance(-1);
        }
        catch (ArgumentOutOfRangeException)
        {
            advanceThrew = true;
        }

        bool rewindThrew = false;
        try
        {
            reader.Rewind(-1);
        }
        catch (ArgumentOutOfRangeException)
        {
            rewindThrew = true;
        }

        bool readThrew = false;
        try
        {
            _ = reader.TryRead(-1, out _);
        }
        catch (ArgumentOutOfRangeException)
        {
            readThrew = true;
        }

        Assert.True(advanceThrew);
        Assert.True(rewindThrew);
        Assert.True(readThrew);
    }
}
