namespace ModernPDF.CorpusTests;

public sealed class CorpusManifestTests
{
    [Fact]
    public void ManifestUsesSupportedSchemaVersion()
    {
        CorpusManifest manifest = CorpusManifestLoader.Load();

        Assert.Equal(2, manifest.Version);
    }

    [Fact]
    public void ManifestHasUniqueFixtureIds()
    {
        CorpusManifest manifest = CorpusManifestLoader.Load();
        string[] duplicateIds = manifest.Fixtures
            .GroupBy(fixture => fixture.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicateIds);
    }

    [Fact]
    public void ManifestContainsEnabledSmokeFixtures()
    {
        CorpusManifest manifest = CorpusManifestLoader.Load();
        IReadOnlyList<CorpusFixture> smokeFixtures = CorpusManifestLoader.SelectFixtures(
            manifest,
            profile: "smoke",
            enabledOnly: true);

        Assert.NotEmpty(smokeFixtures);
    }

    [Fact]
    public void ManifestContainsLargeEnabledFullCorpusFromMultipleSources()
    {
        CorpusManifest manifest = CorpusManifestLoader.Load();
        IReadOnlyList<CorpusFixture> fullFixtures = CorpusManifestLoader.SelectFixtures(
            manifest,
            profile: "full",
            enabledOnly: true);

        Assert.True(fullFixtures.Count >= 50, $"Expected at least 50 enabled full fixtures, found {fullFixtures.Count}.");

        int distinctSources = fullFixtures
            .Select(fixture => fixture.SourceRepository)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.True(distinctSources >= 3, $"Expected at least 3 fixture sources, found {distinctSources}.");
    }

    [Fact]
    public void ManifestFixturesContainRequiredMetadata()
    {
        CorpusManifest manifest = CorpusManifestLoader.Load();
        HashSet<string> expectedResults = new(StringComparer.OrdinalIgnoreCase)
        {
            "any",
            "opened",
            "not_supported",
            "unauthorized",
            "format_error",
        };

        Assert.All(
            manifest.Fixtures,
            fixture =>
            {
                Assert.False(string.IsNullOrWhiteSpace(fixture.Id));
                Assert.False(string.IsNullOrWhiteSpace(fixture.SourcePath));
                Assert.False(string.IsNullOrWhiteSpace(fixture.SourceRepository));
                Assert.False(string.IsNullOrWhiteSpace(fixture.SourceCommit));
                Assert.False(string.IsNullOrWhiteSpace(fixture.LocalPath));
                Assert.False(string.IsNullOrWhiteSpace(fixture.Sha256));
                Assert.Matches("^[a-f0-9]{64}$", fixture.Sha256);
                Assert.Matches("^[a-f0-9]{40}$", fixture.SourceCommit);
                Assert.NotEmpty(fixture.Profiles);
                Assert.Contains(fixture.ExpectedOpenResult, expectedResults);
            });
    }
}
