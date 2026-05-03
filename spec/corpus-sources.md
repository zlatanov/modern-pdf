# Corpus fixture sources

This repository uses a manifest-driven corpus harness under `tests\ModernPDF.CorpusTests`.

Current manifest footprint: **82 enabled full fixtures** and **2 smoke fixtures**.

## Current external sources

| Source | Commit pin | License | Why |
|---|---|---|---|
| `qpdf/qpdf` | `40801e523e1fb0ccbc1a09c4d573a3e92d2b46c0` | Apache-2.0 | Real malformed/edge-case PDFs for parser and open-path hardening |
| `apache/pdfbox` | `aba136447b22287e875496abbad3a14b5005459f` | Apache-2.0 | Parser/extraction-oriented test fixtures from a mature Java PDF stack |
| `mozilla/pdf.js` | `f54f4b606d900d0f556492fda1a91437e87611ef` | Apache-2.0 | Broad browser-focused PDF rendering/parser fixtures and annotation cases |

## Fixture policy

1. Fixtures are downloaded by script, not fetched during `dotnet test`.
2. Every fixture must have a SHA-256 checksum in `fixtures\manifest.json`.
3. Smoke corpus should run on PR CI; full corpus should run on scheduled pipelines or locally.
4. New fixture sources must include clear redistribution terms before enabling in CI.

## Commands

```powershell
# Smoke profile (fast)
pwsh tests\ModernPDF.CorpusTests\scripts\sync-fixtures.ps1 -Profile smoke
$env:MODERNPDF_RUN_CORPUS = "1"
dotnet test tests\ModernPDF.CorpusTests\ModernPDF.CorpusTests.csproj --filter "Category=Smoke"

# Full profile (large interoperability/hardening pass)
pwsh tests\ModernPDF.CorpusTests\scripts\sync-fixtures.ps1 -Profile full
$env:MODERNPDF_RUN_CORPUS = "1"
dotnet test tests\ModernPDF.CorpusTests\ModernPDF.CorpusTests.csproj --filter "Category=Full"
```
