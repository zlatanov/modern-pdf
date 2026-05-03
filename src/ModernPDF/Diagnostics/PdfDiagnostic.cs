namespace ModernPDF.Diagnostics;

internal readonly struct PdfDiagnostic : IEquatable<PdfDiagnostic>
{
    public PdfDiagnostic(PdfDiagnosticSeverity severity, string code, string message, long? byteOffset = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Diagnostic code cannot be empty.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Diagnostic message cannot be empty.", nameof(message));
        }

        if (byteOffset is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Byte offset cannot be negative.");
        }

        Severity = severity;
        Code = code;
        Message = message;
        ByteOffset = byteOffset;
    }

    public PdfDiagnosticSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public long? ByteOffset { get; }

    public bool Equals(PdfDiagnostic other)
    {
        return Severity == other.Severity
            && Code == other.Code
            && Message == other.Message
            && ByteOffset == other.ByteOffset;
    }

    public override bool Equals(object? obj)
    {
        return obj is PdfDiagnostic other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine((int)Severity, Code, Message, ByteOffset);
    }

    public override string ToString()
    {
        return ByteOffset is null
            ? $"{Severity} {Code}: {Message}"
            : $"{Severity} {Code}@{ByteOffset.Value}: {Message}";
    }

    public static bool operator ==(PdfDiagnostic left, PdfDiagnostic right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(PdfDiagnostic left, PdfDiagnostic right)
    {
        return !(left == right);
    }
}
