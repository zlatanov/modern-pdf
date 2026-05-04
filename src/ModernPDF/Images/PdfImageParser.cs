namespace ModernPDF.Images;

internal static class PdfImageParser
{
    public static PdfRasterImage Parse(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (TryParseJpeg(imageBytes, out PdfRasterImage image, out string? parseError))
        {
            return image;
        }

        throw new NotSupportedException(parseError ?? "Only JPEG images are currently supported.");
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

    private static bool IsStandaloneJpegMarker(byte marker)
    {
        return marker is >= 0xD0 and <= 0xD7 or 0x01;
    }

    private static bool IsStartOfFrameMarker(byte marker)
    {
        return marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
    }
}
