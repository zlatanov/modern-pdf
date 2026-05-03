namespace ModernPDF.Diagnostics;

internal sealed class PdfDiagnosticBag
{
    private readonly List<PdfDiagnostic> _diagnostics = [];

    public IReadOnlyList<PdfDiagnostic> Items => _diagnostics;

    public bool HasErrors
    {
        get
        {
            foreach (PdfDiagnostic diagnostic in _diagnostics)
            {
                if (diagnostic.Severity == PdfDiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Add(PdfDiagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
    }

    public void AddInfo(string code, string message, long? byteOffset = null)
    {
        _diagnostics.Add(new PdfDiagnostic(PdfDiagnosticSeverity.Info, code, message, byteOffset));
    }

    public void AddWarning(string code, string message, long? byteOffset = null)
    {
        _diagnostics.Add(new PdfDiagnostic(PdfDiagnosticSeverity.Warning, code, message, byteOffset));
    }

    public void AddError(string code, string message, long? byteOffset = null)
    {
        _diagnostics.Add(new PdfDiagnostic(PdfDiagnosticSeverity.Error, code, message, byteOffset));
    }
}
