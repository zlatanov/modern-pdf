namespace ModernPDF.CorpusTests;

/// <summary>
/// Adapted from high-level loading/error scenarios in pdf.js test/unit/api_spec.js.
/// Source commit: f54f4b606d900d0f556492fda1a91437e87611ef
/// </summary>
public sealed class PdfJsApiSpecAdaptedTests
{
    public static IEnumerable<object?[]> ApiSpecScenarios()
    {
        yield return ScenarioOpen("pdfjs-api-basicapi", password: null, expectedPageCount: 3);
        yield return ScenarioError("pdfjs-api-tracemonkey", null, "ModernPDF.Format.PdfFormatException", "Stream /Length must be an integer number.");
        yield return ScenarioError("pdfjs-api-bug1020226", null, "ModernPDF.Format.PdfFormatException", "Missing startxref marker.");
        yield return ScenarioError("pdfjs-api-empty", null, "ModernPDF.Format.PdfFormatException", "Only classic xref tables are supported in the current reader slice.");

        yield return ScenarioError("pdfjs-api-pr6531-1", null, "ModernPDF.Format.PdfFormatException", "Only classic xref tables are supported in the current reader slice.");
        yield return ScenarioError("pdfjs-api-pr6531-1", "qwerty", "ModernPDF.Format.PdfFormatException", "Only classic xref tables are supported in the current reader slice.");
        yield return ScenarioError("pdfjs-api-pr6531-1", "asdfasdf", "ModernPDF.Format.PdfFormatException", "Only classic xref tables are supported in the current reader slice.");

        yield return ScenarioError("pdfjs-api-pr6531-2", null, typeof(NotSupportedException).FullName!, "Only Standard security handler V=1 R=2 (40-bit) is currently supported.");
        yield return ScenarioError("pdfjs-api-pr6531-2", "qwerty", typeof(NotSupportedException).FullName!, "Only Standard security handler V=1 R=2 (40-bit) is currently supported.");
        yield return ScenarioError("pdfjs-api-pr6531-2", "asdfasdf", typeof(NotSupportedException).FullName!, "Only Standard security handler V=1 R=2 (40-bit) is currently supported.");

        yield return ScenarioError("pdfjs-api-issue3371", null, "ModernPDF.Format.PdfFormatException", "Only classic xref tables are supported in the current reader slice.");
        yield return ScenarioError("pdfjs-api-pdfbox-4352-0", null, typeof(FormatException).FullName!, "The input string");
        yield return ScenarioError("pdfjs-api-ghostscript-698804-1-fuzzed", null, typeof(OverflowException).FullName!, "Value was either too large or too small for an Int32.");
        yield return ScenarioError("pdfjs-api-redhat-1531897-0", null, "ModernPDF.Format.PdfFormatException", "Only classic xref tables are supported in the current reader slice.");
        yield return ScenarioError("pdfjs-api-poppler-395-0-fuzzed", null, "ModernPDF.Format.PdfFormatException", "Object header mismatch for object 2.");

        yield return ScenarioOpen("pdfjs-api-poppler-67295-0", password: null, expectedPageCount: 1);
        yield return ScenarioError("pdfjs-api-poppler-85140-0", null, "ModernPDF.Format.PdfFormatException", "Missing startxref marker.");
        yield return ScenarioError("pdfjs-api-poppler-91414-0-53", null, "ModernPDF.Format.PdfFormatException", "Stream /Length must be an integer number.");
        yield return ScenarioError("pdfjs-api-poppler-91414-0-54", null, "ModernPDF.Format.PdfFormatException", "Stream /Length must be an integer number.");
        yield return ScenarioError("pdfjs-api-poppler-742-0-fuzzed", null, "ModernPDF.Format.PdfFormatException", "Only classic xref tables are supported in the current reader slice.");
        yield return ScenarioError("pdfjs-api-poppler-937-0-fuzzed", null, typeof(FormatException).FullName!, "The input string");

        yield return ScenarioOpen("pdfjs-api-issue15150", password: null, expectedPageCount: 1);
        yield return ScenarioError("pdfjs-api-issue15590", null, "ModernPDF.Format.PdfFormatException", "Missing startxref marker.");
        yield return ScenarioError("pdfjs-api-issue6010-1", null, typeof(NotSupportedException).FullName!, "Only Standard security handler V=1 R=2 (40-bit) is currently supported.");
        yield return ScenarioError("pdfjs-api-bug1980958", null, "ModernPDF.Format.PdfFormatException", "Missing startxref marker.");
    }

    [Theory]
    [MemberData(nameof(ApiSpecScenarios))]
    [Trait("Category", "Corpus")]
    [Trait("Category", "Full")]
    [Trait("Source", "pdf.js")]
    public void ApiSpecScenarioMatchesCurrentModernPdfBehavior(
        string fixtureId,
        string? password,
        bool shouldOpen,
        int expectedPageCount,
        string expectedExceptionType,
        string expectedMessagePart)
    {
        if (!ShouldRunCorpusTests())
        {
            return;
        }

        CorpusManifest manifest = CorpusManifestLoader.Load();
        CorpusFixture fixture = manifest.Fixtures.Single(
            candidate => string.Equals(candidate.Id, fixtureId, StringComparison.OrdinalIgnoreCase));
        string fixturePath = CorpusManifestLoader.GetDownloadedFixturePath(fixture);

        Assert.True(
            File.Exists(fixturePath),
            $"Fixture '{fixture.Id}' is missing at '{fixturePath}'. Run `pwsh tests\\ModernPDF.CorpusTests\\scripts\\sync-fixtures.ps1 -Profile full`.");

        OpenResult result = TryOpen(fixturePath, password);

        if (shouldOpen)
        {
            Assert.True(result.Opened);
            Assert.Equal(expectedPageCount, result.PageCount);
            return;
        }

        Assert.False(result.Opened);
        Assert.Equal(expectedExceptionType, result.ExceptionType);
        Assert.Contains(expectedMessagePart, result.ExceptionMessage, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Corpus")]
    [Trait("Category", "Full")]
    [Trait("Source", "pdf.js")]
    public void BasicApiFixtureOpensFromByteArrayAndSpan()
    {
        if (!ShouldRunCorpusTests())
        {
            return;
        }

        CorpusManifest manifest = CorpusManifestLoader.Load();
        CorpusFixture fixture = manifest.Fixtures.Single(
            candidate => string.Equals(candidate.Id, "pdfjs-api-basicapi", StringComparison.OrdinalIgnoreCase));
        string fixturePath = CorpusManifestLoader.GetDownloadedFixturePath(fixture);
        byte[] bytes = File.ReadAllBytes(fixturePath);

        PdfDocument fromBytes = PdfDocument.Open(bytes);
        PdfDocument fromSpan = PdfDocument.Open(bytes.AsSpan());

        Assert.Equal(3, fromBytes.PageCount);
        Assert.Equal(3, fromSpan.PageCount);
    }

    private static object?[] ScenarioOpen(string fixtureId, string? password, int expectedPageCount)
    {
        return
        [
            fixtureId,
            password,
            true,
            expectedPageCount,
            string.Empty,
            string.Empty,
        ];
    }

    private static object?[] ScenarioError(string fixtureId, string? password, string expectedExceptionType, string expectedMessagePart)
    {
        return
        [
            fixtureId,
            password,
            false,
            0,
            expectedExceptionType,
            expectedMessagePart,
        ];
    }

    private static OpenResult TryOpen(string path, string? password)
    {
        try
        {
            PdfDocument document = string.IsNullOrEmpty(password)
                ? PdfDocument.Open(path)
                : PdfDocument.Open(path, password);

            return new OpenResult(
                Opened: true,
                PageCount: document.PageCount,
                ExceptionType: string.Empty,
                ExceptionMessage: string.Empty);
        }
        catch (Exception exception)
        {
            return new OpenResult(
                Opened: false,
                PageCount: 0,
                ExceptionType: exception.GetType().FullName ?? exception.GetType().Name,
                ExceptionMessage: exception.Message);
        }
    }

    private static bool ShouldRunCorpusTests()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("MODERNPDF_RUN_CORPUS"),
            "1",
            StringComparison.Ordinal);
    }

    private sealed record OpenResult(bool Opened, int PageCount, string ExceptionType, string ExceptionMessage);
}
