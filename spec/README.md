# ModernPDF spec map

This folder is the repository-local map of the PDF specification material that ModernPDF will implement against.

It does **not** contain the ISO or Adobe specification text. Instead, it contains:

- official source links
- our internal terminology
- implementation-focused coverage notes
- agent guidance for mapping features back to the spec

## Intended use

- Read `sources.md` to find the canonical standards and public reference material.
- Read `coverage-map.md` before implementing a feature area.
- Read `glossary.md` when naming APIs or internal concepts.

## Baseline standard target

ModernPDF should primarily target **ISO 32000-2:2020 (PDF 2.0)** as the main reference point, while remaining practical about interoperability with older PDF files encountered in the wild.

## Rules

- Do not copy large passages from the specification into this repository.
- Prefer citing clause numbers, document titles, and URLs.
- Summaries in this folder should be implementation-oriented and written in our own words.
- If a behavior depends on errata, note that explicitly.
