using System.Text;

namespace ModernPDF.CorpusTests;

public sealed class StreamWriterCompatibilityTests
{
    public static IEnumerable<object[]> KnownOpenFixtureIds()
    {
        yield return new object[] { "pdfjs-api-basicapi" };
        yield return new object[] { "pdfjs-api-poppler-67295-0" };
        yield return new object[] { "pdfjs-api-issue15150" };
    }

    [Theory]
    [MemberData(nameof(KnownOpenFixtureIds))]
    [Trait("Category", "Corpus")]
    [Trait("Category", "Full")]
    public void FullSaveWithStreamCrossReferenceStyleRoundTripsKnownOpenFixtures(string fixtureId)
    {
        if (!ShouldRunCorpusTests())
        {
            return;
        }

        CorpusFixture fixture = LoadFixture(fixtureId);
        string fixturePath = CorpusManifestLoader.GetDownloadedFixturePath(fixture);
        Assert.True(
            File.Exists(fixturePath),
            $"Fixture '{fixture.Id}' is missing at '{fixturePath}'. Run `pwsh tests\\ModernPDF.CorpusTests\\scripts\\sync-fixtures.ps1 -Profile full`.");

        PdfDocument document = PdfDocument.Open(fixturePath);
        int baselinePageCount = document.PageCount;
        byte[] roundTripped = document.Save(
            new PdfSaveOptions
            {
                CrossReferenceStyle = PdfCrossReferenceStyle.Stream,
            });

        string text = Encoding.ASCII.GetString(roundTripped);
        Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);

        PdfDocument reopened = PdfDocument.Open(roundTripped);
        Assert.Equal(baselinePageCount, reopened.PageCount);
    }

    [Theory]
    [MemberData(nameof(KnownOpenFixtureIds))]
    [Trait("Category", "Corpus")]
    [Trait("Category", "Full")]
    public void IncrementalSaveWithStreamCrossReferenceStyleRoundTripsKnownOpenFixtures(string fixtureId)
    {
        if (!ShouldRunCorpusTests())
        {
            return;
        }

        CorpusFixture fixture = LoadFixture(fixtureId);
        string fixturePath = CorpusManifestLoader.GetDownloadedFixturePath(fixture);
        Assert.True(
            File.Exists(fixturePath),
            $"Fixture '{fixture.Id}' is missing at '{fixturePath}'. Run `pwsh tests\\ModernPDF.CorpusTests\\scripts\\sync-fixtures.ps1 -Profile full`.");

        PdfDocument document = PdfDocument.Open(fixturePath);
        int baselinePageCount = document.PageCount;
        document.SetInfoProducer("ModernPDF corpus stream incremental");
        byte[] incremental = document.Save(
            new PdfSaveOptions
            {
                Mode = PdfSaveMode.Incremental,
                CrossReferenceStyle = PdfCrossReferenceStyle.Stream,
            });

        string text = Encoding.ASCII.GetString(incremental);
        Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);
        Assert.Contains("/Prev", text, StringComparison.Ordinal);

        PdfDocument reopened = PdfDocument.Open(incremental);
        Assert.Equal(baselinePageCount, reopened.PageCount);
        Assert.Equal("ModernPDF corpus stream incremental", reopened.GetInfoProducer());
    }

    private static CorpusFixture LoadFixture(string fixtureId)
    {
        CorpusManifest manifest = CorpusManifestLoader.Load();
        return manifest.Fixtures.Single(
            fixture => string.Equals(fixture.Id, fixtureId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldRunCorpusTests()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("MODERNPDF_RUN_CORPUS"),
            "1",
            StringComparison.Ordinal);
    }
}
