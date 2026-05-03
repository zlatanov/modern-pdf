# PDF glossary for ModernPDF

Short, implementation-oriented terminology for consistent naming. These entries are summaries, not specification text.

## Core file concepts

### object

A PDF value stored either directly or indirectly. Objects include primitives such as names and arrays, and compound values such as dictionaries and streams.

### indirect object

An object identified by object number and generation number so other parts of the file can reference it.

### reference

A pointer to an indirect object.

### stream

A dictionary plus a byte sequence. Streams hold larger or structured payloads such as page content, images, and embedded font data.

### cross-reference

The lookup structure that lets a reader find indirect objects in the file.

### trailer

The structure that points to key document-level objects and file-level state.

### incremental update

A save model that appends new or changed objects and a new cross-reference section instead of rewriting the whole file.

## Document concepts

### catalog

The top-level document dictionary that anchors the logical structure of the PDF.

### page tree

The hierarchical structure used to organize pages and inherit some page properties.

### resources

Fonts, graphics state definitions, color spaces, patterns, and other reusable data referenced by content streams.

### content stream

An instruction stream that describes what appears on a page or form XObject.

## Text concepts

### text state

The current configuration used by text operators, such as font, size, spacing, and transformation values.

### glyph mapping

The process of translating encoded character data in a content stream into Unicode text or equivalent logical text output.

## Editing and redaction concepts

### full save

A save path that rewrites the entire document.

### redaction

A destructive operation that removes or rewrites sensitive information so it is no longer available through ordinary PDF data access paths.

## Security concepts

### security handler

The mechanism that defines how encryption, passwords, and permissions are represented and enforced in a PDF.

### user password

The password typically used to open a protected document with standard access.

### owner password

The password associated with changing restrictions or full control under the standard security model.

### permissions

Document restriction flags associated with actions such as printing, modifying, or copying content.
