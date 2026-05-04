using System.Text;
using System.Reflection;
using ModernPDF.DocumentModel;
using ModernPDF.Fonts;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests;

public sealed class PdfDocumentTests
{
    [Fact]
    public void CreateReturnsNewDocument()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.NotNull(document);
        Assert.Equal(0, document.PageCount);
        Assert.Equal("2.0", document.Version);
    }

    [Fact]
    public void SaveAndOpenRoundTripMinimalDocument()
    {
        PdfDocument original = PdfDocument.Create();
        byte[] bytes = original.Save();

        PdfDocument opened = PdfDocument.Open(bytes);

        Assert.Equal(0, opened.PageCount);
        Assert.Equal("2.0", opened.Version);
    }

    [Fact]
    public void OpenWithPasswordAcceptsUnencryptedPdf()
    {
        PdfDocument original = PdfDocument.Create();
        byte[] bytes = original.Save();

        PdfDocument opened = PdfDocument.Open(bytes, "unused-password");

        Assert.Equal(0, opened.PageCount);
    }

    [Fact]
    public void OpenReadOnlySpanOverloadWorksForUnencryptedAndEncryptedPdf()
    {
        PdfDocument unencrypted = PdfDocument.Create();
        unencrypted.AddTextPage("span-open");

        PdfDocument openedUnencrypted = PdfDocument.Open(unencrypted.Save().AsSpan());
        Assert.Equal("span-open", openedUnencrypted.ExtractText());

        PdfDocument encrypted = PdfDocument.Create();
        encrypted.AddTextPage("secret");
        byte[] encryptedBytes = encrypted.Save(new PdfSaveOptions
        {
            Security = new PdfSecurityOptions
            {
                UserPassword = "pw",
            },
        });

        Assert.Throws<UnauthorizedAccessException>(() => PdfDocument.Open(encryptedBytes.AsSpan()));

        PdfDocument openedEncrypted = PdfDocument.Open(encryptedBytes.AsSpan(), "pw");
        Assert.Equal("secret", openedEncrypted.ExtractText());
    }

    [Fact]
    public void SaveWithIncrementalModeThrowsUntilImplemented()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<NotSupportedException>(() => document.Save(new PdfSaveOptions { Mode = PdfSaveMode.Incremental }));
    }

    [Fact]
    public void SaveWithSecurityOptionsThrowsUntilImplemented()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("secure text");

        byte[] encryptedBytes = document.Save(
            new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = "user-pass",
                    OwnerPassword = "owner-pass",
                    Permissions = PdfPermissions.Print | PdfPermissions.Copy,
                },
            });

        PdfEncryptionInfo? info = PdfDocument.InspectEncryption(encryptedBytes);
        Assert.NotNull(info);
        Assert.Equal("Standard", info.Filter);
        Assert.Equal(1, info.AlgorithmVersion);
        Assert.Equal(40, info.KeyLengthBits);

        PdfDocument opened = PdfDocument.Open(encryptedBytes, "user-pass");
        Assert.Equal("secure text", opened.ExtractText());
    }

    [Fact]
    public void SaveWithSecurityOptionsRejectsMissingUserPassword()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentException>(
            () => document.Save(new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = "",
                    Permissions = PdfPermissions.All,
                },
            }));
    }

    [Fact]
    public void SaveWithSecurityOptionsRejectsUnsupportedPermissionFlags()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<NotSupportedException>(
            () => document.Save(new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = "user-pass",
                    Permissions = PdfPermissions.FillForms,
                },
            }));
    }

    [Fact]
    public void SaveToPathAndOpenFromPathWorks()
    {
        PdfDocument document = PdfDocument.Create();
        string path = Path.GetTempFileName();

        try
        {
            document.Save(path);
            PdfDocument reopened = PdfDocument.Open(path);
            Assert.Equal(0, reopened.PageCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenPathOverloadValidatesArgumentsAndAcceptsPassword()
    {
        Assert.Throws<ArgumentException>(() => PdfDocument.Open(" "));
        Assert.Throws<ArgumentException>(() => PdfDocument.Open(" ", "password"));

        PdfDocument source = PdfDocument.Create();
        source.AddTextPage("path-open");
        byte[] encryptedBytes = source.Save(new PdfSaveOptions
        {
            Security = new PdfSecurityOptions
            {
                UserPassword = "pw",
            },
        });

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, encryptedBytes);
            Assert.Throws<ArgumentNullException>(() => PdfDocument.Open(path, null!));

            PdfDocument reopened = PdfDocument.Open(path, "pw");
            Assert.Equal("path-open", reopened.ExtractText());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenThrowsForUnsupportedEncryptedProfile()
    {
        byte[] encryptedPdfBytes = CreateEncryptedPdf();

        Assert.Throws<NotSupportedException>(() => PdfDocument.Open(encryptedPdfBytes));
    }

    [Fact]
    public void OpenWithPasswordThrowsForEncryptedPdfUntilSecuritySupportArrives()
    {
        byte[] encryptedPdfBytes = CreateEncryptedPdf();

        Assert.Throws<NotSupportedException>(() => PdfDocument.Open(encryptedPdfBytes, "password"));
    }

    [Fact]
    public void InspectEncryptionReturnsNullForUnencryptedPdf()
    {
        PdfDocument document = PdfDocument.Create();
        byte[] bytes = document.Save();

        PdfEncryptionInfo? info = PdfDocument.InspectEncryption(bytes);

        Assert.Null(info);
    }

    [Fact]
    public void InspectEncryptionReturnsBasicDictionaryDetails()
    {
        byte[] encryptedPdfBytes = CreateEncryptedPdf();

        PdfEncryptionInfo? info = PdfDocument.InspectEncryption(encryptedPdfBytes);

        Assert.NotNull(info);
        Assert.Equal("Standard", info.Filter);
        Assert.Equal(4, info.AlgorithmVersion);
        Assert.Equal(128, info.KeyLengthBits);
    }

    [Fact]
    public void InspectEncryptionFromPathReturnsEncryptionInfo()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("encrypted");

        string path = Path.GetTempFileName();
        try
        {
            byte[] bytes = document.Save(new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = "pw",
                },
            });
            File.WriteAllBytes(path, bytes);

            PdfEncryptionInfo? info = PdfDocument.InspectEncryption(path);
            Assert.NotNull(info);
            Assert.Equal("Standard", info.Filter);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenWithWrongPasswordThrowsUnauthorizedAccessException()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("protected");
        byte[] encryptedBytes = document.Save(new PdfSaveOptions
        {
            Security = new PdfSecurityOptions
            {
                UserPassword = "correct-password",
            },
        });

        Assert.Throws<UnauthorizedAccessException>(() => PdfDocument.Open(encryptedBytes, "wrong-password"));
    }

    [Fact]
    public void InspectEncryptionThrowsForMalformedEncryptionDictionaryWithoutDocumentId()
    {
        byte[] malformedPdfBytes = CreateMalformedEncryptedPdfWithoutDocumentId();

        Assert.Throws<PdfFormatException>(() => PdfDocument.InspectEncryption(malformedPdfBytes));
    }

    [Fact]
    public void OpenThrowsForMalformedEncryptionDictionaryWithInvalidOwnerEntryLength()
    {
        byte[] malformedPdfBytes = CreateMalformedEncryptedPdfWithInvalidOwnerEntry();

        Assert.Throws<PdfFormatException>(() => PdfDocument.Open(malformedPdfBytes, "password"));
    }

    [Fact]
    public void AddPageIncreasesPageCountAndRoundTrips()
    {
        PdfDocument document = PdfDocument.Create();

        int pageIndex = document.AddPage();

        Assert.Equal(0, pageIndex);
        Assert.Equal(1, document.PageCount);

        byte[] bytes = document.Save();
        PdfDocument reopened = PdfDocument.Open(bytes);
        Assert.Equal(1, reopened.PageCount);
    }

    [Fact]
    public void AddTextPageMakesTextExtractable()
    {
        PdfDocument document = PdfDocument.Create();

        int pageIndex = document.AddTextPage("Hello ModernPDF");

        Assert.Equal(0, pageIndex);
        Assert.Equal("Hello ModernPDF", document.ExtractText());
    }

    [Fact]
    public void AddTextPageEscapesLiteralStringCharacters()
    {
        PdfDocument document = PdfDocument.Create();
        string expected = "Text (with) \\ characters";

        document.AddTextPage(expected);

        Assert.Equal(expected, document.ExtractText());
    }

    [Fact]
    public void AddTextPageWithEmbeddedTrueTypeFontEmbedsFontAndExtractsText()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();

        document.AddTextPage(
            "Embedded font text",
            textOptions: new PdfTextOptions
            {
                TrueTypeFontPath = fontPath,
                SubsetFont = true,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal("Embedded font text", document.ExtractText());
        Assert.Contains("/FontFile2", ascii, StringComparison.Ordinal);
        Assert.Contains("/CIDFontType2", ascii, StringComparison.Ordinal);
        Assert.Contains("/CIDToGIDMap", ascii, StringComparison.Ordinal);
        Assert.Contains("/Type0", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTextPageWithEmbeddedTrueTypeFontSupportsSurrogatePairs()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        const string text = "emoji \U0001F600";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                TrueTypeFontPath = fontPath,
                SubsetFont = true,
            });

        Assert.Equal(text, document.ExtractText());
    }

    [Fact]
    public void AddTextPageWithEmbeddedTrueTypeFontShapesLigaturesAndExtractsText()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        const string text = "office ffi";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                TrueTypeFontPath = fontPath,
                SubsetFont = true,
            });

        Assert.Equal(text, document.ExtractText());
    }

    [Fact]
    public void AddTextPageWithEmbeddedTrueTypeFontWrapsParagraphAndPreservesText()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        const string text = "This is a long paragraph that should wrap into multiple lines while keeping extracted text stable.";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                FontSize = 20,
                X = 72,
                Y = 700,
                TrueTypeFontPath = fontPath,
                SubsetFont = true,
                MaxWidth = 120,
                LineHeightMultiplier = 1.5,
                Alignment = PdfTextAlignment.Center,
                Direction = PdfTextDirection.LeftToRight,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal(text, document.ExtractText());
        Assert.Contains(" 700 Tm <", ascii, StringComparison.Ordinal);
        Assert.Contains(" 670 Tm <", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTextPageWithEmbeddedTrueTypeFontSupportsRtlDirection()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        const string text = "مرحبا بالعالم";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                TrueTypeFontPath = fontPath,
                SubsetFont = true,
                MaxWidth = 240,
                Alignment = PdfTextAlignment.Right,
                Direction = PdfTextDirection.RightToLeft,
            });

        Assert.Equal(text, document.ExtractText());
    }

    [Fact]
    public void AddTextPageWithEmbeddedTrueTypeFontUsesFallbackChain()
    {
        PdfDocument document = PdfDocument.Create();
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ttf");
        string fallbackPath = GetFixtureFontPath();
        const string text = "Fallback chain \U0001F600";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                TrueTypeFontPath = missingPath,
                FallbackTrueTypeFontPaths = [fallbackPath],
                SubsetFont = true,
            });

        Assert.Equal(text, document.ExtractText());
    }

    [Fact]
    public void AddTextPageWithEmbeddedTrueTypeFontSupportsMixedFallbackRuns()
    {
        PdfDocument document = PdfDocument.Create();
        string fullFontPath = GetFixtureFontPath();
        string subsetPath = CreateTempSubsetFontFile(fullFontPath, "Fallback mixed runs abc ");
        const string text = "Fallback mixed runs café abc";

        try
        {
            document.AddTextPage(
                text,
                textOptions: new PdfTextOptions
                {
                    TrueTypeFontPath = subsetPath,
                    FallbackTrueTypeFontPaths = [fullFontPath],
                    SubsetFont = true,
                    MaxWidth = 320,
                });
        }
        finally
        {
            File.Delete(subsetPath);
        }

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal(text, document.ExtractText());
        Assert.Contains("/FontFile2", ascii, StringComparison.Ordinal);
        Assert.Contains("/F2", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTextPageWithBuiltInFontSupportsJustifyAlignment()
    {
        PdfDocument document = PdfDocument.Create();
        const string text = "This line should use justify spacing across multiple wrapped lines.";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                FontSize = 12,
                X = 72,
                Y = 720,
                MaxWidth = 140,
                Alignment = PdfTextAlignment.Justify,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal(text, document.ExtractText());
        Assert.Contains(" Tw ", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTextPageWithBuiltInFontJustifyWithoutSpacesUsesCharacterSpacing()
    {
        PdfDocument document = PdfDocument.Create();
        const string text = "Supercalifragilisticexpialidociousword";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                FontSize = 12,
                X = 72,
                Y = 720,
                MaxWidth = 120,
                Alignment = PdfTextAlignment.Justify,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal(text, document.ExtractText());
        Assert.Contains(" Tc ", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTextPageWithVerticalWritingModePlacesGlyphsTopToBottom()
    {
        PdfDocument document = PdfDocument.Create();
        const string text = "ABC";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                FontSize = 12,
                X = 100,
                Y = 700,
                WritingMode = PdfWritingMode.Vertical,
                LineHeightMultiplier = 1,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal(text, document.ExtractText());
        Assert.Contains("1 0 0 1 100 700 Tm (A) Tj", ascii, StringComparison.Ordinal);
        Assert.Contains("1 0 0 1 100 688 Tm (B) Tj", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTextPageWithVerticalRtlDirectionMovesColumnsRightToLeft()
    {
        PdfDocument document = PdfDocument.Create();
        const string text = "ABCD";

        document.AddTextPage(
            text,
            textOptions: new PdfTextOptions
            {
                FontSize = 12,
                X = 100,
                Y = 700,
                WritingMode = PdfWritingMode.Vertical,
                LineHeightMultiplier = 1,
                MaxWidth = 24,
                Direction = PdfTextDirection.RightToLeft,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal(text, document.ExtractText());
        Assert.Contains("1 0 0 1 100 700 Tm (A) Tj", ascii, StringComparison.Ordinal);
        Assert.Contains("1 0 0 1 88 700 Tm (C) Tj", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRichTextPageSupportsMixedSpanSizes()
    {
        PdfDocument document = PdfDocument.Create();

        document.AddRichTextPage(
            [
                new PdfTextSpan { Text = "Hello " },
                new PdfTextSpan { Text = "BIG", FontSize = 24 },
                new PdfTextSpan { Text = " world", FontSize = 12 },
            ],
            textOptions: new PdfTextOptions
            {
                X = 72,
                Y = 720,
                MaxWidth = 400,
                Alignment = PdfTextAlignment.Justify,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal("Hello BIG world", document.ExtractText());
        Assert.Contains("/F1 24 Tf", ascii, StringComparison.Ordinal);
        Assert.Contains("/F1 12 Tf", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRichTextPageSupportsEmbeddedTrueTypeSpans()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();

        document.AddRichTextPage(
        [
            new PdfTextSpan { Text = "Rich " },
            new PdfTextSpan { Text = "embedded", FontSize = 20 },
            new PdfTextSpan { Text = " 👩‍💻 text", FontSize = 14 },
        ],
            textOptions: new PdfTextOptions
            {
                FontSize = 14,
                X = 72,
                Y = 720,
                MaxWidth = 420,
                TrueTypeFontPath = fontPath,
                FallbackTrueTypeFontPaths = [fontPath],
                Alignment = PdfTextAlignment.Justify,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal("Rich embedded 👩‍💻 text", document.ExtractText());
        Assert.Contains("/FontFile2", ascii, StringComparison.Ordinal);
        Assert.Contains(" Tf 1 0 0 1 ", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacePageRichTextUpdatesPageText()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("old");

        document.ReplacePageRichText(
            0,
            [
                new PdfTextSpan { Text = "new " },
                new PdfTextSpan { Text = "value", FontSize = 18 },
            ]);

        Assert.Equal("new value", document.ExtractText());
    }

    [Fact]
    public void AddTextPageUsesDocumentDefaultTextOptionsWhenOptionsAreNotProvided()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        document.DefaultTextOptions = new PdfTextOptions
        {
            TrueTypeFontPath = fontPath,
            SubsetFont = true,
        };

        document.AddTextPage("Default embedded font");

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal("Default embedded font", document.ExtractText());
        Assert.Contains("/FontFile2", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTextPagePrefersExplicitTextOptionsOverDocumentDefaultTextOptions()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        document.DefaultTextOptions = new PdfTextOptions
        {
            TrueTypeFontPath = fontPath,
            SubsetFont = true,
        };

        document.AddTextPage(
            "Built-in font text",
            textOptions: new PdfTextOptions());

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal("Built-in font text", document.ExtractText());
        Assert.DoesNotContain("/FontFile2", ascii, StringComparison.Ordinal);
        Assert.Contains("/BaseFont /Helvetica", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTextPageSubsettingProducesSmallerOutputThanFullEmbedding()
    {
        string fontPath = GetFixtureFontPath();

        PdfDocument subset = PdfDocument.Create();
        subset.AddTextPage(
            "Subset me",
            textOptions: new PdfTextOptions
            {
                TrueTypeFontPath = fontPath,
                SubsetFont = true,
            });

        PdfDocument full = PdfDocument.Create();
        full.AddTextPage(
            "Subset me",
            textOptions: new PdfTextOptions
            {
                TrueTypeFontPath = fontPath,
                SubsetFont = false,
            });

        byte[] subsetBytes = subset.Save();
        byte[] fullBytes = full.Save();

        Assert.True(subsetBytes.Length < fullBytes.Length);
    }

    [Fact]
    public void AddPageUsesProvidedDimensions()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddPage(new PdfPageOptions { Width = 300, Height = 400 });

        byte[] bytes = document.Save();
        PdfFile file = PdfFileReader.Read(bytes);
        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);

        Assert.Single(model.Pages);
        Assert.NotNull(model.Pages[0].MediaBox);
        Assert.Equal(300, model.Pages[0].MediaBox?.Right);
        Assert.Equal(400, model.Pages[0].MediaBox?.Top);
    }

    [Fact]
    public void AddPageRejectsInvalidPageOptions()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPage(new PdfPageOptions { Width = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPage(new PdfPageOptions { Height = double.PositiveInfinity }));
    }

    [Fact]
    public void ExtractTextReturnsTextFromTjOperators()
    {
        byte[] pdfBytes = CreateSinglePageTextPdf("BT (Hello) Tj ( World) Tj ET");
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Equal("Hello World", document.ExtractText());
        Assert.Equal("Hello World", document.ExtractText(0));
    }

    [Fact]
    public void ExtractTextReturnsTextFromTjArrayOperator()
    {
        byte[] pdfBytes = CreateSinglePageTextPdf("BT [(A) -120 (B)] TJ ET");
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Equal("AB", document.ExtractText());
    }

    [Fact]
    public void ExtractTextThrowsForInvalidPageIndex()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.ExtractText(1));
    }

    [Fact]
    public void ExtractTextReturnsEmptyStringWhenPageHasNoContents()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(contents: null);
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Equal(string.Empty, document.ExtractText());
    }

    [Fact]
    public void ExtractTextThrowsWhenContentsIsNotReferenceOrArray()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(new PdfStringObject("invalid"));
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Throws<PdfFormatException>(() => document.ExtractText());
    }

    [Fact]
    public void ExtractTextThrowsWhenContentsArrayContainsNonReference()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(
            new PdfArrayObject(
            [
                new PdfStringObject("invalid"),
            ]));
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Throws<PdfFormatException>(() => document.ExtractText());
    }

    [Fact]
    public void ReplacePageTextUpdatesExtractedText()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("Old text");

        document.ReplacePageText(0, "New text");

        Assert.Equal("New text", document.ExtractText());
    }

    [Fact]
    public void ReplacePageTextWithEmbeddedTrueTypeFontUpdatesResourcesAndText()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        document.AddTextPage("Old text");

        document.ReplacePageText(
            0,
            "New embedded text",
            new PdfTextOptions
            {
                TrueTypeFontPath = fontPath,
                SubsetFont = true,
            });

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal("New embedded text", document.ExtractText());
        Assert.Contains("/CIDToGIDMap", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacePageTextWithEmbeddedTrueTypeFontSupportsSurrogatePairs()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        document.AddTextPage("Old text");

        document.ReplacePageText(
            0,
            "Updated \U0001F600",
            new PdfTextOptions
            {
                TrueTypeFontPath = fontPath,
                SubsetFont = true,
            });

        Assert.Equal("Updated \U0001F600", document.ExtractText());
    }

    [Fact]
    public void ReplacePageTextUsesDocumentDefaultTextOptionsWhenOptionsAreNotProvided()
    {
        PdfDocument document = PdfDocument.Create();
        string fontPath = GetFixtureFontPath();
        document.AddTextPage("Old text");
        document.DefaultTextOptions = new PdfTextOptions
        {
            TrueTypeFontPath = fontPath,
            SubsetFont = true,
        };

        document.ReplacePageText(0, "New embedded text");

        byte[] bytes = document.Save();
        string ascii = Encoding.ASCII.GetString(bytes);

        Assert.Equal("New embedded text", document.ExtractText());
        Assert.Contains("/CIDToGIDMap", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacePageContentsRejectsInvalidPageIndex()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageContents(0, "BT (X) Tj ET"));
    }

    [Fact]
    public void SetInfoProducerPersistsThroughSaveAndOpen()
    {
        PdfDocument document = PdfDocument.Create();

        document.SetInfoProducer("ModernPDF Unit Test");
        Assert.Equal("ModernPDF Unit Test", document.GetInfoProducer());

        byte[] bytes = document.Save();
        PdfDocument reopened = PdfDocument.Open(bytes);
        Assert.Equal("ModernPDF Unit Test", reopened.GetInfoProducer());
    }

    [Fact]
    public void GetInfoProducerThrowsWhenInfoReferenceObjectIsMissing()
    {
        PdfDocument document = PdfDocument.Open(CreatePdfWithMissingInfoObject());

        Assert.Throws<PdfFormatException>(() => document.GetInfoProducer());
    }

    [Fact]
    public void SetInfoProducerUpdatesExistingInfoDictionary()
    {
        PdfDocument document = PdfDocument.Create();
        document.SetInfoProducer("First producer");

        document.SetInfoProducer("Second producer");

        Assert.Equal("Second producer", document.GetInfoProducer());
    }

    [Fact]
    public void GetInfoProducerReturnsNullWhenMissingOrNotAString()
    {
        PdfDocument empty = PdfDocument.Create();
        Assert.Null(empty.GetInfoProducer());

        PdfDocument withoutProducer = PdfDocument.Open(CreatePdfWithInfoWithoutProducer());
        Assert.Null(withoutProducer.GetInfoProducer());
    }

    [Fact]
    public void RedactTextRemovesOccurrencesFromExtractedText()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("secret token secret");

        int count = document.RedactText("secret");

        Assert.Equal(2, count);
        Assert.Equal(" token ", document.ExtractText());
    }

    [Fact]
    public void RedactTextPersistsThroughSaveAndOpen()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("hello world");
        document.RedactText("world", "[REDACTED]");

        byte[] bytes = document.Save();
        PdfDocument reopened = PdfDocument.Open(bytes);

        Assert.Equal("hello [REDACTED]", reopened.ExtractText());
    }

    [Fact]
    public void RedactTextReturnsZeroWhenTargetIsMissing()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("alpha");

        int count = document.RedactText("beta");

        Assert.Equal(0, count);
        Assert.Equal("alpha", document.ExtractText());
    }

    [Fact]
    public void ExtractTextSupportsContentsArrayOfReferences()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(
            new PdfArrayObject(
            [
                new PdfReferenceObject(new PdfObjectId(4, 0)),
            ]));
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Equal("X", document.ExtractText());
    }

    [Fact]
    public void RedactTextRejectsEmptyTarget()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentException>(() => document.RedactText(""));
    }

    [Fact]
    public void RedactTextReturnsZeroWhenPageHasNoContents()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(contents: null);
        PdfDocument document = PdfDocument.Open(pdfBytes);

        int count = document.RedactText("secret");

        Assert.Equal(0, count);
    }

    [Fact]
    public void RedactTextThrowsWhenContentsIsNotReferenceOrArray()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(new PdfStringObject("invalid"));
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Throws<NotSupportedException>(() => document.RedactText("invalid"));
    }

    [Fact]
    public void RedactTextThrowsWhenContentsArrayContainsNonReference()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(
            new PdfArrayObject(
            [
                new PdfStringObject("invalid"),
            ]));
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Throws<NotSupportedException>(() => document.RedactText("invalid"));
    }

    [Fact]
    public void RedactTextSupportsContentsArrayOfReferences()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(
            new PdfArrayObject(
            [
                new PdfReferenceObject(new PdfObjectId(4, 0)),
            ]));
        PdfDocument document = PdfDocument.Open(pdfBytes);

        int count = document.RedactText("X", "Y");

        Assert.Equal(1, count);
        Assert.Equal("Y", document.ExtractText());
    }

    [Fact]
    public void ReplacePageContentsThrowsWhenContentsIsNotReference()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(
            new PdfArrayObject(
            [
                new PdfReferenceObject(new PdfObjectId(4, 0)),
            ]));
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Throws<NotSupportedException>(() => document.ReplacePageContents(0, "BT (X) Tj ET"));
    }

    [Fact]
    public void ReplacePageContentsThrowsWhenReferencedContentsObjectIsMissing()
    {
        byte[] pdfBytes = CreateSinglePagePdfWithCustomContents(new PdfReferenceObject(new PdfObjectId(9, 0)));
        PdfDocument document = PdfDocument.Open(pdfBytes);

        Assert.Throws<PdfFormatException>(() => document.ReplacePageContents(0, "BT (X) Tj ET"));
    }

    [Fact]
    public void InternalHelpersThrowForMissingDictionaryEntryAndReplacementObject()
    {
        MethodInfo requireDictionaryEntry = typeof(PdfDocument).GetMethod("RequireDictionaryEntry", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo replaceObject = typeof(PdfDocument).GetMethod("ReplaceObject", BindingFlags.NonPublic | BindingFlags.Static)!;

        PdfDictionaryObject dictionary = new([]);
        List<PdfIndirectObject> objects = [];

        TargetInvocationException missingEntry = Assert.Throws<TargetInvocationException>(
            () => requireDictionaryEntry.Invoke(null, [dictionary, "Missing"]));
        TargetInvocationException missingObject = Assert.Throws<TargetInvocationException>(
            () => replaceObject.Invoke(null, [objects, new PdfObjectId(10, 0), new PdfStringObject("value")]));

        Assert.IsType<PdfFormatException>(missingEntry.InnerException);
        Assert.IsType<PdfFormatException>(missingObject.InnerException);
    }

    [Fact]
    public void ReplacePageTextRejectsInvalidTextOptions()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("initial");

        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { FontSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { X = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { Y = double.PositiveInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { MaxWidth = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { LineHeightMultiplier = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { Alignment = (PdfTextAlignment)999 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { Direction = (PdfTextDirection)999 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { WritingMode = (PdfWritingMode)999 }));
        Assert.Throws<ArgumentException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { FallbackTrueTypeFontPaths = [""] }));
        Assert.Throws<ArgumentException>(() => document.ReplacePageText(0, "x", new PdfTextOptions { TrueTypeFontPath = " " }));
    }

    [Fact]
    public void ReplacePageRichTextRejectsInvalidSpanFallbackEntries()
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("initial");

        Assert.Throws<ArgumentException>(
            () => document.ReplacePageRichText(
                0,
                [
                    new PdfTextSpan
                    {
                        Text = "x",
                        FallbackTrueTypeFontPaths = [" "],
                    },
                ]));
    }

    [Fact]
    public void DefaultTextOptionsRejectsInvalidValues()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentNullException>(() => document.DefaultTextOptions = null!);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.DefaultTextOptions = new PdfTextOptions { FontSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => document.DefaultTextOptions = new PdfTextOptions { X = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => document.DefaultTextOptions = new PdfTextOptions { Y = double.PositiveInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => document.DefaultTextOptions = new PdfTextOptions { MaxWidth = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => document.DefaultTextOptions = new PdfTextOptions { LineHeightMultiplier = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => document.DefaultTextOptions = new PdfTextOptions { Alignment = (PdfTextAlignment)999 });
        Assert.Throws<ArgumentOutOfRangeException>(() => document.DefaultTextOptions = new PdfTextOptions { Direction = (PdfTextDirection)999 });
        Assert.Throws<ArgumentOutOfRangeException>(() => document.DefaultTextOptions = new PdfTextOptions { WritingMode = (PdfWritingMode)999 });
        Assert.Throws<ArgumentException>(() => document.DefaultTextOptions = new PdfTextOptions { FallbackTrueTypeFontPaths = [" "] });
        Assert.Throws<ArgumentException>(() => document.DefaultTextOptions = new PdfTextOptions { TrueTypeFontPath = " " });
    }

    [Fact]
    public void SaveWithSecurityOptionsRejectsEmptyOwnerPasswordAndUnknownPermissionBits()
    {
        PdfDocument document = PdfDocument.Create();

        Assert.Throws<ArgumentException>(
            () => document.Save(new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = "pw",
                    OwnerPassword = "",
                },
            }));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => document.Save(new PdfSaveOptions
            {
                Security = new PdfSecurityOptions
                {
                    UserPassword = "pw",
                    Permissions = (PdfPermissions)256,
                },
            }));
    }

    private static string GetFixtureFontPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "LiberationSans-Regular.ttf");
        Assert.True(File.Exists(path), $"Expected test font fixture at '{path}'.");
        return path;
    }

    private static string CreateTempSubsetFontFile(string sourceFontPath, string subsetText)
    {
        PdfEmbeddedTrueTypeFont subset = PdfTrueTypeFontEmbedder.Build(
            sourceFontPath,
            subsetText,
            subsetFont: true,
            direction: PdfTextDirection.Auto);
        string path = Path.Combine(Path.GetTempPath(), $"modernpdf-subset-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(path, subset.FontProgram);
        return path;
    }

    private static byte[] CreateSinglePageTextPdf(string contentStream)
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry(
                "Kids",
                new PdfArrayObject(
                [
                    new PdfReferenceObject(new PdfObjectId(3, 0)),
                ])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(1, isInteger: true)),
        ]);

        PdfDictionaryObject page = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Page")),
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(new PdfObjectId(2, 0))),
            new PdfDictionaryEntry(
                "MediaBox",
                new PdfArrayObject(
                [
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(500, isInteger: true),
                    new PdfNumberObject(700, isInteger: true),
                ])),
            new PdfDictionaryEntry("Contents", new PdfReferenceObject(new PdfObjectId(4, 0))),
        ]);

        PdfStreamObject stream = new(
            new PdfDictionaryObject([]),
            Encoding.ASCII.GetBytes(contentStream));

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), page),
                new PdfIndirectObject(new PdfObjectId(4, 0), stream),
            ],
            trailer);

        return PdfFileWriter.Write(file);
    }

    private static byte[] CreateSinglePagePdfWithCustomContents(PdfObject? contents)
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry(
                "Kids",
                new PdfArrayObject(
                [
                    new PdfReferenceObject(new PdfObjectId(3, 0)),
                ])),
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Count", new PdfNumberObject(1, isInteger: true)),
        ]);

        List<PdfDictionaryEntry> pageEntries =
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Page")),
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(new PdfObjectId(2, 0))),
            new PdfDictionaryEntry(
                "MediaBox",
                new PdfArrayObject(
                [
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(500, isInteger: true),
                    new PdfNumberObject(700, isInteger: true),
                ])),
        ];

        if (contents is not null)
        {
            pageEntries.Add(new PdfDictionaryEntry("Contents", contents));
        }

        PdfDictionaryObject page = new(pageEntries);

        List<PdfIndirectObject> objects =
        [
            new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
            new PdfIndirectObject(new PdfObjectId(2, 0), pages),
            new PdfIndirectObject(new PdfObjectId(3, 0), page),
        ];

        objects.Add(
            new PdfIndirectObject(
                new PdfObjectId(4, 0),
                new PdfStreamObject(new PdfDictionaryObject([]), Encoding.ASCII.GetBytes("BT (X) Tj ET"))));

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        return PdfFileWriter.Write(new PdfFile("2.0", objects, trailer));
    }

    private static byte[] CreatePdfWithMissingInfoObject()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(0, isInteger: true)),
        ]);

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Info", new PdfReferenceObject(new PdfObjectId(9, 0))),
        ]);

        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
            ],
            trailer);

        return PdfFileWriter.Write(file);
    }

    private static byte[] CreateEncryptedPdf()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(0, isInteger: true)),
        ]);

        PdfDictionaryObject encryptDictionary = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(4, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(4, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject(128, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfStringObject("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")),
            new PdfDictionaryEntry("U", new PdfStringObject("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")),
        ]);

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Encrypt", new PdfReferenceObject(new PdfObjectId(3, 0))),
            new PdfDictionaryEntry(
                "ID",
                new PdfArrayObject(
                [
                    new PdfStringObject("0123456789ABCDEF"),
                    new PdfStringObject("0123456789ABCDEF"),
                ])),
        ]);

        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), encryptDictionary),
            ],
            trailer);

        return PdfFileWriter.Write(file);
    }

    private static byte[] CreateMalformedEncryptedPdfWithoutDocumentId()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(0, isInteger: true)),
        ]);

        PdfDictionaryObject encryptDictionary = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject(40, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfStringObject("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")),
            new PdfDictionaryEntry("U", new PdfStringObject("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")),
        ]);

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Encrypt", new PdfReferenceObject(new PdfObjectId(3, 0))),
        ]);

        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), encryptDictionary),
            ],
            trailer);

        return PdfFileWriter.Write(file);
    }

    private static byte[] CreateMalformedEncryptedPdfWithInvalidOwnerEntry()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(0, isInteger: true)),
        ]);

        PdfDictionaryObject encryptDictionary = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject(40, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfStringObject("short-owner")),
            new PdfDictionaryEntry("U", new PdfStringObject("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")),
        ]);

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Encrypt", new PdfReferenceObject(new PdfObjectId(3, 0))),
            new PdfDictionaryEntry(
                "ID",
                new PdfArrayObject(
                [
                    new PdfStringObject("0123456789ABCDEF"),
                    new PdfStringObject("0123456789ABCDEF"),
                ])),
        ]);

        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), encryptDictionary),
            ],
            trailer);

        return PdfFileWriter.Write(file);
    }

    private static byte[] CreatePdfWithInfoWithoutProducer()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
            new PdfDictionaryEntry("Pages", new PdfReferenceObject(new PdfObjectId(2, 0))),
        ]);

        PdfDictionaryObject pages = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pages")),
            new PdfDictionaryEntry("Kids", new PdfArrayObject([])),
            new PdfDictionaryEntry("Count", new PdfNumberObject(0, isInteger: true)),
        ]);

        PdfDictionaryObject info = new(
        [
            new PdfDictionaryEntry("Title", new PdfStringObject("No producer here")),
        ]);

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Info", new PdfReferenceObject(new PdfObjectId(3, 0))),
        ]);

        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
                new PdfIndirectObject(new PdfObjectId(3, 0), info),
            ],
            trailer);

        return PdfFileWriter.Write(file);
    }
}
