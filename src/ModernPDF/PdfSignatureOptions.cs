namespace ModernPDF;

/// <summary>
/// Configures creation of a detached signature form field and placeholder.
/// </summary>
public sealed class PdfSignatureOptions
{
    /// <summary>AcroForm field name used for the signature widget.</summary>
    public string FieldName { get; init; } = "Signature1";

    /// <summary>Zero-based page index where the visible signature widget is placed.</summary>
    public int PageIndex { get; init; }

    /// <summary>Widget rectangle left coordinate.</summary>
    public double X { get; init; }

    /// <summary>Widget rectangle bottom coordinate.</summary>
    public double Y { get; init; }

    /// <summary>Widget rectangle width.</summary>
    public double Width { get; init; }

    /// <summary>Widget rectangle height.</summary>
    public double Height { get; init; }

    /// <summary>
    /// Reserved hex byte length for <c>/Contents</c>; must fit the final CMS payload.
    /// </summary>
    public int ContentsByteLength { get; init; } = 8192;

    /// <summary>Signature dictionary <c>/Filter</c> value.</summary>
    public string Filter { get; init; } = "Adobe.PPKLite";

    /// <summary>Signature dictionary <c>/SubFilter</c> value.</summary>
    public string SubFilter { get; init; } = "adbe.pkcs7.detached";

    /// <summary>Signing timestamp written to <c>/M</c>; defaults to current UTC time.</summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>Optional signer display name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional signing reason text.</summary>
    public string? Reason { get; init; }

    /// <summary>Optional signing location text.</summary>
    public string? Location { get; init; }

    /// <summary>Optional signer contact text.</summary>
    public string? ContactInfo { get; init; }
}
