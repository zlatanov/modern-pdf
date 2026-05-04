namespace ModernPDF.Images;

internal readonly record struct PdfImageSoftMask(
    int Width,
    int Height,
    int BitsPerComponent,
    string Filter,
    byte[] EncodedBytes,
    int? Predictor = null,
    int? Colors = null,
    int? Columns = null);
