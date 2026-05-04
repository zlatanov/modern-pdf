using ModernPDF.DocumentModel;
using ModernPDF.Fonts;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using ModernPDF.Security;
using ModernPDF.Text;
using System.Globalization;
using System.Text;

namespace ModernPDF;

public sealed class PdfDocument
{
    private PdfFile _file;
    private PdfDocumentModel _model;
    private PdfTextOptions _defaultTextOptions = new();

    private PdfDocument(PdfFile file, PdfDocumentModel model)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public static PdfDocument Create()
    {
        PdfFile file = CreateEmptyFile();
        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);
        return new PdfDocument(file, model);
    }

    public static PdfDocument Open(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Open(data.AsSpan(), password: null);
    }

    public static PdfDocument Open(byte[] data, string password)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(password);
        return Open(data.AsSpan(), password);
    }

    public static PdfDocument Open(ReadOnlySpan<byte> data)
    {
        return Open(data, password: null);
    }

    public static PdfDocument Open(ReadOnlySpan<byte> data, string? password)
    {
        PdfFile file = PdfFileReader.Read(data);
        if (PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out _))
        {
            if (!PdfStandardSecurityProcessor.IsSupportedStandardHandler(file))
            {
                throw new NotSupportedException("Only Standard security handler V=1 R=2 (40-bit) is currently supported.");
            }

            if (string.IsNullOrEmpty(password))
            {
                throw new UnauthorizedAccessException("Password is required to open encrypted PDFs.");
            }

            file = PdfStandardSecurityProcessor.Decrypt(file, password);
        }

        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);
        return new PdfDocument(file, model);
    }

    public static PdfDocument Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Open(File.ReadAllBytes(path));
    }

    public static PdfDocument Open(string path, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(password);
        return Open(File.ReadAllBytes(path), password);
    }

    public static PdfEncryptionInfo? InspectEncryption(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return InspectEncryption(data.AsSpan());
    }

    public static PdfEncryptionInfo? InspectEncryption(ReadOnlySpan<byte> data)
    {
        PdfFile file = PdfFileReader.Read(data);
        return PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out PdfEncryptionInfo? info) ? info : null;
    }

    public static PdfEncryptionInfo? InspectEncryption(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return InspectEncryption(File.ReadAllBytes(path));
    }

    public string Version => _file.Version;

    public int PageCount => _model.Pages.Count;

    public PdfTextOptions DefaultTextOptions
    {
        get => _defaultTextOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidateTextOptions(value);
            _defaultTextOptions = value;
        }
    }

    public int AddPage(PdfPageOptions? options = null)
    {
        PdfPageOptions pageOptions = options ?? new PdfPageOptions();
        return AddPageCore(pageOptions, text: null, textOptions: null);
    }

    public int AddTextPage(string text, PdfPageOptions? pageOptions = null, PdfTextOptions? textOptions = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        PdfPageOptions effectivePageOptions = pageOptions ?? new PdfPageOptions();
        PdfTextOptions effectiveTextOptions = ResolveTextOptions(textOptions);
        return AddPageCore(effectivePageOptions, text, effectiveTextOptions);
    }

    public int AddRichTextPage(
        IReadOnlyList<PdfTextSpan> spans,
        PdfPageOptions? pageOptions = null,
        PdfTextOptions? textOptions = null)
    {
        ValidateTextSpans(spans);

        int pageIndex = AddPage(pageOptions);
        ReplacePageRichText(pageIndex, spans, textOptions);
        return pageIndex;
    }

    public string ExtractText()
    {
        return PdfTextExtractor.ExtractAll(_file, _model);
    }

    public string ExtractText(int pageIndex)
    {
        return PdfTextExtractor.ExtractPage(_file, _model, pageIndex);
    }

    public byte[] Save(PdfSaveOptions? options = null)
    {
        PdfSaveOptions effectiveOptions = options ?? new PdfSaveOptions();
        if (effectiveOptions.Mode == PdfSaveMode.Incremental)
        {
            throw new NotSupportedException("Incremental save is not supported yet.");
        }

        PdfFile outputFile = _file;
        if (effectiveOptions.Security is not null)
        {
            ValidateSecurityOptions(effectiveOptions.Security);
            outputFile = PdfStandardSecurityProcessor.Encrypt(_file, effectiveOptions.Security);
        }

        return PdfFileWriter.Write(outputFile);
    }

    public void Save(string path, PdfSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, Save(options));
    }

    public void ReplacePageContents(int pageIndex, string rawContentStream)
    {
        ArgumentNullException.ThrowIfNull(rawContentStream);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfPageModel page = _model.Pages[pageIndex];
        if (page.Contents is not PdfReferenceObject contentsReference)
        {
            throw new NotSupportedException("Only page /Contents references are supported for replacement.");
        }

        PdfStreamObject existingStream = RequireStreamObject(contentsReference.ObjectId, "Page contents");
        PdfStreamObject updatedStream = new(existingStream.Dictionary, System.Text.Encoding.ASCII.GetBytes(rawContentStream));

        List<PdfIndirectObject> objects = [.. _file.Objects];
        ReplaceObject(objects, contentsReference.ObjectId, updatedStream);

        _file = new PdfFile(_file.Version, objects, _file.Trailer);
        _model = PdfDocumentModelBuilder.Build(_file);
        _model.Mutations.MarkDirty(contentsReference.ObjectId);
        _model.Mutations.MarkDirty(page.ObjectId);
    }

    public void ReplacePageText(int pageIndex, string text, PdfTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        PdfTextOptions effectiveOptions = ResolveTextOptions(options);

        if (effectiveOptions.TrueTypeFontPath is null)
        {
            ReplacePageContents(pageIndex, BuildTextContentStream(text, effectiveOptions));
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfPageModel page = _model.Pages[pageIndex];
        if (page.Contents is not PdfReferenceObject contentsReference)
        {
            throw new NotSupportedException("Only page /Contents references are supported for replacement.");
        }

        List<PdfIndirectObject> objects = [.. _file.Objects];
        int nextObjectNumber = GetNextObjectNumber(objects);

        PdfEmbeddedFontPlan embeddedPlan = BuildEmbeddedFontPlan(
            text,
            effectiveOptions,
            nextObjectNumber);

        objects.AddRange(embeddedPlan.ObjectsToAdd);

        PdfStreamObject existingStream = RequireStreamObject(contentsReference.ObjectId, "Page contents");
        PdfStreamObject updatedStream = new(existingStream.Dictionary, embeddedPlan.ContentStreamBytes);
        ReplaceObject(objects, contentsReference.ObjectId, updatedStream);

        PdfDictionaryObject pageDictionary = RequireDictionaryObject(page.ObjectId, "Page");
        PdfDictionaryObject updatedPage = ReplaceDictionaryEntries(
            pageDictionary,
            new PdfDictionaryEntry("Resources", new PdfReferenceObject(embeddedPlan.ResourcesObjectId)));
        ReplaceObject(objects, page.ObjectId, updatedPage);

        _file = new PdfFile(_file.Version, objects, _file.Trailer);
        _model = PdfDocumentModelBuilder.Build(_file);

        _model.Mutations.MarkDirty(page.ObjectId);
        _model.Mutations.MarkDirty(contentsReference.ObjectId);
        foreach (PdfObjectId objectId in embeddedPlan.DirtyObjectIds)
        {
            _model.Mutations.MarkDirty(objectId);
        }
    }

    public void ReplacePageRichText(int pageIndex, IReadOnlyList<PdfTextSpan> spans, PdfTextOptions? options = null)
    {
        ValidateTextSpans(spans);
        PdfTextOptions effectiveOptions = ResolveTextOptions(options);

        if (effectiveOptions.TrueTypeFontPath is not null || spans.Any(static span => span.TrueTypeFontPath is not null))
        {
            throw new NotSupportedException("Rich text spans currently support built-in Type1 rendering only.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfPageModel page = _model.Pages[pageIndex];
        if (page.Contents is not PdfReferenceObject contentsReference)
        {
            throw new NotSupportedException("Only page /Contents references are supported for replacement.");
        }

        List<PdfIndirectObject> objects = [.. _file.Objects];
        int nextObjectNumber = GetNextObjectNumber(objects);
        PdfObjectId fontId = new(nextObjectNumber++, 0);
        PdfObjectId resourcesId = new(nextObjectNumber++, 0);

        PdfDictionaryObject fontDictionary = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Font")),
            new PdfDictionaryEntry("Subtype", new PdfNameObject("Type1")),
            new PdfDictionaryEntry("BaseFont", new PdfNameObject("Helvetica")),
        ]);
        PdfDictionaryObject resourcesDictionary = new(
        [
            new PdfDictionaryEntry(
                "Font",
                new PdfDictionaryObject(
                [
                    new PdfDictionaryEntry("F1", new PdfReferenceObject(fontId)),
                ])),
        ]);

        objects.Add(new PdfIndirectObject(fontId, fontDictionary));
        objects.Add(new PdfIndirectObject(resourcesId, resourcesDictionary));

        string content = BuildRichTextContentStream(spans, effectiveOptions);
        PdfStreamObject existingStream = RequireStreamObject(contentsReference.ObjectId, "Page contents");
        PdfStreamObject updatedStream = new(existingStream.Dictionary, System.Text.Encoding.ASCII.GetBytes(content));
        ReplaceObject(objects, contentsReference.ObjectId, updatedStream);

        PdfDictionaryObject pageDictionary = RequireDictionaryObject(page.ObjectId, "Page");
        PdfDictionaryObject updatedPage = ReplaceDictionaryEntries(
            pageDictionary,
            new PdfDictionaryEntry("Resources", new PdfReferenceObject(resourcesId)));
        ReplaceObject(objects, page.ObjectId, updatedPage);

        _file = new PdfFile(_file.Version, objects, _file.Trailer);
        _model = PdfDocumentModelBuilder.Build(_file);
        _model.Mutations.MarkDirty(page.ObjectId);
        _model.Mutations.MarkDirty(contentsReference.ObjectId);
        _model.Mutations.MarkDirty(fontId);
        _model.Mutations.MarkDirty(resourcesId);
    }

    public void SetInfoProducer(string producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);

        List<PdfIndirectObject> objects = [.. _file.Objects];
        PdfObjectId infoId;

        if (_model.InfoObjectId is PdfObjectId existingInfoId)
        {
            PdfDictionaryObject infoDictionary = RequireDictionaryObject(existingInfoId, "Info");
            PdfDictionaryObject updatedInfo = ReplaceDictionaryEntries(
                infoDictionary,
                new PdfDictionaryEntry("Producer", new PdfStringObject(producer)));
            ReplaceObject(objects, existingInfoId, updatedInfo);
            infoId = existingInfoId;
        }
        else
        {
            int nextObjectNumber = GetNextObjectNumber(objects);
            infoId = new PdfObjectId(nextObjectNumber, 0);
            PdfDictionaryObject infoDictionary = new(
            [
                new PdfDictionaryEntry("Producer", new PdfStringObject(producer)),
            ]);

            objects.Add(new PdfIndirectObject(infoId, infoDictionary));

            PdfDictionaryObject updatedTrailer = ReplaceDictionaryEntries(
                _file.Trailer,
                new PdfDictionaryEntry("Info", new PdfReferenceObject(infoId)));

            _file = new PdfFile(_file.Version, objects, updatedTrailer);
            _model = PdfDocumentModelBuilder.Build(_file);
            _model.Mutations.MarkDirty(infoId);
            return;
        }

        _file = new PdfFile(_file.Version, objects, _file.Trailer);
        _model = PdfDocumentModelBuilder.Build(_file);
        _model.Mutations.MarkDirty(infoId);
    }

    public string? GetInfoProducer()
    {
        if (_model.InfoObjectId is not PdfObjectId infoId)
        {
            return null;
        }

        PdfDictionaryObject infoDictionary = RequireDictionaryObject(infoId, "Info");
        if (!TryGetDictionaryEntry(infoDictionary, "Producer", out PdfObject? producerObject))
        {
            return null;
        }

        return producerObject is PdfStringObject producer ? producer.Value : null;
    }

    public int RedactText(string target, string replacement = "")
    {
        if (string.IsNullOrEmpty(target))
        {
            throw new ArgumentException("Redaction target cannot be null or empty.", nameof(target));
        }

        ArgumentNullException.ThrowIfNull(replacement);

        List<PdfIndirectObject> objects = [.. _file.Objects];
        HashSet<PdfObjectId> processedStreamIds = [];
        HashSet<PdfObjectId> changedStreamIds = [];
        int totalRedactions = 0;

        foreach (PdfPageModel page in _model.Pages)
        {
            foreach (PdfObjectId streamId in EnumerateContentStreamReferences(page.Contents))
            {
                if (!processedStreamIds.Add(streamId))
                {
                    continue;
                }

                PdfStreamObject stream = RequireStreamObject(streamId, "Page contents");
                string content = System.Text.Encoding.ASCII.GetString(stream.Data.Span);
                int replacements = CountOccurrences(content, target);
                if (replacements == 0)
                {
                    continue;
                }

                string redactedContent = content.Replace(target, replacement, StringComparison.Ordinal);
                PdfStreamObject updated = new(stream.Dictionary, System.Text.Encoding.ASCII.GetBytes(redactedContent));
                ReplaceObject(objects, streamId, updated);
                changedStreamIds.Add(streamId);

                totalRedactions += replacements;
            }
        }

        if (totalRedactions == 0)
        {
            return 0;
        }

        _file = new PdfFile(_file.Version, objects, _file.Trailer);
        _model = PdfDocumentModelBuilder.Build(_file);

        foreach (PdfObjectId streamId in changedStreamIds)
        {
            _model.Mutations.MarkDirty(streamId);
        }

        return totalRedactions;
    }

    private int AddPageCore(PdfPageOptions pageOptions, string? text, PdfTextOptions? textOptions)
    {
        ValidatePageOptions(pageOptions);

        List<PdfIndirectObject> objects = [.. _file.Objects];
        PdfDictionaryObject pagesRoot = RequireDictionaryObject(_model.PagesRootObjectId, "Pages root");
        PdfArrayObject existingKids = RequireArrayEntry(pagesRoot, "Kids");

        int nextObjectNumber = GetNextObjectNumber(objects);
        PdfObjectId pageId = new(nextObjectNumber++, 0);
        PdfObjectId contentsId = new(nextObjectNumber++, 0);
        PdfObjectId? resourcesId = null;
        List<PdfObjectId> dirtyObjectIds = [pageId, contentsId];

        PdfObject? resourcesEntryValue = null;
        ReadOnlyMemory<byte> contentBytes = ReadOnlyMemory<byte>.Empty;

        if (text is not null)
        {
            PdfTextOptions effectiveTextOptions = textOptions ?? _defaultTextOptions;
            ValidateTextOptions(effectiveTextOptions);

            if (effectiveTextOptions.TrueTypeFontPath is null)
            {
                PdfObjectId fontId = new(nextObjectNumber++, 0);
                resourcesId = new PdfObjectId(nextObjectNumber++, 0);

                PdfDictionaryObject fontDictionary = new(
                [
                    new PdfDictionaryEntry("Type", new PdfNameObject("Font")),
                    new PdfDictionaryEntry("Subtype", new PdfNameObject("Type1")),
                    new PdfDictionaryEntry("BaseFont", new PdfNameObject("Helvetica")),
                ]);

                PdfDictionaryObject resourcesDictionary = new(
                [
                    new PdfDictionaryEntry(
                        "Font",
                        new PdfDictionaryObject(
                        [
                            new PdfDictionaryEntry("F1", new PdfReferenceObject(fontId)),
                        ])),
                ]);

                objects.Add(new PdfIndirectObject(fontId, fontDictionary));
                objects.Add(new PdfIndirectObject(resourcesId.Value, resourcesDictionary));
                resourcesEntryValue = new PdfReferenceObject(resourcesId.Value);
                contentBytes = System.Text.Encoding.ASCII.GetBytes(BuildTextContentStream(text, effectiveTextOptions));

                dirtyObjectIds.Add(fontId);
                dirtyObjectIds.Add(resourcesId.Value);
            }
            else
            {
                PdfEmbeddedFontPlan embeddedPlan = BuildEmbeddedFontPlan(text, effectiveTextOptions, nextObjectNumber);
                objects.AddRange(embeddedPlan.ObjectsToAdd);
                resourcesId = embeddedPlan.ResourcesObjectId;
                resourcesEntryValue = new PdfReferenceObject(resourcesId.Value);
                contentBytes = embeddedPlan.ContentStreamBytes;
                dirtyObjectIds.AddRange(embeddedPlan.DirtyObjectIds);
            }
        }

        PdfDictionaryObject pageDictionary = CreatePageDictionary(
            parentId: _model.PagesRootObjectId,
            contentsId,
            pageOptions,
            resourcesEntryValue);

        PdfStreamObject contentsObject = new(new PdfDictionaryObject([]), contentBytes);
        objects.Add(new PdfIndirectObject(pageId, pageDictionary));
        objects.Add(new PdfIndirectObject(contentsId, contentsObject));

        PdfArrayObject updatedKids = new([.. existingKids.Items, new PdfReferenceObject(pageId)]);
        PdfDictionaryObject updatedPagesRoot = ReplaceDictionaryEntries(
            pagesRoot,
            new PdfDictionaryEntry("Kids", updatedKids),
            new PdfDictionaryEntry("Count", new PdfNumberObject(_model.Pages.Count + 1, isInteger: true)));

        ReplaceObject(objects, _model.PagesRootObjectId, updatedPagesRoot);
        _file = new PdfFile(_file.Version, objects, _file.Trailer);
        _model = PdfDocumentModelBuilder.Build(_file);

        _model.Mutations.MarkDirty(_model.PagesRootObjectId);
        foreach (PdfObjectId objectId in dirtyObjectIds)
        {
            _model.Mutations.MarkDirty(objectId);
        }

        return _model.Pages.Count - 1;
    }

    private PdfTextOptions ResolveTextOptions(PdfTextOptions? options)
    {
        PdfTextOptions effectiveOptions = options ?? _defaultTextOptions;
        ValidateTextOptions(effectiveOptions);
        return effectiveOptions;
    }

    private static string ResolveEmbeddedFontPath(string text, PdfTextOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TrueTypeFontPath))
        {
            throw new ArgumentException("Embedded font path is required.", nameof(options));
        }

        List<string> candidates = [options.TrueTypeFontPath];
        if (options.FallbackTrueTypeFontPaths is not null)
        {
            candidates.AddRange(options.FallbackTrueTypeFontPaths.Where(static path => !string.IsNullOrWhiteSpace(path)));
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (PdfTrueTypeFontEmbedder.CanRenderText(candidate, text, options.Direction))
                {
                    return candidate;
                }
            }
            catch (PdfFormatException) when (candidate != options.TrueTypeFontPath)
            {
                // Ignore malformed fallback fonts and continue probing.
            }
            catch (NotSupportedException) when (candidate != options.TrueTypeFontPath)
            {
                // Ignore malformed fallback fonts and continue probing.
            }
            catch (IOException) when (candidate != options.TrueTypeFontPath)
            {
                // Ignore malformed fallback fonts and continue probing.
            }
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return options.TrueTypeFontPath;
    }

    private static void ValidateTextSpans(IReadOnlyList<PdfTextSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);

        for (int index = 0; index < spans.Count; index++)
        {
            PdfTextSpan span = spans[index] ?? throw new ArgumentException($"Text span at index {index} cannot be null.", nameof(spans));
            ArgumentNullException.ThrowIfNull(span.Text);

            if (span.FontSize is double fontSize && (!double.IsFinite(fontSize) || fontSize <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(spans), $"Text span at index {index} has an invalid FontSize.");
            }

            if (span.TrueTypeFontPath is not null && string.IsNullOrWhiteSpace(span.TrueTypeFontPath))
            {
                throw new ArgumentException($"Text span at index {index} has a blank TrueTypeFontPath.", nameof(spans));
            }
        }
    }

    private static PdfFile CreateEmptyFile()
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
        ]);

        return new PdfFile(
            version: "2.0",
            objects:
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(2, 0), pages),
            ],
            trailer);
    }

    private PdfDictionaryObject RequireDictionaryObject(PdfObjectId id, string context)
    {
        foreach (PdfIndirectObject indirectObject in _file.Objects)
        {
            if (indirectObject.ObjectId == id)
            {
                return indirectObject.Value as PdfDictionaryObject
                    ?? throw new PdfFormatException($"{context} object {id} is not a dictionary.");
            }
        }

        throw new PdfFormatException($"{context} object {id} was not found.");
    }

    private PdfStreamObject RequireStreamObject(PdfObjectId id, string context)
    {
        foreach (PdfIndirectObject indirectObject in _file.Objects)
        {
            if (indirectObject.ObjectId == id)
            {
                return indirectObject.Value as PdfStreamObject
                    ?? throw new PdfFormatException($"{context} object {id} is not a stream.");
            }
        }

        throw new PdfFormatException($"{context} object {id} was not found.");
    }

    private static PdfArrayObject RequireArrayEntry(PdfDictionaryObject dictionary, string key)
    {
        PdfObject value = RequireDictionaryEntry(dictionary, key);
        return value as PdfArrayObject
            ?? throw new PdfFormatException($"Dictionary entry '/{key}' must be an array.");
    }

    private static PdfObject RequireDictionaryEntry(PdfDictionaryObject dictionary, string key)
    {
        foreach (PdfDictionaryEntry entry in dictionary.Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        throw new PdfFormatException($"Required dictionary entry '/{key}' was not found.");
    }

    private static bool TryGetDictionaryEntry(PdfDictionaryObject dictionary, string key, out PdfObject? value)
    {
        foreach (PdfDictionaryEntry entry in dictionary.Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static IEnumerable<PdfObjectId> EnumerateContentStreamReferences(PdfObject? contents)
    {
        if (contents is null)
        {
            yield break;
        }

        if (contents is PdfReferenceObject reference)
        {
            yield return reference.ObjectId;
            yield break;
        }

        if (contents is PdfArrayObject array)
        {
            foreach (PdfObject item in array.Items)
            {
                if (item is not PdfReferenceObject itemReference)
                {
                    throw new NotSupportedException("Page /Contents arrays must contain references.");
                }

                yield return itemReference.ObjectId;
            }

            yield break;
        }

        throw new NotSupportedException("Page /Contents must be a reference or reference array.");
    }

    private static int CountOccurrences(string input, string target)
    {
        int count = 0;
        int index = 0;

        while (index < input.Length)
        {
            int found = input.IndexOf(target, index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            count++;
            index = found + target.Length;
        }

        return count;
    }

    private static int GetNextObjectNumber(IReadOnlyList<PdfIndirectObject> objects)
    {
        int maxNumber = 0;

        foreach (PdfIndirectObject item in objects)
        {
            if (item.ObjectId.ObjectNumber > maxNumber)
            {
                maxNumber = item.ObjectId.ObjectNumber;
            }
        }

        return maxNumber + 1;
    }

    private static PdfDictionaryObject CreatePageDictionary(
        PdfObjectId parentId,
        PdfObjectId contentsId,
        PdfPageOptions pageOptions,
        PdfObject? resourcesEntryValue)
    {
        List<PdfDictionaryEntry> entries =
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Page")),
            new PdfDictionaryEntry("Parent", new PdfReferenceObject(parentId)),
            new PdfDictionaryEntry(
                "MediaBox",
                new PdfArrayObject(
                [
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(0, isInteger: true),
                    new PdfNumberObject(pageOptions.Width, isInteger: false),
                    new PdfNumberObject(pageOptions.Height, isInteger: false),
                ])),
            new PdfDictionaryEntry("Contents", new PdfReferenceObject(contentsId)),
        ];

        if (resourcesEntryValue is not null)
        {
            entries.Add(new PdfDictionaryEntry("Resources", resourcesEntryValue));
        }

        return new PdfDictionaryObject(entries);
    }

    private static PdfDictionaryObject ReplaceDictionaryEntries(PdfDictionaryObject dictionary, params PdfDictionaryEntry[] replacements)
    {
        List<PdfDictionaryEntry> updated = [];
        HashSet<string> replacementKeys = replacements.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        foreach (PdfDictionaryEntry entry in dictionary.Entries)
        {
            if (!replacementKeys.Contains(entry.Key))
            {
                updated.Add(entry);
            }
        }

        updated.AddRange(replacements);
        return new PdfDictionaryObject(updated);
    }

    private static void ReplaceObject(List<PdfIndirectObject> objects, PdfObjectId objectId, PdfObject value)
    {
        for (int index = 0; index < objects.Count; index++)
        {
            if (objects[index].ObjectId == objectId)
            {
                objects[index] = new PdfIndirectObject(objectId, value);
                return;
            }
        }

        throw new PdfFormatException($"Object {objectId} was not found for replacement.");
    }

    private static PdfEmbeddedFontPlan BuildEmbeddedFontPlan(string text, PdfTextOptions options, int nextObjectNumber)
    {
        if (string.IsNullOrWhiteSpace(options.TrueTypeFontPath))
        {
            throw new ArgumentException("Embedded font path is required.", nameof(options));
        }

        string selectedFontPath = ResolveEmbeddedFontPath(text, options);
        PdfEmbeddedTrueTypeFont embedded = PdfTrueTypeFontEmbedder.Build(
            selectedFontPath,
            text,
            options.SubsetFont,
            options.Direction);

        PdfObjectId fontFileId = new(nextObjectNumber++, 0);
        PdfObjectId descriptorId = new(nextObjectNumber++, 0);
        PdfObjectId cidToGidId = new(nextObjectNumber++, 0);
        PdfObjectId toUnicodeId = new(nextObjectNumber++, 0);
        PdfObjectId descendantFontId = new(nextObjectNumber++, 0);
        PdfObjectId type0FontId = new(nextObjectNumber++, 0);
        PdfObjectId resourcesId = new(nextObjectNumber++, 0);

        PdfStreamObject fontFileStream = new(
            new PdfDictionaryObject(
            [
                new PdfDictionaryEntry("Length1", new PdfNumberObject(embedded.FontProgram.Length, isInteger: true)),
            ]),
            embedded.FontProgram);

        PdfDictionaryObject descriptor = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("FontDescriptor")),
            new PdfDictionaryEntry("FontName", new PdfNameObject(embedded.BaseFontName)),
            new PdfDictionaryEntry(
                "FontBBox",
                new PdfArrayObject(
                [
                    new PdfNumberObject(embedded.XMin, isInteger: true),
                    new PdfNumberObject(embedded.YMin, isInteger: true),
                    new PdfNumberObject(embedded.XMax, isInteger: true),
                    new PdfNumberObject(embedded.YMax, isInteger: true),
                ])),
            new PdfDictionaryEntry("Ascent", new PdfNumberObject(embedded.Ascent, isInteger: true)),
            new PdfDictionaryEntry("Descent", new PdfNumberObject(embedded.Descent, isInteger: true)),
            new PdfDictionaryEntry("CapHeight", new PdfNumberObject(embedded.Ascent, isInteger: true)),
            new PdfDictionaryEntry("StemV", new PdfNumberObject(80, isInteger: true)),
            new PdfDictionaryEntry("Flags", new PdfNumberObject(32, isInteger: true)),
            new PdfDictionaryEntry("ItalicAngle", new PdfNumberObject(0, isInteger: true)),
            new PdfDictionaryEntry("FontFile2", new PdfReferenceObject(fontFileId)),
        ]);

        byte[] cidToGidBytes = BuildCidToGidMapBytes(embedded.CidToGlyphId);
        PdfStreamObject cidToGidMap = new(new PdfDictionaryObject([]), cidToGidBytes);

        byte[] toUnicodeBytes = BuildToUnicodeCMapBytes(embedded.CidToUnicode);
        PdfStreamObject toUnicode = new(new PdfDictionaryObject([]), toUnicodeBytes);

        PdfDictionaryObject descendantFont = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Font")),
            new PdfDictionaryEntry("Subtype", new PdfNameObject("CIDFontType2")),
            new PdfDictionaryEntry("BaseFont", new PdfNameObject(embedded.BaseFontName)),
            new PdfDictionaryEntry(
                "CIDSystemInfo",
                new PdfDictionaryObject(
                [
                    new PdfDictionaryEntry("Registry", new PdfStringObject("Adobe")),
                    new PdfDictionaryEntry("Ordering", new PdfStringObject("Identity")),
                    new PdfDictionaryEntry("Supplement", new PdfNumberObject(0, isInteger: true)),
                ])),
            new PdfDictionaryEntry("FontDescriptor", new PdfReferenceObject(descriptorId)),
            new PdfDictionaryEntry("CIDToGIDMap", new PdfReferenceObject(cidToGidId)),
            new PdfDictionaryEntry("DW", new PdfNumberObject(1000, isInteger: true)),
            new PdfDictionaryEntry("W", BuildWidthArray(embedded.CidToWidth)),
        ]);

        PdfDictionaryObject type0Font = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Font")),
            new PdfDictionaryEntry("Subtype", new PdfNameObject("Type0")),
            new PdfDictionaryEntry("BaseFont", new PdfNameObject(embedded.BaseFontName)),
            new PdfDictionaryEntry("Encoding", new PdfNameObject("Identity-H")),
            new PdfDictionaryEntry(
                "DescendantFonts",
                new PdfArrayObject(
                [
                    new PdfReferenceObject(descendantFontId),
                ])),
            new PdfDictionaryEntry("ToUnicode", new PdfReferenceObject(toUnicodeId)),
        ]);

        PdfDictionaryObject resources = new(
        [
            new PdfDictionaryEntry(
                "Font",
                new PdfDictionaryObject(
                [
                    new PdfDictionaryEntry("F1", new PdfReferenceObject(type0FontId)),
                ])),
        ]);

        ReadOnlyMemory<byte> contentBytes = BuildEmbeddedTextContentStream(
            embedded.GlyphRun,
            embedded.CidToUnicode,
            embedded.UnitsPerEm,
            options);

        List<PdfIndirectObject> objects =
        [
            new PdfIndirectObject(fontFileId, fontFileStream),
            new PdfIndirectObject(descriptorId, descriptor),
            new PdfIndirectObject(cidToGidId, cidToGidMap),
            new PdfIndirectObject(toUnicodeId, toUnicode),
            new PdfIndirectObject(descendantFontId, descendantFont),
            new PdfIndirectObject(type0FontId, type0Font),
            new PdfIndirectObject(resourcesId, resources),
        ];

        List<PdfObjectId> dirtyIds =
        [
            fontFileId,
            descriptorId,
            cidToGidId,
            toUnicodeId,
            descendantFontId,
            type0FontId,
            resourcesId,
        ];

        return new PdfEmbeddedFontPlan(
            resourcesId,
            contentBytes,
            objects,
            dirtyIds);
    }

    private static PdfArrayObject BuildWidthArray(IReadOnlyDictionary<int, int> unicodeToWidth)
    {
        List<int> codes = [.. unicodeToWidth.Keys.OrderBy(static value => value)];
        List<PdfObject> entries = [];
        int index = 0;
        while (index < codes.Count)
        {
            int startCode = codes[index];
            List<PdfObject> runWidths = [];
            int current = startCode;
            while (index < codes.Count && codes[index] == current)
            {
                int width = unicodeToWidth[current];
                runWidths.Add(new PdfNumberObject(width, isInteger: true));
                current++;
                index++;
            }

            entries.Add(new PdfNumberObject(startCode, isInteger: true));
            entries.Add(new PdfArrayObject(runWidths));
        }

        return new PdfArrayObject(entries);
    }

    private static byte[] BuildCidToGidMapBytes(IReadOnlyDictionary<int, ushort> unicodeToGlyphId)
    {
        int maxCode = unicodeToGlyphId.Count == 0 ? 0 : unicodeToGlyphId.Keys.Max();
        byte[] bytes = new byte[(maxCode + 1) * 2];

        foreach ((int unicode, ushort glyphId) in unicodeToGlyphId)
        {
            int offset = unicode * 2;
            bytes[offset] = (byte)(glyphId >> 8);
            bytes[offset + 1] = (byte)glyphId;
        }

        return bytes;
    }

    private static byte[] BuildToUnicodeCMapBytes(IReadOnlyDictionary<int, string> cidToUnicode)
    {
        List<int> cids = [.. cidToUnicode.Keys.OrderBy(static value => value)];
        System.Text.StringBuilder builder = new();

        builder.AppendLine("/CIDInit /ProcSet findresource begin");
        builder.AppendLine("12 dict begin");
        builder.AppendLine("begincmap");
        builder.AppendLine("/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def");
        builder.AppendLine("/CMapName /Adobe-Identity-UCS def");
        builder.AppendLine("/CMapType 2 def");
        builder.AppendLine("1 begincodespacerange");
        builder.AppendLine("<0000> <FFFF>");
        builder.AppendLine("endcodespacerange");

        int cursor = 0;
        while (cursor < cids.Count)
        {
            int batchSize = Math.Min(100, cids.Count - cursor);
            builder.Append(batchSize.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(" beginbfchar");
            for (int index = 0; index < batchSize; index++)
            {
                int cid = cids[cursor + index];
                string unicode = cidToUnicode[cid];
                string destination = Convert.ToHexString(System.Text.Encoding.BigEndianUnicode.GetBytes(unicode));
                builder.Append('<');
                builder.Append(cid.ToString("X4", CultureInfo.InvariantCulture));
                builder.Append("> <");
                builder.Append(destination);
                builder.AppendLine(">");
            }

            builder.AppendLine("endbfchar");
            cursor += batchSize;
        }

        builder.AppendLine("endcmap");
        builder.AppendLine("CMapName currentdict /CMap defineresource pop");
        builder.AppendLine("end");
        builder.AppendLine("end");

        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static ReadOnlyMemory<byte> BuildEmbeddedTextContentStream(
        IReadOnlyList<PdfShapedGlyph> glyphRun,
        IReadOnlyDictionary<int, string> cidToUnicode,
        ushort unitsPerEm,
        PdfTextOptions options)
    {
        if (options.WritingMode == PdfWritingMode.Vertical)
        {
            return BuildEmbeddedVerticalTextContentStream(glyphRun, unitsPerEm, options);
        }

        string fontSize = options.FontSize.ToString("0.###", CultureInfo.InvariantCulture);
        double lineHeight = options.FontSize * options.LineHeightMultiplier;
        List<List<PdfShapedGlyph>> lines = BuildEmbeddedLines(glyphRun, cidToUnicode, unitsPerEm, options);
        System.Text.StringBuilder builder = new();
        builder.Append("BT /F1 ");
        builder.Append(fontSize);
        builder.Append(" Tf ");

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            List<PdfShapedGlyph> line = lines[lineIndex];
            if (line.Count == 0)
            {
                continue;
            }

            EmbeddedLineMetrics metrics = MeasureEmbeddedLine(line, unitsPerEm, options.FontSize);
            double containerWidth = options.MaxWidth ?? metrics.Width;
            double alignmentOffset = GetAlignmentOffset(
                options.Alignment == PdfTextAlignment.Justify ? PdfTextAlignment.Left : options.Alignment,
                containerWidth,
                metrics.Width);
            double penX = options.X + alignmentOffset - metrics.MinX;
            double penY = options.Y - (lineIndex * lineHeight);
            List<EmbeddedGlyphPlacement> placements = new(line.Count);
            int sequence = 0;
            int whitespaceCount = options.Alignment == PdfTextAlignment.Justify && lineIndex < lines.Count - 1
                ? line.Count(glyph => IsWhitespaceGlyph(glyph, cidToUnicode))
                : 0;
            double extraWordSpacing = whitespaceCount > 0
                ? Math.Max(0, containerWidth - metrics.Width) / whitespaceCount
                : 0;

            foreach (PdfShapedGlyph glyph in line)
            {
                double xOffset = ScaleGlyphUnitToUserSpace(glyph.XOffset, unitsPerEm, options.FontSize);
                double yOffset = ScaleGlyphUnitToUserSpace(glyph.YOffset, unitsPerEm, options.FontSize);
                double glyphX = penX + xOffset;
                double glyphY = penY + yOffset;

                placements.Add(new EmbeddedGlyphPlacement(glyph, glyphX, glyphY, sequence++));

                penX += ScaleGlyphUnitToUserSpace(glyph.XAdvance, unitsPerEm, options.FontSize);
                if (extraWordSpacing > 0 && IsWhitespaceGlyph(glyph, cidToUnicode))
                {
                    penX += extraWordSpacing;
                }

                penY += ScaleGlyphUnitToUserSpace(glyph.YAdvance, unitsPerEm, options.FontSize);
            }

            foreach (EmbeddedGlyphPlacement placement in placements
                .OrderBy(static value => value.Glyph.Cluster)
                .ThenBy(static value => value.Sequence))
            {
                builder.Append("1 0 0 1 ");
                builder.Append(placement.X.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(' ');
                builder.Append(placement.Y.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tm <");
                builder.Append(placement.Glyph.Cid.ToString("X4", CultureInfo.InvariantCulture));
                builder.Append("> Tj ");
            }
        }

        builder.Append("ET");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static ReadOnlyMemory<byte> BuildEmbeddedVerticalTextContentStream(
        IReadOnlyList<PdfShapedGlyph> glyphRun,
        ushort unitsPerEm,
        PdfTextOptions options)
    {
        string fontSize = options.FontSize.ToString("0.###", CultureInfo.InvariantCulture);
        double step = options.FontSize * options.LineHeightMultiplier;
        double maxColumnHeight = options.MaxWidth ?? double.PositiveInfinity;
        double x = options.X;
        double y = options.Y;
        double columnHeight = 0;

        System.Text.StringBuilder builder = new();
        builder.Append("BT /F1 ");
        builder.Append(fontSize);
        builder.Append(" Tf ");

        foreach (PdfShapedGlyph glyph in glyphRun.OrderBy(static value => value.Cluster))
        {
            if (double.IsFinite(maxColumnHeight) && maxColumnHeight > 0 && columnHeight + step > maxColumnHeight && columnHeight > 0)
            {
                x += step;
                y = options.Y;
                columnHeight = 0;
            }

            builder.Append("1 0 0 1 ");
            builder.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(y.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(" Tm <");
            builder.Append(glyph.Cid.ToString("X4", CultureInfo.InvariantCulture));
            builder.Append("> Tj ");

            y -= Math.Abs(ScaleGlyphUnitToUserSpace(glyph.XAdvance, unitsPerEm, options.FontSize));
            columnHeight += step;
        }

        builder.Append("ET");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static double ScaleGlyphUnitToUserSpace(int value, ushort unitsPerEm, double fontSize)
    {
        return value * fontSize / unitsPerEm;
    }

    private static string BuildTextContentStream(string text, PdfTextOptions options)
    {
        if (options.WritingMode == PdfWritingMode.Vertical)
        {
            return BuildVerticalTextContentStream(text, options);
        }

        if (options.MaxWidth is null
            && options.Alignment is PdfTextAlignment.Left or PdfTextAlignment.Justify
            && options.LineHeightMultiplier == 1.2
            && text.IndexOfAny(['\r', '\n']) < 0)
        {
            string escapedSimple = EscapeLiteralString(text);
            string xSimple = options.X.ToString("0.###", CultureInfo.InvariantCulture);
            string ySimple = options.Y.ToString("0.###", CultureInfo.InvariantCulture);
            string fontSizeSimple = options.FontSize.ToString("0.###", CultureInfo.InvariantCulture);

            return $"BT /F1 {fontSizeSimple} Tf {xSimple} {ySimple} Td ({escapedSimple}) Tj ET";
        }

        IReadOnlyList<string> lines = BuildSimpleTextLines(text, options);
        double lineHeight = options.FontSize * options.LineHeightMultiplier;
        string fontSize = options.FontSize.ToString("0.###", CultureInfo.InvariantCulture);
        System.Text.StringBuilder builder = new();
        builder.Append("BT /F1 ");
        builder.Append(fontSize);
        builder.Append(" Tf ");

        for (int index = 0; index < lines.Count; index++)
        {
            string line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            double lineWidth = EstimateSimpleTextWidth(line, options.FontSize);
            double containerWidth = options.MaxWidth ?? lineWidth;
            bool justifyLine = options.Alignment == PdfTextAlignment.Justify && index < lines.Count - 1;
            double alignmentOffset = GetAlignmentOffset(
                justifyLine ? PdfTextAlignment.Left : options.Alignment,
                containerWidth,
                lineWidth);
            double x = options.X + alignmentOffset;
            double y = options.Y - (index * lineHeight);
            double extra = Math.Max(0, containerWidth - lineWidth);
            int spaceCount = justifyLine ? line.Count(static value => value == ' ') : 0;
            double wordSpacing = spaceCount > 0 ? extra / spaceCount : 0;

            if (wordSpacing > 0)
            {
                builder.Append(wordSpacing.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tw ");
            }

            builder.Append("1 0 0 1 ");
            builder.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(y.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(" Tm (");
            builder.Append(EscapeLiteralString(line));
            builder.Append(") Tj ");

            if (wordSpacing > 0)
            {
                builder.Append("0 Tw ");
            }
        }

        builder.Append("ET");
        return builder.ToString();
    }

    private static string BuildRichTextContentStream(IReadOnlyList<PdfTextSpan> spans, PdfTextOptions options)
    {
        if (options.WritingMode == PdfWritingMode.Vertical)
        {
            return BuildVerticalRichTextContentStream(spans, options);
        }

        List<RichTextAtom> atoms = BuildRichTextAtoms(spans, options);
        List<List<RichTextAtom>> lines = options.MaxWidth is null
            ? SplitRichAtomsByExplicitBreaks(atoms)
            : BuildWrappedRichTextLines(atoms, options);

        System.Text.StringBuilder builder = new();
        builder.Append("BT ");
        double y = options.Y;

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            List<RichTextAtom> line = lines[lineIndex];
            if (line.Count == 0)
            {
                y -= options.FontSize * options.LineHeightMultiplier;
                continue;
            }

            double lineHeight = line.Max(static atom => atom.FontSize) * options.LineHeightMultiplier;
            double lineWidth = MeasureRichLineWidth(line);
            double containerWidth = options.MaxWidth ?? lineWidth;
            bool justifyLine = options.Alignment == PdfTextAlignment.Justify && lineIndex < lines.Count - 1;
            double alignmentOffset = GetAlignmentOffset(
                justifyLine ? PdfTextAlignment.Left : options.Alignment,
                containerWidth,
                lineWidth);
            double x = options.X + alignmentOffset;

            double extra = Math.Max(0, containerWidth - lineWidth);
            int spaceCount = justifyLine ? CountRichSpaces(line) : 0;
            double wordSpacing = spaceCount > 0 ? extra / spaceCount : 0;
            if (wordSpacing > 0)
            {
                builder.Append(wordSpacing.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tw ");
            }

            int atomIndex = 0;
            while (atomIndex < line.Count)
            {
                double fontSize = line[atomIndex].FontSize;
                System.Text.StringBuilder segment = new();
                int segmentSpaceCount = 0;

                while (atomIndex < line.Count && Math.Abs(line[atomIndex].FontSize - fontSize) < 0.001)
                {
                    segment.Append(line[atomIndex].Text);
                    segmentSpaceCount += line[atomIndex].Text.Count(static value => value == ' ');
                    atomIndex++;
                }

                string segmentText = segment.ToString();
                builder.Append("/F1 ");
                builder.Append(fontSize.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tf 1 0 0 1 ");
                builder.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(' ');
                builder.Append(y.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tm (");
                builder.Append(EscapeLiteralString(segmentText));
                builder.Append(") Tj ");

                x += EstimateSimpleTextWidth(segmentText, fontSize);
                if (wordSpacing > 0)
                {
                    x += segmentSpaceCount * wordSpacing;
                }
            }

            if (wordSpacing > 0)
            {
                builder.Append("0 Tw ");
            }

            y -= lineHeight;
        }

        builder.Append("ET");
        return builder.ToString();
    }

    private static string BuildVerticalRichTextContentStream(IReadOnlyList<PdfTextSpan> spans, PdfTextOptions options)
    {
        List<RichTextAtom> atoms = BuildRichTextAtoms(spans, options);

        System.Text.StringBuilder builder = new();
        builder.Append("BT ");

        double x = options.X;
        double y = options.Y;
        double columnHeight = 0;
        double maxColumnHeight = options.MaxWidth ?? double.PositiveInfinity;

        foreach (RichTextAtom atom in atoms)
        {
            if (atom.Text == "\n")
            {
                x += atom.FontSize * options.LineHeightMultiplier;
                y = options.Y;
                columnHeight = 0;
                continue;
            }

            double step = atom.FontSize * options.LineHeightMultiplier;
            if (double.IsFinite(maxColumnHeight) && maxColumnHeight > 0 && columnHeight + step > maxColumnHeight && columnHeight > 0)
            {
                x += step;
                y = options.Y;
                columnHeight = 0;
            }

            builder.Append("/F1 ");
            builder.Append(atom.FontSize.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(" Tf 1 0 0 1 ");
            builder.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(y.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(" Tm (");
            builder.Append(EscapeLiteralString(atom.Text));
            builder.Append(") Tj ");

            y -= step;
            columnHeight += step;
        }

        builder.Append("ET");
        return builder.ToString();
    }

    private static List<RichTextAtom> BuildRichTextAtoms(IReadOnlyList<PdfTextSpan> spans, PdfTextOptions options)
    {
        List<RichTextAtom> atoms = [];
        foreach (PdfTextSpan span in spans)
        {
            double fontSize = span.FontSize ?? options.FontSize;
            string normalized = span.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            foreach (string element in EnumerateTextElements(normalized))
            {
                atoms.Add(new RichTextAtom(element, fontSize));
            }
        }

        return atoms;
    }

    private static List<List<RichTextAtom>> SplitRichAtomsByExplicitBreaks(IReadOnlyList<RichTextAtom> atoms)
    {
        List<List<RichTextAtom>> lines = [[]];
        foreach (RichTextAtom atom in atoms)
        {
            if (atom.Text == "\n")
            {
                lines.Add([]);
                continue;
            }

            lines[^1].Add(atom);
        }

        return lines;
    }

    private static List<List<RichTextAtom>> BuildWrappedRichTextLines(IReadOnlyList<RichTextAtom> atoms, PdfTextOptions options)
    {
        List<List<RichTextAtom>> lines = [[]];
        double maxWidth = options.MaxWidth ?? double.PositiveInfinity;
        int currentLineIndex = 0;
        int lastBreakIndex = -1;
        double currentWidth = 0;

        foreach (RichTextAtom atom in atoms)
        {
            if (atom.Text == "\n")
            {
                currentLineIndex++;
                lines.Add([]);
                lastBreakIndex = -1;
                currentWidth = 0;
                continue;
            }

            List<RichTextAtom> currentLine = lines[currentLineIndex];
            currentLine.Add(atom);
            currentWidth += EstimateSimpleTextWidth(atom.Text, atom.FontSize);

            if (IsSimpleBreakOpportunity(atom.Text))
            {
                lastBreakIndex = currentLine.Count - 1;
            }

            if (double.IsFinite(maxWidth) && maxWidth > 0 && currentWidth > maxWidth && currentLine.Count > 1)
            {
                List<RichTextAtom> overflow;
                if (lastBreakIndex >= 0)
                {
                    int breakAfter = lastBreakIndex + 1;
                    overflow = currentLine.GetRange(breakAfter, currentLine.Count - breakAfter);
                    currentLine.RemoveRange(breakAfter, currentLine.Count - breakAfter);
                }
                else if (options.EnableHyphenation && currentLine.Count >= 3)
                {
                    RichTextAtom pushed = currentLine[^1];
                    currentLine.RemoveAt(currentLine.Count - 1);
                    currentLine.Add(new RichTextAtom("-", pushed.FontSize));
                    overflow = [pushed];
                }
                else
                {
                    RichTextAtom pushed = currentLine[^1];
                    currentLine.RemoveAt(currentLine.Count - 1);
                    overflow = [pushed];
                }

                currentLineIndex++;
                lines.Add(overflow);
                currentWidth = MeasureRichLineWidth(lines[currentLineIndex]);
                lastBreakIndex = FindLastRichBreak(lines[currentLineIndex]);
            }
        }

        return lines;
    }

    private static int FindLastRichBreak(List<RichTextAtom> atoms)
    {
        for (int index = atoms.Count - 1; index >= 0; index--)
        {
            if (IsSimpleBreakOpportunity(atoms[index].Text))
            {
                return index;
            }
        }

        return -1;
    }

    private static double MeasureRichLineWidth(IEnumerable<RichTextAtom> line)
    {
        return line.Sum(static atom => EstimateSimpleTextWidth(atom.Text, atom.FontSize));
    }

    private static int CountRichSpaces(IEnumerable<RichTextAtom> line)
    {
        return line.Sum(static atom => atom.Text.Count(static value => value == ' '));
    }

    private static List<List<PdfShapedGlyph>> BuildEmbeddedLines(
        IReadOnlyList<PdfShapedGlyph> glyphRun,
        IReadOnlyDictionary<int, string> cidToUnicode,
        ushort unitsPerEm,
        PdfTextOptions options)
    {
        List<List<PdfShapedGlyph>> lines = [[]];
        int currentLineIndex = 0;
        int lastBreakGlyphIndex = -1;
        double currentAdvance = 0;
        double widthAtBreak = 0;

        double maxWidth = options.MaxWidth ?? double.PositiveInfinity;
        foreach (PdfShapedGlyph glyph in glyphRun)
        {
            if (IsLineBreakGlyph(glyph, cidToUnicode))
            {
                currentLineIndex++;
                lines.Add([]);
                currentAdvance = 0;
                lastBreakGlyphIndex = -1;
                widthAtBreak = 0;
                continue;
            }

            List<PdfShapedGlyph> currentLine = lines[currentLineIndex];
            currentLine.Add(glyph);
            currentAdvance += Math.Abs(ScaleGlyphUnitToUserSpace(glyph.XAdvance, unitsPerEm, options.FontSize));

            if (IsWhitespaceGlyph(glyph, cidToUnicode))
            {
                lastBreakGlyphIndex = currentLine.Count - 1;
                widthAtBreak = currentAdvance;
            }

            if (double.IsFinite(maxWidth) && maxWidth > 0 && currentAdvance > maxWidth && currentLine.Count > 1)
            {
                List<PdfShapedGlyph> overflow;
                if (lastBreakGlyphIndex >= 0)
                {
                    int breakAfter = lastBreakGlyphIndex + 1;
                    overflow = currentLine.GetRange(breakAfter, currentLine.Count - breakAfter);
                    currentLine.RemoveRange(breakAfter, currentLine.Count - breakAfter);
                    currentAdvance = widthAtBreak;
                }
                else
                {
                    PdfShapedGlyph pushed = currentLine[^1];
                    currentLine.RemoveAt(currentLine.Count - 1);
                    overflow = [pushed];
                    currentAdvance = ComputeEmbeddedLineAdvance(currentLine, unitsPerEm, options.FontSize);
                }

                currentLineIndex++;
                lines.Add(overflow);
                currentAdvance = ComputeEmbeddedLineAdvance(lines[currentLineIndex], unitsPerEm, options.FontSize);
                lastBreakGlyphIndex = FindLastWhitespaceGlyph(lines[currentLineIndex], cidToUnicode);
                widthAtBreak = lastBreakGlyphIndex >= 0
                    ? ComputeEmbeddedLineAdvance(
                        lines[currentLineIndex].Take(lastBreakGlyphIndex + 1),
                        unitsPerEm,
                        options.FontSize)
                    : 0;
            }
        }

        return lines;
    }

    private static bool IsWhitespaceGlyph(PdfShapedGlyph glyph, IReadOnlyDictionary<int, string> cidToUnicode)
    {
        if (!cidToUnicode.TryGetValue(glyph.Cid, out string? value))
        {
            return false;
        }

        return value.Length > 0 && value.All(char.IsWhiteSpace);
    }

    private static bool IsLineBreakGlyph(PdfShapedGlyph glyph, IReadOnlyDictionary<int, string> cidToUnicode)
    {
        if (!cidToUnicode.TryGetValue(glyph.Cid, out string? value))
        {
            return false;
        }

        return value.IndexOfAny(['\r', '\n']) >= 0;
    }

    private static int FindLastWhitespaceGlyph(List<PdfShapedGlyph> line, IReadOnlyDictionary<int, string> cidToUnicode)
    {
        for (int index = line.Count - 1; index >= 0; index--)
        {
            if (IsWhitespaceGlyph(line[index], cidToUnicode))
            {
                return index;
            }
        }

        return -1;
    }

    private static double ComputeEmbeddedLineAdvance(IEnumerable<PdfShapedGlyph> glyphs, ushort unitsPerEm, double fontSize)
    {
        return glyphs.Sum(glyph => Math.Abs(ScaleGlyphUnitToUserSpace(glyph.XAdvance, unitsPerEm, fontSize)));
    }

    private static EmbeddedLineMetrics MeasureEmbeddedLine(
        List<PdfShapedGlyph> glyphs,
        ushort unitsPerEm,
        double fontSize)
    {
        double penX = 0;
        double minX = 0;
        double maxX = 0;

        foreach (PdfShapedGlyph glyph in glyphs)
        {
            double xOffset = ScaleGlyphUnitToUserSpace(glyph.XOffset, unitsPerEm, fontSize);
            double x = penX + xOffset;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);

            penX += ScaleGlyphUnitToUserSpace(glyph.XAdvance, unitsPerEm, fontSize);
            minX = Math.Min(minX, penX);
            maxX = Math.Max(maxX, penX);
        }

        return new EmbeddedLineMetrics(minX, maxX - minX);
    }

    private static IReadOnlyList<string> BuildSimpleTextLines(string text, PdfTextOptions options)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (options.MaxWidth is null)
        {
            return normalized.Split('\n');
        }

        List<string> lines = [];
        foreach (string paragraph in normalized.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }
            AppendWrappedSimpleParagraph(paragraph, options, lines);
        }

        return lines;
    }

    private static double EstimateSimpleTextWidth(string text, double fontSize)
    {
        double widthUnits = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            widthUnits += GetApproximateHelveticaWidth(rune);
        }

        return widthUnits * fontSize / 1000.0;
    }

    private static double GetAlignmentOffset(PdfTextAlignment alignment, double containerWidth, double lineWidth)
    {
        return alignment switch
        {
            PdfTextAlignment.Left => 0,
            PdfTextAlignment.Center => (containerWidth - lineWidth) / 2,
            PdfTextAlignment.Right => containerWidth - lineWidth,
            PdfTextAlignment.Justify => 0,
            _ => 0,
        };
    }

    private static string BuildVerticalTextContentStream(string text, PdfTextOptions options)
    {
        System.Text.StringBuilder builder = new();
        builder.Append("BT /F1 ");
        builder.Append(options.FontSize.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(" Tf ");

        IReadOnlyList<string> elements = EnumerateTextElements(text);
        double step = options.FontSize * options.LineHeightMultiplier;
        double maxColumnHeight = options.MaxWidth ?? double.PositiveInfinity;
        double x = options.X;
        double y = options.Y;
        double columnHeight = 0;

        foreach (string element in elements)
        {
            if (element is "\r" or "\n")
            {
                x += step;
                y = options.Y;
                columnHeight = 0;
                continue;
            }

            if (double.IsFinite(maxColumnHeight) && maxColumnHeight > 0 && columnHeight + step > maxColumnHeight && columnHeight > 0)
            {
                x += step;
                y = options.Y;
                columnHeight = 0;
            }

            builder.Append("1 0 0 1 ");
            builder.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(y.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(" Tm (");
            builder.Append(EscapeLiteralString(element));
            builder.Append(") Tj ");

            y -= step;
            columnHeight += step;
        }

        builder.Append("ET");
        return builder.ToString();
    }

    private static void AppendWrappedSimpleParagraph(string paragraph, PdfTextOptions options, List<string> lines)
    {
        List<string> elements = EnumerateTextElements(paragraph);
        int lineStart = 0;
        int lastBreak = -1;
        double lineWidth = 0;
        int index = 0;

        while (index < elements.Count)
        {
            string element = elements[index];
            lineWidth += EstimateSimpleTextWidth(element, options.FontSize);
            if (IsSimpleBreakOpportunity(element))
            {
                lastBreak = index;
            }

            if (lineWidth > options.MaxWidth!.Value && index > lineStart)
            {
                if (lastBreak >= lineStart)
                {
                    int breakAfter = lastBreak + 1;
                    lines.Add(string.Concat(elements.Skip(lineStart).Take(breakAfter - lineStart)));
                    lineStart = breakAfter;
                    index = lineStart;
                    lineWidth = 0;
                    lastBreak = -1;
                    continue;
                }

                if (options.EnableHyphenation && index - lineStart >= 3)
                {
                    string hyphenated = string.Concat(elements.Skip(lineStart).Take(index - lineStart)) + "-";
                    lines.Add(hyphenated);
                    lineStart = index;
                    lineWidth = EstimateSimpleTextWidth(elements[index], options.FontSize);
                    lastBreak = -1;
                    index++;
                    continue;
                }

                lines.Add(string.Concat(elements.Skip(lineStart).Take(index - lineStart)));
                lineStart = index;
                lineWidth = EstimateSimpleTextWidth(elements[index], options.FontSize);
                lastBreak = -1;
                index++;
                continue;
            }

            index++;
        }

        if (lineStart < elements.Count)
        {
            lines.Add(string.Concat(elements.Skip(lineStart)));
        }
    }

    private static bool IsSimpleBreakOpportunity(string element)
    {
        if (element.Length == 0)
        {
            return false;
        }

        return char.IsWhiteSpace(element, 0) || element is "-" or "‐" or "‑" or "‒" or "–" or "—";
    }

    private static List<string> EnumerateTextElements(string text)
    {
        List<string> elements = [];
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            if (enumerator.GetTextElement() is string element)
            {
                elements.Add(element);
            }
        }

        return elements;
    }

    private static int GetApproximateHelveticaWidth(Rune rune)
    {
        if (rune.Value <= 0x20)
        {
            return rune.Value == 0x20 ? 278 : 0;
        }

        if (rune.IsAscii)
        {
            char character = (char)rune.Value;
            return character switch
            {
                >= 'A' and <= 'Z' => 667,
                >= 'a' and <= 'z' => 556,
                >= '0' and <= '9' => 556,
                '.' or ',' or ':' or ';' => 278,
                '!' or '?' => 333,
                '-' => 333,
                '"' or '\'' => 222,
                '(' or ')' or '[' or ']' or '{' or '}' => 333,
                '/' or '\\' => 278,
                '+' or '=' or '<' or '>' => 584,
                '@' => 1015,
                _ => 500,
            };
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category switch
        {
            UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark => 0,
            UnicodeCategory.SpaceSeparator => 500,
            _ => 1000,
        };
    }

    private static string EscapeLiteralString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static void ValidatePageOptions(PdfPageOptions options)
    {
        if (!double.IsFinite(options.Width) || options.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Page width must be a positive finite number.");
        }

        if (!double.IsFinite(options.Height) || options.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Page height must be a positive finite number.");
        }
    }

    private static void ValidateTextOptions(PdfTextOptions options)
    {
        if (!double.IsFinite(options.FontSize) || options.FontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Text font size must be a positive finite number.");
        }

        if (!double.IsFinite(options.X))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Text X position must be finite.");
        }

        if (!double.IsFinite(options.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Text Y position must be finite.");
        }

        if (options.TrueTypeFontPath is not null && string.IsNullOrWhiteSpace(options.TrueTypeFontPath))
        {
            throw new ArgumentException("TrueTypeFontPath cannot be blank when provided.", nameof(options));
        }

        if (options.MaxWidth is double maxWidth && (!double.IsFinite(maxWidth) || maxWidth <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Text MaxWidth must be a positive finite number when provided.");
        }

        if (!double.IsFinite(options.LineHeightMultiplier) || options.LineHeightMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Text LineHeightMultiplier must be a positive finite number.");
        }

        if (!Enum.IsDefined(options.Alignment))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Text Alignment contains an unsupported value.");
        }

        if (!Enum.IsDefined(options.Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Text Direction contains an unsupported value.");
        }

        if (!Enum.IsDefined(options.WritingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Text WritingMode contains an unsupported value.");
        }

        if (options.FallbackTrueTypeFontPaths is not null)
        {
            for (int index = 0; index < options.FallbackTrueTypeFontPaths.Count; index++)
            {
                string? fallback = options.FallbackTrueTypeFontPaths[index];
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    throw new ArgumentException($"FallbackTrueTypeFontPaths[{index}] cannot be blank.", nameof(options));
                }
            }
        }
    }

    private static void ValidateSecurityOptions(PdfSecurityOptions security)
    {
        if (string.IsNullOrWhiteSpace(security.UserPassword))
        {
            throw new ArgumentException("Security UserPassword is required when security options are provided.", nameof(security));
        }

        if (security.OwnerPassword is not null && security.OwnerPassword.Length == 0)
        {
            throw new ArgumentException("Security OwnerPassword cannot be empty when provided.", nameof(security));
        }

        const PdfPermissions knownPermissions = PdfPermissions.All;
        if ((security.Permissions & ~knownPermissions) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(security), "Security permissions contain unsupported flags.");
        }
    }

    private readonly record struct PdfEmbeddedFontPlan(
        PdfObjectId ResourcesObjectId,
        ReadOnlyMemory<byte> ContentStreamBytes,
        IReadOnlyList<PdfIndirectObject> ObjectsToAdd,
        IReadOnlyList<PdfObjectId> DirtyObjectIds);

    private readonly record struct EmbeddedLineMetrics(double MinX, double Width);

    private readonly record struct EmbeddedGlyphPlacement(
        PdfShapedGlyph Glyph,
        double X,
        double Y,
        int Sequence);

    private readonly record struct RichTextAtom(string Text, double FontSize);
}
