using System.Security.Cryptography.X509Certificates;

namespace ModernPDF;

/// <summary>
/// Configures detached signature validation behavior.
/// </summary>
public sealed class PdfDetachedSignatureValidationOptions
{
    /// <summary>
    /// Enables X.509 chain building and trust evaluation for each signer certificate.
    /// </summary>
    public bool VerifyCertificateChain { get; init; }

    /// <summary>
    /// Requires revocation status information to be available and valid.
    /// </summary>
    public bool RequireRevocationStatus { get; init; }

    /// <summary>
    /// Selects whether revocation checks use offline caches only or allow online retrieval.
    /// </summary>
    public PdfRevocationCheckMode RevocationCheckMode { get; init; } = PdfRevocationCheckMode.Offline;

    /// <summary>
    /// Requires the signature payload to carry a signing-time value.
    /// </summary>
    public bool RequireSigningTime { get; init; }

    /// <summary>
    /// Overrides the validation instant; when not set, current UTC time is used.
    /// </summary>
    public DateTimeOffset? ValidationTime { get; init; }

    /// <summary>
    /// Requires at least one matching certificate policy OID in the validated chain.
    /// </summary>
    public IReadOnlyList<string>? RequiredCertificatePolicyOids { get; init; }

    /// <summary>
    /// Replaces system trust anchors with this explicit root set.
    /// </summary>
    public IReadOnlyList<X509Certificate2>? TrustedRoots { get; init; }
}
