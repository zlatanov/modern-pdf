using System.Security.Cryptography.X509Certificates;

namespace ModernPDF;

public sealed class PdfDetachedSignatureValidationOptions
{
    public bool VerifyCertificateChain { get; init; }

    public bool RequireRevocationStatus { get; init; }

    public bool RequireSigningTime { get; init; }

    public DateTimeOffset? ValidationTime { get; init; }

    public IReadOnlyList<string>? RequiredCertificatePolicyOids { get; init; }

    public IReadOnlyList<X509Certificate2>? TrustedRoots { get; init; }
}
