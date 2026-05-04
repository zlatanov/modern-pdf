namespace ModernPDF.Images;

internal readonly record struct PdfRasterImage(
    int Width,
    int Height,
    int BitsPerComponent,
    string ColorSpace,
    string Filter,
    byte[] EncodedBytes);
