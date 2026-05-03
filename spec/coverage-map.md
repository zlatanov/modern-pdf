# PDF coverage map

This document maps major PDF capability areas to ModernPDF implementation phases and internal namespaces. Clause references should be added as implementation proceeds.

## Foundation and file structure

**Internal area:** `Format`, `Primitives`

Focus:

- file header and version handling
- lexical structure and tokenization
- primitive objects
- indirect objects and references
- streams
- cross-reference tables
- trailers
- incremental update foundations

ModernPDF phase alignment:

- Phase 1: low-level PDF format engine

## Document structure

**Internal area:** `DocumentModel`

Focus:

- catalog
- page tree
- page dictionaries
- resources
- metadata
- outlines and annotations as later extensions

ModernPDF phase alignment:

- Phase 2: internal document model
- Phase 3: public document API

## Content generation and graphics/text operators

**Internal area:** `Format`, `Text`, public creation APIs

Focus:

- content streams
- graphics state basics
- text state basics
- text showing operators
- resource registration
- fonts and encodings required for useful output

ModernPDF phase alignment:

- Phase 4: content generation

## Text extraction

**Internal area:** `Text`

Focus:

- operator interpretation
- text matrices and placement
- glyph-to-Unicode mapping
- extraction order heuristics
- positional results usable by redaction

ModernPDF phase alignment:

- Phase 5: text extraction

## Editing and save behavior

**Internal area:** `Editing`, `DocumentModel`, `Format`

Focus:

- object updates
- page edits
- metadata edits
- full rewrite save path
- incremental save path

ModernPDF phase alignment:

- Phase 6: editing

## Redaction

**Internal area:** `Redaction`, `Text`, `Editing`

Focus:

- target discovery from text and geometry
- destructive content removal or rewrite
- metadata cleanup
- saved-output verification that removed content is not recoverable through normal extraction

ModernPDF phase alignment:

- Phase 7: redaction

## Security

**Internal area:** `Security`, `Format`, `Editing`

Focus:

- encryption dictionaries
- user and owner password workflows
- permissions flags
- encrypted string and stream handling
- interoperability of encrypted outputs

ModernPDF phase alignment:

- Phase 8: security

## Deferred areas

These are not part of the initial roadmap, though the architecture should avoid blocking them:

- digital signatures
- forms beyond basic structural support
- tagged PDF and accessibility depth
- rendering and visual comparison infrastructure
- advanced color management
- embedded files and portfolios

## Agent guidance

- Treat this file as a routing map, not as the source of truth for behavior.
- When implementing a feature, record the relevant clause numbers and errata in the nearest code or test notes once they are known.
- Prefer adding support intentionally for a subset over claiming broad coverage prematurely.
