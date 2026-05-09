namespace ModernPDF;

/// <summary>
/// Describes the outcome of validating one detached signature dictionary.
/// </summary>
public sealed class PdfDetachedSignatureValidationResult
{
    /// <summary>
    /// Initializes a detached signature validation result.
    /// </summary>
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

    /// <summary>The object number of the signature dictionary.</summary>
    public int SignatureObjectNumber { get; }

    /// <summary>The value of the signature dictionary <c>/Filter</c> entry.</summary>
    public string? Filter { get; }

    /// <summary>The value of the signature dictionary <c>/SubFilter</c> entry.</summary>
    public string? SubFilter { get; }

    /// <summary>
    /// Indicates whether all enabled checks passed.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Indicates whether CMS signature verification succeeded.
    /// </summary>
    public bool CryptographicallyValid { get; }

    /// <summary>
    /// Indicates whether configured trust checks succeeded.
    /// </summary>
    public bool TrustChecksPassed { get; }

    /// <summary>The number of signer infos in the CMS payload.</summary>
    public int SignerCount { get; }

    /// <summary>
    /// High-level failure reason when validation fails.
    /// </summary>
    public string? FailureReason { get; }

    /// <summary>
    /// Chain-validation result when chain verification was performed.
    /// </summary>
    public bool? CertificateChainValid { get; }

    /// <summary>
    /// Revocation-validation result when revocation checks were performed.
    /// </summary>
    public bool? RevocationValid { get; }

    /// <summary>
    /// Signing-time validation result when signing-time checks were performed.
    /// </summary>
    public bool? SigningTimeValid { get; }

    /// <summary>The parsed signing-time value from the CMS payload, if present.</summary>
    public DateTimeOffset? SigningTime { get; }

    /// <summary>
    /// Certificate-policy validation result when policy checks were performed.
    /// </summary>
    public bool? CertificatePolicyValid { get; }

    /// <summary>
    /// Detailed validation notes collected during processing.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }
}
