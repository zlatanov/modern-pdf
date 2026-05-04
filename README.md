# ModernPDF

ModernPDF is a managed, cross-platform `.NET 10` PDF library built from first principles.

Current implemented scope includes:

- create/open/save PDF documents
- text extraction
- page/content editing
- destructive text redaction
- password security for Standard handler `V=1 / R=2 / 40-bit` (with permission flags)
- TrueType font embedding for generated/replaced text with automatic subsetting and OpenType shaping
- corpus-based interoperability/hardening test harness

## Repository layout

- `src\ModernPDF` — main library
- `tests\ModernPDF.Tests` — unit/integration tests
- `tests\ModernPDF.CorpusTests` — external corpus interoperability tests
- `spec\` — spec mapping and corpus source documentation

## Prerequisites

- .NET SDK `10.0.x`
- PowerShell (`pwsh`) for corpus fixture sync scripts

## Build and test

```powershell
dotnet build ModernPDF.slnx
dotnet test ModernPDF.slnx
```

## Samples

The repository includes a runnable sample console app at `samples\ModernPDF.Samples` built with `System.CommandLine`.
On Windows, sample commands configure `PdfDocument.DefaultTextOptions` automatically to use a system TrueType font when available.

```powershell
# list commands and options
dotnet run --project samples\ModernPDF.Samples -- --help

# run individual samples
dotnet run --project samples\ModernPDF.Samples -- create
dotnet run --project samples\ModernPDF.Samples -- extract --output .\artifacts\samples
dotnet run --project samples\ModernPDF.Samples -- redact --output .\artifacts\samples
dotnet run --project samples\ModernPDF.Samples -- secure --output .\artifacts\samples
```

## Embedded TrueType fonts

`PdfTextOptions` supports embedding a TrueType font file, fallback font chains (including mixed fallback runs within one line), OpenType shaping (including surrogate pairs), paragraph wrapping, justification, and subsetting glyphs by default.
You can configure this once per document via `DefaultTextOptions` and still override per call:

```csharp
PdfDocument document = PdfDocument.Create();
document.DefaultTextOptions = new PdfTextOptions
{
    TrueTypeFontPath = @"C:\fonts\MyFont.ttf",
    SubsetFont = true, // default
};
document.AddTextPage("Uses the document default font");

document.AddTextPage(
    "Per-call override still works",
    textOptions: new PdfTextOptions
    {
        TrueTypeFontPath = @"C:\fonts\AnotherFont.ttf",
        FallbackTrueTypeFontPaths = [@"C:\fonts\Fallback.ttf"],
        SubsetFont = false,
        MaxWidth = 420,
        LineHeightMultiplier = 1.4,
        Alignment = PdfTextAlignment.Justify,
        Direction = PdfTextDirection.Auto,
        WritingMode = PdfWritingMode.Horizontal,
        EnableHyphenation = true,
    });
```

Rich spans in a single paragraph (including embedded TrueType rendering):

```csharp
document.AddRichTextPage(
[
    new PdfTextSpan { Text = "Normal " },
    new PdfTextSpan { Text = "Big", FontSize = 24 },
    new PdfTextSpan { Text = " text", FontSize = 12 },
],
textOptions: new PdfTextOptions
{
    TrueTypeFontPath = @"C:\fonts\MyFont.ttf",
    FallbackTrueTypeFontPaths = [@"C:\fonts\Fallback.ttf"],
});
```

## Corpus tests

Corpus tests are opt-in and use fixtures downloaded from pinned external source commits with SHA-256 verification.

Smoke corpus:

```powershell
pwsh tests\ModernPDF.CorpusTests\scripts\sync-fixtures.ps1 -Profile smoke
$env:MODERNPDF_RUN_CORPUS = "1"
dotnet test tests\ModernPDF.CorpusTests\ModernPDF.CorpusTests.csproj --filter "Category=Smoke"
```

Full corpus:

```powershell
pwsh tests\ModernPDF.CorpusTests\scripts\sync-fixtures.ps1 -Profile full
$env:MODERNPDF_RUN_CORPUS = "1"
dotnet test tests\ModernPDF.CorpusTests\ModernPDF.CorpusTests.csproj --filter "Category=Full"
```

See `spec\corpus-sources.md` for source commits, license notes, and corpus policy.

## Current limitations

- incremental save is not implemented
- digital signatures are not implemented
- security support is intentionally limited to Standard handler `V=1 / R=2`
