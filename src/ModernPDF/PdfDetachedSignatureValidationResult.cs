namespace ModernPDF;

public sealed class PdfDetachedSignatureValidationResult
{
    public PdfDetachedSignatureValidationResult(
        int signatureObjectNumber,
        string? filter,
        string? subFilter,
        bool isValid,
        bool cryptographicallyValid,
        bool trustChecksPassed,
        int signerCount,
        string? failureReason,
        bool? certificateChainValid = null,
        bool? revocationValid = null,
        bool? signingTimeValid = null,
        DateTimeOffset? signingTime = null,
        bool? certificatePolicyValid = null,
        IReadOnlyList<string>? diagnostics = null)
    {
        SignatureObjectNumber = signatureObjectNumber;
        Filter = filter;
        SubFilter = subFilter;
        IsValid = isValid;
        CryptographicallyValid = cryptographicallyValid;
        TrustChecksPassed = trustChecksPassed;
        SignerCount = signerCount;
        FailureReason = failureReason;
        CertificateChainValid = certificateChainValid;
        RevocationValid = revocationValid;
        SigningTimeValid = signingTimeValid;
        SigningTime = signingTime;
        CertificatePolicyValid = certificatePolicyValid;
        Diagnostics = diagnostics ?? Array.Empty<string>();
    }

    public int SignatureObjectNumber { get; }

    public string? Filter { get; }

    public string? SubFilter { get; }

    public bool IsValid { get; }

    public bool CryptographicallyValid { get; }

    public bool TrustChecksPassed { get; }

    public int SignerCount { get; }

    public string? FailureReason { get; }

    public bool? CertificateChainValid { get; }

    public bool? RevocationValid { get; }

    public bool? SigningTimeValid { get; }

    public DateTimeOffset? SigningTime { get; }

    public bool? CertificatePolicyValid { get; }

    public IReadOnlyList<string> Diagnostics { get; }
}
