# ModernPDF

ModernPDF is a managed, cross-platform `.NET 10` library for reading, writing, editing, and securing PDF documents.

## Key features

- Create, open, edit, and save PDF documents
- Text extraction
- Page/content editing
- JPEG/PNG image page authoring and replacement
- Vector shape drawing with styling, transforms, gradients, clipping, and shape IDs
- Hard/soft redaction APIs (literal and pattern-based)
- Password security (`RC4/AES` standard handler profiles)
- Detached digital signatures (CMS callback + ByteRange patching)
- TrueType font embedding with fallback chains, shaping, and glyph subsetting

## Install

```bash
dotnet add package ModernPDF
```

## Quick start

```csharp
using ModernPDF;

// Create a new document and add a page with text
PdfDocument document = PdfDocument.Create();
document.AddTextPage("Hello from ModernPDF!");

// Save
document.Save("hello.pdf");

// Reopen and extract text
PdfDocument reopened = PdfDocument.Open("hello.pdf");
string text = reopened.ExtractText();
```

## Font and rich text example

```csharp
PdfDocument document = PdfDocument.Create();
document.DefaultTextOptions = new PdfTextOptions
{
    TrueTypeFontPath = @"C:\fonts\MyFont.ttf",
    FallbackTrueTypeFontPaths = [@"C:\fonts\Fallback.ttf"],
    SubsetFont = true
};

document.AddRichTextPage(
[
    new PdfTextSpan { Text = "Normal " },
    new PdfTextSpan { Text = "Large", FontSize = 24 },
    new PdfTextSpan { Text = " text", FontSize = 12 }
]);
```
