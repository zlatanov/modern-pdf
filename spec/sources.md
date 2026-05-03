# PDF sources

## Primary standards target

### ISO 32000-2:2020

Primary specification target for ModernPDF.

- Reference page: <https://pdfa.org/resource/iso-32000-pdf/>
- Notes:
  - PDF 2.0 was first published in 2017.
  - ISO 32000-2:2020 is the corrected and clarified dated revision.
  - The PDF Association provides access to the current specification bundle.

## Errata and issue tracking

### PDF Association errata site

- Public errata index: <https://pdfa.org/pdf-issues/>
- Clause browser for ISO 32000-2:2020: <https://pdf-issues.pdfa.org/32000-2-2020/>
- Example clause page: <https://pdf-issues.pdfa.org/32000-2-2020/clause00.html>

Use this when the base specification text is ambiguous, corrected, or clarified by industry-ratified errata.

## Normative references

### PDF Association archive of normative references

- Reference archive: <https://pdfa.org/resource/normative-references-of-iso-32000/>

Some parts of the PDF standard rely on external reference documents. Those references matter especially for:

- fonts and character mapping
- color spaces and ICC profiles
- cryptographic algorithms and identifiers
- metadata and embedded file conventions

## Examples and interoperability material

### PDF 2.0 sample files

- Sample repository: <https://github.com/pdf-association/pdf20examples>

Use sample files for parser, writer, round-trip, and interoperability coverage once test infrastructure exists.

### Application notes

The PDF Association also publishes PDF 2.0 application notes for specific feature areas.

- Entry point: <https://pdfa.org/resource/iso-32000-pdf/>

## Practical implementation stance

ModernPDF should use these sources in this order:

1. ISO 32000-2:2020 as the main behavioral reference
2. ratified errata where they clarify or correct the standard
3. normative references when a PDF clause depends on them
4. sample files and application notes for implementation validation

## Initial scope alignment

For the first roadmap, prioritize sources that affect:

- document structure
- objects, streams, and cross-reference handling
- page/content representation
- text extraction basics
- incremental update behavior
- password encryption and permissions

Digital signatures remain outside the first delivery roadmap.
