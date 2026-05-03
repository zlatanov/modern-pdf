using ModernPDF.DocumentModel;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using ModernPDF.Text;
using System.Globalization;

namespace ModernPDF;

public sealed class PdfDocument
{
    private PdfFile _file;
    private PdfDocumentModel _model;

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
        return Open(data.AsSpan());
    }

    public static PdfDocument Open(ReadOnlySpan<byte> data)
    {
        PdfFile file = PdfFileReader.Read(data);
        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);
        return new PdfDocument(file, model);
    }

    public static PdfDocument Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Open(File.ReadAllBytes(path));
    }

    public string Version => _file.Version;

    public int PageCount => _model.Pages.Count;

    public int AddPage(PdfPageOptions? options = null)
    {
        PdfPageOptions pageOptions = options ?? new PdfPageOptions();
        return AddPageCore(pageOptions, text: null, textOptions: null);
    }

    public int AddTextPage(string text, PdfPageOptions? pageOptions = null, PdfTextOptions? textOptions = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        PdfPageOptions effectivePageOptions = pageOptions ?? new PdfPageOptions();
        PdfTextOptions effectiveTextOptions = textOptions ?? new PdfTextOptions();
        return AddPageCore(effectivePageOptions, text, effectiveTextOptions);
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

        return PdfFileWriter.Write(_file);
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
        PdfTextOptions effectiveOptions = options ?? new PdfTextOptions();
        ValidateTextOptions(effectiveOptions);
        ReplacePageContents(pageIndex, BuildTextContentStream(text, effectiveOptions));
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
        PdfObjectId? fontId = null;

        PdfObject? resourcesEntryValue = null;
        ReadOnlyMemory<byte> contentBytes = ReadOnlyMemory<byte>.Empty;

        if (text is not null)
        {
            PdfTextOptions effectiveTextOptions = textOptions ?? new PdfTextOptions();
            ValidateTextOptions(effectiveTextOptions);

            fontId = new PdfObjectId(nextObjectNumber++, 0);
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
                        new PdfDictionaryEntry("F1", new PdfReferenceObject(fontId.Value)),
                    ])),
            ]);

            objects.Add(new PdfIndirectObject(fontId.Value, fontDictionary));
            objects.Add(new PdfIndirectObject(resourcesId.Value, resourcesDictionary));
            resourcesEntryValue = new PdfReferenceObject(resourcesId.Value);

            string contentStream = BuildTextContentStream(text, effectiveTextOptions);
            contentBytes = System.Text.Encoding.ASCII.GetBytes(contentStream);
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
        _model.Mutations.MarkDirty(pageId);
        _model.Mutations.MarkDirty(contentsId);
        if (fontId is not null)
        {
            _model.Mutations.MarkDirty(fontId.Value);
        }

        if (resourcesId is not null)
        {
            _model.Mutations.MarkDirty(resourcesId.Value);
        }

        return _model.Pages.Count - 1;
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

    private static string BuildTextContentStream(string text, PdfTextOptions options)
    {
        string escaped = EscapeLiteralString(text);
        string x = options.X.ToString("0.###", CultureInfo.InvariantCulture);
        string y = options.Y.ToString("0.###", CultureInfo.InvariantCulture);
        string fontSize = options.FontSize.ToString("0.###", CultureInfo.InvariantCulture);

        return $"BT /F1 {fontSize} Tf {x} {y} Td ({escaped}) Tj ET";
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
    }
}
