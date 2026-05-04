using System.IO.Compression;
using System.IO;

namespace ModernPDF.Images;

internal static class PdfImageParser
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static PdfRasterImage Parse(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (TryParseJpeg(imageBytes, out PdfRasterImage image, out string? parseError))
        {
            return image;
        }

        if (TryParsePng(imageBytes, out image, out parseError))
        {
            return image;
        }

        throw new NotSupportedException(parseError ?? "Only JPEG and PNG images are currently supported.");
    }

    private static bool TryParseJpeg(ReadOnlySpan<byte> imageBytes, out PdfRasterImage image, out string? error)
    {
        image = default;
        error = null;

        if (imageBytes.Length < 4 || imageBytes[0] != 0xFF || imageBytes[1] != 0xD8)
        {
            error = "Only JPEG images are currently supported.";
            return false;
        }

        int offset = 2;
        while (offset < imageBytes.Length)
        {
            if (imageBytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < imageBytes.Length && imageBytes[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= imageBytes.Length)
            {
                break;
            }

            byte marker = imageBytes[offset++];
            if (marker == 0x00)
            {
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA)
            {
                break;
            }

            if (IsStandaloneJpegMarker(marker))
            {
                continue;
            }

            if (offset + 1 >= imageBytes.Length)
            {
                error = "JPEG segment header is truncated.";
                return false;
            }

            int segmentLength = (imageBytes[offset] << 8) | imageBytes[offset + 1];
            offset += 2;
            if (segmentLength < 2 || offset + segmentLength - 2 > imageBytes.Length)
            {
                error = "JPEG segment length is invalid.";
                return false;
            }

            int payloadOffset = offset;
            int payloadLength = segmentLength - 2;
            if (IsStartOfFrameMarker(marker))
            {
                if (payloadLength < 6)
                {
                    error = "JPEG frame header is truncated.";
                    return false;
                }

                int bitsPerComponent = imageBytes[payloadOffset];
                int height = (imageBytes[payloadOffset + 1] << 8) | imageBytes[payloadOffset + 2];
                int width = (imageBytes[payloadOffset + 3] << 8) | imageBytes[payloadOffset + 4];
                int componentCount = imageBytes[payloadOffset + 5];
                if (width <= 0 || height <= 0)
                {
                    error = "JPEG image dimensions must be positive.";
                    return false;
                }

                if (bitsPerComponent != 8)
                {
                    error = "Only 8-bit JPEG images are currently supported.";
                    return false;
                }

                string colorSpace = componentCount switch
                {
                    1 => "DeviceGray",
                    3 => "DeviceRGB",
                    4 => "DeviceCMYK",
                    _ => string.Empty,
                };
                if (colorSpace.Length == 0)
                {
                    error = $"JPEG images with {componentCount} components are not currently supported.";
                    return false;
                }

                image = new PdfRasterImage(
                    width,
                    height,
                    bitsPerComponent,
                    colorSpace,
                    "DCTDecode",
                    imageBytes.ToArray());
                return true;
            }

            offset += payloadLength;
        }

        error = "JPEG frame header is missing.";
        return false;
    }

    private static bool TryParsePng(ReadOnlySpan<byte> imageBytes, out PdfRasterImage image, out string? error)
    {
        image = default;
        error = null;

        if (imageBytes.Length < PngSignature.Length || !imageBytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            error = "Only JPEG and PNG images are currently supported.";
            return false;
        }

        int offset = PngSignature.Length;
        bool sawIhdr = false;
        bool sawIend = false;
        int width = 0;
        int height = 0;
        int bitsPerComponent = 0;
        int colorType = -1;
        int compressionMethod = -1;
        int filterMethod = -1;
        int interlaceMethod = -1;
        List<byte> idatBytes = [];

        while (offset + 12 <= imageBytes.Length)
        {
            int chunkLength = ReadUInt32BigEndian(imageBytes, offset);
            offset += 4;
            string chunkType = ReadChunkType(imageBytes, offset);
            offset += 4;

            if (chunkLength < 0 || offset + chunkLength + 4 > imageBytes.Length)
            {
                error = "PNG chunk length is invalid.";
                return false;
            }

            ReadOnlySpan<byte> chunkData = imageBytes.Slice(offset, chunkLength);
            offset += chunkLength;
            offset += 4; // CRC

            if (string.Equals(chunkType, "IHDR", StringComparison.Ordinal))
            {
                if (sawIhdr)
                {
                    error = "PNG image contains multiple IHDR chunks.";
                    return false;
                }

                if (chunkData.Length != 13)
                {
                    error = "PNG IHDR chunk is malformed.";
                    return false;
                }

                width = ReadUInt32BigEndian(chunkData, 0);
                height = ReadUInt32BigEndian(chunkData, 4);
                bitsPerComponent = chunkData[8];
                colorType = chunkData[9];
                compressionMethod = chunkData[10];
                filterMethod = chunkData[11];
                interlaceMethod = chunkData[12];
                sawIhdr = true;
            }
            else if (string.Equals(chunkType, "IDAT", StringComparison.Ordinal))
            {
                idatBytes.AddRange(chunkData.ToArray());
            }
            else if (string.Equals(chunkType, "IEND", StringComparison.Ordinal))
            {
                sawIend = true;
                break;
            }
        }

        if (!sawIhdr || !sawIend)
        {
            error = "PNG image is missing required IHDR or IEND chunks.";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = "PNG image dimensions must be positive.";
            return false;
        }

        if (idatBytes.Count == 0)
        {
            error = "PNG image is missing IDAT data.";
            return false;
        }

        if (compressionMethod != 0 || filterMethod != 0)
        {
            error = "PNG image uses unsupported compression or filter method.";
            return false;
        }

        if (interlaceMethod != 0)
        {
            error = "Interlaced PNG images are currently not supported.";
            return false;
        }

        if (bitsPerComponent != 8)
        {
            error = "Only 8-bit PNG images are currently supported.";
            return false;
        }

        string colorSpace;
        int colors;
        switch (colorType)
        {
            case 0:
                colorSpace = "DeviceGray";
                colors = 1;
                break;
            case 2:
                colorSpace = "DeviceRGB";
                colors = 3;
                break;
            case 4:
            case 6:
                if (!TryDecodePngImageData(idatBytes.ToArray(), width, height, bitsPerComponent, colorType, out byte[]? decodedPixels, out string? decodeError))
                {
                    error = decodeError;
                    return false;
                }

                if (!TrySplitPngAlphaChannels(decodedPixels!, width, height, colorType, out byte[]? colorBytes, out byte[]? alphaBytes, out string? splitError))
                {
                    error = splitError;
                    return false;
                }

                colorSpace = colorType == 6 ? "DeviceRGB" : "DeviceGray";
                colors = colorType == 6 ? 3 : 1;
                image = new PdfRasterImage(
                    width,
                    height,
                    bitsPerComponent,
                    colorSpace,
                    "FlateDecode",
                    CompressZlib(colorBytes!),
                    SoftMask: new PdfImageSoftMask(
                        width,
                        height,
                        bitsPerComponent,
                        "FlateDecode",
                        CompressZlib(alphaBytes!)));
                return true;
            case 3:
                error = "Indexed-color PNG images are not currently supported.";
                return false;
            default:
                error = $"PNG color type '{colorType}' is not currently supported.";
                return false;
        }

        image = new PdfRasterImage(
            width,
            height,
            bitsPerComponent,
            colorSpace,
            "FlateDecode",
            idatBytes.ToArray(),
            Predictor: 15,
            Colors: colors,
            Columns: width);
        return true;
    }

    private static bool TryDecodePngImageData(
        byte[] idatBytes,
        int width,
        int height,
        int bitsPerComponent,
        int colorType,
        out byte[]? decodedPixels,
        out string? error)
    {
        decodedPixels = null;
        error = null;

        int components = colorType switch
        {
            4 => 2,
            6 => 4,
            _ => 0,
        };
        if (components == 0 || bitsPerComponent != 8)
        {
            error = "PNG image data decoding supports only 8-bit grayscale+alpha or RGBA images.";
            return false;
        }

        if (!TryDecompressZlib(idatBytes, out byte[]? inflatedBytes, out string? inflateError))
        {
            error = inflateError;
            return false;
        }

        int bytesPerPixel = components;
        int rowLength = checked(width * bytesPerPixel);
        int expectedInflatedLength = checked(height * (rowLength + 1));
        if (inflatedBytes!.Length != expectedInflatedLength)
        {
            error = "PNG IDAT payload length does not match image dimensions.";
            return false;
        }

        byte[] reconstructed = new byte[checked(width * height * bytesPerPixel)];
        byte[] previousRow = new byte[rowLength];
        byte[] currentRow = new byte[rowLength];
        int sourceOffset = 0;
        int destinationOffset = 0;
        for (int row = 0; row < height; row++)
        {
            byte filterType = inflatedBytes[sourceOffset++];
            for (int column = 0; column < rowLength; column++)
            {
                int raw = inflatedBytes[sourceOffset++];
                int left = column >= bytesPerPixel ? currentRow[column - bytesPerPixel] : 0;
                int up = previousRow[column];
                int upperLeft = column >= bytesPerPixel ? previousRow[column - bytesPerPixel] : 0;
                int value = filterType switch
                {
                    0 => raw,
                    1 => raw + left,
                    2 => raw + up,
                    3 => raw + ((left + up) / 2),
                    4 => raw + PaethPredictor(left, up, upperLeft),
                    _ => -1,
                };
                if (value < 0)
                {
                    error = $"PNG uses unsupported filter type '{filterType}'.";
                    return false;
                }

                currentRow[column] = (byte)value;
            }

            Buffer.BlockCopy(currentRow, 0, reconstructed, destinationOffset, rowLength);
            destinationOffset += rowLength;
            (previousRow, currentRow) = (currentRow, previousRow);
        }

        decodedPixels = reconstructed;
        return true;
    }

    private static bool TrySplitPngAlphaChannels(
        byte[] decodedPixels,
        int width,
        int height,
        int colorType,
        out byte[]? colorBytes,
        out byte[]? alphaBytes,
        out string? error)
    {
        colorBytes = null;
        alphaBytes = null;
        error = null;
        int pixelCount = checked(width * height);

        switch (colorType)
        {
            case 4:
            {
                colorBytes = new byte[pixelCount];
                alphaBytes = new byte[pixelCount];
                for (int index = 0; index < pixelCount; index++)
                {
                    int sourceOffset = index * 2;
                    colorBytes[index] = decodedPixels[sourceOffset];
                    alphaBytes[index] = decodedPixels[sourceOffset + 1];
                }

                return true;
            }
            case 6:
            {
                colorBytes = new byte[checked(pixelCount * 3)];
                alphaBytes = new byte[pixelCount];
                for (int index = 0; index < pixelCount; index++)
                {
                    int sourceOffset = index * 4;
                    int targetOffset = index * 3;
                    colorBytes[targetOffset] = decodedPixels[sourceOffset];
                    colorBytes[targetOffset + 1] = decodedPixels[sourceOffset + 1];
                    colorBytes[targetOffset + 2] = decodedPixels[sourceOffset + 2];
                    alphaBytes[index] = decodedPixels[sourceOffset + 3];
                }

                return true;
            }
            default:
                error = "PNG alpha-channel split only supports grayscale+alpha or RGBA input.";
                return false;
        }
    }

    private static bool TryDecompressZlib(byte[] source, out byte[]? decompressed, out string? error)
    {
        decompressed = null;
        error = null;
        try
        {
            using MemoryStream sourceStream = new(source);
            using ZLibStream zlib = new(sourceStream, CompressionMode.Decompress);
            using MemoryStream destinationStream = new();
            zlib.CopyTo(destinationStream);
            decompressed = destinationStream.ToArray();
            return true;
        }
        catch (InvalidDataException exception)
        {
            if (TryDecompressZlibUsingRawDeflate(source, out decompressed))
            {
                return true;
            }

            error = $"PNG IDAT payload could not be decompressed: {exception.Message}";
            return false;
        }
    }

    private static bool TryDecompressZlibUsingRawDeflate(byte[] source, out byte[]? decompressed)
    {
        decompressed = null;
        if (source.Length <= 6 || (source[0] & 0x0F) != 8)
        {
            return false;
        }

        try
        {
            using MemoryStream sourceStream = new(source, 2, source.Length - 6, writable: false);
            using DeflateStream deflate = new(sourceStream, CompressionMode.Decompress);
            using MemoryStream destinationStream = new();
            deflate.CopyTo(destinationStream);
            decompressed = destinationStream.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static byte[] CompressZlib(byte[] source)
    {
        using MemoryStream destinationStream = new();
        using (ZLibStream zlib = new(destinationStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(source, 0, source.Length);
        }

        return destinationStream.ToArray();
    }

    private static int PaethPredictor(int left, int up, int upperLeft)
    {
        int prediction = left + up - upperLeft;
        int leftDelta = Math.Abs(prediction - left);
        int upDelta = Math.Abs(prediction - up);
        int upperLeftDelta = Math.Abs(prediction - upperLeft);
        if (leftDelta <= upDelta && leftDelta <= upperLeftDelta)
        {
            return left;
        }

        if (upDelta <= upperLeftDelta)
        {
            return up;
        }

        return upperLeft;
    }

    private static int ReadUInt32BigEndian(ReadOnlySpan<byte> source, int offset)
    {
        return (source[offset] << 24)
            | (source[offset + 1] << 16)
            | (source[offset + 2] << 8)
            | source[offset + 3];
    }

    private static string ReadChunkType(ReadOnlySpan<byte> source, int offset)
    {
        return string.Create(
            4,
            source.Slice(offset, 4).ToArray(),
            static (span, state) =>
            {
                span[0] = (char)state[0];
                span[1] = (char)state[1];
                span[2] = (char)state[2];
                span[3] = (char)state[3];
            });
    }

    private static bool IsStandaloneJpegMarker(byte marker)
    {
        return marker is >= 0xD0 and <= 0xD7 or 0x01;
    }

    private static bool IsStartOfFrameMarker(byte marker)
    {
        return marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
    }
}
