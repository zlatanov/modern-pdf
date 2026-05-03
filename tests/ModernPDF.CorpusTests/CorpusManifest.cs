using System.Text.Json;

namespace ModernPDF.CorpusTests;

internal sealed class CorpusManifest
{
    public required int Version { get; init; }

    public required IReadOnlyList<CorpusFixture> Fixtures { get; init; }
}

internal sealed class CorpusFixture
{
    public required string Id { get; init; }

    public required string SourcePath { get; init; }

    public required string SourceRepository { get; init; }

    public required string SourceCommit { get; init; }

    public required string LocalPath { get; init; }

    public required string Sha256 { get; init; }

    public required IReadOnlyList<string> Profiles { get; init; }

    public required bool Enabled { get; init; }

    public required string ExpectedOpenResult { get; init; }

    public string? Password { get; init; }
}

internal static class CorpusManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static CorpusManifest Load()
    {
        string manifestPath = GetManifestPath();
        string json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<CorpusManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse corpus fixture manifest.");
    }

    public static IReadOnlyList<CorpusFixture> SelectFixtures(CorpusManifest manifest, string profile, bool enabledOnly)
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        List<CorpusFixture> selected = manifest.Fixtures
            .Where(fixture => fixture.Profiles.Any(profileName => comparer.Equals(profileName, profile)))
            .ToList();

        if (enabledOnly)
        {
            selected = selected.Where(fixture => fixture.Enabled).ToList();
        }

        return selected;
    }

    public static string GetDownloadedFixturePath(CorpusFixture fixture)
    {
        string localPath = fixture.LocalPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(GetRepositoryRoot(), "tests", "ModernPDF.CorpusTests", "fixtures", "downloaded", localPath);
    }

    public static string ClassifyOpenResult(byte[] bytes, string? password)
    {
        try
        {
            _ = string.IsNullOrEmpty(password)
                ? PdfDocument.Open(bytes)
                : PdfDocument.Open(bytes, password);
            return "opened";
        }
        catch (Exception exception) when (exception is NotSupportedException)
        {
            return "not_supported";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException)
        {
            return "unauthorized";
        }
        catch (Exception exception) when (string.Equals(exception.GetType().FullName, "ModernPDF.Format.PdfFormatException", StringComparison.Ordinal))
        {
            return "format_error";
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return "format_error";
        }
        catch
        {
            return "other_error";
        }
    }

    private static string GetManifestPath()
    {
        return Path.Combine(GetRepositoryRoot(), "tests", "ModernPDF.CorpusTests", "fixtures", "manifest.json");
    }

    private static string GetRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ModernPDF.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing ModernPDF.slnx.");
    }
}
