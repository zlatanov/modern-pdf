# ModernPDF

ModernPDF is a managed, cross-platform `.NET 10` PDF library built from first principles.

Current implemented scope includes:

- create/open/save PDF documents
- text extraction
- page/content editing
- JPEG image page authoring and replacement
- destructive text redaction
- password security for Standard handler profiles `V=1 / R=2 / 40-bit RC4`, `V=2 / R=3 / 128-bit RC4`, `V=4 / R=4 / 128-bit AES`, and `V=5 / R=6 / 256-bit AES`
- detached digital signatures (callback-based CMS embedding with ByteRange patching)
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

## Image pages (JPEG)

You can add a JPEG image as a page or replace an existing page with a JPEG image:

```csharp
PdfDocument document = PdfDocument.Create();
document.AddImagePage(@"C:\images\cover.jpg");
document.ReplacePageImage(0, @"C:\images\updated-cover.jpg");
```

## Save cross-reference style

You can choose classic xref tables or stream-style xref output during save:

```csharp
byte[] streamStylePdf = document.Save(
    new PdfSaveOptions
    {
        CrossReferenceStyle = PdfCrossReferenceStyle.Stream,
    });
```

## Security profiles

`PdfSecurityOptions.Profile` selects the Standard security handler profile used when saving:

```csharp
byte[] encryptedPdf = document.Save(
    new PdfSaveOptions
    {
        Security = new PdfSecurityOptions
        {
            UserPassword = "pw",
            Profile = PdfSecurityProfile.Standard128BitAes,
            Permissions = PdfPermissions.Print | PdfPermissions.Copy | PdfPermissions.FillForms,
        },
    });
```

When you open an encrypted PDF with a password, subsequent `Save()` calls preserve encryption automatically, including append-only incremental saves.

## Detached signatures (MVP)

The signature API is callback-based: ModernPDF computes and patches `/ByteRange`, provides the exact signed payload bytes, and embeds returned CMS bytes into `/Contents`.

```csharp
PdfDocument document = PdfDocument.Create();
document.AddTextPage("Signed content");

byte[] signedBytes = document.SaveSignedDetached(
    payloadToSign =>
    {
        // Replace with your CMS/PKCS#7 detached signer implementation.
        return MyCmsSigner.SignDetached(payloadToSign.Span);
    },
    new PdfSignatureOptions
    {
        ContentsByteLength = 8192,
        Reason = "Approval",
    });
```

You can call `SaveSignedDetached(...)` again on an opened signed document to append additional detached signatures incrementally.

Validate detached signatures (CMS/PKCS#7):

```csharp
PdfDocument signed = PdfDocument.Open(signedBytes);
IReadOnlyList<PdfDetachedSignatureValidationResult> results =
    signed.ValidateDetachedSignatures(
        new PdfDetachedSignatureValidationOptions
        {
            VerifyCertificateChain = true,
            RequireSigningTime = true,
            RequireRevocationStatus = true,
            RevocationCheckMode = PdfRevocationCheckMode.Online,
            RequiredCertificatePolicyOids = ["1.2.3.4.5"],
            ValidationTime = DateTimeOffset.UtcNow,
        });
```

Each `PdfDetachedSignatureValidationResult` reports cryptographic validity and trust diagnostics separately (`CryptographicallyValid`, `TrustChecksPassed`, chain/revocation/signing-time/policy outcomes, and diagnostic messages).

## Corpus tests

Corpus tests are opt-in and use fixtures downloaded from pinned external source commits with SHA-256 verification. The full corpus suite includes stream-writer round-trip compatibility tests for known open fixtures.

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

- signature validation currently supports CMS subfilters `/adbe.pkcs7.detached`, `/ETSI.CAdES.detached`, `/adbe.pkcs7.sha1`, and `/ETSI.RFC3161`
- revocation validation supports online retrieval (`RevocationCheckMode = PdfRevocationCheckMode.Online`) and offline DSS OCSP/CRL evidence when embedded in `/DSS`; offline OCSP validation includes delegated responders when `id-kp-OCSPSigning` is present and the responder certificate chains to the OCSP certificate issuer, and enforces signature-scoped `/DSS /VRI` evidence matching; deterministic offline mode remains the default (`Offline`)
- image APIs currently support JPEG input only
- security support is intentionally limited to Standard handler profiles `V=1 / R=2`, `V=2 / R=3`, `V=4 / R=4`, and `V=5 / R=6`
- explicit `PdfSaveOptions.Security` with `PdfSaveMode.Incremental` is supported only for documents opened from encrypted PDFs, and must match the opened security context (password/profile/permissions)
