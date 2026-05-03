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
    public void HasErrorsReturnsFalseWhenNoErrorDiagnosticsExist()
    {
        PdfDiagnosticBag diagnostics = new();

        diagnostics.AddInfo("PDF-I0001", "Information.");
        diagnostics.AddWarning("PDF-W0001", "Warning.");

        Assert.False(diagnostics.HasErrors);
    }

    [Fact]
    public void AddStoresProvidedDiagnostic()
    {
        PdfDiagnostic diagnostic = new(PdfDiagnosticSeverity.Warning, "PDF-W1000", "Custom warning.", 10);
        PdfDiagnosticBag diagnostics = new();

        diagnostics.Add(diagnostic);

        Assert.Single(diagnostics.Items);
        Assert.Equal(diagnostic, diagnostics.Items[0]);
    }

    [Fact]
    public void DiagnosticEqualityAndToStringIncludeOffsetWhenPresent()
    {
        PdfDiagnostic left = new(PdfDiagnosticSeverity.Error, "PDF-E0100", "Boom", 123);
        PdfDiagnostic right = new(PdfDiagnosticSeverity.Error, "PDF-E0100", "Boom", 123);
        PdfDiagnostic withoutOffset = new(PdfDiagnosticSeverity.Error, "PDF-E0100", "Boom");

        Assert.True(left == right);
        Assert.False(left != right);
        Assert.NotEqual(left, withoutOffset);
        Assert.Equal("Error PDF-E0100@123: Boom", left.ToString());
        Assert.Equal("Error PDF-E0100: Boom", withoutOffset.ToString());
    }

    [Fact]
    public void DiagnosticConstructorRejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => _ = new PdfDiagnostic(PdfDiagnosticSeverity.Info, "", "Message"));
        Assert.Throws<ArgumentException>(() => _ = new PdfDiagnostic(PdfDiagnosticSeverity.Warning, "PDF-W0002", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfDiagnostic(PdfDiagnosticSeverity.Error, "PDF-E0002", "Message", -1));
    }
}
