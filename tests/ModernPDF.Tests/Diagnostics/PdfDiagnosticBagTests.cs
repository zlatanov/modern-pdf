using ModernPDF.Diagnostics;

namespace ModernPDF.Tests.Diagnostics;

public sealed class PdfDiagnosticBagTests
{
    [Fact]
    public void AddErrorSetsErrorState()
    {
        PdfDiagnosticBag diagnostics = new();

        diagnostics.AddWarning("PDF-W0001", "Non-critical issue.");
        diagnostics.AddError("PDF-E0001", "Critical parse error.", 128);

        Assert.Equal(2, diagnostics.Items.Count);
        Assert.True(diagnostics.HasErrors);
        Assert.Equal(PdfDiagnosticSeverity.Error, diagnostics.Items[1].Severity);
        Assert.Equal((long)128, diagnostics.Items[1].ByteOffset);
    }

    [Fact]
    public void DiagnosticConstructorRejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => _ = new PdfDiagnostic(PdfDiagnosticSeverity.Info, "", "Message"));
        Assert.Throws<ArgumentException>(() => _ = new PdfDiagnostic(PdfDiagnosticSeverity.Warning, "PDF-W0002", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfDiagnostic(PdfDiagnosticSeverity.Error, "PDF-E0002", "Message", -1));
    }
}
