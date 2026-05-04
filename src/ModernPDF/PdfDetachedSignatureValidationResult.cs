namespace ModernPDF;

public sealed class PdfDetachedSignatureValidationResult
{
    public PdfDetachedSignatureValidationResult(
        int signatureObjectNumber,
        string? filter,
        string? subFilter,
        bool isValid,
        int signerCount,
        string? failureReason)
    {
        SignatureObjectNumber = signatureObjectNumber;
        Filter = filter;
        SubFilter = subFilter;
        IsValid = isValid;
        SignerCount = signerCount;
        FailureReason = failureReason;
    }

    public int SignatureObjectNumber { get; }

    public string? Filter { get; }

    public string? SubFilter { get; }

    public bool IsValid { get; }

    public int SignerCount { get; }

    public string? FailureReason { get; }
}
