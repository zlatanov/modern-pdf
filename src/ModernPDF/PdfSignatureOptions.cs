namespace ModernPDF;

public sealed class PdfSignatureOptions
{
    public string FieldName { get; init; } = "Signature1";

    public int PageIndex { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public int ContentsByteLength { get; init; } = 8192;

    public string Filter { get; init; } = "Adobe.PPKLite";

    public string SubFilter { get; init; } = "adbe.pkcs7.detached";

    public DateTimeOffset? SigningTime { get; init; }

    public string? Name { get; init; }

    public string? Reason { get; init; }

    public string? Location { get; init; }

    public string? ContactInfo { get; init; }
}
