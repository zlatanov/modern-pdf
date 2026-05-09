namespace ModernPDF;

/// <summary>
/// Defines how certificate revocation data is obtained during signature validation.
/// </summary>
public enum PdfRevocationCheckMode
{
    /// <summary>Uses locally available revocation information only.</summary>
    Offline = 0,
    /// <summary>Allows online revocation retrieval (for example, CRL/OCSP).</summary>
    Online = 1,
}
