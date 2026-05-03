namespace ModernPDF.CorpusTests;

public sealed class CorpusFullTests
{
    public static IEnumerable<object[]> EnabledFullFixtureIds()
    {
        CorpusManifest manifest = CorpusManifestLoader.Load();
        return CorpusManifestLoader.SelectFixtures(manifest, profile: "full", enabledOnly: true)
            .Select(fixture => new object[] { fixture.Id });
    }

    [Theory]
    [MemberData(nameof(EnabledFullFixtureIds))]
    [Trait("Category", "Corpus")]
    [Trait("Category", "Full")]
    public void FullFixtureMatchesExpectedOpenOutcomeWhenCorpusRuns(string fixtureId)
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

        byte[] bytes = File.ReadAllBytes(fixturePath);
        string actualResult = CorpusManifestLoader.ClassifyOpenResult(bytes, fixture.Password);

        if (!string.Equals(fixture.ExpectedOpenResult, "any", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(fixture.ExpectedOpenResult, actualResult, ignoreCase: true);
        }
    }

    private static bool ShouldRunCorpusTests()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("MODERNPDF_RUN_CORPUS"),
            "1",
            StringComparison.Ordinal);
    }
}
