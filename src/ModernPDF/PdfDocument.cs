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
    private readonly HashSet<PdfObjectId> _dirtyObjectIds = [];
    private bool _openedEncrypted;

    private PdfDocument(PdfFile file, PdfDocumentModel model, bool openedEncrypted)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _openedEncrypted = openedEncrypted;
    }

    public static PdfDocument Create()
    {
        PdfFile file = CreateEmptyFile();
        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);
        return new PdfDocument(file, model, openedEncrypted: false);
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
        bool openedEncrypted = PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out _);
        if (openedEncrypted)
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
        return new PdfDocument(file, model, openedEncrypted);
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
        if (effectiveOptions.Mode == PdfSaveMode.Incremental && effectiveOptions.Security is not null)
        {
            throw new NotSupportedException("Incremental save with security options is not currently supported.");
        }

        if (effectiveOptions.Security is not null)
        {
            ValidateSecurityOptions(effectiveOptions.Security);
            PdfFile encryptedFile = PdfStandardSecurityProcessor.Encrypt(_file, effectiveOptions.Security);
            return PdfFileWriter.Write(encryptedFile);
        }

        if (effectiveOptions.Mode == PdfSaveMode.Incremental)
        {
            if (_openedEncrypted)
            {
                throw new NotSupportedException("Incremental save is not supported for documents opened from encrypted PDFs.");
            }

            byte[] incrementalBytes = PdfFileWriter.WriteIncremental(_file, _dirtyObjectIds);
            RebaseFromSavedBytes(incrementalBytes);
            return incrementalBytes;
        }

        byte[] fullBytes = PdfFileWriter.Write(_file);
        RebaseFromSavedBytes(fullBytes);
        return fullBytes;
    }

    public void Save(string path, PdfSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, Save(options));
    }

    private void RebaseFromSavedBytes(byte[] savedBytes)
    {
        PdfFile rebasedFile = PdfFileReader.Read(savedBytes);
        _file = rebasedFile;
        _model = PdfDocumentModelBuilder.Build(rebasedFile);
        _dirtyObjectIds.Clear();
        _openedEncrypted = false;
    }

    private void MarkDirty(PdfObjectId objectId)
    {
        _dirtyObjectIds.Add(objectId);
        _model.Mutations.MarkDirty(objectId);
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

        _file = new PdfFile(
            _file.Version,
            objects,
            _file.Trailer,
            _file.SourceBytes,
            _file.StartXrefOffset,
            _file.XrefEntries);
        _model = PdfDocumentModelBuilder.Build(_file);
        MarkDirty(contentsReference.ObjectId);
        MarkDirty(page.ObjectId);
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

        _file = new PdfFile(
            _file.Version,
            objects,
            _file.Trailer,
            _file.SourceBytes,
            _file.StartXrefOffset,
            _file.XrefEntries);
        _model = PdfDocumentModelBuilder.Build(_file);

        MarkDirty(page.ObjectId);
        MarkDirty(contentsReference.ObjectId);
        foreach (PdfObjectId objectId in embeddedPlan.DirtyObjectIds)
        {
            MarkDirty(objectId);
        }
    }

    public void ReplacePageRichText(int pageIndex, IReadOnlyList<PdfTextSpan> spans, PdfTextOptions? options = null)
    {
        ValidateTextSpans(spans);
        PdfTextOptions effectiveOptions = ResolveTextOptions(options);

        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfPageModel page = _model.Pages[pageIndex];
        if (page.Contents is not PdfReferenceObject contentsReference)
        {
            throw new NotSupportedException("Only page /Contents references are supported for replacement.");
        }

        List<PdfIndirectObject> objects = [.. _file.Objects];
        int nextObjectNumber = GetNextObjectNumber(objects);
        bool useEmbedded = effectiveOptions.TrueTypeFontPath is not null
            || spans.Any(static span => span.TrueTypeFontPath is not null);

        PdfObjectId resourcesId;
        ReadOnlyMemory<byte> contentBytes;
        List<PdfObjectId> dirtyObjectIds = [];

        if (!useEmbedded)
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
            objects.Add(new PdfIndirectObject(resourcesId, resourcesDictionary));

            string content = BuildRichTextContentStream(spans, effectiveOptions);
            contentBytes = System.Text.Encoding.ASCII.GetBytes(content);
            dirtyObjectIds.Add(fontId);
            dirtyObjectIds.Add(resourcesId);
        }
        else
        {
            PdfEmbeddedFontPlan embeddedPlan = BuildEmbeddedRichTextPlan(spans, effectiveOptions, nextObjectNumber);
            objects.AddRange(embeddedPlan.ObjectsToAdd);
            resourcesId = embeddedPlan.ResourcesObjectId;
            contentBytes = embeddedPlan.ContentStreamBytes;
            dirtyObjectIds.AddRange(embeddedPlan.DirtyObjectIds);
        }

        PdfStreamObject existingStream = RequireStreamObject(contentsReference.ObjectId, "Page contents");
        PdfStreamObject updatedStream = new(existingStream.Dictionary, contentBytes);
        ReplaceObject(objects, contentsReference.ObjectId, updatedStream);

        PdfDictionaryObject pageDictionary = RequireDictionaryObject(page.ObjectId, "Page");
        PdfDictionaryObject updatedPage = ReplaceDictionaryEntries(
            pageDictionary,
            new PdfDictionaryEntry("Resources", new PdfReferenceObject(resourcesId)));
        ReplaceObject(objects, page.ObjectId, updatedPage);

        _file = new PdfFile(
            _file.Version,
            objects,
            _file.Trailer,
            _file.SourceBytes,
            _file.StartXrefOffset,
            _file.XrefEntries);
        _model = PdfDocumentModelBuilder.Build(_file);
        MarkDirty(page.ObjectId);
        MarkDirty(contentsReference.ObjectId);
        foreach (PdfObjectId objectId in dirtyObjectIds)
        {
            MarkDirty(objectId);
        }
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

            _file = new PdfFile(
                _file.Version,
                objects,
                updatedTrailer,
                _file.SourceBytes,
                _file.StartXrefOffset,
                _file.XrefEntries);
            _model = PdfDocumentModelBuilder.Build(_file);
            MarkDirty(infoId);
            return;
        }

        _file = new PdfFile(
            _file.Version,
            objects,
            _file.Trailer,
            _file.SourceBytes,
            _file.StartXrefOffset,
            _file.XrefEntries);
        _model = PdfDocumentModelBuilder.Build(_file);
        MarkDirty(infoId);
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

        _file = new PdfFile(
            _file.Version,
            objects,
            _file.Trailer,
            _file.SourceBytes,
            _file.StartXrefOffset,
            _file.XrefEntries);
        _model = PdfDocumentModelBuilder.Build(_file);

        foreach (PdfObjectId streamId in changedStreamIds)
        {
            MarkDirty(streamId);
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
        _file = new PdfFile(
            _file.Version,
            objects,
            _file.Trailer,
            _file.SourceBytes,
            _file.StartXrefOffset,
            _file.XrefEntries);
        _model = PdfDocumentModelBuilder.Build(_file);

        MarkDirty(_model.PagesRootObjectId);
        foreach (PdfObjectId objectId in dirtyObjectIds)
        {
            MarkDirty(objectId);
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

            if (span.FallbackTrueTypeFontPaths is not null)
            {
                for (int fallbackIndex = 0; fallbackIndex < span.FallbackTrueTypeFontPaths.Count; fallbackIndex++)
                {
                    string? fallback = span.FallbackTrueTypeFontPaths[fallbackIndex];
                    if (string.IsNullOrWhiteSpace(fallback))
                    {
                        throw new ArgumentException(
                            $"Text span at index {index} has a blank FallbackTrueTypeFontPaths[{fallbackIndex}] entry.",
                            nameof(spans));
                    }
                }
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

        IReadOnlyList<EmbeddedTextSegment> segments = BuildEmbeddedTextSegments(
            text,
            options.FontSize,
            options.TrueTypeFontPath,
            options.FallbackTrueTypeFontPaths,
            options.Direction);

        return BuildEmbeddedFontPlanFromSegments(segments, options, nextObjectNumber);
    }

    private static PdfEmbeddedFontPlan BuildEmbeddedRichTextPlan(IReadOnlyList<PdfTextSpan> spans, PdfTextOptions options, int nextObjectNumber)
    {
        IReadOnlyList<EmbeddedTextSegment> segments = BuildEmbeddedTextSegments(spans, options);
        return BuildEmbeddedFontPlanFromSegments(segments, options, nextObjectNumber);
    }

    private static PdfEmbeddedFontPlan BuildEmbeddedFontPlanFromSegments(
        IReadOnlyList<EmbeddedTextSegment> segments,
        PdfTextOptions options,
        int nextObjectNumber)
    {
        List<PdfIndirectObject> objects = [];
        List<PdfObjectId> dirtyIds = [];
        List<PdfDictionaryEntry> fontEntries = [];
        List<EmbeddedGlyphToken> glyphs = [];
        Dictionary<EmbeddedFontBuildKey, EmbeddedFontResource> fontResources = [];
        int nextFontIndex = 1;

        foreach (EmbeddedTextSegment segment in segments)
        {
            if (segment.IsLineBreak)
            {
                glyphs.Add(new EmbeddedGlyphToken(
                    FontResourceName: string.Empty,
                    Cid: 0,
                    Cluster: segment.SourceStart,
                    Unicode: "\n",
                    FontSize: segment.FontSize,
                    XAdvance: 0,
                    YAdvance: 0,
                    XOffset: 0,
                    YOffset: 0,
                    IsLineBreak: true));
                continue;
            }

            if (string.IsNullOrEmpty(segment.Text) || segment.FontPath is null)
            {
                continue;
            }

            EmbeddedFontBuildKey buildKey = new(segment.FontPath, segment.Text);
            if (!fontResources.TryGetValue(buildKey, out EmbeddedFontResource fontResource))
            {
                PdfEmbeddedTrueTypeFont embedded = PdfTrueTypeFontEmbedder.Build(
                    segment.FontPath,
                    segment.Text,
                    options.SubsetFont,
                    options.Direction);

                string fontResourceName = $"F{nextFontIndex.ToString(CultureInfo.InvariantCulture)}";
                nextFontIndex++;

                PdfObjectId type0FontId = AppendEmbeddedFontObjects(
                    embedded,
                    options.WritingMode,
                    ref nextObjectNumber,
                    objects,
                    dirtyIds);

                fontEntries.Add(new PdfDictionaryEntry(fontResourceName, new PdfReferenceObject(type0FontId)));
                fontResource = new EmbeddedFontResource(fontResourceName, embedded);
                fontResources[buildKey] = fontResource;
            }

            foreach (PdfShapedGlyph glyph in fontResource.EmbeddedFont.GlyphRun)
            {
                string unicode = fontResource.EmbeddedFont.CidToUnicode.TryGetValue(glyph.Cid, out string? value)
                    ? value
                    : string.Empty;
                glyphs.Add(new EmbeddedGlyphToken(
                    fontResource.FontResourceName,
                    glyph.Cid,
                    segment.SourceStart + glyph.Cluster,
                    unicode,
                    segment.FontSize,
                    ScaleGlyphUnitToUserSpace(glyph.XAdvance, fontResource.EmbeddedFont.UnitsPerEm, segment.FontSize),
                    ScaleGlyphUnitToUserSpace(glyph.YAdvance, fontResource.EmbeddedFont.UnitsPerEm, segment.FontSize),
                    ScaleGlyphUnitToUserSpace(glyph.XOffset, fontResource.EmbeddedFont.UnitsPerEm, segment.FontSize),
                    ScaleGlyphUnitToUserSpace(glyph.YOffset, fontResource.EmbeddedFont.UnitsPerEm, segment.FontSize),
                    IsLineBreak: false));
            }
        }

        PdfObjectId resourcesId = new(nextObjectNumber++, 0);
        PdfDictionaryObject resources = new(
        [
            new PdfDictionaryEntry("Font", new PdfDictionaryObject(fontEntries)),
        ]);

        objects.Add(new PdfIndirectObject(resourcesId, resources));
        dirtyIds.Add(resourcesId);

        ReadOnlyMemory<byte> contentBytes = BuildEmbeddedTextContentStream(glyphs, options);
        return new PdfEmbeddedFontPlan(resourcesId, contentBytes, objects, dirtyIds);
    }

    private static PdfObjectId AppendEmbeddedFontObjects(
        PdfEmbeddedTrueTypeFont embedded,
        PdfWritingMode writingMode,
        ref int nextObjectNumber,
        List<PdfIndirectObject> objects,
        List<PdfObjectId> dirtyIds)
    {
        PdfObjectId fontFileId = new(nextObjectNumber++, 0);
        PdfObjectId descriptorId = new(nextObjectNumber++, 0);
        PdfObjectId cidToGidId = new(nextObjectNumber++, 0);
        PdfObjectId toUnicodeId = new(nextObjectNumber++, 0);
        PdfObjectId descendantFontId = new(nextObjectNumber++, 0);
        PdfObjectId type0FontId = new(nextObjectNumber++, 0);

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
            new PdfDictionaryEntry("Encoding", new PdfNameObject(
                writingMode == PdfWritingMode.Vertical ? "Identity-V" : "Identity-H")),
            new PdfDictionaryEntry(
                "DescendantFonts",
                new PdfArrayObject(
                [
                    new PdfReferenceObject(descendantFontId),
                ])),
            new PdfDictionaryEntry("ToUnicode", new PdfReferenceObject(toUnicodeId)),
        ]);

        objects.AddRange(
        [
            new PdfIndirectObject(fontFileId, fontFileStream),
            new PdfIndirectObject(descriptorId, descriptor),
            new PdfIndirectObject(cidToGidId, cidToGidMap),
            new PdfIndirectObject(toUnicodeId, toUnicode),
            new PdfIndirectObject(descendantFontId, descendantFont),
            new PdfIndirectObject(type0FontId, type0Font),
        ]);

        dirtyIds.AddRange(
        [
            fontFileId,
            descriptorId,
            cidToGidId,
            toUnicodeId,
            descendantFontId,
            type0FontId,
        ]);

        return type0FontId;
    }

    private static List<EmbeddedTextSegment> BuildEmbeddedTextSegments(
        string text,
        double fontSize,
        string trueTypeFontPath,
        IReadOnlyList<string>? fallbackPaths,
        PdfTextDirection direction)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return BuildEmbeddedTextSegmentsCore(
            normalized,
            fontSize,
            trueTypeFontPath,
            fallbackPaths,
            direction,
            sourceStartOffset: 0,
            probeCache: []);
    }

    private static List<EmbeddedTextSegment> BuildEmbeddedTextSegments(
        IReadOnlyList<PdfTextSpan> spans,
        PdfTextOptions options)
    {
        List<EmbeddedTextSegment> segments = [];
        Dictionary<FontProbeKey, bool> probeCache = [];
        int sourceStart = 0;

        foreach (PdfTextSpan span in spans)
        {
            string normalized = span.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            double spanFontSize = span.FontSize ?? options.FontSize;
            string? spanFontPath = span.TrueTypeFontPath ?? options.TrueTypeFontPath;

            if (string.IsNullOrWhiteSpace(spanFontPath))
            {
                throw new ArgumentException(
                    "Rich text embedded rendering requires TrueTypeFontPath either in options or per span.",
                    nameof(spans));
            }

            List<string> fallbackPaths = [];
            if (span.FallbackTrueTypeFontPaths is not null)
            {
                fallbackPaths.AddRange(span.FallbackTrueTypeFontPaths);
            }

            if (options.FallbackTrueTypeFontPaths is not null)
            {
                fallbackPaths.AddRange(options.FallbackTrueTypeFontPaths);
            }

            IReadOnlyList<EmbeddedTextSegment> spanSegments = BuildEmbeddedTextSegmentsCore(
                normalized,
                spanFontSize,
                spanFontPath,
                fallbackPaths,
                options.Direction,
                sourceStart,
                probeCache);
            segments.AddRange(spanSegments);
            sourceStart += normalized.Length;
        }

        return segments;
    }

    private static List<EmbeddedTextSegment> BuildEmbeddedTextSegmentsCore(
        string normalizedText,
        double fontSize,
        string trueTypeFontPath,
        IReadOnlyList<string>? fallbackPaths,
        PdfTextDirection direction,
        int sourceStartOffset,
        Dictionary<FontProbeKey, bool> probeCache)
    {
        if (normalizedText.Length == 0)
        {
            return [];
        }

        List<string> candidates = BuildFontCandidates(trueTypeFontPath, fallbackPaths);
        List<(string Element, int Index)> elements = EnumerateTextElementsWithIndex(normalizedText);

        List<EmbeddedTextSegment> segments = [];
        StringBuilder currentText = new();
        string? currentFontPath = null;
        int currentStart = sourceStartOffset;

        void FlushCurrent()
        {
            if (currentFontPath is null || currentText.Length == 0)
            {
                return;
            }

            segments.Add(new EmbeddedTextSegment(
                currentText.ToString(),
                currentFontPath,
                fontSize,
                currentStart,
                IsLineBreak: false));
            currentText.Clear();
            currentFontPath = null;
        }

        foreach ((string element, int index) in elements)
        {
            int cluster = sourceStartOffset + index;
            if (element == "\n")
            {
                FlushCurrent();
                segments.Add(new EmbeddedTextSegment(
                    element,
                    FontPath: null,
                    fontSize,
                    cluster,
                    IsLineBreak: true));
                continue;
            }

            string selectedFont = SelectFontForTextElement(
                element,
                candidates,
                trueTypeFontPath,
                direction,
                probeCache);

            if (!string.Equals(currentFontPath, selectedFont, StringComparison.Ordinal))
            {
                FlushCurrent();
                currentFontPath = selectedFont;
                currentStart = cluster;
            }

            currentText.Append(element);
        }

        FlushCurrent();
        return segments;
    }

    private static List<string> BuildFontCandidates(string trueTypeFontPath, IReadOnlyList<string>? fallbackPaths)
    {
        List<string> candidates = [trueTypeFontPath];
        if (fallbackPaths is not null)
        {
            candidates.AddRange(fallbackPaths.Where(static path => !string.IsNullOrWhiteSpace(path)));
        }

        return [.. candidates.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string SelectFontForTextElement(
        string element,
        IReadOnlyList<string> candidates,
        string primaryFontPath,
        PdfTextDirection direction,
        Dictionary<FontProbeKey, bool> probeCache)
    {
        foreach (string candidate in candidates)
        {
            FontProbeKey key = new(candidate, element, direction);
            if (!probeCache.TryGetValue(key, out bool canRender))
            {
                canRender = ProbeFont(candidate, element, primaryFontPath, direction);
                probeCache[key] = canRender;
            }

            if (canRender)
            {
                return candidate;
            }
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return primaryFontPath;
    }

    private static bool ProbeFont(string candidate, string element, string primaryFontPath, PdfTextDirection direction)
    {
        try
        {
            return PdfTrueTypeFontEmbedder.CanMapText(candidate, element);
        }
        catch (PdfFormatException) when (!string.Equals(candidate, primaryFontPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        catch (NotSupportedException) when (!string.Equals(candidate, primaryFontPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        catch (IOException) when (!string.Equals(candidate, primaryFontPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    private static List<(string Element, int Index)> EnumerateTextElementsWithIndex(string text)
    {
        List<(string Element, int Index)> elements = [];
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            string element = enumerator.GetTextElement();
            elements.Add((element, enumerator.ElementIndex));
        }

        return elements;
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
        IReadOnlyList<EmbeddedGlyphToken> glyphRun,
        PdfTextOptions options)
    {
        if (options.WritingMode == PdfWritingMode.Vertical)
        {
            return BuildEmbeddedVerticalTextContentStream(glyphRun, options);
        }

        List<List<EmbeddedGlyphToken>> lines = BuildEmbeddedLines(glyphRun, options);
        System.Text.StringBuilder builder = new();
        builder.Append("BT ");

        double y = options.Y;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            List<EmbeddedGlyphToken> line = lines[lineIndex];
            if (line.Count == 0)
            {
                y -= options.FontSize * options.LineHeightMultiplier;
                continue;
            }

            EmbeddedLineMetrics metrics = MeasureEmbeddedLine(line);
            double lineHeight = line.Max(static item => item.FontSize) * options.LineHeightMultiplier;
            double containerWidth = options.MaxWidth ?? metrics.Width;
            double alignmentOffset = GetAlignmentOffset(
                options.Alignment == PdfTextAlignment.Justify ? PdfTextAlignment.Left : options.Alignment,
                containerWidth,
                metrics.Width);
            double penX = options.X + alignmentOffset - metrics.MinX;
            double penY = y;

            int whitespaceCount = options.Alignment == PdfTextAlignment.Justify && lineIndex < lines.Count - 1
                ? line.Count(static glyph => IsWhitespaceGlyph(glyph))
                : 0;
            double extraWordSpacing = whitespaceCount > 0
                ? Math.Max(0, containerWidth - metrics.Width) / whitespaceCount
                : 0;
            double extraCharacterSpacing = extraWordSpacing == 0
                && options.Alignment == PdfTextAlignment.Justify
                && lineIndex < lines.Count - 1
                && line.Count > 1
                    ? Math.Max(0, containerWidth - metrics.Width) / (line.Count - 1)
                    : 0;

            List<EmbeddedGlyphPlacementToken> placements = new(line.Count);
            int sequence = 0;
            for (int glyphIndex = 0; glyphIndex < line.Count; glyphIndex++)
            {
                EmbeddedGlyphToken glyph = line[glyphIndex];
                double glyphX = penX + glyph.XOffset;
                double glyphY = penY + glyph.YOffset;
                placements.Add(new EmbeddedGlyphPlacementToken(glyph, glyphX, glyphY, sequence++));

                penX += glyph.XAdvance;
                penY += glyph.YAdvance;

                if (extraWordSpacing > 0 && IsWhitespaceGlyph(glyph))
                {
                    penX += extraWordSpacing;
                }
                else if (extraCharacterSpacing > 0 && glyphIndex < line.Count - 1)
                {
                    penX += extraCharacterSpacing;
                }
            }

            foreach (EmbeddedGlyphPlacementToken placement in placements
                .OrderBy(static value => value.Glyph.Cluster)
                .ThenBy(static value => value.Sequence))
            {
                builder.Append('/');
                builder.Append(placement.Glyph.FontResourceName);
                builder.Append(' ');
                builder.Append(placement.Glyph.FontSize.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tf 1 0 0 1 ");
                builder.Append(placement.X.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(' ');
                builder.Append(placement.Y.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tm <");
                builder.Append(placement.Glyph.Cid.ToString("X4", CultureInfo.InvariantCulture));
                builder.Append("> Tj ");
            }

            y -= lineHeight;
        }

        builder.Append("ET");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static ReadOnlyMemory<byte> BuildEmbeddedVerticalTextContentStream(
        IReadOnlyList<EmbeddedGlyphToken> glyphRun,
        PdfTextOptions options)
    {
        double directionSign = options.Direction == PdfTextDirection.RightToLeft ? -1 : 1;
        double maxColumnHeight = options.MaxWidth ?? double.PositiveInfinity;
        double x = options.X;
        double y = options.Y;
        double columnHeight = 0;

        System.Text.StringBuilder builder = new();
        builder.Append("BT ");

        foreach (EmbeddedGlyphToken glyph in glyphRun.OrderBy(static value => value.Cluster))
        {
            double step = glyph.FontSize * options.LineHeightMultiplier;
            if (glyph.IsLineBreak)
            {
                x += step * directionSign;
                y = options.Y;
                columnHeight = 0;
                continue;
            }

            if (double.IsFinite(maxColumnHeight) && maxColumnHeight > 0 && columnHeight + step > maxColumnHeight && columnHeight > 0)
            {
                x += step * directionSign;
                y = options.Y;
                columnHeight = 0;
            }

            builder.Append('/');
            builder.Append(glyph.FontResourceName);
            builder.Append(' ');
            builder.Append(glyph.FontSize.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(" Tf 1 0 0 1 ");
            builder.Append((x + glyph.XOffset).ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append((y + glyph.YOffset).ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(" Tm <");
            builder.Append(glyph.Cid.ToString("X4", CultureInfo.InvariantCulture));
            builder.Append("> Tj ");

            y -= step;
            columnHeight += step;
        }

        builder.Append("ET");
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
            int graphemeCount = justifyLine ? EnumerateTextElements(line).Count : 0;
            double characterSpacing = wordSpacing == 0 && graphemeCount > 1 ? extra / (graphemeCount - 1) : 0;

            if (wordSpacing > 0)
            {
                builder.Append(wordSpacing.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tw ");
            }
            else if (characterSpacing > 0)
            {
                builder.Append(characterSpacing.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tc ");
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
            else if (characterSpacing > 0)
            {
                builder.Append("0 Tc ");
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
            int graphemeCount = justifyLine ? CountRichTextElements(line) : 0;
            double characterSpacing = wordSpacing == 0 && graphemeCount > 1 ? extra / (graphemeCount - 1) : 0;
            if (wordSpacing > 0)
            {
                builder.Append(wordSpacing.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tw ");
            }
            else if (characterSpacing > 0)
            {
                builder.Append(characterSpacing.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(" Tc ");
            }

            int atomIndex = 0;
            while (atomIndex < line.Count)
            {
                double fontSize = line[atomIndex].FontSize;
                System.Text.StringBuilder segment = new();
                int segmentSpaceCount = 0;
                int segmentGraphemeCount = 0;

                while (atomIndex < line.Count && Math.Abs(line[atomIndex].FontSize - fontSize) < 0.001)
                {
                    segment.Append(line[atomIndex].Text);
                    segmentSpaceCount += line[atomIndex].Text.Count(static value => value == ' ');
                    segmentGraphemeCount += line[atomIndex].Text == "\n" ? 0 : 1;
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
                else if (characterSpacing > 0 && segmentGraphemeCount > 0)
                {
                    bool hasFollowingSegment = atomIndex < line.Count;
                    int characterGaps = Math.Max(0, segmentGraphemeCount - 1) + (hasFollowingSegment ? 1 : 0);
                    x += characterGaps * characterSpacing;
                }
            }

            if (wordSpacing > 0)
            {
                builder.Append("0 Tw ");
            }
            else if (characterSpacing > 0)
            {
                builder.Append("0 Tc ");
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

        double directionSign = options.Direction == PdfTextDirection.RightToLeft ? -1 : 1;
        double x = options.X;
        double y = options.Y;
        double columnHeight = 0;
        double maxColumnHeight = options.MaxWidth ?? double.PositiveInfinity;

        foreach (RichTextAtom atom in atoms)
        {
            if (atom.Text == "\n")
            {
                x += atom.FontSize * options.LineHeightMultiplier * directionSign;
                y = options.Y;
                columnHeight = 0;
                continue;
            }

            double step = atom.FontSize * options.LineHeightMultiplier;
            if (double.IsFinite(maxColumnHeight) && maxColumnHeight > 0 && columnHeight + step > maxColumnHeight && columnHeight > 0)
            {
                x += step * directionSign;
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
                else if (options.EnableHyphenation
                    && TryApplyRichHyphenation(currentLine, maxWidth, out List<RichTextAtom> hyphenatedOverflow))
                {
                    overflow = hyphenatedOverflow;
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

    private static bool TryApplyRichHyphenation(
        List<RichTextAtom> currentLine,
        double maxWidth,
        out List<RichTextAtom> overflow)
    {
        overflow = [];
        if (currentLine.Count < 3)
        {
            return false;
        }

        int splitIndex = currentLine.Count - 1;
        while (splitIndex >= 2)
        {
            RichTextAtom left = currentLine[splitIndex - 1];
            RichTextAtom right = currentLine[splitIndex];
            if (!IsHyphenationBoundary(left.Text, right.Text))
            {
                splitIndex--;
                continue;
            }

            List<RichTextAtom> prefix = currentLine.Take(splitIndex).ToList();
            double width = MeasureRichLineWidth(prefix) + EstimateSimpleTextWidth("-", left.FontSize);
            if (width <= maxWidth)
            {
                List<RichTextAtom> trailing = currentLine.GetRange(splitIndex, currentLine.Count - splitIndex);
                currentLine.RemoveRange(splitIndex, currentLine.Count - splitIndex);
                currentLine.Add(new RichTextAtom("-", left.FontSize));
                overflow = trailing;
                return true;
            }

            splitIndex--;
        }

        return false;
    }

    private static bool IsHyphenationBoundary(string leftElement, string rightElement)
    {
        if (leftElement.Length == 0 || rightElement.Length == 0)
        {
            return false;
        }

        if (!Rune.TryGetRuneAt(leftElement, 0, out Rune leftRune)
            || !Rune.TryGetRuneAt(rightElement, 0, out Rune rightRune))
        {
            return false;
        }

        return Rune.IsLetter(leftRune) && Rune.IsLetter(rightRune);
    }

    private static double MeasureRichLineWidth(IEnumerable<RichTextAtom> line)
    {
        return line.Sum(static atom => EstimateSimpleTextWidth(atom.Text, atom.FontSize));
    }

    private static int CountRichSpaces(IEnumerable<RichTextAtom> line)
    {
        return line.Sum(static atom => atom.Text.Count(static value => value == ' '));
    }

    private static int CountRichTextElements(IEnumerable<RichTextAtom> line)
    {
        return line.Sum(static atom => atom.Text == "\n" ? 0 : 1);
    }

    private static List<List<EmbeddedGlyphToken>> BuildEmbeddedLines(
        IReadOnlyList<EmbeddedGlyphToken> glyphRun,
        PdfTextOptions options)
    {
        List<List<EmbeddedGlyphToken>> lines = [[]];
        int currentLineIndex = 0;
        int lastBreakGlyphIndex = -1;
        double currentAdvance = 0;
        double widthAtBreak = 0;

        double maxWidth = options.MaxWidth ?? double.PositiveInfinity;
        foreach (EmbeddedGlyphToken glyph in glyphRun)
        {
            if (glyph.IsLineBreak)
            {
                currentLineIndex++;
                lines.Add([]);
                currentAdvance = 0;
                lastBreakGlyphIndex = -1;
                widthAtBreak = 0;
                continue;
            }

            List<EmbeddedGlyphToken> currentLine = lines[currentLineIndex];
            currentLine.Add(glyph);
            currentAdvance += Math.Abs(glyph.XAdvance);

            if (IsWhitespaceGlyph(glyph))
            {
                lastBreakGlyphIndex = currentLine.Count - 1;
                widthAtBreak = currentAdvance;
            }

            if (double.IsFinite(maxWidth) && maxWidth > 0 && currentAdvance > maxWidth && currentLine.Count > 1)
            {
                List<EmbeddedGlyphToken> overflow;
                if (lastBreakGlyphIndex >= 0)
                {
                    int breakAfter = lastBreakGlyphIndex + 1;
                    overflow = currentLine.GetRange(breakAfter, currentLine.Count - breakAfter);
                    currentLine.RemoveRange(breakAfter, currentLine.Count - breakAfter);
                    currentAdvance = widthAtBreak;
                }
                else
                {
                    EmbeddedGlyphToken pushed = currentLine[^1];
                    currentLine.RemoveAt(currentLine.Count - 1);
                    overflow = [pushed];
                    currentAdvance = ComputeEmbeddedLineAdvance(currentLine);
                }

                currentLineIndex++;
                lines.Add(overflow);
                currentAdvance = ComputeEmbeddedLineAdvance(lines[currentLineIndex]);
                lastBreakGlyphIndex = FindLastWhitespaceGlyph(lines[currentLineIndex]);
                widthAtBreak = lastBreakGlyphIndex >= 0
                    ? ComputeEmbeddedLineAdvance(lines[currentLineIndex].Take(lastBreakGlyphIndex + 1))
                    : 0;
            }
        }

        return lines;
    }

    private static bool IsWhitespaceGlyph(EmbeddedGlyphToken glyph)
    {
        return !glyph.IsLineBreak
            && glyph.Unicode.Length > 0
            && glyph.Unicode.All(char.IsWhiteSpace);
    }

    private static int FindLastWhitespaceGlyph(List<EmbeddedGlyphToken> line)
    {
        for (int index = line.Count - 1; index >= 0; index--)
        {
            if (IsWhitespaceGlyph(line[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static double ComputeEmbeddedLineAdvance(IEnumerable<EmbeddedGlyphToken> glyphs)
    {
        return glyphs.Sum(static glyph => Math.Abs(glyph.XAdvance));
    }

    private static EmbeddedLineMetrics MeasureEmbeddedLine(List<EmbeddedGlyphToken> glyphs)
    {
        double penX = 0;
        double minX = 0;
        double maxX = 0;

        foreach (EmbeddedGlyphToken glyph in glyphs)
        {
            double x = penX + glyph.XOffset;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);

            penX += glyph.XAdvance;
            minX = Math.Min(minX, penX);
            maxX = Math.Max(maxX, penX);
        }

        return new EmbeddedLineMetrics(minX, maxX - minX);
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
        double directionSign = options.Direction == PdfTextDirection.RightToLeft ? -1 : 1;
        double maxColumnHeight = options.MaxWidth ?? double.PositiveInfinity;
        double x = options.X;
        double y = options.Y;
        double columnHeight = 0;

        foreach (string element in elements)
        {
            if (element is "\r" or "\n")
            {
                x += step * directionSign;
                y = options.Y;
                columnHeight = 0;
                continue;
            }

            if (double.IsFinite(maxColumnHeight) && maxColumnHeight > 0 && columnHeight + step > maxColumnHeight && columnHeight > 0)
            {
                x += step * directionSign;
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

                if (options.EnableHyphenation
                    && TryApplySimpleHyphenation(
                        elements,
                        options.FontSize,
                        options.MaxWidth!.Value,
                        lineStart,
                        index,
                        out string hyphenatedLine,
                        out int nextLineStart))
                {
                    lines.Add(hyphenatedLine);
                    lineStart = nextLineStart;
                    index = lineStart;
                    lineWidth = 0;
                    lastBreak = -1;
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

    private static bool TryApplySimpleHyphenation(
        List<string> elements,
        double fontSize,
        double maxWidth,
        int lineStart,
        int overflowIndex,
        out string hyphenatedLine,
        out int nextLineStart)
    {
        hyphenatedLine = string.Empty;
        nextLineStart = overflowIndex;

        int splitIndex = overflowIndex;
        while (splitIndex >= lineStart + 2)
        {
            string left = elements[splitIndex - 1];
            string right = elements[splitIndex];
            if (!IsHyphenationBoundary(left, right))
            {
                splitIndex--;
                continue;
            }

            string candidate = string.Concat(elements.Skip(lineStart).Take(splitIndex - lineStart)) + "-";
            if (EstimateSimpleTextWidth(candidate, fontSize) <= maxWidth)
            {
                hyphenatedLine = candidate;
                nextLineStart = splitIndex;
                return true;
            }

            splitIndex--;
        }

        return false;
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

    private readonly record struct EmbeddedTextSegment(
        string Text,
        string? FontPath,
        double FontSize,
        int SourceStart,
        bool IsLineBreak);

    private readonly record struct EmbeddedGlyphToken(
        string FontResourceName,
        int Cid,
        int Cluster,
        string Unicode,
        double FontSize,
        double XAdvance,
        double YAdvance,
        double XOffset,
        double YOffset,
        bool IsLineBreak);

    private readonly record struct EmbeddedGlyphPlacementToken(
        EmbeddedGlyphToken Glyph,
        double X,
        double Y,
        int Sequence);

    private readonly record struct EmbeddedFontBuildKey(string FontPath, string Text);

    private readonly record struct EmbeddedFontResource(string FontResourceName, PdfEmbeddedTrueTypeFont EmbeddedFont);

    private readonly record struct FontProbeKey(string FontPath, string TextElement, PdfTextDirection Direction);

    private readonly record struct RichTextAtom(string Text, double FontSize);
}
