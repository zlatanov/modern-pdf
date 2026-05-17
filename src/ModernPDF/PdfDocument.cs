using ModernPDF.DocumentModel;
using ModernPDF.Fonts;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Images;
using ModernPDF.Primitives;
using ModernPDF.Security;
using ModernPDF.Text;
using System.Formats.Asn1;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace ModernPDF;

/// <summary>
/// Represents an in-memory PDF document that supports reading, editing, and writing.
/// </summary>
public sealed class PdfDocument
{
    private static readonly HashSet<string> SupportedCmsSubFilters =
    [
        "adbe.pkcs7.detached",
        "ETSI.CAdES.detached",
        "adbe.pkcs7.sha1",
        "ETSI.RFC3161",
    ];
    private static readonly Encoding ContentStreamEncoding = Encoding.Latin1;

    private sealed class PdfFontGlyphWidths
    {
        private readonly IReadOnlyDictionary<int, double> _glyphWidths;

        public PdfFontGlyphWidths(bool isComposite, IReadOnlyDictionary<int, double> glyphWidths, double defaultWidthUnits)
        {
            IsComposite = isComposite;
            _glyphWidths = glyphWidths;
            DefaultWidthUnits = defaultWidthUnits;
        }

        public bool IsComposite { get; }

        public double DefaultWidthUnits { get; }

        public double GetGlyphWidthUnits(int characterCode)
        {
            return _glyphWidths.TryGetValue(characterCode, out double width)
                ? width
                : DefaultWidthUnits;
        }
    }

    private PdfFile _file;
    private PdfDocumentModel _model;
    private PdfTextOptions _defaultTextOptions = new();
    private readonly HashSet<PdfObjectId> _dirtyObjectIds = [];
    private bool _openedEncrypted;
    private PdfSecurityOptions? _openedSecurityOptions;
    private PdfFile? _openedEncryptedFile;

    private PdfDocument(
        PdfFile file,
        PdfDocumentModel model,
        bool openedEncrypted,
        PdfSecurityOptions? openedSecurityOptions,
        PdfFile? openedEncryptedFile)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _openedEncrypted = openedEncrypted;
        _openedSecurityOptions = openedSecurityOptions;
        _openedEncryptedFile = openedEncryptedFile;
    }

    /// <summary>
    /// Creates a new empty PDF document.
    /// </summary>
    public static PdfDocument Create()
    {
        PdfFile file = CreateEmptyFile();
        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);
        return new PdfDocument(
            file,
            model,
            openedEncrypted: false,
            openedSecurityOptions: null,
            openedEncryptedFile: null);
    }

    /// <summary>
    /// Opens a PDF document from bytes.
    /// </summary>
    public static PdfDocument Open(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Open(data.AsSpan(), password: null);
    }

    /// <summary>
    /// Opens an encrypted PDF document from bytes using the provided password.
    /// </summary>
    public static PdfDocument Open(byte[] data, string password)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(password);
        return Open(data.AsSpan(), password);
    }

    /// <summary>
    /// Opens a PDF document from a read-only byte span.
    /// </summary>
    public static PdfDocument Open(ReadOnlySpan<byte> data)
    {
        return Open(data, password: null);
    }

    /// <summary>
    /// Opens a PDF document from a read-only byte span, optionally decrypting it with a password.
    /// </summary>
    public static PdfDocument Open(ReadOnlySpan<byte> data, string? password)
    {
        PdfFile file = PdfFileReader.Read(data);
        PdfFile? encryptedFile = null;
        bool openedEncrypted = PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out _);
        PdfSecurityOptions? openedSecurityOptions = null;
        if (openedEncrypted)
        {
            encryptedFile = file;
            if (!PdfStandardSecurityProcessor.IsSupportedStandardHandler(file))
            {
                throw new NotSupportedException("Only Standard security handler profiles V=1/R=2 (40-bit RC4), V=2/R=3 (128-bit RC4), V=4/R=4 (128-bit AES), and V=5/R=6 (256-bit AES) are currently supported.");
            }

            if (string.IsNullOrEmpty(password))
            {
                throw new UnauthorizedAccessException("Password is required to open encrypted PDFs.");
            }

            file = PdfStandardSecurityProcessor.Decrypt(file, password, out PdfSecurityOptions reopenedSecurity);
            openedSecurityOptions = reopenedSecurity;
        }

        PdfDocumentModel model = PdfDocumentModelBuilder.Build(file);
        return new PdfDocument(file, model, openedEncrypted, openedSecurityOptions, encryptedFile);
    }

    /// <summary>
    /// Opens a PDF document from disk.
    /// </summary>
    public static PdfDocument Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Open(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Opens an encrypted PDF document from disk using the provided password.
    /// </summary>
    public static PdfDocument Open(string path, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(password);
        return Open(File.ReadAllBytes(path), password);
    }

    /// <summary>
    /// Reads encryption metadata from PDF bytes without opening the document.
    /// </summary>
    public static PdfEncryptionInfo? InspectEncryption(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return InspectEncryption(data.AsSpan());
    }

    /// <summary>
    /// Reads encryption metadata from PDF bytes without opening the document.
    /// </summary>
    public static PdfEncryptionInfo? InspectEncryption(ReadOnlySpan<byte> data)
    {
        PdfFile file = PdfFileReader.Read(data);
        return PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out PdfEncryptionInfo? info) ? info : null;
    }

    /// <summary>
    /// Reads encryption metadata from a PDF file without opening the document.
    /// </summary>
    public static PdfEncryptionInfo? InspectEncryption(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return InspectEncryption(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Gets the PDF version string currently associated with the in-memory file model.
    /// </summary>
    public string Version => _file.Version;

    /// <summary>
    /// Gets the current number of pages in the document.
    /// </summary>
    public int PageCount => _model.Pages.Count;

    /// <summary>
    /// Gets or sets default text options used by text-writing APIs when per-call options are omitted.
    /// </summary>
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

    /// <summary>
    /// Appends a blank page and returns its zero-based page index.
    /// </summary>
    public int AddPage(PdfPageOptions? options = null)
    {
        PdfPageOptions pageOptions = options ?? new PdfPageOptions();
        return AddPageCore(pageOptions, text: null, textOptions: null);
    }

    /// <summary>
    /// Appends a page with text content and returns its zero-based page index.
    /// </summary>
    public int AddTextPage(string text, PdfPageOptions? pageOptions = null, PdfTextOptions? textOptions = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        PdfPageOptions effectivePageOptions = pageOptions ?? new PdfPageOptions();
        PdfTextOptions effectiveTextOptions = ResolveTextOptions(textOptions);
        return AddPageCore(effectivePageOptions, text, effectiveTextOptions);
    }

    /// <summary>
    /// Appends a page populated from rich text spans and returns its zero-based page index.
    /// </summary>
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

    /// <summary>
    /// Appends an image-only page from raster bytes and returns its zero-based page index.
    /// </summary>
    public int AddImagePage(byte[] imageBytes, PdfPageOptions? pageOptions = null, PdfImageOptions? imageOptions = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        PdfRasterImage image = PdfImageParser.Parse(imageBytes);
        PdfPageOptions effectivePageOptions = pageOptions ?? new PdfPageOptions
        {
            Width = image.Width,
            Height = image.Height,
        };

        int pageIndex = AddPage(effectivePageOptions);
        PdfImageOptions effectiveImageOptions = imageOptions ?? new PdfImageOptions
        {
            X = 0,
            Y = 0,
            Width = effectivePageOptions.Width,
            Height = effectivePageOptions.Height,
            PreserveAspectRatio = true,
        };
        ReplacePageImage(pageIndex, imageBytes, effectiveImageOptions);
        return pageIndex;
    }

    /// <summary>
    /// Appends an image-only page from an image file path and returns its zero-based page index.
    /// </summary>
    public int AddImagePage(string imagePath, PdfPageOptions? pageOptions = null, PdfImageOptions? imageOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        return AddImagePage(File.ReadAllBytes(imagePath), pageOptions, imageOptions);
    }

    /// <summary>
    /// Extracts text from all pages and concatenates page results with newline separators.
    /// </summary>
    public string ExtractText()
    {
        return PdfTextExtractor.ExtractAll(_file, _model);
    }

    /// <summary>
    /// Extracts text from a single page.
    /// </summary>
    public string ExtractText(int pageIndex)
    {
        return PdfTextExtractor.ExtractPage(_file, _model, pageIndex);
    }

    /// <summary>
    /// Extracts text regions with coordinates from every page.
    /// </summary>
    public IReadOnlyList<PdfTextRegion> ExtractTextRegions()
    {
        return ExtractTextRegionsCore(pageIndex: null);
    }

    /// <summary>
    /// Extracts text regions with coordinates from a single page.
    /// </summary>
    public IReadOnlyList<PdfTextRegion> ExtractTextRegions(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);
        return ExtractTextRegionsCore(pageIndex);
    }

    /// <summary>
    /// Finds regex matches in extracted text regions across all pages.
    /// </summary>
    public IReadOnlyList<PdfTextMatch> FindText(string pattern, RegexOptions regexOptions = RegexOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        Regex regex = new(pattern, regexOptions);
        return BuildTextMatches(ExtractTextRegionsCore(pageIndex: null), regex);
    }

    /// <summary>
    /// Finds regex matches in extracted text regions on a specific page.
    /// </summary>
    public IReadOnlyList<PdfTextMatch> FindText(int pageIndex, string pattern, RegexOptions regexOptions = RegexOptions.None)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        Regex regex = new(pattern, regexOptions);
        return BuildTextMatches(ExtractTextRegionsCore(pageIndex), regex);
    }

    /// <summary>
    /// Serializes the document to PDF bytes using the provided save options.
    /// </summary>
    public byte[] Save(PdfSaveOptions? options = null)
    {
        PdfSaveOptions effectiveOptions = options ?? new PdfSaveOptions();
        if (!Enum.IsDefined(effectiveOptions.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Save Mode contains an unsupported value.");
        }

        if (!Enum.IsDefined(effectiveOptions.CrossReferenceStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Save CrossReferenceStyle contains an unsupported value.");
        }

        PdfSecurityOptions? securityToApply = effectiveOptions.Security;
        if (securityToApply is null && _openedEncrypted)
        {
            securityToApply = _openedSecurityOptions ?? throw new InvalidOperationException("Encrypted document cannot be saved because original security settings are unavailable.");
        }

        if (effectiveOptions.Mode == PdfSaveMode.Incremental)
        {
            if (_openedEncrypted)
            {
                PdfSecurityOptions incrementalSecurity = ResolveIncrementalEncryptedSecurityOptions(effectiveOptions.Security);
                return SaveIncrementalEncrypted(effectiveOptions.CrossReferenceStyle, incrementalSecurity);
            }

            if (effectiveOptions.Security is not null)
            {
                throw new NotSupportedException("Incremental save with security options is supported only for documents opened from encrypted PDFs.");
            }

            byte[] incrementalBytes = PdfFileWriter.WriteIncremental(_file, _dirtyObjectIds, effectiveOptions.CrossReferenceStyle);
            RebaseFromSavedBytes(incrementalBytes);
            return incrementalBytes;
        }

        if (securityToApply is not null)
        {
            ValidateSecurityOptions(securityToApply);
            PdfFile encryptedFile = PdfStandardSecurityProcessor.Encrypt(_file, securityToApply);
            byte[] encryptedBytes = PdfFileWriter.Write(encryptedFile, effectiveOptions.CrossReferenceStyle);
            _dirtyObjectIds.Clear();

            if (_openedEncrypted)
            {
                _openedEncryptedFile = PdfFileReader.Read(encryptedBytes);
                _openedSecurityOptions = securityToApply;
            }

            return encryptedBytes;
        }

        byte[] fullBytes = PdfFileWriter.Write(_file, effectiveOptions.CrossReferenceStyle);
        RebaseFromSavedBytes(fullBytes);
        return fullBytes;
    }

    /// <summary>
    /// Serializes the document and writes it to disk.
    /// </summary>
    public void Save(string path, PdfSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, Save(options));
    }

    /// <summary>
    /// Creates a detached signature placeholder, invokes <paramref name="signer"/>, and persists signed bytes.
    /// </summary>
    public byte[] SaveSignedDetached(Func<ReadOnlyMemory<byte>, byte[]> signer, PdfSignatureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(signer);

        if (_openedEncrypted)
        {
            throw new NotSupportedException("Detached signatures are not supported for documents opened from encrypted PDFs.");
        }

        PdfSignatureOptions effectiveOptions = options ?? new PdfSignatureOptions();
        ValidateSignatureOptions(effectiveOptions);

        PdfFile unsignedFile = BuildDetachedSignatureFile(effectiveOptions, out HashSet<PdfObjectId> dirtyObjectIds);
        byte[] unsignedBytes = PdfFileWriter.WriteIncremental(unsignedFile, dirtyObjectIds);
        byte[] signedBytes = ApplyDetachedSignature(unsignedBytes, signer);
        RebaseFromSavedBytes(signedBytes);
        return signedBytes;
    }

    /// <summary>
    /// Validates all detached signatures using default validation options.
    /// </summary>
    public IReadOnlyList<PdfDetachedSignatureValidationResult> ValidateDetachedSignatures(bool verifyCertificateChain = false)
    {
        return ValidateDetachedSignatures(
            new PdfDetachedSignatureValidationOptions
            {
                VerifyCertificateChain = verifyCertificateChain,
            });
    }

    /// <summary>
    /// Validates all detached signatures using the specified validation options.
    /// </summary>
    public IReadOnlyList<PdfDetachedSignatureValidationResult> ValidateDetachedSignatures(PdfDetachedSignatureValidationOptions? options)
    {
        PdfDetachedSignatureValidationOptions effectiveOptions = options ?? new PdfDetachedSignatureValidationOptions();
        ValidateSignatureValidationOptions(effectiveOptions);
        List<PdfDetachedSignatureValidationResult> results = [];
        DssValidationEvidence dssEvidence = ReadDssValidationEvidence();
        try
        {
            foreach (PdfIndirectObject indirectObject in _file.Objects.OrderBy(static objectItem => objectItem.ObjectId.ObjectNumber))
            {
                if (indirectObject.Value is not PdfDictionaryObject dictionary || !IsSignatureDictionary(dictionary))
                {
                    continue;
                }

                results.Add(ValidateDetachedSignature(indirectObject.ObjectId.ObjectNumber, dictionary, effectiveOptions, dssEvidence));
            }
        }
        finally
        {
            foreach (X509Certificate2 certificate in dssEvidence.Certificates)
            {
                certificate.Dispose();
            }
        }

        return results;
    }

    private PdfFile BuildDetachedSignatureFile(PdfSignatureOptions options, out HashSet<PdfObjectId> dirtyObjectIds)
    {
        if (_model.Pages.Count == 0)
        {
            throw new InvalidOperationException("At least one page is required before adding a detached signature.");
        }

        if (options.PageIndex < 0 || options.PageIndex >= _model.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Signature PageIndex is out of range.");
        }

        List<PdfIndirectObject> objects = [.. _file.Objects];
        int nextObjectNumber = GetNextObjectNumber(objects);
        PdfObjectId signatureId = new(nextObjectNumber++, 0);
        PdfObjectId widgetId = new(nextObjectNumber++, 0);
        dirtyObjectIds = [signatureId, widgetId];

        DateTimeOffset signingTime = options.SigningTime ?? DateTimeOffset.UtcNow;
        List<PdfDictionaryEntry> signatureEntries =
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Sig")),
            new PdfDictionaryEntry("Filter", new PdfNameObject(options.Filter)),
            new PdfDictionaryEntry("SubFilter", new PdfNameObject(options.SubFilter)),
            new PdfDictionaryEntry(
                "ByteRange",
                new PdfArrayObject(
                [
                    CreateByteRangePlaceholderNumber(),
                    CreateByteRangePlaceholderNumber(),
                    CreateByteRangePlaceholderNumber(),
                    CreateByteRangePlaceholderNumber(),
                ])),
            new PdfDictionaryEntry("Contents", new PdfByteStringObject(new byte[options.ContentsByteLength])),
            new PdfDictionaryEntry("M", new PdfStringObject(FormatPdfDate(signingTime))),
        ];

        AddOptionalSignatureString(signatureEntries, "Name", options.Name);
        AddOptionalSignatureString(signatureEntries, "Reason", options.Reason);
        AddOptionalSignatureString(signatureEntries, "Location", options.Location);
        AddOptionalSignatureString(signatureEntries, "ContactInfo", options.ContactInfo);

        PdfDictionaryObject signatureDictionary = new(signatureEntries);
        objects.Add(new PdfIndirectObject(signatureId, signatureDictionary));

        PdfPageModel page = _model.Pages[options.PageIndex];
        PdfDictionaryObject pageDictionary = RequireDictionaryObject(page.ObjectId, "Page");
        PdfArrayObject rect = new(
        [
            new PdfNumberObject(options.X, isInteger: false),
            new PdfNumberObject(options.Y, isInteger: false),
            new PdfNumberObject(options.X + options.Width, isInteger: false),
            new PdfNumberObject(options.Y + options.Height, isInteger: false),
        ]);

        PdfDictionaryObject widgetDictionary = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Annot")),
            new PdfDictionaryEntry("Subtype", new PdfNameObject("Widget")),
            new PdfDictionaryEntry("FT", new PdfNameObject("Sig")),
            new PdfDictionaryEntry("T", new PdfStringObject(options.FieldName)),
            new PdfDictionaryEntry("Rect", rect),
            new PdfDictionaryEntry("V", new PdfReferenceObject(signatureId)),
            new PdfDictionaryEntry("F", new PdfNumberObject(132, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfReferenceObject(page.ObjectId)),
        ]);

        objects.Add(new PdfIndirectObject(widgetId, widgetDictionary));
        AppendWidgetToPageAnnotations(objects, pageDictionary, page.ObjectId, widgetId, dirtyObjectIds);
        AttachWidgetToAcroForm(objects, widgetId, ref nextObjectNumber, dirtyObjectIds);

        return new PdfFile(
            _file.Version,
            objects,
            _file.Trailer,
            _file.SourceBytes,
            _file.StartXrefOffset,
            _file.XrefEntries);
    }

    private static void AddOptionalSignatureString(List<PdfDictionaryEntry> entries, string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        entries.Add(new PdfDictionaryEntry(key, new PdfStringObject(value)));
    }

    private static PdfNumberObject CreateByteRangePlaceholderNumber()
    {
        return new PdfNumberObject(9999999999, isInteger: true);
    }

    private void AppendWidgetToPageAnnotations(
        List<PdfIndirectObject> objects,
        PdfDictionaryObject pageDictionary,
        PdfObjectId pageObjectId,
        PdfObjectId widgetId,
        HashSet<PdfObjectId> dirtyObjectIds)
    {
        PdfReferenceObject widgetReference = new(widgetId);
        if (TryGetDictionaryEntry(pageDictionary, "Annots", out PdfObject? annotsObject))
        {
            switch (annotsObject)
            {
                case PdfArrayObject annotsArray:
                {
                    PdfArrayObject updatedAnnots = new([.. annotsArray.Items, widgetReference]);
                    PdfDictionaryObject updatedPage = ReplaceDictionaryEntries(
                        pageDictionary,
                        new PdfDictionaryEntry("Annots", updatedAnnots));
                    ReplaceObject(objects, pageObjectId, updatedPage);
                    dirtyObjectIds.Add(pageObjectId);
                    return;
                }
                case PdfReferenceObject annotsReference:
                {
                    PdfArrayObject annotsArray = RequireArrayObject(annotsReference.ObjectId, "Page annotations");
                    PdfArrayObject updatedAnnots = new([.. annotsArray.Items, widgetReference]);
                    ReplaceObject(objects, annotsReference.ObjectId, updatedAnnots);
                    dirtyObjectIds.Add(annotsReference.ObjectId);
                    return;
                }
                default:
                    throw new NotSupportedException("Page /Annots must be an array or an array reference.");
            }
        }

        PdfDictionaryObject pageWithAnnots = ReplaceDictionaryEntries(
            pageDictionary,
            new PdfDictionaryEntry("Annots", new PdfArrayObject([widgetReference])));
        ReplaceObject(objects, pageObjectId, pageWithAnnots);
        dirtyObjectIds.Add(pageObjectId);
    }

    private void AttachWidgetToAcroForm(
        List<PdfIndirectObject> objects,
        PdfObjectId widgetId,
        ref int nextObjectNumber,
        HashSet<PdfObjectId> dirtyObjectIds)
    {
        PdfDictionaryObject catalog = RequireDictionaryObject(_model.CatalogObjectId, "Catalog");
        PdfReferenceObject widgetReference = new(widgetId);
        if (!TryGetDictionaryEntry(catalog, "AcroForm", out PdfObject? acroFormObject))
        {
            PdfObjectId acroFormId = new(nextObjectNumber++, 0);
            PdfDictionaryObject acroFormDictionary = new(
            [
                new PdfDictionaryEntry("Fields", new PdfArrayObject([widgetReference])),
                new PdfDictionaryEntry("SigFlags", new PdfNumberObject(3, isInteger: true)),
            ]);
            objects.Add(new PdfIndirectObject(acroFormId, acroFormDictionary));

            PdfDictionaryObject updatedCatalog = ReplaceDictionaryEntries(
                catalog,
                new PdfDictionaryEntry("AcroForm", new PdfReferenceObject(acroFormId)));
            ReplaceObject(objects, _model.CatalogObjectId, updatedCatalog);
            dirtyObjectIds.Add(acroFormId);
            dirtyObjectIds.Add(_model.CatalogObjectId);
            return;
        }

        if (acroFormObject is PdfReferenceObject acroFormReference)
        {
            PdfDictionaryObject acroFormDictionary = RequireDictionaryObject(acroFormReference.ObjectId, "AcroForm");
            PdfDictionaryObject updatedAcroForm = AddWidgetToAcroFormDictionary(
                acroFormDictionary,
                widgetReference,
                objects,
                dirtyObjectIds);
            ReplaceObject(objects, acroFormReference.ObjectId, updatedAcroForm);
            dirtyObjectIds.Add(acroFormReference.ObjectId);
            return;
        }

        if (acroFormObject is PdfDictionaryObject directAcroForm)
        {
            PdfDictionaryObject updatedAcroForm = AddWidgetToAcroFormDictionary(
                directAcroForm,
                widgetReference,
                objects,
                dirtyObjectIds);
            PdfDictionaryObject updatedCatalog = ReplaceDictionaryEntries(
                catalog,
                new PdfDictionaryEntry("AcroForm", updatedAcroForm));
            ReplaceObject(objects, _model.CatalogObjectId, updatedCatalog);
            dirtyObjectIds.Add(_model.CatalogObjectId);
            return;
        }

        throw new NotSupportedException("Catalog /AcroForm must be a dictionary or dictionary reference.");
    }

    private static PdfDictionaryObject AddWidgetToAcroFormDictionary(
        PdfDictionaryObject acroFormDictionary,
        PdfReferenceObject widgetReference,
        List<PdfIndirectObject> objects,
        HashSet<PdfObjectId> dirtyObjectIds)
    {
        PdfObject fieldsObject;
        if (TryGetDictionaryEntry(acroFormDictionary, "Fields", out PdfObject? existingFields))
        {
            switch (existingFields)
            {
                case PdfArrayObject fieldsArray:
                    fieldsObject = new PdfArrayObject([.. fieldsArray.Items, widgetReference]);
                    break;
                case PdfReferenceObject fieldsReference:
                {
                    PdfArrayObject fieldsArray = RequireArrayObjectFromList(objects, fieldsReference.ObjectId, "AcroForm fields");
                    PdfArrayObject updatedFields = new([.. fieldsArray.Items, widgetReference]);
                    ReplaceObject(objects, fieldsReference.ObjectId, updatedFields);
                    dirtyObjectIds.Add(fieldsReference.ObjectId);
                    fieldsObject = fieldsReference;
                    break;
                }
                default:
                    throw new NotSupportedException("AcroForm /Fields must be an array or an array reference.");
            }
        }
        else
        {
            fieldsObject = new PdfArrayObject([widgetReference]);
        }

        int sigFlags = 3;
        if (TryGetDictionaryEntry(acroFormDictionary, "SigFlags", out PdfObject? existingSigFlags)
            && existingSigFlags is PdfNumberObject flagsNumber
            && flagsNumber.IsInteger)
        {
            sigFlags |= Convert.ToInt32(flagsNumber.Value, CultureInfo.InvariantCulture);
        }

        return ReplaceDictionaryEntries(
            acroFormDictionary,
            new PdfDictionaryEntry("Fields", fieldsObject),
            new PdfDictionaryEntry("SigFlags", new PdfNumberObject(sigFlags, isInteger: true)));
    }

    private static PdfArrayObject RequireArrayObjectFromList(
        IReadOnlyList<PdfIndirectObject> objects,
        PdfObjectId objectId,
        string context)
    {
        foreach (PdfIndirectObject indirectObject in objects)
        {
            if (indirectObject.ObjectId == objectId)
            {
                return indirectObject.Value as PdfArrayObject
                    ?? throw new PdfFormatException($"{context} object {objectId} is not an array.");
            }
        }

        throw new PdfFormatException($"{context} object {objectId} was not found.");
    }

    private static byte[] ApplyDetachedSignature(byte[] unsignedBytes, Func<ReadOnlyMemory<byte>, byte[]> signer)
    {
        string text = Encoding.ASCII.GetString(unsignedBytes);
        const string typeMarker = "/Type /Sig";
        int signatureTypeIndex = text.LastIndexOf(typeMarker, StringComparison.Ordinal);
        if (signatureTypeIndex < 0)
        {
            throw new PdfFormatException("Could not locate signature dictionary.");
        }

        int signatureEndIndex = text.IndexOf("endobj", signatureTypeIndex, StringComparison.Ordinal);
        if (signatureEndIndex < 0)
        {
            throw new PdfFormatException("Could not locate end of signature dictionary object.");
        }

        const string byteRangeMarker = "/ByteRange [";
        int byteRangeIndex = text.IndexOf(byteRangeMarker, signatureTypeIndex, StringComparison.Ordinal);
        if (byteRangeIndex < 0 || byteRangeIndex > signatureEndIndex)
        {
            throw new PdfFormatException("Could not locate signature /ByteRange entry.");
        }

        const string contentsMarker = "/Contents <";
        int contentsIndex = text.IndexOf(contentsMarker, signatureTypeIndex, StringComparison.Ordinal);
        if (contentsIndex < 0 || contentsIndex > signatureEndIndex)
        {
            throw new PdfFormatException("Could not locate signature /Contents entry.");
        }

        int contentsHexStart = contentsIndex + contentsMarker.Length;
        int contentsHexEnd = text.IndexOf('>', contentsHexStart);
        if (contentsHexEnd < 0 || contentsHexEnd > signatureEndIndex)
        {
            throw new PdfFormatException("Could not locate end of signature /Contents hex string.");
        }

        int contentsHexLength = contentsHexEnd - contentsHexStart;
        if ((contentsHexLength & 1) == 1)
        {
            throw new PdfFormatException("Signature /Contents placeholder length must be even.");
        }

        List<(int Start, int Length)> byteRangeTokenSlots = ParseByteRangeTokenSlots(text, byteRangeIndex + byteRangeMarker.Length);
        long[] byteRangeValues =
        [
            0,
            contentsHexStart - 1,
            contentsHexEnd + 1,
            unsignedBytes.LongLength - (contentsHexEnd + 1L),
        ];

        for (int index = 0; index < byteRangeTokenSlots.Count; index++)
        {
            WriteFixedWidthNumber(unsignedBytes, byteRangeTokenSlots[index], byteRangeValues[index]);
        }

        int firstRangeLength = checked((int)byteRangeValues[1]);
        int secondRangeStart = checked((int)byteRangeValues[2]);
        int secondRangeLength = checked((int)byteRangeValues[3]);

        byte[] signedPayload = new byte[firstRangeLength + secondRangeLength];
        Buffer.BlockCopy(unsignedBytes, 0, signedPayload, 0, firstRangeLength);
        Buffer.BlockCopy(unsignedBytes, secondRangeStart, signedPayload, firstRangeLength, secondRangeLength);

        byte[] signatureBytes = signer(signedPayload) ?? throw new InvalidOperationException("Detached signature callback returned null.");
        int placeholderBytesLength = contentsHexLength / 2;
        if (signatureBytes.Length > placeholderBytesLength)
        {
            throw new ArgumentException(
                $"Detached signature callback produced {signatureBytes.Length} bytes, which exceeds configured placeholder size {placeholderBytesLength} bytes.",
                nameof(signer));
        }

        string signatureHex = Convert.ToHexString(signatureBytes);
        string paddedHex = signatureHex.PadRight(contentsHexLength, '0');
        Encoding.ASCII.GetBytes(paddedHex, 0, paddedHex.Length, unsignedBytes, contentsHexStart);
        return unsignedBytes;
    }

    private PdfDetachedSignatureValidationResult ValidateDetachedSignature(
        int signatureObjectNumber,
        PdfDictionaryObject dictionary,
        PdfDetachedSignatureValidationOptions options,
        DssValidationEvidence dssEvidence)
    {
        string? filter = TryReadNameEntry(dictionary, "Filter");
        string? subFilter = TryReadNameEntry(dictionary, "SubFilter");
        if (!IsSupportedCmsSubFilter(subFilter))
        {
            return CreateInvalidSignatureResult(
                signatureObjectNumber,
                filter,
                subFilter,
                "Only CMS signatures with /SubFilter /adbe.pkcs7.detached, /ETSI.CAdES.detached, /adbe.pkcs7.sha1, or /ETSI.RFC3161 are currently supported.");
        }

        if (_file.SourceBytes is null)
        {
            return CreateInvalidSignatureResult(
                signatureObjectNumber,
                filter,
                subFilter,
                "Signature validation requires source bytes from an opened or saved document.");
        }

        if (!TryReadByteRange(dictionary, _file.SourceBytes.Length, out (int Start, int Length) firstRange, out (int Start, int Length) secondRange, out string? byteRangeError))
        {
            return CreateInvalidSignatureResult(signatureObjectNumber, filter, subFilter, byteRangeError);
        }

        if (!TryReadCmsBytes(dictionary, out byte[]? cmsBytes, out string? cmsError))
        {
            return CreateInvalidSignatureResult(signatureObjectNumber, filter, subFilter, cmsError);
        }
        byte[] cmsSignatureBytes = cmsBytes!;

        byte[] signedPayload = new byte[firstRange.Length + secondRange.Length];
        Buffer.BlockCopy(_file.SourceBytes, firstRange.Start, signedPayload, 0, firstRange.Length);
        Buffer.BlockCopy(_file.SourceBytes, secondRange.Start, signedPayload, firstRange.Length, secondRange.Length);

        try
        {
            SignedCms signedCms = ValidateCmsSignature(cmsSignatureBytes, signedPayload, subFilter!, out DateTimeOffset? timestampSigningTime);

            if (signedCms.SignerInfos.Count == 0)
            {
                return CreateInvalidSignatureResult(
                    signatureObjectNumber,
                    filter,
                    subFilter,
                    "CMS signature does not contain signer information.");
            }

            List<string> diagnostics = [];
            if (!string.IsNullOrWhiteSpace(dssEvidence.Diagnostic))
            {
                diagnostics.Add(dssEvidence.Diagnostic);
            }
            DateTimeOffset? signingTime = timestampSigningTime ?? TryReadFirstSigningTime(signedCms);
            bool? signingTimeValid = null;
            bool? certificateChainValid = null;
            bool? revocationValid = null;
            bool? certificatePolicyValid = null;
            bool trustChecksPassed = true;
            (IReadOnlyList<CrlEvidence> scopedCrls, IReadOnlyList<OcspEvidence> scopedOcsps) =
                ResolveSignatureScopedRevocationEvidence(cmsSignatureBytes, dssEvidence, diagnostics);

            if (options.RequireSigningTime)
            {
                if (!signingTime.HasValue)
                {
                    trustChecksPassed = false;
                    signingTimeValid = false;
                    diagnostics.Add("Signing time is required but missing from CMS signed attributes.");
                }
                else
                {
                    signingTimeValid = IsSigningTimeWithinSignerCertificateValidity(signedCms, signingTime.Value);
                    if (signingTimeValid is false)
                    {
                        trustChecksPassed = false;
                        diagnostics.Add("Signing time is outside signer certificate validity.");
                    }
                }
            }

            bool checkChain = options.VerifyCertificateChain || options.RequireRevocationStatus;
            if (checkChain)
            {
                (bool chainIsValid, bool revocationIsValid, List<string> chainDiagnostics) = EvaluateSignerChains(
                    signedCms,
                    options,
                    signingTime,
                    dssEvidence.Certificates,
                    scopedCrls,
                    scopedOcsps);
                diagnostics.AddRange(chainDiagnostics);
                certificateChainValid = chainIsValid;
                revocationValid = options.RequireRevocationStatus ? revocationIsValid : null;
                if (!chainIsValid || (options.RequireRevocationStatus && !revocationIsValid))
                {
                    trustChecksPassed = false;
                }
            }

            if (options.RequiredCertificatePolicyOids is { Count: > 0 })
            {
                bool policyValid = EvaluateSignerPolicies(signedCms, options.RequiredCertificatePolicyOids, diagnostics);
                certificatePolicyValid = policyValid;
                if (!policyValid)
                {
                    trustChecksPassed = false;
                }
            }

            string? failureReason = trustChecksPassed ? null : diagnostics.FirstOrDefault() ?? "Detached signature trust checks failed.";
            return new PdfDetachedSignatureValidationResult(
                signatureObjectNumber,
                filter,
                subFilter,
                isValid: trustChecksPassed,
                cryptographicallyValid: true,
                trustChecksPassed: trustChecksPassed,
                signerCount: signedCms.SignerInfos.Count,
                failureReason: failureReason,
                certificateChainValid: certificateChainValid,
                revocationValid: revocationValid,
                signingTimeValid: signingTimeValid,
                signingTime: signingTime,
                certificatePolicyValid: certificatePolicyValid,
                diagnostics: diagnostics);
        }
        catch (CryptographicException exception)
        {
            return CreateInvalidSignatureResult(
                signatureObjectNumber,
                filter,
                subFilter,
                $"Cryptographic signature validation failed: {exception.Message}");
        }
    }

    private static PdfDetachedSignatureValidationResult CreateInvalidSignatureResult(
        int signatureObjectNumber,
        string? filter,
        string? subFilter,
        string? reason)
    {
        return new PdfDetachedSignatureValidationResult(
            signatureObjectNumber,
            filter,
            subFilter,
            isValid: false,
            cryptographicallyValid: false,
            trustChecksPassed: false,
            signerCount: 0,
            failureReason: reason ?? "Signature validation failed.",
            diagnostics: reason is null ? null : [reason]);
    }

    private static SignedCms ValidateCmsSignature(byte[] cmsSignatureBytes, byte[] signedPayload, string subFilter, out DateTimeOffset? timestampSigningTime)
    {
        timestampSigningTime = null;
        return subFilter switch
        {
            "adbe.pkcs7.sha1" => ValidatePkcs7Sha1Signature(cmsSignatureBytes, signedPayload),
            "ETSI.RFC3161" => ValidateRfc3161TimestampSignature(cmsSignatureBytes, signedPayload, out timestampSigningTime),
            _ => ValidateDetachedCmsSignature(cmsSignatureBytes, signedPayload),
        };
    }

    private static SignedCms ValidateDetachedCmsSignature(byte[] cmsSignatureBytes, byte[] signedPayload)
    {
        SignedCms signedCms = new(new ContentInfo(signedPayload), detached: true);
        signedCms.Decode(cmsSignatureBytes);
        signedCms.CheckSignature(verifySignatureOnly: true);
        return signedCms;
    }

    private static SignedCms ValidatePkcs7Sha1Signature(byte[] cmsSignatureBytes, byte[] signedPayload)
    {
        SignedCms signedCms = new();
        signedCms.Decode(cmsSignatureBytes);

        if (signedCms.Detached)
        {
            SignedCms detachedSha1 = new(new ContentInfo(signedPayload), detached: true);
            detachedSha1.Decode(cmsSignatureBytes);
            detachedSha1.CheckSignature(verifySignatureOnly: true);
            return detachedSha1;
        }

        signedCms.CheckSignature(verifySignatureOnly: true);
#pragma warning disable CA5350 // adbe.pkcs7.sha1 compatibility requires SHA-1 digest comparison
        byte[] expectedDigest = SHA1.HashData(signedPayload);
#pragma warning restore CA5350
        if (!signedCms.ContentInfo.Content.AsSpan().SequenceEqual(expectedDigest))
        {
            throw new CryptographicException("Signature /SubFilter /adbe.pkcs7.sha1 CMS payload digest does not match the signed /ByteRange content.");
        }

        return signedCms;
    }

    private static SignedCms ValidateRfc3161TimestampSignature(byte[] cmsSignatureBytes, byte[] signedPayload, out DateTimeOffset? timestampSigningTime)
    {
        SignedCms signedCms = new();
        signedCms.Decode(cmsSignatureBytes);
        if (signedCms.Detached)
        {
            throw new CryptographicException("RFC3161 timestamp CMS must include embedded TSTInfo content.");
        }

        signedCms.CheckSignature(verifySignatureOnly: true);
        if (!string.Equals(signedCms.ContentInfo.ContentType.Value, "1.2.840.113549.1.9.16.1.4", StringComparison.Ordinal))
        {
            throw new CryptographicException("RFC3161 timestamp CMS content type must be id-ct-TSTInfo.");
        }

        byte[] tstInfoBytes = signedCms.ContentInfo.Content;
        if (!TryReadRfc3161MessageImprint(tstInfoBytes, out string? hashAlgorithmOid, out byte[]? messageImprint, out DateTimeOffset generatedTime, out string? parseError))
        {
            throw new CryptographicException(parseError ?? "RFC3161 timestamp token TSTInfo payload is malformed.");
        }

        byte[] payloadDigest = ComputeDigestForOid(hashAlgorithmOid!, signedPayload);
        if (!CryptographicOperations.FixedTimeEquals(payloadDigest, messageImprint!))
        {
            throw new CryptographicException("RFC3161 message imprint digest does not match the signed /ByteRange content.");
        }

        timestampSigningTime = generatedTime;
        return signedCms;
    }

    private static bool TryReadRfc3161MessageImprint(
        ReadOnlySpan<byte> tstInfoBytes,
        out string? hashAlgorithmOid,
        out byte[]? messageImprint,
        out DateTimeOffset generatedTime,
        out string? error)
    {
        hashAlgorithmOid = null;
        messageImprint = null;
        generatedTime = default;
        error = null;

        try
        {
            AsnReader reader = new(tstInfoBytes.ToArray(), AsnEncodingRules.DER);
            AsnReader tstInfo = reader.ReadSequence();
            _ = tstInfo.ReadInteger();
            _ = tstInfo.ReadObjectIdentifier();

            AsnReader messageImprintReader = tstInfo.ReadSequence();
            AsnReader algorithmIdentifier = messageImprintReader.ReadSequence();
            hashAlgorithmOid = algorithmIdentifier.ReadObjectIdentifier();
            while (algorithmIdentifier.HasData)
            {
                _ = algorithmIdentifier.ReadEncodedValue();
            }

            messageImprint = messageImprintReader.ReadOctetString();
            messageImprintReader.ThrowIfNotEmpty();

            _ = tstInfo.ReadInteger();
            generatedTime = tstInfo.ReadGeneralizedTime();

            reader.ThrowIfNotEmpty();
            return true;
        }
        catch (AsnContentException)
        {
            error = "RFC3161 timestamp token TSTInfo payload is malformed.";
            return false;
        }
    }

    private static byte[] ComputeDigestForOid(string hashAlgorithmOid, ReadOnlySpan<byte> payload)
    {
        return hashAlgorithmOid switch
        {
            "2.16.840.1.101.3.4.2.1" => SHA256.HashData(payload),
            "2.16.840.1.101.3.4.2.2" => SHA384.HashData(payload),
            "2.16.840.1.101.3.4.2.3" => SHA512.HashData(payload),
#pragma warning disable CA5350 // RFC3161 compatibility may require SHA-1 message imprint verification
            "1.3.14.3.2.26" => SHA1.HashData(payload),
#pragma warning restore CA5350
            _ => throw new CryptographicException($"RFC3161 timestamp token uses unsupported message imprint algorithm OID '{hashAlgorithmOid}'."),
        };
    }

    private static DateTimeOffset? TryReadFirstSigningTime(SignedCms signedCms)
    {
        foreach (SignerInfo signerInfo in signedCms.SignerInfos)
        {
            if (TryReadSigningTime(signerInfo, out DateTimeOffset signingTime))
            {
                return signingTime;
            }
        }

        return null;
    }

    private static bool TryReadSigningTime(SignerInfo signerInfo, out DateTimeOffset signingTime)
    {
        foreach (CryptographicAttributeObject attribute in signerInfo.SignedAttributes)
        {
            if (!string.Equals(attribute.Oid?.Value, "1.2.840.113549.1.9.5", StringComparison.Ordinal) || attribute.Values.Count == 0)
            {
                continue;
            }

            try
            {
                Pkcs9SigningTime pkcsSigningTime = new(attribute.Values[0].RawData);
                signingTime = pkcsSigningTime.SigningTime;
                return true;
            }
            catch (CryptographicException)
            {
                break;
            }
        }

        signingTime = default;
        return false;
    }

    private static bool IsSigningTimeWithinSignerCertificateValidity(SignedCms signedCms, DateTimeOffset signingTime)
    {
        bool hasCertificate = false;
        foreach (SignerInfo signerInfo in signedCms.SignerInfos)
        {
            X509Certificate2? certificate = signerInfo.Certificate;
            if (certificate is null)
            {
                continue;
            }

            hasCertificate = true;
            if (signingTime.UtcDateTime < certificate.NotBefore || signingTime.UtcDateTime > certificate.NotAfter)
            {
                return false;
            }
        }

        return hasCertificate;
    }

    private static (bool ChainValid, bool RevocationValid, List<string> Diagnostics) EvaluateSignerChains(
        SignedCms signedCms,
        PdfDetachedSignatureValidationOptions options,
        DateTimeOffset? signingTime,
        IReadOnlyList<X509Certificate2> dssCertificates,
        IReadOnlyList<CrlEvidence> dssCrls,
        IReadOnlyList<OcspEvidence> dssOcsps)
    {
        bool chainValid = true;
        bool revocationValid = true;
        List<string> diagnostics = [];
        DateTimeOffset verificationMoment = options.ValidationTime ?? signingTime ?? DateTimeOffset.UtcNow;

        foreach (SignerInfo signerInfo in signedCms.SignerInfos)
        {
            X509Certificate2? certificate = signerInfo.Certificate;
            if (certificate is null)
            {
                chainValid = false;
                diagnostics.Add("Signer certificate is missing from CMS payload.");
                if (options.RequireRevocationStatus)
                {
                    revocationValid = false;
                }

                continue;
            }

            using X509Chain chain = new();
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.VerificationTime = verificationMoment.UtcDateTime;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
            chain.ChainPolicy.RevocationMode = options.RequireRevocationStatus
                ? options.RevocationCheckMode switch
                {
                    PdfRevocationCheckMode.Online => X509RevocationMode.Online,
                    _ => X509RevocationMode.Offline,
                }
                : X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = !(options.RequireRevocationStatus && options.RevocationCheckMode == PdfRevocationCheckMode.Online);

            if (options.TrustedRoots is { Count: > 0 })
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                foreach (X509Certificate2 trustedRoot in options.TrustedRoots)
                {
                    chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
                }
            }

            foreach (X509Certificate2 cmsCertificate in signedCms.Certificates)
            {
                chain.ChainPolicy.ExtraStore.Add(cmsCertificate);
            }

            foreach (X509Certificate2 dssCertificate in dssCertificates)
            {
                chain.ChainPolicy.ExtraStore.Add(dssCertificate);
            }

            bool signerChainValid = chain.Build(certificate);
            List<string> nonRevocationDiagnostics = chain.ChainStatus
                .Where(static status => status.Status != X509ChainStatusFlags.NoError && !IsRevocationChainStatus(status.Status))
                .Select(static status => $"Certificate chain status: {status.Status} ({status.StatusInformation.Trim()})")
                .ToList();
            bool signerChainValidIgnoringRevocation = nonRevocationDiagnostics.Count == 0;
            chainValid &= signerChainValidIgnoringRevocation;
            if (!signerChainValidIgnoringRevocation)
            {
                diagnostics.AddRange(nonRevocationDiagnostics);
            }

            if (options.RequireRevocationStatus)
            {
                bool hasRevocationPointers = certificate.Extensions["2.5.29.31"] is not null
                    || certificate.Extensions["1.3.6.1.5.5.7.1.1"] is not null;
                bool hasRevocationChainFailure = chain.ChainStatus.Any(static status => IsRevocationChainStatus(status.Status));
                bool signerRevocationValid;
                if (!signerChainValidIgnoringRevocation)
                {
                    signerRevocationValid = false;
                }
                else if (!hasRevocationChainFailure && hasRevocationPointers)
                {
                    signerRevocationValid = true;
                }
                else if (options.RevocationCheckMode == PdfRevocationCheckMode.Offline)
                {
                    signerRevocationValid = TryValidateRevocationWithOfflineDss(chain, dssCrls, dssOcsps, dssCertificates, verificationMoment, diagnostics);
                }
                else
                {
                    signerRevocationValid = false;
                }

                revocationValid &= signerRevocationValid;
                if (!signerRevocationValid)
                {
                    diagnostics.Add(options.RevocationCheckMode == PdfRevocationCheckMode.Online
                        ? hasRevocationPointers
                            ? "Revocation status could not be established for all certificates in the signer chain using online OCSP/CRL retrieval."
                            : "Revocation status validation requires CRL or AIA certificate extensions."
                        : "Revocation status could not be established for all certificates in the signer chain using offline evidence.");
                }
            }
        }

        return (chainValid, revocationValid, diagnostics);
    }

    private static (IReadOnlyList<CrlEvidence> Crls, IReadOnlyList<OcspEvidence> Ocsps) ResolveSignatureScopedRevocationEvidence(
        byte[] cmsSignatureBytes,
        DssValidationEvidence dssEvidence,
        List<string> diagnostics)
    {
        if (dssEvidence.VriEvidenceByKey.Count == 0)
        {
            return (dssEvidence.Crls, dssEvidence.Ocsps);
        }

        List<string> lookupKeys = GetSignatureVriLookupKeys(cmsSignatureBytes);
        foreach (string lookupKey in lookupKeys)
        {
            if (!dssEvidence.VriEvidenceByKey.TryGetValue(lookupKey, out DssVriEvidence scopedEvidence))
            {
                continue;
            }

            return (scopedEvidence.Crls, scopedEvidence.Ocsps);
        }

        diagnostics.Add($"No /DSS /VRI entry matched signature digest key(s): {string.Join(", ", lookupKeys)}.");
        return ([], []);
    }

    private static List<string> GetSignatureVriLookupKeys(ReadOnlySpan<byte> cmsSignatureBytes)
    {
        List<string> keys = [];
#pragma warning disable CA5350 // DSS /VRI interoperability may use SHA-1 digest keys
        keys.Add(Convert.ToHexString(SHA1.HashData(cmsSignatureBytes)));
#pragma warning restore CA5350
        keys.Add(Convert.ToHexString(SHA256.HashData(cmsSignatureBytes)));
        return keys;
    }

    private static string NormalizeDssVriKey(string key)
    {
        return key.Trim().TrimStart('/').ToUpperInvariant();
    }

    private static bool IsRevocationChainStatus(X509ChainStatusFlags status)
    {
        return status is X509ChainStatusFlags.Revoked
            or X509ChainStatusFlags.RevocationStatusUnknown
            or X509ChainStatusFlags.OfflineRevocation
            or X509ChainStatusFlags.NoIssuanceChainPolicy;
    }

    private static bool TryValidateRevocationWithOfflineDss(
        X509Chain chain,
        IReadOnlyList<CrlEvidence> dssCrls,
        IReadOnlyList<OcspEvidence> dssOcsps,
        IReadOnlyList<X509Certificate2> dssCertificates,
        DateTimeOffset verificationMoment,
        List<string> diagnostics)
    {
        if (dssCrls.Count == 0 && dssOcsps.Count == 0)
        {
            diagnostics.Add("No DSS /CRLs or /OCSPs evidence is available for offline revocation validation.");
            return false;
        }

        if (chain.ChainElements.Count <= 1)
        {
            return true;
        }

        for (int index = 0; index < chain.ChainElements.Count - 1; index++)
        {
            X509Certificate2 certificate = chain.ChainElements[index].Certificate;
            X509Certificate2 issuerCertificate = chain.ChainElements[index + 1].Certificate;
            OcspValidationStatus ocspStatus = ValidateCertificateWithOfflineOcsp(certificate, issuerCertificate, verificationMoment, dssOcsps, dssCertificates, diagnostics);
            if (ocspStatus == OcspValidationStatus.Valid)
            {
                continue;
            }

            if (ocspStatus == OcspValidationStatus.Invalid)
            {
                return false;
            }

            if (!TryFindValidCrlForCertificate(
                certificate,
                issuerCertificate,
                verificationMoment,
                dssCrls,
                out CrlEvidence crlEvidence,
                out string? crlError))
            {
                diagnostics.Add(crlError ?? $"No applicable DSS CRL was found for certificate '{certificate.Subject}'.");
                return false;
            }

            string certificateSerial = NormalizeSerialHex(certificate.GetSerialNumber().Reverse().ToArray());
            if (crlEvidence.RevokedSerialNumbers.Contains(certificateSerial))
            {
                diagnostics.Add($"Certificate '{certificate.Subject}' is revoked according to embedded DSS CRL evidence.");
                return false;
            }
        }

        return true;
    }

    private static OcspValidationStatus ValidateCertificateWithOfflineOcsp(
        X509Certificate2 certificate,
        X509Certificate2 issuerCertificate,
        DateTimeOffset verificationMoment,
        IReadOnlyList<OcspEvidence> dssOcsps,
        IReadOnlyList<X509Certificate2> dssCertificates,
        List<string> diagnostics)
    {
        if (dssOcsps.Count == 0)
        {
            return OcspValidationStatus.NoEvidence;
        }

        string serialHex = NormalizeSerialHex(certificate.GetSerialNumber().Reverse().ToArray());
        List<(OcspEvidence Evidence, OcspSingleResponseEvidence Response)> candidates = [];
        foreach (OcspEvidence ocspEvidence in dssOcsps)
        {
            foreach (OcspSingleResponseEvidence singleResponse in ocspEvidence.Responses)
            {
                if (singleResponse.SerialNumberHex != serialHex)
                {
                    continue;
                }

                if (IsOcspSingleResponseMatch(singleResponse, certificate, issuerCertificate))
                {
                    candidates.Add((ocspEvidence, singleResponse));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return OcspValidationStatus.NoEvidence;
        }

        foreach ((OcspEvidence evidence, OcspSingleResponseEvidence response) in candidates
            .OrderByDescending(static candidate => candidate.Evidence.ProducedAt)
            .ThenByDescending(static candidate => candidate.Response.ThisUpdate))
        {
            if (!VerifyOcspSignature(evidence, issuerCertificate, verificationMoment, dssCertificates, out string? signatureError))
            {
                diagnostics.Add(signatureError ?? $"DSS OCSP response signature verification failed for issuer '{issuerCertificate.Subject}'.");
                continue;
            }

            if (evidence.ProducedAt > verificationMoment)
            {
                diagnostics.Add($"DSS OCSP response produced at '{evidence.ProducedAt:O}' is newer than validation time '{verificationMoment:O}'.");
                continue;
            }

            if (response.ThisUpdate > verificationMoment)
            {
                diagnostics.Add($"DSS OCSP response thisUpdate '{response.ThisUpdate:O}' is newer than validation time '{verificationMoment:O}'.");
                continue;
            }

            if (response.NextUpdate is DateTimeOffset nextUpdate && nextUpdate < verificationMoment)
            {
                diagnostics.Add($"DSS OCSP response expired at '{nextUpdate:O}' before validation time '{verificationMoment:O}'.");
                continue;
            }

            switch (response.CertStatus)
            {
                case OcspCertStatus.Good:
                    return OcspValidationStatus.Valid;
                case OcspCertStatus.Revoked:
                    diagnostics.Add($"Certificate '{certificate.Subject}' is revoked according to embedded DSS OCSP evidence.");
                    return OcspValidationStatus.Invalid;
                default:
                    diagnostics.Add($"Certificate '{certificate.Subject}' has unknown status in embedded DSS OCSP evidence.");
                    return OcspValidationStatus.Invalid;
            }
        }

        diagnostics.Add($"No fresh, verifiable DSS OCSP response was found for certificate '{certificate.Subject}'.");
        return OcspValidationStatus.Invalid;
    }

    private static bool IsOcspSingleResponseMatch(
        OcspSingleResponseEvidence response,
        X509Certificate2 certificate,
        X509Certificate2 issuerCertificate)
    {
        try
        {
            byte[] expectedIssuerNameHash = ComputeDigestForOid(response.CertIdHashAlgorithmOid, issuerCertificate.SubjectName.RawData);
            if (!expectedIssuerNameHash.AsSpan().SequenceEqual(response.IssuerNameHash))
            {
                return false;
            }

            byte[] expectedIssuerKeyHash = ComputeDigestForOid(response.CertIdHashAlgorithmOid, issuerCertificate.PublicKey.EncodedKeyValue.RawData);
            if (!expectedIssuerKeyHash.AsSpan().SequenceEqual(response.IssuerKeyHash))
            {
                return false;
            }

            string serialHex = NormalizeSerialHex(certificate.GetSerialNumber().Reverse().ToArray());
            return string.Equals(serialHex, response.SerialNumberHex, StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool VerifyOcspSignature(
        OcspEvidence evidence,
        X509Certificate2 issuerCertificate,
        DateTimeOffset verificationMoment,
        IReadOnlyList<X509Certificate2> dssCertificates,
        out string? error)
    {
        error = null;
        if (!TryResolveSignatureAlgorithm(evidence.SignatureAlgorithmOid, out HashAlgorithmName hashAlgorithm, out bool useRsa))
        {
            error = $"DSS OCSP response uses unsupported signature algorithm OID '{evidence.SignatureAlgorithmOid}'.";
            return false;
        }

        List<X509Certificate2> candidates = [issuerCertificate];
        foreach (X509Certificate2 dssCertificate in dssCertificates)
        {
            if (!candidates.Any(candidate => string.Equals(candidate.Thumbprint, dssCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(dssCertificate);
            }
        }

        List<X509Certificate2> embeddedResponderCertificates = [];
        foreach (byte[] rawCertificate in evidence.EmbeddedCertificates)
        {
            try
            {
                X509Certificate2 responderCertificate = X509CertificateLoader.LoadCertificate(rawCertificate);
                embeddedResponderCertificates.Add(responderCertificate);
                if (!candidates.Any(candidate => string.Equals(candidate.Thumbprint, responderCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(responderCertificate);
                }
            }
            catch (CryptographicException)
            {
                continue;
            }
        }

        try
        {
            foreach (X509Certificate2 signerCandidate in candidates)
            {
                if (!IsMatchingOcspResponderCertificate(signerCandidate, evidence.ResponderIdentifier))
                {
                    continue;
                }

                if (!VerifySignatureWithCertificate(signerCandidate, evidence.TbsResponseData, evidence.SignatureValue, hashAlgorithm, useRsa))
                {
                    continue;
                }

                if (string.Equals(signerCandidate.Thumbprint, issuerCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!HasOcspSigningEku(signerCandidate))
                {
                    error = $"Delegated OCSP responder certificate '{signerCandidate.Subject}' is missing id-kp-OCSPSigning extended key usage.";
                    return false;
                }

                if (!IsDelegatedOcspResponderChainedToIssuer(signerCandidate, issuerCertificate, verificationMoment, candidates, out string? chainError))
                {
                    error = chainError;
                    return false;
                }

                return true;
            }
        }
        finally
        {
            foreach (X509Certificate2 certificate in embeddedResponderCertificates)
            {
                certificate.Dispose();
            }
        }

        error = $"DSS OCSP response signature could not be verified for issuer '{issuerCertificate.Subject}'.";
        return false;
    }

    private static bool VerifySignatureWithCertificate(
        X509Certificate2 certificate,
        byte[] payload,
        byte[] signature,
        HashAlgorithmName hashAlgorithm,
        bool useRsa)
    {
        if (useRsa)
        {
            using RSA? rsa = certificate.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(payload, signature, hashAlgorithm, RSASignaturePadding.Pkcs1);
        }

        using ECDsa? ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa is not null && ecdsa.VerifyData(payload, signature, hashAlgorithm);
    }

    private static bool IsMatchingOcspResponderCertificate(X509Certificate2 certificate, OcspResponderIdentifier responderIdentifier)
    {
        return responderIdentifier.Kind switch
        {
            OcspResponderIdentifierKind.ByName => responderIdentifier.NameRaw is not null
                && certificate.SubjectName.RawData.AsSpan().SequenceEqual(responderIdentifier.NameRaw),
            OcspResponderIdentifierKind.ByKey => responderIdentifier.KeyHash is not null
                && TryComputeOcspResponderKeyHash(certificate, out byte[]? keyHash)
                && keyHash.AsSpan().SequenceEqual(responderIdentifier.KeyHash),
            _ => false,
        };
    }

    private static bool TryComputeOcspResponderKeyHash(X509Certificate2 certificate, out byte[]? keyHash)
    {
        keyHash = null;
        try
        {
            byte[] subjectPublicKey = certificate.GetPublicKey();
#pragma warning disable CA5350 // OCSP responder key hash is defined as SHA-1 by RFC 6960
            keyHash = SHA1.HashData(subjectPublicKey);
#pragma warning restore CA5350
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool HasOcspSigningEku(X509Certificate2 certificate)
    {
        X509EnhancedKeyUsageExtension? eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null)
        {
            return false;
        }

        return eku.EnhancedKeyUsages.Cast<Oid>().Any(static oid => string.Equals(oid.Value, "1.3.6.1.5.5.7.3.9", StringComparison.Ordinal));
    }

    private static bool IsDelegatedOcspResponderChainedToIssuer(
        X509Certificate2 responderCertificate,
        X509Certificate2 issuerCertificate,
        DateTimeOffset verificationMoment,
        IReadOnlyList<X509Certificate2> candidateCertificates,
        out string? error)
    {
        error = null;
        using X509Chain chain = new();
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.VerificationTime = verificationMoment.UtcDateTime;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        chain.ChainPolicy.ExtraStore.Add(issuerCertificate);
        foreach (X509Certificate2 candidateCertificate in candidateCertificates)
        {
            chain.ChainPolicy.ExtraStore.Add(candidateCertificate);
        }

        bool buildSucceeded = chain.Build(responderCertificate);
        if (!buildSucceeded
            && chain.ChainStatus.Any(static status => status.Status is not (X509ChainStatusFlags.NoError
                or X509ChainStatusFlags.UntrustedRoot
                or X509ChainStatusFlags.PartialChain)))
        {
            error = $"Delegated OCSP responder certificate '{responderCertificate.Subject}' could not be chained to issuer '{issuerCertificate.Subject}'.";
            return false;
        }

        if (!chain.ChainElements
            .Cast<X509ChainElement>()
            .Any(element => string.Equals(element.Certificate.Thumbprint, issuerCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Delegated OCSP responder certificate '{responderCertificate.Subject}' does not chain to issuer '{issuerCertificate.Subject}'.";
            return false;
        }

        return true;
    }

    private static bool TryFindValidCrlForCertificate(
        X509Certificate2 certificate,
        X509Certificate2 issuerCertificate,
        DateTimeOffset verificationMoment,
        IReadOnlyList<CrlEvidence> dssCrls,
        out CrlEvidence crlEvidence,
        out string? error)
    {
        crlEvidence = default;
        error = null;
        byte[] expectedIssuerName = certificate.IssuerName.RawData;

        List<CrlEvidence> candidates = dssCrls
            .Where(candidate => candidate.IssuerNameRaw.AsSpan().SequenceEqual(expectedIssuerName))
            .OrderByDescending(static candidate => candidate.ThisUpdate)
            .ToList();
        if (candidates.Count == 0)
        {
            error = $"No DSS CRL was found for issuer '{certificate.Issuer}'.";
            return false;
        }

        foreach (CrlEvidence candidate in candidates)
        {
            if (candidate.ThisUpdate > verificationMoment)
            {
                continue;
            }

            if (candidate.NextUpdate is DateTimeOffset nextUpdate && nextUpdate < verificationMoment)
            {
                continue;
            }

            if (!VerifyCrlSignature(candidate, issuerCertificate, out string? signatureError))
            {
                error = signatureError ?? $"DSS CRL signature verification failed for issuer '{issuerCertificate.Subject}'.";
                continue;
            }

            crlEvidence = candidate;
            return true;
        }

        error ??= $"No fresh, verifiable DSS CRL was found for issuer '{certificate.Issuer}'.";
        return false;
    }

    private static bool VerifyCrlSignature(CrlEvidence crlEvidence, X509Certificate2 issuerCertificate, out string? error)
    {
        error = null;
        if (!crlEvidence.IssuerNameRaw.AsSpan().SequenceEqual(issuerCertificate.SubjectName.RawData))
        {
            error = $"DSS CRL issuer does not match certificate issuer '{issuerCertificate.Subject}'.";
            return false;
        }

        if (!TryResolveSignatureAlgorithm(crlEvidence.SignatureAlgorithmOid, out HashAlgorithmName hashAlgorithm, out bool useRsa))
        {
            error = $"DSS CRL uses unsupported signature algorithm OID '{crlEvidence.SignatureAlgorithmOid}'.";
            return false;
        }

        bool isValid;
        if (useRsa)
        {
            using RSA? rsa = issuerCertificate.GetRSAPublicKey();
            if (rsa is null)
            {
                error = $"Issuer certificate '{issuerCertificate.Subject}' does not expose an RSA public key required for CRL validation.";
                return false;
            }

            isValid = rsa.VerifyData(crlEvidence.TbsCertList, crlEvidence.SignatureValue, hashAlgorithm, RSASignaturePadding.Pkcs1);
        }
        else
        {
            using ECDsa? ecdsa = issuerCertificate.GetECDsaPublicKey();
            if (ecdsa is null)
            {
                error = $"Issuer certificate '{issuerCertificate.Subject}' does not expose an ECDSA public key required for CRL validation.";
                return false;
            }

            isValid = ecdsa.VerifyData(crlEvidence.TbsCertList, crlEvidence.SignatureValue, hashAlgorithm);
        }

        if (!isValid)
        {
            error = $"DSS CRL signature could not be verified for issuer '{issuerCertificate.Subject}'.";
        }

        return isValid;
    }

    private static bool TryResolveSignatureAlgorithm(string signatureAlgorithmOid, out HashAlgorithmName hashAlgorithm, out bool useRsa)
    {
        useRsa = true;
        switch (signatureAlgorithmOid)
        {
#pragma warning disable CA5350 // CRL signature compatibility may require SHA-1 verification
            case "1.2.840.113549.1.1.5":
                hashAlgorithm = HashAlgorithmName.SHA1;
                return true;
#pragma warning restore CA5350
            case "1.2.840.113549.1.1.11":
                hashAlgorithm = HashAlgorithmName.SHA256;
                return true;
            case "1.2.840.113549.1.1.12":
                hashAlgorithm = HashAlgorithmName.SHA384;
                return true;
            case "1.2.840.113549.1.1.13":
                hashAlgorithm = HashAlgorithmName.SHA512;
                return true;
#pragma warning disable CA5350 // CRL signature compatibility may require SHA-1 verification
            case "1.2.840.10045.4.1":
                hashAlgorithm = HashAlgorithmName.SHA1;
                useRsa = false;
                return true;
#pragma warning restore CA5350
            case "1.2.840.10045.4.3.2":
                hashAlgorithm = HashAlgorithmName.SHA256;
                useRsa = false;
                return true;
            case "1.2.840.10045.4.3.3":
                hashAlgorithm = HashAlgorithmName.SHA384;
                useRsa = false;
                return true;
            case "1.2.840.10045.4.3.4":
                hashAlgorithm = HashAlgorithmName.SHA512;
                useRsa = false;
                return true;
            default:
                hashAlgorithm = default;
                return false;
        }
    }

    private static string NormalizeSerialHex(ReadOnlySpan<byte> serialBytes)
    {
        int index = 0;
        while (index < serialBytes.Length - 1 && serialBytes[index] == 0)
        {
            index++;
        }

        return Convert.ToHexString(serialBytes[index..]);
    }

    private DssValidationEvidence ReadDssValidationEvidence()
    {
        if (!TryReadDssDictionary(out PdfDictionaryObject? dssDictionary, out string? dssDictionaryError))
        {
            return new DssValidationEvidence([], [], [], new Dictionary<string, DssVriEvidence>(StringComparer.OrdinalIgnoreCase), dssDictionaryError);
        }

        PdfDictionaryObject resolvedDssDictionary = dssDictionary!;
        List<X509Certificate2> certificates = [];
        List<CrlEvidence> crls = [];
        List<OcspEvidence> ocsps = [];
        Dictionary<string, DssVriEvidence> vriEvidenceByKey = new(StringComparer.OrdinalIgnoreCase);
        List<string> diagnostics = [];
        if (!string.IsNullOrWhiteSpace(dssDictionaryError))
        {
            diagnostics.Add(dssDictionaryError);
        }

        HashSet<string> seenCertificateFingerprints = [];
        List<byte[]> certificatePayloads = [];
        TryCollectResolvedBinaryEntriesFromDssArray(resolvedDssDictionary, "Certs", "DSS /Certs", certificatePayloads, diagnostics);
        for (int index = 0; index < certificatePayloads.Count; index++)
        {
            TryAddDssCertificate(certificatePayloads[index], $"DSS /Certs[{index}]", certificates, seenCertificateFingerprints, diagnostics);
        }

        List<byte[]> globalCrlPayloads = [];
        List<byte[]> globalOcspPayloads = [];
        TryCollectResolvedBinaryEntriesFromDssArray(resolvedDssDictionary, "CRLs", "DSS /CRLs", globalCrlPayloads, diagnostics);
        TryCollectResolvedBinaryEntriesFromDssArray(resolvedDssDictionary, "OCSPs", "DSS /OCSPs", globalOcspPayloads, diagnostics);
        crls.AddRange(ParseDistinctDssCrlEvidence(globalCrlPayloads, "DSS /CRLs", diagnostics));
        ocsps.AddRange(ParseDistinctDssOcspEvidence(globalOcspPayloads, "DSS /OCSPs", diagnostics));

        if (TryGetDictionaryEntry(resolvedDssDictionary, "VRI", out PdfObject? vriObject))
        {
            if (!TryResolveDictionaryObject(vriObject!, out PdfDictionaryObject? vriDictionary))
            {
                diagnostics.Add("Document /DSS /VRI entry is not a dictionary; VRI revocation evidence was ignored.");
            }
            else
            {
                foreach (PdfDictionaryEntry vriEntry in vriDictionary!.Entries)
                {
                    if (!TryResolveDictionaryObject(vriEntry.Value, out PdfDictionaryObject? vriItemDictionary))
                    {
                        diagnostics.Add($"Document /DSS /VRI '{vriEntry.Key}' is not a dictionary; VRI item was ignored.");
                        continue;
                    }

                    string normalizedVriKey = NormalizeDssVriKey(vriEntry.Key);
                    List<byte[]> vriCertificatePayloads = [];
                    List<byte[]> vriCrlPayloads = [];
                    List<byte[]> vriOcspPayloads = [];
                    TryCollectResolvedBinaryEntriesFromDssArray(vriItemDictionary!, "Cert", $"/DSS /VRI '{vriEntry.Key}' /Cert", vriCertificatePayloads, diagnostics);
                    TryCollectResolvedBinaryEntriesFromDssArray(vriItemDictionary!, "CRL", $"/DSS /VRI '{vriEntry.Key}' /CRL", vriCrlPayloads, diagnostics);
                    TryCollectResolvedBinaryEntriesFromDssArray(vriItemDictionary!, "OCSP", $"/DSS /VRI '{vriEntry.Key}' /OCSP", vriOcspPayloads, diagnostics);

                    for (int index = 0; index < vriCertificatePayloads.Count; index++)
                    {
                        TryAddDssCertificate(vriCertificatePayloads[index], $"/DSS /VRI '{vriEntry.Key}' /Cert[{index}]", certificates, seenCertificateFingerprints, diagnostics);
                    }

                    List<CrlEvidence> vriCrls = ParseDistinctDssCrlEvidence(vriCrlPayloads, $"/DSS /VRI '{vriEntry.Key}' /CRL", diagnostics);
                    List<OcspEvidence> vriOcsps = ParseDistinctDssOcspEvidence(vriOcspPayloads, $"/DSS /VRI '{vriEntry.Key}' /OCSP", diagnostics);
                    if (vriEvidenceByKey.TryGetValue(normalizedVriKey, out DssVriEvidence existingVriEvidence))
                    {
                        vriEvidenceByKey[normalizedVriKey] = new DssVriEvidence(
                            [.. existingVriEvidence.Crls, .. vriCrls],
                            [.. existingVriEvidence.Ocsps, .. vriOcsps]);
                    }
                    else
                    {
                        vriEvidenceByKey[normalizedVriKey] = new DssVriEvidence(vriCrls, vriOcsps);
                    }
                }
            }
        }

        string? diagnostic = diagnostics.Count == 0
            ? null
            : string.Join(" | ", diagnostics.Distinct(StringComparer.Ordinal));
        return new DssValidationEvidence(certificates, crls, ocsps, vriEvidenceByKey, diagnostic);
    }

    private static void TryAddDssCertificate(
        byte[] certificateBytes,
        string context,
        List<X509Certificate2> certificates,
        HashSet<string> seenCertificateFingerprints,
        List<string> diagnostics)
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData(certificateBytes));
        if (!seenCertificateFingerprints.Add(fingerprint))
        {
            return;
        }

        try
        {
            certificates.Add(X509CertificateLoader.LoadCertificate(certificateBytes));
        }
        catch (CryptographicException exception)
        {
            diagnostics.Add($"{context} is invalid: {exception.Message}");
        }
    }

    private static List<CrlEvidence> ParseDistinctDssCrlEvidence(List<byte[]> payloads, string context, List<string> diagnostics)
    {
        List<CrlEvidence> parsedEvidence = [];
        HashSet<string> seenFingerprints = [];
        for (int index = 0; index < payloads.Count; index++)
        {
            byte[] crlBytes = payloads[index];
            string fingerprint = Convert.ToHexString(SHA256.HashData(crlBytes));
            if (!seenFingerprints.Add(fingerprint))
            {
                continue;
            }

            if (TryParseCrlEvidence(crlBytes, out CrlEvidence crlEvidence, out string? crlError))
            {
                parsedEvidence.Add(crlEvidence);
            }
            else if (!string.IsNullOrWhiteSpace(crlError))
            {
                diagnostics.Add($"{context}[{index}] {crlError}");
            }
        }

        return parsedEvidence;
    }

    private static List<OcspEvidence> ParseDistinctDssOcspEvidence(List<byte[]> payloads, string context, List<string> diagnostics)
    {
        List<OcspEvidence> parsedEvidence = [];
        HashSet<string> seenFingerprints = [];
        for (int index = 0; index < payloads.Count; index++)
        {
            byte[] ocspBytes = payloads[index];
            string fingerprint = Convert.ToHexString(SHA256.HashData(ocspBytes));
            if (!seenFingerprints.Add(fingerprint))
            {
                continue;
            }

            if (TryParseOcspEvidence(ocspBytes, out OcspEvidence ocspEvidence, out string? ocspError))
            {
                parsedEvidence.Add(ocspEvidence);
            }
            else if (!string.IsNullOrWhiteSpace(ocspError))
            {
                diagnostics.Add($"{context}[{index}] {ocspError}");
            }
        }

        return parsedEvidence;
    }

    private bool TryReadDssDictionary(out PdfDictionaryObject? dssDictionary, out string? diagnostic)
    {
        dssDictionary = null;
        diagnostic = null;
        if (!TryGetDictionaryEntry(_file.Trailer, "Root", out PdfObject? rootObject))
        {
            diagnostic = "Document trailer is missing /Root entry; DSS evidence could not be loaded.";
            return false;
        }

        if (!TryResolveDictionaryObject(rootObject!, out PdfDictionaryObject? catalog))
        {
            diagnostic = "Document catalog could not be resolved; DSS evidence could not be loaded.";
            return false;
        }

        if (!TryGetDictionaryEntry(catalog!, "DSS", out PdfObject? dssObject))
        {
            return false;
        }

        if (!TryResolveDictionaryObject(dssObject!, out PdfDictionaryObject? resolvedDssDictionary))
        {
            diagnostic = "Document /DSS entry is not a dictionary; DSS evidence was ignored.";
            return false;
        }

        dssDictionary = resolvedDssDictionary!;
        return true;
    }

    private void TryCollectResolvedBinaryEntriesFromDssArray(
        PdfDictionaryObject dictionary,
        string key,
        string context,
        List<byte[]> target,
        List<string> diagnostics)
    {
        if (!TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            return;
        }

        if (!TryResolveArrayObject(value!, out PdfArrayObject? array))
        {
            diagnostics.Add($"{context} must be an array; evidence was ignored.");
            return;
        }

        for (int index = 0; index < array!.Items.Count; index++)
        {
            if (TryResolveBinaryBytes(array.Items[index], out byte[]? resolvedBytes, out string? error))
            {
                target.Add(resolvedBytes!);
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                diagnostics.Add($"{context}[{index}] {error}");
            }
        }
    }

    private bool TryResolveBinaryBytes(PdfObject value, out byte[]? data, out string? error)
    {
        data = null;
        error = null;
        if (!TryResolveObject(value, out PdfObject? resolved))
        {
            error = "reference could not be resolved.";
            return false;
        }

        switch (resolved)
        {
            case PdfStreamObject stream:
                data = stream.Data.ToArray();
                return true;
            case PdfByteStringObject byteString:
                data = byteString.Bytes.ToArray();
                return true;
            case PdfStringObject literalString:
                data = Encoding.ASCII.GetBytes(literalString.Value);
                return true;
            default:
                error = resolved is null
                    ? "contains unsupported object type '<null>'."
                    : $"contains unsupported object type '{resolved.GetType().Name}'.";
                return false;
        }
    }

    private static bool TryParseCrlEvidence(byte[] crlBytes, out CrlEvidence evidence, out string? error)
    {
        evidence = default;
        error = null;

        try
        {
            AsnReader certificateListReader = new(crlBytes, AsnEncodingRules.DER);
            AsnReader certificateList = certificateListReader.ReadSequence();
            ReadOnlyMemory<byte> tbsCertListBytes = certificateList.ReadEncodedValue();
            AsnReader tbsCertList = new AsnReader(tbsCertListBytes.ToArray(), AsnEncodingRules.DER).ReadSequence();

            Asn1Tag integerTag = new(UniversalTagNumber.Integer);
            if (tbsCertList.HasData && tbsCertList.PeekTag().HasSameClassAndValue(integerTag))
            {
                _ = tbsCertList.ReadInteger();
            }

            _ = ReadAlgorithmIdentifierOid(tbsCertList);
            byte[] issuerNameRaw = tbsCertList.ReadEncodedValue().ToArray();
            DateTimeOffset thisUpdate = ReadAsnTime(tbsCertList);
            DateTimeOffset? nextUpdate = null;
            if (tbsCertList.HasData && IsAsnTimeTag(tbsCertList.PeekTag()))
            {
                nextUpdate = ReadAsnTime(tbsCertList);
            }

            HashSet<string> revokedSerialNumbers = [];
            Asn1Tag sequenceTag = new(UniversalTagNumber.Sequence);
            if (tbsCertList.HasData && tbsCertList.PeekTag().HasSameClassAndValue(sequenceTag))
            {
                AsnReader revokedCertificates = tbsCertList.ReadSequence();
                while (revokedCertificates.HasData)
                {
                    AsnReader revokedCertificate = revokedCertificates.ReadSequence();
                    byte[] serialNumber = revokedCertificate.ReadIntegerBytes().ToArray();
                    revokedSerialNumbers.Add(NormalizeSerialHex(serialNumber));
                    _ = ReadAsnTime(revokedCertificate);
                    while (revokedCertificate.HasData)
                    {
                        _ = revokedCertificate.ReadEncodedValue();
                    }
                }
            }

            while (tbsCertList.HasData)
            {
                _ = tbsCertList.ReadEncodedValue();
            }

            string signatureAlgorithmOid = ReadAlgorithmIdentifierOid(certificateList);
            byte[] signatureValue = certificateList.ReadBitString(out _);
            certificateListReader.ThrowIfNotEmpty();

            evidence = new CrlEvidence(
                tbsCertListBytes.ToArray(),
                issuerNameRaw,
                thisUpdate,
                nextUpdate,
                revokedSerialNumbers,
                signatureAlgorithmOid,
                signatureValue);
            return true;
        }
        catch (AsnContentException)
        {
            error = "A DSS CRL payload is malformed and was ignored.";
            return false;
        }
    }

    private static bool TryParseOcspEvidence(byte[] ocspBytes, out OcspEvidence evidence, out string? error)
    {
        evidence = default;
        error = null;

        try
        {
            AsnReader ocspReader = new(ocspBytes, AsnEncodingRules.DER);
            AsnReader ocspResponse = ocspReader.ReadSequence();
            OcspResponseStatus responseStatus = ocspResponse.ReadEnumeratedValue<OcspResponseStatus>();
            if (responseStatus != OcspResponseStatus.Successful)
            {
                error = $"A DSS OCSP response has non-success status '{responseStatus}' and was ignored.";
                return false;
            }

            Asn1Tag responseBytesTag = new(TagClass.ContextSpecific, 0);
            if (!ocspResponse.HasData || !ocspResponse.PeekTag().HasSameClassAndValue(responseBytesTag))
            {
                error = "A DSS OCSP response is missing responseBytes for successful status.";
                return false;
            }

            AsnReader responseBytesContainer = ocspResponse.ReadSequence(responseBytesTag);
            AsnReader responseBytes = responseBytesContainer.ReadSequence();
            string responseTypeOid = responseBytes.ReadObjectIdentifier();
            if (!string.Equals(responseTypeOid, "1.3.6.1.5.5.7.48.1.1", StringComparison.Ordinal))
            {
                error = $"A DSS OCSP response has unsupported responseType OID '{responseTypeOid}'.";
                return false;
            }

            byte[] basicResponseBytes = responseBytes.ReadOctetString();
            responseBytes.ThrowIfNotEmpty();
            responseBytesContainer.ThrowIfNotEmpty();
            ocspResponse.ThrowIfNotEmpty();
            ocspReader.ThrowIfNotEmpty();

            return TryParseBasicOcspResponseEvidence(basicResponseBytes, out evidence, out error);
        }
        catch (AsnContentException)
        {
            error = "A DSS OCSP response payload is malformed and was ignored.";
            return false;
        }
    }

    private static bool TryParseBasicOcspResponseEvidence(byte[] basicResponseBytes, out OcspEvidence evidence, out string? error)
    {
        evidence = default;
        error = null;

        try
        {
            AsnReader basicReader = new(basicResponseBytes, AsnEncodingRules.DER);
            AsnReader basicResponse = basicReader.ReadSequence();
            ReadOnlyMemory<byte> tbsResponseDataMemory = basicResponse.ReadEncodedValue();
            AsnReader tbsResponseData = new AsnReader(tbsResponseDataMemory.ToArray(), AsnEncodingRules.DER).ReadSequence();

            Asn1Tag versionTag = new(TagClass.ContextSpecific, 0);
            if (tbsResponseData.HasData && tbsResponseData.PeekTag().HasSameClassAndValue(versionTag))
            {
                AsnReader versionReader = tbsResponseData.ReadSequence(versionTag);
                _ = versionReader.ReadInteger();
                versionReader.ThrowIfNotEmpty();
            }

            Asn1Tag responderByNameTag = new(TagClass.ContextSpecific, 1);
            Asn1Tag responderByKeyTag = new(TagClass.ContextSpecific, 2);
            if (!tbsResponseData.HasData
                || (!tbsResponseData.PeekTag().HasSameClassAndValue(responderByNameTag)
                    && !tbsResponseData.PeekTag().HasSameClassAndValue(responderByKeyTag)))
            {
                error = "A DSS OCSP response is missing responderID.";
                return false;
            }

            if (!TryReadOcspResponderIdentifier(tbsResponseData, out OcspResponderIdentifier responderIdentifier, out string? responderError))
            {
                error = responderError;
                return false;
            }

            DateTimeOffset producedAt = ReadAsnTime(tbsResponseData);

            AsnReader responsesReader = tbsResponseData.ReadSequence();
            List<OcspSingleResponseEvidence> responses = [];
            while (responsesReader.HasData)
            {
                if (!TryParseOcspSingleResponse(responsesReader.ReadSequence(), out OcspSingleResponseEvidence singleResponse, out string? singleResponseError))
                {
                    error = singleResponseError;
                    return false;
                }

                responses.Add(singleResponse);
            }

            while (tbsResponseData.HasData)
            {
                _ = tbsResponseData.ReadEncodedValue();
            }

            string signatureAlgorithmOid = ReadAlgorithmIdentifierOid(basicResponse);
            byte[] signatureValue = basicResponse.ReadBitString(out _);

            Asn1Tag certsTag = new(TagClass.ContextSpecific, 0);
            List<byte[]> embeddedCertificates = [];
            if (basicResponse.HasData && basicResponse.PeekTag().HasSameClassAndValue(certsTag))
            {
                AsnReader certsContainer = basicResponse.ReadSequence(certsTag);
                AsnReader certsSequence = certsContainer.ReadSequence();
                while (certsSequence.HasData)
                {
                    embeddedCertificates.Add(certsSequence.ReadEncodedValue().ToArray());
                }

                certsContainer.ThrowIfNotEmpty();
            }

            basicResponse.ThrowIfNotEmpty();
            basicReader.ThrowIfNotEmpty();
            if (responses.Count == 0)
            {
                error = "A DSS OCSP response contains no SingleResponse entries.";
                return false;
            }

            evidence = new OcspEvidence(
                tbsResponseDataMemory.ToArray(),
                producedAt,
                signatureAlgorithmOid,
                signatureValue,
                responderIdentifier,
                embeddedCertificates,
                responses);
            return true;
        }
        catch (AsnContentException)
        {
            error = "A DSS OCSP basic response payload is malformed and was ignored.";
            return false;
        }
    }

    private static bool TryParseOcspSingleResponse(AsnReader singleResponseReader, out OcspSingleResponseEvidence response, out string? error)
    {
        response = default;
        error = null;
        try
        {
            AsnReader certId = singleResponseReader.ReadSequence();
            string hashAlgorithmOid = ReadAlgorithmIdentifierOid(certId);
            byte[] issuerNameHash = certId.ReadOctetString();
            byte[] issuerKeyHash = certId.ReadOctetString();
            string serialHex = NormalizeSerialHex(certId.ReadIntegerBytes().ToArray());
            certId.ThrowIfNotEmpty();

            if (!singleResponseReader.HasData)
            {
                error = "A DSS OCSP SingleResponse is missing certStatus.";
                return false;
            }

            OcspCertStatus certStatus = ReadOcspCertStatus(singleResponseReader);
            DateTimeOffset thisUpdate = ReadAsnTime(singleResponseReader);
            DateTimeOffset? nextUpdate = null;
            Asn1Tag nextUpdateTag = new(TagClass.ContextSpecific, 0);
            if (singleResponseReader.HasData && singleResponseReader.PeekTag().HasSameClassAndValue(nextUpdateTag))
            {
                AsnReader nextUpdateReader = singleResponseReader.ReadSequence(nextUpdateTag);
                nextUpdate = ReadAsnTime(nextUpdateReader);
                nextUpdateReader.ThrowIfNotEmpty();
            }

            while (singleResponseReader.HasData)
            {
                _ = singleResponseReader.ReadEncodedValue();
            }

            response = new OcspSingleResponseEvidence(
                hashAlgorithmOid,
                issuerNameHash,
                issuerKeyHash,
                serialHex,
                certStatus,
                thisUpdate,
                nextUpdate);
            return true;
        }
        catch (AsnContentException)
        {
            error = "A DSS OCSP SingleResponse payload is malformed and was ignored.";
            return false;
        }
    }

    private static bool TryReadOcspResponderIdentifier(
        AsnReader tbsResponseData,
        out OcspResponderIdentifier responderIdentifier,
        out string? error)
    {
        responderIdentifier = default;
        error = null;
        Asn1Tag byNameTag = new(TagClass.ContextSpecific, 1);
        Asn1Tag byKeyTag = new(TagClass.ContextSpecific, 2);
        Asn1Tag identifierTag = tbsResponseData.PeekTag();

        try
        {
            if (identifierTag.HasSameClassAndValue(byNameTag))
            {
                AsnReader byName = tbsResponseData.ReadSequence(byNameTag);
                byte[] responderName = byName.ReadEncodedValue().ToArray();
                byName.ThrowIfNotEmpty();
                responderIdentifier = new OcspResponderIdentifier(OcspResponderIdentifierKind.ByName, responderName, null);
                return true;
            }

            if (identifierTag.HasSameClassAndValue(byKeyTag))
            {
                byte[] responderKeyHash = tbsResponseData.ReadOctetString(byKeyTag);
                responderIdentifier = new OcspResponderIdentifier(OcspResponderIdentifierKind.ByKey, null, responderKeyHash);
                return true;
            }
        }
        catch (AsnContentException)
        {
            error = "A DSS OCSP responderID payload is malformed and was ignored.";
            return false;
        }

        error = "A DSS OCSP response has unsupported responderID format.";
        return false;
    }

    private static OcspCertStatus ReadOcspCertStatus(AsnReader singleResponseReader)
    {
        Asn1Tag goodTag = new(TagClass.ContextSpecific, 0);
        Asn1Tag revokedTag = new(TagClass.ContextSpecific, 1);
        Asn1Tag unknownTag = new(TagClass.ContextSpecific, 2);
        Asn1Tag statusTag = singleResponseReader.PeekTag();

        if (statusTag.HasSameClassAndValue(goodTag))
        {
            singleResponseReader.ReadNull(goodTag);
            return OcspCertStatus.Good;
        }

        if (statusTag.HasSameClassAndValue(revokedTag))
        {
            _ = singleResponseReader.ReadEncodedValue();
            return OcspCertStatus.Revoked;
        }

        if (statusTag.HasSameClassAndValue(unknownTag))
        {
            _ = singleResponseReader.ReadEncodedValue();
            return OcspCertStatus.Unknown;
        }

        throw new AsnContentException("Unsupported OCSP certStatus tag.");
    }

    private static string ReadAlgorithmIdentifierOid(AsnReader reader)
    {
        AsnReader algorithmIdentifier = reader.ReadSequence();
        string oid = algorithmIdentifier.ReadObjectIdentifier();
        while (algorithmIdentifier.HasData)
        {
            _ = algorithmIdentifier.ReadEncodedValue();
        }

        return oid;
    }

    private static bool IsAsnTimeTag(Asn1Tag tag)
    {
        Asn1Tag utcTag = new(UniversalTagNumber.UtcTime);
        Asn1Tag generalizedTag = new(UniversalTagNumber.GeneralizedTime);
        return tag.HasSameClassAndValue(utcTag) || tag.HasSameClassAndValue(generalizedTag);
    }

    private static DateTimeOffset ReadAsnTime(AsnReader reader)
    {
        Asn1Tag tag = reader.PeekTag();
        Asn1Tag utcTag = new(UniversalTagNumber.UtcTime);
        if (tag.HasSameClassAndValue(utcTag))
        {
            return reader.ReadUtcTime();
        }

        Asn1Tag generalizedTag = new(UniversalTagNumber.GeneralizedTime);
        if (tag.HasSameClassAndValue(generalizedTag))
        {
            return reader.ReadGeneralizedTime();
        }

        throw new AsnContentException("Expected ASN.1 time value.");
    }

    private bool TryResolveDictionaryObject(PdfObject value, out PdfDictionaryObject? dictionary)
    {
        dictionary = null;
        if (!TryResolveObject(value, out PdfObject? resolved))
        {
            return false;
        }

        dictionary = resolved as PdfDictionaryObject;
        return dictionary is not null;
    }

    private bool TryResolveArrayObject(PdfObject value, out PdfArrayObject? array)
    {
        array = null;
        if (!TryResolveObject(value, out PdfObject? resolved))
        {
            return false;
        }

        array = resolved as PdfArrayObject;
        return array is not null;
    }

    private bool TryResolveObject(PdfObject value, out PdfObject? resolved)
    {
        if (value is PdfReferenceObject reference)
        {
            foreach (PdfIndirectObject indirectObject in _file.Objects)
            {
                if (indirectObject.ObjectId == reference.ObjectId)
                {
                    resolved = indirectObject.Value;
                    return true;
                }
            }

            resolved = null;
            return false;
        }

        resolved = value;
        return true;
    }

    private readonly record struct PdfImagePlacement(
        double X,
        double Y,
        double Width,
        double Height);

    private readonly record struct PdfShapeResourceNames(
        string? GraphicsStateName,
        string? PatternName);

    private readonly record struct PdfPageResourceEntry(
        string CategoryKey,
        string ResourceName,
        PdfObjectId ObjectId);

    private readonly record struct PdfShapeResourceAllocation(
        PdfShapeResourceNames Names,
        List<PdfIndirectObject> ObjectsToAdd,
        List<PdfPageResourceEntry> ResourceEntries,
        List<PdfObjectId> DirtyObjectIds);

    private readonly record struct PdfRedactionRectangle(
        int PageIndex,
        double X,
        double Y,
        double Width,
        double Height);

    private readonly record struct PdfTextAnchorKey(
        int StreamObjectNumber,
        int StreamObjectGeneration,
        int StringTokenIndex);

    private readonly record struct PdfTextSelectionRange(
        int Start,
        int Length);

    private readonly record struct DssValidationEvidence(
        IReadOnlyList<X509Certificate2> Certificates,
        IReadOnlyList<CrlEvidence> Crls,
        IReadOnlyList<OcspEvidence> Ocsps,
        IReadOnlyDictionary<string, DssVriEvidence> VriEvidenceByKey,
        string? Diagnostic);

    private readonly record struct DssVriEvidence(
        IReadOnlyList<CrlEvidence> Crls,
        IReadOnlyList<OcspEvidence> Ocsps);

    private readonly record struct CrlEvidence(
        byte[] TbsCertList,
        byte[] IssuerNameRaw,
        DateTimeOffset ThisUpdate,
        DateTimeOffset? NextUpdate,
        HashSet<string> RevokedSerialNumbers,
        string SignatureAlgorithmOid,
        byte[] SignatureValue);

    private readonly record struct OcspEvidence(
        byte[] TbsResponseData,
        DateTimeOffset ProducedAt,
        string SignatureAlgorithmOid,
        byte[] SignatureValue,
        OcspResponderIdentifier ResponderIdentifier,
        IReadOnlyList<byte[]> EmbeddedCertificates,
        IReadOnlyList<OcspSingleResponseEvidence> Responses);

    private readonly record struct OcspSingleResponseEvidence(
        string CertIdHashAlgorithmOid,
        byte[] IssuerNameHash,
        byte[] IssuerKeyHash,
        string SerialNumberHex,
        OcspCertStatus CertStatus,
        DateTimeOffset ThisUpdate,
        DateTimeOffset? NextUpdate);

    private readonly record struct OcspResponderIdentifier(
        OcspResponderIdentifierKind Kind,
        byte[]? NameRaw,
        byte[]? KeyHash);

    private enum OcspResponderIdentifierKind
    {
        ByName = 0,
        ByKey = 1,
    }

    private enum OcspValidationStatus
    {
        NoEvidence = 0,
        Valid = 1,
        Invalid = 2,
    }

    private enum OcspCertStatus
    {
        Good = 0,
        Revoked = 1,
        Unknown = 2,
    }

    private enum OcspResponseStatus
    {
        Successful = 0,
        MalformedRequest = 1,
        InternalError = 2,
        TryLater = 3,
        SigRequired = 5,
        Unauthorized = 6,
    }

    private static bool EvaluateSignerPolicies(SignedCms signedCms, IReadOnlyList<string> requiredPolicyOids, List<string> diagnostics)
    {
        HashSet<string> requiredPolicies = new(requiredPolicyOids.Where(static oid => !string.IsNullOrWhiteSpace(oid)), StringComparer.Ordinal);
        if (requiredPolicies.Count == 0)
        {
            diagnostics.Add("Certificate policy validation requested but no non-empty policy OIDs were provided.");
            return false;
        }

        bool anySigner = false;
        foreach (SignerInfo signerInfo in signedCms.SignerInfos)
        {
            X509Certificate2? certificate = signerInfo.Certificate;
            if (certificate is null)
            {
                diagnostics.Add("Signer certificate is missing for certificate policy validation.");
                return false;
            }

            anySigner = true;
            HashSet<string> signerPolicies = ReadCertificatePolicyOids(certificate);
            if (!signerPolicies.Overlaps(requiredPolicies))
            {
                diagnostics.Add(
                    $"Signer certificate policies [{string.Join(", ", signerPolicies.OrderBy(static oid => oid))}] do not satisfy required policies [{string.Join(", ", requiredPolicies.OrderBy(static oid => oid))}].");
                return false;
            }
        }

        if (!anySigner)
        {
            diagnostics.Add("No signer certificates were available for certificate policy validation.");
        }

        return anySigner;
    }

    private static HashSet<string> ReadCertificatePolicyOids(X509Certificate2 certificate)
    {
        X509Extension? policyExtension = certificate.Extensions["2.5.29.32"];
        if (policyExtension is null)
        {
            return [];
        }

        try
        {
            AsnReader reader = new(policyExtension.RawData, AsnEncodingRules.DER);
            AsnReader policySequence = reader.ReadSequence();
            HashSet<string> oids = [];
            while (policySequence.HasData)
            {
                AsnReader policyInformation = policySequence.ReadSequence();
                oids.Add(policyInformation.ReadObjectIdentifier());
            }

            return oids;
        }
        catch (AsnContentException)
        {
            return [];
        }
    }

    private static bool IsSignatureDictionary(PdfDictionaryObject dictionary)
    {
        if (TryGetDictionaryEntry(dictionary, "Type", out PdfObject? typeObject)
            && typeObject is PdfNameObject typeName
            && string.Equals(typeName.Value, "Sig", StringComparison.Ordinal))
        {
            return true;
        }

        return TryGetDictionaryEntry(dictionary, "ByteRange", out _)
            && TryGetDictionaryEntry(dictionary, "Contents", out _)
            && TryGetDictionaryEntry(dictionary, "Filter", out _);
    }

    private static string? TryReadNameEntry(PdfDictionaryObject dictionary, string key)
    {
        if (!TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            return null;
        }

        return value is PdfNameObject name ? name.Value : null;
    }

    private static bool IsSupportedCmsSubFilter(string? subFilter)
    {
        return subFilter is not null && SupportedCmsSubFilters.Contains(subFilter);
    }

    private static bool TryReadByteRange(
        PdfDictionaryObject dictionary,
        int sourceLength,
        out (int Start, int Length) firstRange,
        out (int Start, int Length) secondRange,
        out string? error)
    {
        firstRange = default;
        secondRange = default;

        if (!TryGetDictionaryEntry(dictionary, "ByteRange", out PdfObject? byteRangeObject) || byteRangeObject is not PdfArrayObject byteRangeArray)
        {
            error = "Signature dictionary is missing /ByteRange array.";
            return false;
        }

        if (byteRangeArray.Items.Count != 4)
        {
            error = "Signature /ByteRange must contain exactly four entries.";
            return false;
        }

        int[] values = new int[4];
        for (int index = 0; index < byteRangeArray.Items.Count; index++)
        {
            if (byteRangeArray.Items[index] is not PdfNumberObject numberObject
                || !numberObject.IsInteger
                || !double.IsFinite(numberObject.Value)
                || numberObject.Value < 0
                || numberObject.Value > int.MaxValue)
            {
                error = "Signature /ByteRange entries must be non-negative integers.";
                return false;
            }

            values[index] = Convert.ToInt32(numberObject.Value, CultureInfo.InvariantCulture);
        }

        long firstEnd = values[0] + (long)values[1];
        long secondEnd = values[2] + (long)values[3];
        if (firstEnd > sourceLength || secondEnd > sourceLength)
        {
            error = "Signature /ByteRange exceeds the available source byte length.";
            return false;
        }

        if (values[2] < firstEnd)
        {
            error = "Signature /ByteRange spans overlap.";
            return false;
        }

        if (values[0] != 0)
        {
            error = "Signature /ByteRange must start at offset 0.";
            return false;
        }

        firstRange = (values[0], values[1]);
        secondRange = (values[2], values[3]);
        error = null;
        return true;
    }

    private static bool TryReadCmsBytes(PdfDictionaryObject dictionary, out byte[]? cmsBytes, out string? error)
    {
        cmsBytes = null;
        if (!TryGetDictionaryEntry(dictionary, "Contents", out PdfObject? contentsObject) || contentsObject is not PdfByteStringObject byteString)
        {
            error = "Signature dictionary is missing byte-string /Contents.";
            return false;
        }

        ReadOnlySpan<byte> bytes = byteString.Bytes.Span;
        if (bytes.Length == 0)
        {
            error = "Signature /Contents does not contain CMS signature bytes.";
            return false;
        }

        if (!TryReadDerEncodedLength(bytes, out int cmsLength))
        {
            error = "Signature /Contents does not contain a valid DER-encoded CMS object.";
            return false;
        }

        cmsBytes = bytes[..cmsLength].ToArray();
        error = null;
        return true;
    }

    private static bool TryReadDerEncodedLength(ReadOnlySpan<byte> bytes, out int totalLength)
    {
        totalLength = 0;
        if (bytes.Length < 2)
        {
            return false;
        }

        byte firstLengthByte = bytes[1];
        if ((firstLengthByte & 0x80) == 0)
        {
            int contentLength = firstLengthByte;
            totalLength = 2 + contentLength;
            return totalLength <= bytes.Length;
        }

        int lengthByteCount = firstLengthByte & 0x7F;
        if (lengthByteCount is <= 0 or > 4)
        {
            return false;
        }

        if (2 + lengthByteCount > bytes.Length)
        {
            return false;
        }

        int contentLengthLong = 0;
        for (int index = 0; index < lengthByteCount; index++)
        {
            contentLengthLong = (contentLengthLong << 8) | bytes[2 + index];
        }

        if (contentLengthLong < 0)
        {
            return false;
        }

        totalLength = 2 + lengthByteCount + contentLengthLong;
        return totalLength <= bytes.Length;
    }

    private static List<(int Start, int Length)> ParseByteRangeTokenSlots(string text, int byteRangeArrayStart)
    {
        int arrayEnd = text.IndexOf(']', byteRangeArrayStart);
        if (arrayEnd < 0)
        {
            throw new PdfFormatException("Signature /ByteRange array was not terminated.");
        }

        List<(int Start, int Length)> slots = [];
        int cursor = byteRangeArrayStart;
        while (cursor < arrayEnd)
        {
            while (cursor < arrayEnd && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            if (cursor >= arrayEnd)
            {
                break;
            }

            int tokenStart = cursor;
            while (cursor < arrayEnd && char.IsAsciiDigit(text[cursor]))
            {
                cursor++;
            }

            if (tokenStart == cursor)
            {
                throw new PdfFormatException("Signature /ByteRange entries must be integer tokens.");
            }

            slots.Add((tokenStart, cursor - tokenStart));
        }

        if (slots.Count != 4)
        {
            throw new PdfFormatException("Signature /ByteRange must contain exactly four integer entries.");
        }

        return slots;
    }

    private static void WriteFixedWidthNumber(byte[] buffer, (int Start, int Length) slot, long value)
    {
        string number = value.ToString(CultureInfo.InvariantCulture);
        if (number.Length > slot.Length)
        {
            throw new PdfFormatException("Signature /ByteRange value exceeded placeholder width.");
        }

        string padded = number.PadLeft(slot.Length, '0');
        Encoding.ASCII.GetBytes(padded, 0, padded.Length, buffer, slot.Start);
    }

    private static string FormatPdfDate(DateTimeOffset value)
    {
        string date = value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        TimeSpan offset = value.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        int hours = Math.Abs(offset.Hours);
        int minutes = Math.Abs(offset.Minutes);
        return $"D:{date}{sign}{hours:00}'{minutes:00}'";
    }

    private void RebaseFromSavedBytes(byte[] savedBytes)
    {
        PdfFile rebasedFile = PdfFileReader.Read(savedBytes);
        _file = rebasedFile;
        _model = PdfDocumentModelBuilder.Build(rebasedFile);
        _dirtyObjectIds.Clear();
        _openedEncrypted = false;
        _openedSecurityOptions = null;
        _openedEncryptedFile = null;
    }

    private PdfSecurityOptions ResolveIncrementalEncryptedSecurityOptions(PdfSecurityOptions? requestedSecurity)
    {
        PdfSecurityOptions openedSecurity = _openedSecurityOptions
            ?? throw new InvalidOperationException("Encrypted incremental save requires preserved security options.");
        if (requestedSecurity is null)
        {
            return openedSecurity;
        }

        ValidateSecurityOptions(requestedSecurity);
        EnsureMatchingIncrementalSecurityContext(requestedSecurity, openedSecurity);
        return openedSecurity;
    }

    private static void EnsureMatchingIncrementalSecurityContext(PdfSecurityOptions requested, PdfSecurityOptions opened)
    {
        if (!string.Equals(requested.UserPassword, opened.UserPassword, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Incremental save security options must use the same UserPassword as the opened encrypted document.");
        }

        if (!string.Equals(requested.OwnerPassword, opened.OwnerPassword, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Incremental save security options must use the same OwnerPassword as the opened encrypted document.");
        }

        if (requested.Profile != opened.Profile)
        {
            throw new InvalidOperationException("Incremental save security options must use the same Profile as the opened encrypted document.");
        }

        if (requested.Permissions != opened.Permissions)
        {
            throw new InvalidOperationException("Incremental save security options must use the same Permissions as the opened encrypted document.");
        }
    }

    private byte[] SaveIncrementalEncrypted(PdfCrossReferenceStyle crossReferenceStyle, PdfSecurityOptions security)
    {
        if (_openedEncryptedFile is null)
        {
            throw new InvalidOperationException("Encrypted incremental save requires the original encrypted source file.");
        }

        if (string.IsNullOrWhiteSpace(security.UserPassword))
        {
            throw new InvalidOperationException("Encrypted incremental save requires the original password.");
        }

        PdfFile encryptedIncrementalFile = PdfStandardSecurityProcessor.PrepareIncrementalEncryptedFile(
            _openedEncryptedFile,
            _file,
            _dirtyObjectIds,
            security.UserPassword);
        byte[] encryptedBytes = PdfFileWriter.WriteIncremental(encryptedIncrementalFile, _dirtyObjectIds, crossReferenceStyle);
        _dirtyObjectIds.Clear();
        _openedEncryptedFile = PdfFileReader.Read(encryptedBytes);
        _openedSecurityOptions = security;
        return encryptedBytes;
    }

    private void MarkDirty(PdfObjectId objectId)
    {
        _dirtyObjectIds.Add(objectId);
        _model.Mutations.MarkDirty(objectId);
    }

    /// <summary>
    /// Replaces the page content stream with raw PDF content operators.
    /// </summary>
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

    /// <summary>
    /// Replaces page content with a single raster image.
    /// </summary>
    public void ReplacePageImage(int pageIndex, byte[] imageBytes, PdfImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfRasterImage image = PdfImageParser.Parse(imageBytes);
        PdfPageModel page = _model.Pages[pageIndex];
        if (page.Contents is not PdfReferenceObject contentsReference)
        {
            throw new NotSupportedException("Only page /Contents references are supported for replacement.");
        }

        PdfRectangle pageBounds = page.MediaBox ?? throw new NotSupportedException("Page /MediaBox is required for image placement.");
        PdfImageOptions effectiveOptions = options ?? new PdfImageOptions
        {
            X = pageBounds.Left,
            Y = pageBounds.Bottom,
            Width = pageBounds.Width,
            Height = pageBounds.Height,
            PreserveAspectRatio = true,
        };
        ValidateImageOptions(effectiveOptions);
        PdfImagePlacement placement = ResolveImagePlacement(image, effectiveOptions);

        List<PdfIndirectObject> objects = [.. _file.Objects];
        int nextObjectNumber = GetNextObjectNumber(objects);
        PdfObjectId? softMaskId = null;
        PdfObjectId imageId = new(nextObjectNumber++, 0);
        if (image.SoftMask is not null)
        {
            softMaskId = new PdfObjectId(nextObjectNumber++, 0);
        }

        PdfObjectId resourcesId = new(nextObjectNumber++, 0);

        PdfDictionaryObject imageDictionary = CreateImageXObjectDictionary(image, softMaskId);
        PdfDictionaryObject resourcesDictionary = new(
        [
            new PdfDictionaryEntry(
                "XObject",
                new PdfDictionaryObject(
                [
                    new PdfDictionaryEntry("Im1", new PdfReferenceObject(imageId)),
                ])),
        ]);

        objects.Add(new PdfIndirectObject(imageId, new PdfStreamObject(imageDictionary, image.EncodedBytes)));
        if (softMaskId is PdfObjectId actualSoftMaskId)
        {
            PdfImageSoftMask softMask = image.SoftMask!.Value;
            PdfDictionaryObject softMaskDictionary = CreateSoftMaskImageXObjectDictionary(softMask);
            objects.Add(new PdfIndirectObject(actualSoftMaskId, new PdfStreamObject(softMaskDictionary, softMask.EncodedBytes)));
        }

        objects.Add(new PdfIndirectObject(resourcesId, resourcesDictionary));

        string imageContent = BuildImageContentStream(placement, "Im1");
        PdfStreamObject existingStream = RequireStreamObject(contentsReference.ObjectId, "Page contents");
        PdfStreamObject updatedStream = new(existingStream.Dictionary, Encoding.ASCII.GetBytes(imageContent));
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
        MarkDirty(imageId);
        if (softMaskId is PdfObjectId dirtySoftMaskId)
        {
            MarkDirty(dirtySoftMaskId);
        }

        MarkDirty(resourcesId);
    }

    /// <summary>
    /// Replaces page content with a single raster image loaded from disk.
    /// </summary>
    public void ReplacePageImage(int pageIndex, string imagePath, PdfImageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ReplacePageImage(pageIndex, File.ReadAllBytes(imagePath), options);
    }

    /// <summary>
    /// Appends a raster image overlay to an existing page while preserving current content.
    /// </summary>
    public void AddPageImage(int pageIndex, byte[] imageBytes, PdfImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfRasterImage image = PdfImageParser.Parse(imageBytes);
        PdfPageModel page = _model.Pages[pageIndex];
        PdfRectangle pageBounds = page.MediaBox ?? throw new NotSupportedException("Page /MediaBox is required for image placement.");
        PdfImageOptions effectiveOptions = options ?? new PdfImageOptions
        {
            X = pageBounds.Left,
            Y = pageBounds.Bottom,
            Width = image.Width,
            Height = image.Height,
            PreserveAspectRatio = true,
        };
        ValidateImageOptions(effectiveOptions);
        PdfImagePlacement placement = ResolveImagePlacement(image, effectiveOptions);

        List<PdfIndirectObject> objects = [.. _file.Objects];
        int nextObjectNumber = GetNextObjectNumber(objects);
        PdfObjectId? softMaskId = null;
        PdfObjectId imageId = new(nextObjectNumber++, 0);
        if (image.SoftMask is not null)
        {
            softMaskId = new PdfObjectId(nextObjectNumber++, 0);
        }

        PdfObjectId resourcesId = new(nextObjectNumber++, 0);
        PdfObjectId appendedContentId = new(nextObjectNumber++, 0);

        PdfDictionaryObject pageDictionary = RequireDictionaryObject(page.ObjectId, "Page");
        PdfDictionaryObject effectiveResources = ResolveEffectiveResourcesDictionary(page);
        string imageResourceName = AllocateImageResourceName(effectiveResources);
        PdfDictionaryObject mergedResources = BuildResourcesDictionaryWithImage(effectiveResources, imageResourceName, imageId);

        PdfDictionaryObject imageDictionary = CreateImageXObjectDictionary(image, softMaskId);
        objects.Add(new PdfIndirectObject(imageId, new PdfStreamObject(imageDictionary, image.EncodedBytes)));
        if (softMaskId is PdfObjectId actualSoftMaskId)
        {
            PdfImageSoftMask softMask = image.SoftMask!.Value;
            PdfDictionaryObject softMaskDictionary = CreateSoftMaskImageXObjectDictionary(softMask);
            objects.Add(new PdfIndirectObject(actualSoftMaskId, new PdfStreamObject(softMaskDictionary, softMask.EncodedBytes)));
        }

        objects.Add(new PdfIndirectObject(resourcesId, mergedResources));

        string imageContent = BuildImageContentStream(placement, imageResourceName);
        PdfStreamObject appendedImageStream = new(new PdfDictionaryObject([]), Encoding.ASCII.GetBytes(imageContent));
        objects.Add(new PdfIndirectObject(appendedContentId, appendedImageStream));

        PdfObject updatedContents = ComposeAppendedContentsValue(page.Contents, new PdfReferenceObject(appendedContentId));
        PdfDictionaryObject updatedPage = ReplaceDictionaryEntries(
            pageDictionary,
            new PdfDictionaryEntry("Resources", new PdfReferenceObject(resourcesId)),
            new PdfDictionaryEntry("Contents", updatedContents));
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
        MarkDirty(imageId);
        if (softMaskId is PdfObjectId dirtySoftMaskId)
        {
            MarkDirty(dirtySoftMaskId);
        }

        MarkDirty(resourcesId);
        MarkDirty(appendedContentId);
    }

    /// <summary>
    /// Appends a raster image overlay to an existing page using an image file path.
    /// </summary>
    public void AddPageImage(int pageIndex, string imagePath, PdfImageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        AddPageImage(pageIndex, File.ReadAllBytes(imagePath), options);
    }

    /// <summary>
    /// Draws a line on an existing page.
    /// </summary>
    public void AddPageLine(
        int pageIndex,
        double startX,
        double startY,
        double endX,
        double endY,
        PdfShapeOptions? options = null)
    {
        if (!double.IsFinite(startX))
        {
            throw new ArgumentOutOfRangeException(nameof(startX), "Line start X must be finite.");
        }

        if (!double.IsFinite(startY))
        {
            throw new ArgumentOutOfRangeException(nameof(startY), "Line start Y must be finite.");
        }

        if (!double.IsFinite(endX))
        {
            throw new ArgumentOutOfRangeException(nameof(endX), "Line end X must be finite.");
        }

        if (!double.IsFinite(endY))
        {
            throw new ArgumentOutOfRangeException(nameof(endY), "Line end Y must be finite.");
        }

        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        ValidateShapeOptions(effectiveOptions, requireStroke: true);

        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildLineContentStream(startX, startY, endX, endY, effectiveOptions, names));
    }

    /// <summary>
    /// Draws a rectangle on an existing page.
    /// </summary>
    public void AddPageRectangle(
        int pageIndex,
        double x,
        double y,
        double width,
        double height,
        PdfShapeOptions? options = null)
    {
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Rectangle X must be finite.");
        }

        if (!double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y), "Rectangle Y must be finite.");
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Rectangle width must be a positive finite number.");
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Rectangle height must be a positive finite number.");
        }

        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        ValidateShapeOptions(effectiveOptions);

        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildRectangleContentStream(x, y, width, height, effectiveOptions, names));
    }

    /// <summary>
    /// Draws a circle on an existing page.
    /// </summary>
    public void AddPageCircle(
        int pageIndex,
        double centerX,
        double centerY,
        double radius,
        PdfShapeOptions? options = null)
    {
        if (!double.IsFinite(centerX))
        {
            throw new ArgumentOutOfRangeException(nameof(centerX), "Circle center X must be finite.");
        }

        if (!double.IsFinite(centerY))
        {
            throw new ArgumentOutOfRangeException(nameof(centerY), "Circle center Y must be finite.");
        }

        if (!double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Circle radius must be a positive finite number.");
        }

        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        ValidateShapeOptions(effectiveOptions);

        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildCircleContentStream(centerX, centerY, radius, effectiveOptions, names));
    }

    /// <summary>
    /// Draws an ellipse on an existing page.
    /// </summary>
    public void AddPageEllipse(
        int pageIndex,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        PdfShapeOptions? options = null)
    {
        if (!double.IsFinite(centerX))
        {
            throw new ArgumentOutOfRangeException(nameof(centerX), "Ellipse center X must be finite.");
        }

        if (!double.IsFinite(centerY))
        {
            throw new ArgumentOutOfRangeException(nameof(centerY), "Ellipse center Y must be finite.");
        }

        if (!double.IsFinite(radiusX) || radiusX <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX), "Ellipse radiusX must be a positive finite number.");
        }

        if (!double.IsFinite(radiusY) || radiusY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusY), "Ellipse radiusY must be a positive finite number.");
        }

        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        ValidateShapeOptions(effectiveOptions);

        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildEllipseContentStream(centerX, centerY, radiusX, radiusY, effectiveOptions, names));
    }

    /// <summary>
    /// Draws a polygon/polyline on an existing page.
    /// </summary>
    public void AddPagePolygon(
        int pageIndex,
        IReadOnlyList<PdfShapePoint> points,
        bool closePath = true,
        PdfShapeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            throw new ArgumentException("Polygon requires at least two points.", nameof(points));
        }

        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        ValidateShapeOptions(effectiveOptions);

        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildPolygonContentStream(points, closePath, effectiveOptions, names));
    }

    /// <summary>
    /// Draws a custom path on an existing page.
    /// </summary>
    public void AddPagePath(
        int pageIndex,
        IReadOnlyList<PdfPathCommand> commands,
        PdfShapeOptions? options = null)
    {
        ValidatePathCommands(commands);
        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        ValidateShapeOptions(effectiveOptions);

        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildPathContentStream(commands, effectiveOptions, names));
    }

    /// <summary>
    /// Draws a rounded rectangle on an existing page.
    /// </summary>
    public void AddPageRoundedRectangle(
        int pageIndex,
        double x,
        double y,
        double width,
        double height,
        double radiusX,
        double radiusY,
        PdfShapeOptions? options = null)
    {
        if (!double.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Rounded rectangle X must be finite.");
        }

        if (!double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y), "Rounded rectangle Y must be finite.");
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Rounded rectangle width must be a positive finite number.");
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Rounded rectangle height must be a positive finite number.");
        }

        if (!double.IsFinite(radiusX) || radiusX < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX), "Rounded rectangle radiusX must be a non-negative finite number.");
        }

        if (!double.IsFinite(radiusY) || radiusY < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusY), "Rounded rectangle radiusY must be a non-negative finite number.");
        }

        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        ValidateShapeOptions(effectiveOptions);

        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildRoundedRectangleContentStream(x, y, width, height, radiusX, radiusY, effectiveOptions, names));
    }

    /// <summary>
    /// Draws an open arc path on an existing page.
    /// </summary>
    public void AddPageArc(
        int pageIndex,
        double centerX,
        double centerY,
        double radius,
        double startAngleDegrees,
        double endAngleDegrees,
        PdfShapeOptions? options = null)
    {
        if (!double.IsFinite(centerX))
        {
            throw new ArgumentOutOfRangeException(nameof(centerX), "Arc center X must be finite.");
        }

        if (!double.IsFinite(centerY))
        {
            throw new ArgumentOutOfRangeException(nameof(centerY), "Arc center Y must be finite.");
        }

        if (!double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Arc radius must be a positive finite number.");
        }

        if (!double.IsFinite(startAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(startAngleDegrees), "Arc start angle must be finite.");
        }

        if (!double.IsFinite(endAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(endAngleDegrees), "Arc end angle must be finite.");
        }

        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        if (effectiveOptions.FillColor is not null || effectiveOptions.FillLinearGradient is not null)
        {
            throw new ArgumentException("Arc is an open path and does not support fill styles.", nameof(options));
        }

        ValidateShapeOptions(effectiveOptions, requireStroke: true);
        List<PdfPathCommand> commands = BuildArcPathCommands(centerX, centerY, radius, radius, startAngleDegrees, endAngleDegrees);
        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildPathContentStream(commands, effectiveOptions, names));
    }

    /// <summary>
    /// Draws a closed sector (pie slice) on an existing page.
    /// </summary>
    public void AddPageSector(
        int pageIndex,
        double centerX,
        double centerY,
        double radius,
        double startAngleDegrees,
        double endAngleDegrees,
        PdfShapeOptions? options = null)
    {
        if (!double.IsFinite(centerX))
        {
            throw new ArgumentOutOfRangeException(nameof(centerX), "Sector center X must be finite.");
        }

        if (!double.IsFinite(centerY))
        {
            throw new ArgumentOutOfRangeException(nameof(centerY), "Sector center Y must be finite.");
        }

        if (!double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Sector radius must be a positive finite number.");
        }

        if (!double.IsFinite(startAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(startAngleDegrees), "Sector start angle must be finite.");
        }

        if (!double.IsFinite(endAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(endAngleDegrees), "Sector end angle must be finite.");
        }

        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions();
        ValidateShapeOptions(effectiveOptions);

        List<PdfPathCommand> commands = BuildArcPathCommands(centerX, centerY, radius, radius, startAngleDegrees, endAngleDegrees);
        commands.Add(new PdfPathLineTo(centerX, centerY));
        commands.Add(new PdfPathClosePath());

        AddPageShape(
            pageIndex,
            effectiveOptions,
            names => BuildPathContentStream(commands, effectiveOptions, names));
    }

    /// <summary>
    /// Draws a custom path using an explicit transform matrix.
    /// </summary>
    public void AddPagePathTransformed(
        int pageIndex,
        IReadOnlyList<PdfPathCommand> commands,
        PdfShapeTransform transform,
        PdfShapeOptions? options = null)
    {
        PdfShapeOptions mergedOptions = CloneShapeOptions(options ?? new PdfShapeOptions(), transform: transform);
        AddPagePath(pageIndex, commands, mergedOptions);
    }

    /// <summary>
    /// Draws a custom path constrained by a clipping path.
    /// </summary>
    public void AddPagePathClipped(
        int pageIndex,
        IReadOnlyList<PdfPathCommand> clipPath,
        IReadOnlyList<PdfPathCommand> commands,
        PdfShapeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clipPath);
        PdfShapeOptions mergedOptions = CloneShapeOptions(options ?? new PdfShapeOptions(), clipPath: clipPath);
        AddPagePath(pageIndex, commands, mergedOptions);
    }

    /// <summary>
    /// Returns unique shape IDs found on a page in first-seen order.
    /// </summary>
    public IReadOnlyList<string> GetPageShapeIds(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfPageModel page = _model.Pages[pageIndex];
        HashSet<string> seen = [];
        List<string> ids = [];
        foreach (PdfObjectId contentId in EnumerateContentStreamReferences(page.Contents))
        {
            PdfStreamObject stream = RequireStreamObject(contentId, "Page contents");
            string contentText = DecodeContentStreamText(stream, $"Page content stream {contentId}");
            foreach (string shapeId in EnumerateShapeMarkers(contentText))
            {
                if (seen.Add(shapeId))
                {
                    ids.Add(shapeId);
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// Removes shape content associated with a previously assigned shape ID.
    /// </summary>
    public void RemovePageShape(int pageIndex, string shapeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shapeId);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfPageModel page = _model.Pages[pageIndex];
        PdfObjectId contentId = FindShapeContentStreamId(page, shapeId);
        List<PdfIndirectObject> objects = [.. _file.Objects];
        PdfStreamObject existingStream = RequireStreamObject(contentId, "Page shape contents");
        ReplaceObject(objects, contentId, new PdfStreamObject(existingStream.Dictionary, ReadOnlyMemory<byte>.Empty));

        _file = new PdfFile(
            _file.Version,
            objects,
            _file.Trailer,
            _file.SourceBytes,
            _file.StartXrefOffset,
            _file.XrefEntries);
        _model = PdfDocumentModelBuilder.Build(_file);

        MarkDirty(contentId);
        MarkDirty(page.ObjectId);
    }

    /// <summary>
    /// Replaces a shape identified by ID with a new path and style.
    /// </summary>
    public void ReplacePageShape(int pageIndex, string shapeId, IReadOnlyList<PdfPathCommand> commands, PdfShapeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shapeId);
        RemovePageShape(pageIndex, shapeId);
        PdfShapeOptions mergedOptions = CloneShapeOptions(options ?? new PdfShapeOptions(), shapeId: shapeId);
        AddPagePath(pageIndex, commands, mergedOptions);
    }

    /// <summary>
    /// Replaces page text content with a single text block.
    /// </summary>
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

    /// <summary>
    /// Replaces page text content with rich text spans.
    /// </summary>
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

    /// <summary>
    /// Sets the document Info dictionary <c>/Producer</c> entry.
    /// </summary>
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

    /// <summary>
    /// Gets the document Info dictionary <c>/Producer</c> entry, if present.
    /// </summary>
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

    /// <summary>
    /// Performs destructive text replacement in text-showing operators and returns replacement count.
    /// </summary>
    public int RedactText(string target, string replacement = "")
    {
        return SoftRedactText(target, replacement);
    }

    /// <summary>
    /// Performs destructive text replacement in text-showing operators and returns replacement count.
    /// </summary>
    public int SoftRedactText(string target, string replacement = "")
    {
        if (string.IsNullOrEmpty(target))
        {
            throw new ArgumentException("Redaction target cannot be null or empty.", nameof(target));
        }

        ArgumentNullException.ThrowIfNull(replacement);
        return RewriteTextInContentStreams(
            segment =>
            {
                int replacements = CountOccurrences(segment, target);
                return replacements == 0
                    ? (segment, 0)
                    : (segment.Replace(target, replacement, StringComparison.Ordinal), replacements);
            },
            hardTarget: null,
            hardOptions: null,
            rewriteWithLayout: (segment, fontSize) => TryBuildLiteralSoftRedactionParts(segment, target, replacement, fontSize),
            rewriteWithLayoutAndRectangles: null,
            out _);
    }

    /// <summary>
    /// Performs pattern-based destructive text replacement in text-showing operators and returns replacement count.
    /// </summary>
    public int SoftRedactText(string pattern, MatchEvaluator evaluator, RegexOptions regexOptions = RegexOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(evaluator);

        Regex regex = new(pattern, regexOptions);
        return RewriteTextInContentStreams(
            segment =>
            {
                MatchCollection matches = regex.Matches(segment);
                if (matches.Count == 0)
                {
                    return (segment, 0);
                }

                return (regex.Replace(segment, evaluator), matches.Count);
            },
            hardTarget: null,
            hardOptions: null,
            rewriteWithLayout: (segment, fontSize) => TryBuildRegexSoftRedactionParts(segment, regex, evaluator, fontSize),
            rewriteWithLayoutAndRectangles: null,
            out _);
    }

    /// <summary>
    /// Performs pattern-based soft redaction by keeping only directive-selected text elements and boxing hidden spans.
    /// </summary>
    public int SoftRedactText(
        string pattern,
        Func<Match, PdfSoftRedactionDirective> directiveSelector,
        RegexOptions regexOptions = RegexOptions.None,
        PdfHardRedactionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(directiveSelector);

        PdfHardRedactionOptions effectiveOptions = options ?? new PdfHardRedactionOptions();
        ValidateHardRedactionOptions(effectiveOptions);
        Regex regex = new(pattern, regexOptions);

        int replacements = RewriteTextInContentStreams(
            static segment => (segment, 0),
            hardTarget: null,
            hardOptions: null,
            rewriteWithLayout: null,
            rewriteWithLayoutAndRectangles: (segment, fontSize, textX, textY, lineHeight, pageIndex) =>
                TryBuildDirectiveSoftRedactionParts(
                    segment,
                    regex,
                    directiveSelector,
                    effectiveOptions,
                    fontSize,
                    textX,
                    textY,
                    lineHeight,
                    pageIndex),
            out List<PdfRedactionRectangle> rectangles);

        if (replacements == 0 || rectangles.Count == 0)
        {
            return replacements;
        }

        List<PdfRedactionRectangle> mergedRectangles = MergeRedactionRectangles(rectangles);
        PdfShapeOptions boxStyle = new()
        {
            StrokeColor = null,
            FillColor = effectiveOptions.FillColor,
        };

        foreach (PdfRedactionRectangle rectangle in mergedRectangles)
        {
            AddPageRectangle(rectangle.PageIndex, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, boxStyle);
        }

        return replacements;
    }

    /// <summary>
    /// Performs irreversible hard redaction by erasing matched text operators and overlaying opaque blackout rectangles.
    /// </summary>
    public int HardRedactText(string target, PdfHardRedactionOptions? options = null)
    {
        if (string.IsNullOrEmpty(target))
        {
            throw new ArgumentException("Redaction target cannot be null or empty.", nameof(target));
        }

        PdfHardRedactionOptions effectiveOptions = options ?? new PdfHardRedactionOptions();
        ValidateHardRedactionOptions(effectiveOptions);

        Regex regex = new(Regex.Escape(target));
        (List<PdfTextMatch> matches, List<PdfTextMatchGroup> groups) = BuildTextMatchesWithGroups(ExtractTextRegionsCore(pageIndex: null), regex);
        if (matches.Count == 0)
        {
            return 0;
        }

        List<PdfRedactionRectangle>? whitespaceCoverage = ContainsWhitespace(target)
            ? BuildPhraseCoverageRectangles(groups, effectiveOptions)
            : null;

        return HardRedactTextCore(matches, effectiveOptions, whitespaceCoverage);
    }

    /// <summary>
    /// Performs irreversible hard redaction for exact matches returned by <see cref="FindText(string, RegexOptions)"/>.
    /// </summary>
    public int HardRedactText(IReadOnlyList<PdfTextMatch> matches, PdfHardRedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(matches);

        PdfHardRedactionOptions effectiveOptions = options ?? new PdfHardRedactionOptions();
        ValidateHardRedactionOptions(effectiveOptions);
        if (matches.Count == 0)
        {
            return 0;
        }

        return HardRedactTextCore(matches, effectiveOptions, extraRectangles: null);
    }

    private int HardRedactTextCore(
        IReadOnlyList<PdfTextMatch> matches,
        PdfHardRedactionOptions options,
        IReadOnlyList<PdfRedactionRectangle>? extraRectangles)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(options);

        Dictionary<PdfTextAnchorKey, List<PdfTextSelectionRange>> rangesByAnchor = BuildRedactionRangesByAnchor(matches);
        if (rangesByAnchor.Count == 0)
        {
            return 0;
        }

        int replacements = RewriteAnchoredHardRedactionsInContentStreams(
            rangesByAnchor,
            options,
            out List<PdfRedactionRectangle> rectangles);
        if (extraRectangles is not null && extraRectangles.Count > 0)
        {
            rectangles.AddRange(extraRectangles);
        }

        if (replacements == 0 || rectangles.Count == 0)
        {
            return replacements;
        }

        List<PdfRedactionRectangle> mergedRectangles = MergeRedactionRectangles(rectangles);
        PdfShapeOptions boxStyle = new()
        {
            StrokeColor = null,
            FillColor = options.FillColor,
        };

        foreach (PdfRedactionRectangle rectangle in mergedRectangles)
        {
            AddPageRectangle(rectangle.PageIndex, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, boxStyle);
        }

        return replacements;
    }

    /// <summary>
    /// Applies an irreversible hard redaction rectangle to explicit page bounds.
    /// </summary>
    public void HardRedactBounds(int pageIndex, double x, double y, double width, double height, PdfShapeOptions? options = null)
    {
        PdfShapeOptions effectiveOptions = options ?? new PdfShapeOptions
        {
            StrokeColor = null,
            FillColor = PdfRgbColor.Black,
        };

        if (effectiveOptions.FillLinearGradient is not null)
        {
            throw new ArgumentException("Hard redaction bounds do not support gradient fills.", nameof(options));
        }

        if (effectiveOptions.FillColor is null)
        {
            throw new ArgumentException("Hard redaction bounds require a solid FillColor.", nameof(options));
        }

        if (effectiveOptions.FillOpacity is double opacity && opacity < 1)
        {
            throw new ArgumentException("Hard redaction bounds require fully opaque fill.", nameof(options));
        }

        if (effectiveOptions.BlendMode != PdfBlendMode.Normal)
        {
            throw new ArgumentException("Hard redaction bounds require PdfBlendMode.Normal.", nameof(options));
        }

        AddPageRectangle(pageIndex, x, y, width, height, effectiveOptions);
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

    private static PdfImagePlacement ResolveImagePlacement(PdfRasterImage image, PdfImageOptions options)
    {
        double imageWidth = image.Width;
        double imageHeight = image.Height;
        double targetWidth;
        double targetHeight;

        if (options.Width is null && options.Height is null)
        {
            targetWidth = imageWidth;
            targetHeight = imageHeight;
        }
        else if (options.Width is double width && options.Height is null)
        {
            targetWidth = width;
            targetHeight = width * (imageHeight / imageWidth);
        }
        else if (options.Width is null && options.Height is double height)
        {
            targetHeight = height;
            targetWidth = height * (imageWidth / imageHeight);
        }
        else
        {
            targetWidth = options.Width!.Value;
            targetHeight = options.Height!.Value;
            if (options.PreserveAspectRatio)
            {
                double widthScale = targetWidth / imageWidth;
                double heightScale = targetHeight / imageHeight;
                double scale = Math.Min(widthScale, heightScale);
                targetWidth = imageWidth * scale;
                targetHeight = imageHeight * scale;
            }
        }

        double x = options.X;
        double y = options.Y;
        if (options.PreserveAspectRatio && options.Width is not null && options.Height is not null)
        {
            x += (options.Width.Value - targetWidth) / 2;
            y += (options.Height.Value - targetHeight) / 2;
        }

        return new PdfImagePlacement(x, y, targetWidth, targetHeight);
    }

    private static string BuildImageContentStream(PdfImagePlacement placement, string imageResourceName)
    {
        StringBuilder builder = new();
        builder.Append("q ");
        builder.Append(placement.Width.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(" 0 0 ");
        builder.Append(placement.Height.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(placement.X.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(placement.Y.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(" cm /");
        builder.Append(imageResourceName);
        builder.Append(" Do Q");
        return builder.ToString();
    }

    private static string BuildLineContentStream(
        double startX,
        double startY,
        double endX,
        double endY,
        PdfShapeOptions options,
        PdfShapeResourceNames resourceNames)
    {
        StringBuilder pathBuilder = new();
        AppendPdfNumber(pathBuilder, startX);
        pathBuilder.Append(' ');
        AppendPdfNumber(pathBuilder, startY);
        pathBuilder.Append(" m ");
        AppendPdfNumber(pathBuilder, endX);
        pathBuilder.Append(' ');
        AppendPdfNumber(pathBuilder, endY);
        pathBuilder.Append(" l");
        return BuildShapeContentStream(pathBuilder.ToString(), options, resourceNames);
    }

    private static string BuildRectangleContentStream(
        double x,
        double y,
        double width,
        double height,
        PdfShapeOptions options,
        PdfShapeResourceNames resourceNames)
    {
        StringBuilder pathBuilder = new();
        AppendPdfNumber(pathBuilder, x);
        pathBuilder.Append(' ');
        AppendPdfNumber(pathBuilder, y);
        pathBuilder.Append(' ');
        AppendPdfNumber(pathBuilder, width);
        pathBuilder.Append(' ');
        AppendPdfNumber(pathBuilder, height);
        pathBuilder.Append(" re");
        return BuildShapeContentStream(pathBuilder.ToString(), options, resourceNames);
    }

    private static string BuildCircleContentStream(
        double centerX,
        double centerY,
        double radius,
        PdfShapeOptions options,
        PdfShapeResourceNames resourceNames)
    {
        return BuildEllipseContentStream(centerX, centerY, radius, radius, options, resourceNames);
    }

    private static string BuildEllipseContentStream(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        PdfShapeOptions options,
        PdfShapeResourceNames resourceNames)
    {
        StringBuilder pathBuilder = new();
        AppendEllipsePath(pathBuilder, centerX, centerY, radiusX, radiusY);
        return BuildShapeContentStream(pathBuilder.ToString(), options, resourceNames);
    }

    private static string BuildPolygonContentStream(
        IReadOnlyList<PdfShapePoint> points,
        bool closePath,
        PdfShapeOptions options,
        PdfShapeResourceNames resourceNames)
    {
        StringBuilder pathBuilder = new();
        AppendPdfNumber(pathBuilder, points[0].X);
        pathBuilder.Append(' ');
        AppendPdfNumber(pathBuilder, points[0].Y);
        pathBuilder.Append(" m ");
        for (int index = 1; index < points.Count; index++)
        {
            PdfShapePoint point = points[index];
            AppendPdfNumber(pathBuilder, point.X);
            pathBuilder.Append(' ');
            AppendPdfNumber(pathBuilder, point.Y);
            pathBuilder.Append(" l ");
        }

        if (closePath)
        {
            pathBuilder.Append('h');
        }
        else if (pathBuilder.Length > 0 && pathBuilder[pathBuilder.Length - 1] == ' ')
        {
            pathBuilder.Length--;
        }

        return BuildShapeContentStream(pathBuilder.ToString(), options, resourceNames);
    }

    private static string BuildPathContentStream(
        IReadOnlyList<PdfPathCommand> commands,
        PdfShapeOptions options,
        PdfShapeResourceNames resourceNames)
    {
        return BuildShapeContentStream(BuildPathCommandString(commands), options, resourceNames);
    }

    private static string BuildRoundedRectangleContentStream(
        double x,
        double y,
        double width,
        double height,
        double radiusX,
        double radiusY,
        PdfShapeOptions options,
        PdfShapeResourceNames resourceNames)
    {
        StringBuilder pathBuilder = new();
        AppendRoundedRectanglePath(pathBuilder, x, y, width, height, radiusX, radiusY);
        return BuildShapeContentStream(pathBuilder.ToString(), options, resourceNames);
    }

    private static string BuildShapeContentStream(string pathCommands, PdfShapeOptions options, PdfShapeResourceNames resourceNames)
    {
        StringBuilder builder = new();
        builder.Append("q ");

        if (!options.Transform.Equals(PdfShapeTransform.Identity))
        {
            AppendPdfNumber(builder, options.Transform.A);
            builder.Append(' ');
            AppendPdfNumber(builder, options.Transform.B);
            builder.Append(' ');
            AppendPdfNumber(builder, options.Transform.C);
            builder.Append(' ');
            AppendPdfNumber(builder, options.Transform.D);
            builder.Append(' ');
            AppendPdfNumber(builder, options.Transform.E);
            builder.Append(' ');
            AppendPdfNumber(builder, options.Transform.F);
            builder.Append(" cm ");
        }

        if (options.ClipPath is { Count: > 0 } clipPath)
        {
            builder.Append(BuildPathCommandString(clipPath));
            builder.Append(" W n ");
        }

        AppendShapeGraphicsState(builder, options, resourceNames);
        builder.Append(pathCommands.TrimEnd());
        builder.Append(' ');
        builder.Append(ResolveShapePaintOperator(options));
        builder.Append(" Q");
        return builder.ToString();
    }

    private static string BuildPathCommandString(IReadOnlyList<PdfPathCommand> commands)
    {
        StringBuilder builder = new();
        bool subpathStarted = false;
        for (int index = 0; index < commands.Count; index++)
        {
            PdfPathCommand command = commands[index] ?? throw new ArgumentException($"Path command at index {index} cannot be null.", nameof(commands));
            switch (command)
            {
                case PdfPathMoveTo moveTo:
                    AppendPdfNumber(builder, moveTo.X);
                    builder.Append(' ');
                    AppendPdfNumber(builder, moveTo.Y);
                    builder.Append(" m ");
                    subpathStarted = true;
                    break;
                case PdfPathLineTo lineTo:
                    if (!subpathStarted)
                    {
                        throw new ArgumentException("Path line commands must follow a move command.", nameof(commands));
                    }

                    AppendPdfNumber(builder, lineTo.X);
                    builder.Append(' ');
                    AppendPdfNumber(builder, lineTo.Y);
                    builder.Append(" l ");
                    break;
                case PdfPathCurveTo curveTo:
                    if (!subpathStarted)
                    {
                        throw new ArgumentException("Path curve commands must follow a move command.", nameof(commands));
                    }

                    AppendPdfNumber(builder, curveTo.Control1X);
                    builder.Append(' ');
                    AppendPdfNumber(builder, curveTo.Control1Y);
                    builder.Append(' ');
                    AppendPdfNumber(builder, curveTo.Control2X);
                    builder.Append(' ');
                    AppendPdfNumber(builder, curveTo.Control2Y);
                    builder.Append(' ');
                    AppendPdfNumber(builder, curveTo.EndX);
                    builder.Append(' ');
                    AppendPdfNumber(builder, curveTo.EndY);
                    builder.Append(" c ");
                    break;
                case PdfPathClosePath:
                    if (!subpathStarted)
                    {
                        throw new ArgumentException("Path close commands must follow a move command.", nameof(commands));
                    }

                    builder.Append("h ");
                    break;
                default:
                    throw new NotSupportedException($"Path command type '{command.GetType().Name}' is not supported.");
            }
        }

        if (builder.Length > 0 && builder[builder.Length - 1] == ' ')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    private static void AppendEllipsePath(StringBuilder builder, double centerX, double centerY, double radiusX, double radiusY)
    {
        const double BezierControlRatio = 0.5522847498307936d;
        double xOffset = radiusX * BezierControlRatio;
        double yOffset = radiusY * BezierControlRatio;
        double right = centerX + radiusX;
        double left = centerX - radiusX;
        double top = centerY + radiusY;
        double bottom = centerY - radiusY;

        AppendPdfNumber(builder, right);
        builder.Append(' ');
        AppendPdfNumber(builder, centerY);
        builder.Append(" m ");

        AppendPdfNumber(builder, right);
        builder.Append(' ');
        AppendPdfNumber(builder, centerY + yOffset);
        builder.Append(' ');
        AppendPdfNumber(builder, centerX + xOffset);
        builder.Append(' ');
        AppendPdfNumber(builder, top);
        builder.Append(' ');
        AppendPdfNumber(builder, centerX);
        builder.Append(' ');
        AppendPdfNumber(builder, top);
        builder.Append(" c ");

        AppendPdfNumber(builder, centerX - xOffset);
        builder.Append(' ');
        AppendPdfNumber(builder, top);
        builder.Append(' ');
        AppendPdfNumber(builder, left);
        builder.Append(' ');
        AppendPdfNumber(builder, centerY + yOffset);
        builder.Append(' ');
        AppendPdfNumber(builder, left);
        builder.Append(' ');
        AppendPdfNumber(builder, centerY);
        builder.Append(" c ");

        AppendPdfNumber(builder, left);
        builder.Append(' ');
        AppendPdfNumber(builder, centerY - yOffset);
        builder.Append(' ');
        AppendPdfNumber(builder, centerX - xOffset);
        builder.Append(' ');
        AppendPdfNumber(builder, bottom);
        builder.Append(' ');
        AppendPdfNumber(builder, centerX);
        builder.Append(' ');
        AppendPdfNumber(builder, bottom);
        builder.Append(" c ");

        AppendPdfNumber(builder, centerX + xOffset);
        builder.Append(' ');
        AppendPdfNumber(builder, bottom);
        builder.Append(' ');
        AppendPdfNumber(builder, right);
        builder.Append(' ');
        AppendPdfNumber(builder, centerY - yOffset);
        builder.Append(' ');
        AppendPdfNumber(builder, right);
        builder.Append(' ');
        AppendPdfNumber(builder, centerY);
        builder.Append(" c h ");
    }

    private static void AppendRoundedRectanglePath(StringBuilder builder, double x, double y, double width, double height, double radiusX, double radiusY)
    {
        const double BezierControlRatio = 0.5522847498307936d;
        double rx = Math.Min(radiusX, width / 2d);
        double ry = Math.Min(radiusY, height / 2d);
        double kx = rx * BezierControlRatio;
        double ky = ry * BezierControlRatio;

        AppendPdfNumber(builder, x + rx);
        builder.Append(' ');
        AppendPdfNumber(builder, y);
        builder.Append(" m ");

        AppendPdfNumber(builder, x + width - rx);
        builder.Append(' ');
        AppendPdfNumber(builder, y);
        builder.Append(" l ");

        AppendPdfNumber(builder, x + width - rx + kx);
        builder.Append(' ');
        AppendPdfNumber(builder, y);
        builder.Append(' ');
        AppendPdfNumber(builder, x + width);
        builder.Append(' ');
        AppendPdfNumber(builder, y + ry - ky);
        builder.Append(' ');
        AppendPdfNumber(builder, x + width);
        builder.Append(' ');
        AppendPdfNumber(builder, y + ry);
        builder.Append(" c ");

        AppendPdfNumber(builder, x + width);
        builder.Append(' ');
        AppendPdfNumber(builder, y + height - ry);
        builder.Append(" l ");

        AppendPdfNumber(builder, x + width);
        builder.Append(' ');
        AppendPdfNumber(builder, y + height - ry + ky);
        builder.Append(' ');
        AppendPdfNumber(builder, x + width - rx + kx);
        builder.Append(' ');
        AppendPdfNumber(builder, y + height);
        builder.Append(' ');
        AppendPdfNumber(builder, x + width - rx);
        builder.Append(' ');
        AppendPdfNumber(builder, y + height);
        builder.Append(" c ");

        AppendPdfNumber(builder, x + rx);
        builder.Append(' ');
        AppendPdfNumber(builder, y + height);
        builder.Append(" l ");

        AppendPdfNumber(builder, x + rx - kx);
        builder.Append(' ');
        AppendPdfNumber(builder, y + height);
        builder.Append(' ');
        AppendPdfNumber(builder, x);
        builder.Append(' ');
        AppendPdfNumber(builder, y + height - ry + ky);
        builder.Append(' ');
        AppendPdfNumber(builder, x);
        builder.Append(' ');
        AppendPdfNumber(builder, y + height - ry);
        builder.Append(" c ");

        AppendPdfNumber(builder, x);
        builder.Append(' ');
        AppendPdfNumber(builder, y + ry);
        builder.Append(" l ");

        AppendPdfNumber(builder, x);
        builder.Append(' ');
        AppendPdfNumber(builder, y + ry - ky);
        builder.Append(' ');
        AppendPdfNumber(builder, x + rx - kx);
        builder.Append(' ');
        AppendPdfNumber(builder, y);
        builder.Append(' ');
        AppendPdfNumber(builder, x + rx);
        builder.Append(' ');
        AppendPdfNumber(builder, y);
        builder.Append(" c h");
    }

    private static void AppendShapeGraphicsState(StringBuilder builder, PdfShapeOptions options, PdfShapeResourceNames resourceNames)
    {
        if (options.StrokeColor is PdfRgbColor strokeColor)
        {
            AppendRgbColor(builder, strokeColor);
            builder.Append(" RG ");
            AppendPdfNumber(builder, options.StrokeWidth);
            builder.Append(" w ");
            builder.Append(((int)options.StrokeLineCap).ToString(CultureInfo.InvariantCulture));
            builder.Append(" J ");
            builder.Append(((int)options.StrokeLineJoin).ToString(CultureInfo.InvariantCulture));
            builder.Append(" j ");
            AppendPdfNumber(builder, options.StrokeMiterLimit);
            builder.Append(" M ");
            if (options.StrokeDashPattern is PdfShapeDashPattern dashPattern)
            {
                builder.Append('[');
                for (int index = 0; index < dashPattern.Segments.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(' ');
                    }

                    AppendPdfNumber(builder, dashPattern.Segments[index]);
                }

                builder.Append("] ");
                AppendPdfNumber(builder, dashPattern.Phase);
                builder.Append(" d ");
            }
        }

        if (resourceNames.GraphicsStateName is string graphicsStateName)
        {
            builder.Append('/');
            builder.Append(graphicsStateName);
            builder.Append(" gs ");
        }

        if (resourceNames.PatternName is string patternName && options.FillLinearGradient is not null)
        {
            builder.Append("/Pattern cs /");
            builder.Append(patternName);
            builder.Append(" scn ");
        }
        else if (options.FillColor is PdfRgbColor fillColor)
        {
            AppendRgbColor(builder, fillColor);
            builder.Append(" rg ");
        }
    }

    private static void AppendRgbColor(StringBuilder builder, PdfRgbColor color)
    {
        AppendPdfNumber(builder, color.Red);
        builder.Append(' ');
        AppendPdfNumber(builder, color.Green);
        builder.Append(' ');
        AppendPdfNumber(builder, color.Blue);
    }

    private static void AppendPdfNumber(StringBuilder builder, double value)
    {
        builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static string ResolveShapePaintOperator(PdfShapeOptions options)
    {
        bool stroke = options.StrokeColor is not null;
        bool fill = options.FillColor is not null || options.FillLinearGradient is not null;
        return stroke
            ? fill
                ? options.FillRule == PdfShapeFillRule.EvenOdd ? "B*" : "B"
                : "S"
            : options.FillRule == PdfShapeFillRule.EvenOdd ? "f*" : "f";
    }

    private static PdfDictionaryObject CreateImageXObjectDictionary(PdfRasterImage image, PdfObjectId? softMaskObjectId = null)
    {
        List<PdfDictionaryEntry> entries =
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("XObject")),
            new PdfDictionaryEntry("Subtype", new PdfNameObject("Image")),
            new PdfDictionaryEntry("Width", new PdfNumberObject(image.Width, isInteger: true)),
            new PdfDictionaryEntry("Height", new PdfNumberObject(image.Height, isInteger: true)),
            new PdfDictionaryEntry("ColorSpace", new PdfNameObject(image.ColorSpace)),
            new PdfDictionaryEntry("BitsPerComponent", new PdfNumberObject(image.BitsPerComponent, isInteger: true)),
            new PdfDictionaryEntry("Filter", new PdfNameObject(image.Filter)),
        ];

        if (image.Predictor is int predictor && image.Colors is int colors && image.Columns is int columns)
        {
            entries.Add(
                new PdfDictionaryEntry(
                    "DecodeParms",
                    new PdfDictionaryObject(
                    [
                        new PdfDictionaryEntry("Predictor", new PdfNumberObject(predictor, isInteger: true)),
                        new PdfDictionaryEntry("Colors", new PdfNumberObject(colors, isInteger: true)),
                        new PdfDictionaryEntry("BitsPerComponent", new PdfNumberObject(image.BitsPerComponent, isInteger: true)),
                        new PdfDictionaryEntry("Columns", new PdfNumberObject(columns, isInteger: true)),
            ])));
        }

        if (softMaskObjectId is PdfObjectId actualSoftMaskId)
        {
            entries.Add(new PdfDictionaryEntry("SMask", new PdfReferenceObject(actualSoftMaskId)));
        }

        return new PdfDictionaryObject(entries);
    }

    private static PdfDictionaryObject CreateSoftMaskImageXObjectDictionary(PdfImageSoftMask softMask)
    {
        List<PdfDictionaryEntry> entries =
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("XObject")),
            new PdfDictionaryEntry("Subtype", new PdfNameObject("Image")),
            new PdfDictionaryEntry("Width", new PdfNumberObject(softMask.Width, isInteger: true)),
            new PdfDictionaryEntry("Height", new PdfNumberObject(softMask.Height, isInteger: true)),
            new PdfDictionaryEntry("ColorSpace", new PdfNameObject("DeviceGray")),
            new PdfDictionaryEntry("BitsPerComponent", new PdfNumberObject(softMask.BitsPerComponent, isInteger: true)),
            new PdfDictionaryEntry("Filter", new PdfNameObject(softMask.Filter)),
        ];

        if (softMask.Predictor is int predictor && softMask.Colors is int colors && softMask.Columns is int columns)
        {
            entries.Add(
                new PdfDictionaryEntry(
                    "DecodeParms",
                    new PdfDictionaryObject(
                    [
                        new PdfDictionaryEntry("Predictor", new PdfNumberObject(predictor, isInteger: true)),
                        new PdfDictionaryEntry("Colors", new PdfNumberObject(colors, isInteger: true)),
                        new PdfDictionaryEntry("BitsPerComponent", new PdfNumberObject(softMask.BitsPerComponent, isInteger: true)),
                        new PdfDictionaryEntry("Columns", new PdfNumberObject(columns, isInteger: true)),
                    ])));
        }

        return new PdfDictionaryObject(entries);
    }

    private PdfDictionaryObject ResolveEffectiveResourcesDictionary(PdfPageModel page)
    {
        if (page.Resources is null)
        {
            return new PdfDictionaryObject([]);
        }

        if (!TryResolveDictionaryObject(page.Resources, out PdfDictionaryObject? resourcesDictionary))
        {
            throw new NotSupportedException("Page /Resources must be a dictionary or dictionary reference for image composition.");
        }

        return resourcesDictionary!;
    }

    private string AllocateImageResourceName(PdfDictionaryObject resourcesDictionary)
    {
        return AllocateResourceName(resourcesDictionary, "XObject", "Im");
    }

    private PdfDictionaryObject BuildResourcesDictionaryWithImage(PdfDictionaryObject resourcesDictionary, string imageResourceName, PdfObjectId imageId)
    {
        return BuildResourcesDictionaryWithEntries(
            resourcesDictionary,
            [new PdfPageResourceEntry("XObject", imageResourceName, imageId)],
            "image composition");
    }

    private string AllocateResourceName(PdfDictionaryObject resourcesDictionary, string categoryKey, string prefix)
    {
        HashSet<string> usedNames = [];
        if (TryGetDictionaryEntry(resourcesDictionary, categoryKey, out PdfObject? categoryValue))
        {
            if (!TryResolveDictionaryObject(categoryValue!, out PdfDictionaryObject? categoryDictionary))
            {
                throw new NotSupportedException($"Page /Resources /{categoryKey} must be a dictionary or dictionary reference for resource composition.");
            }

            foreach (PdfDictionaryEntry entry in categoryDictionary!.Entries)
            {
                usedNames.Add(entry.Key);
            }
        }

        int index = 1;
        while (true)
        {
            string candidate = $"{prefix}{index.ToString(CultureInfo.InvariantCulture)}";
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private PdfDictionaryObject BuildResourcesDictionaryWithEntries(
        PdfDictionaryObject resourcesDictionary,
        IReadOnlyList<PdfPageResourceEntry> entriesToAdd,
        string operationContext)
    {
        if (entriesToAdd.Count == 0)
        {
            return resourcesDictionary;
        }

        PdfDictionaryObject updatedResources = resourcesDictionary;
        foreach (IGrouping<string, PdfPageResourceEntry> categoryGroup in entriesToAdd.GroupBy(static entry => entry.CategoryKey, StringComparer.Ordinal))
        {
            List<PdfDictionaryEntry> categoryEntries = [];
            if (TryGetDictionaryEntry(updatedResources, categoryGroup.Key, out PdfObject? categoryValue))
            {
                if (!TryResolveDictionaryObject(categoryValue!, out PdfDictionaryObject? categoryDictionary))
                {
                    throw new NotSupportedException($"Page /Resources /{categoryGroup.Key} must be a dictionary or dictionary reference for {operationContext}.");
                }

                categoryEntries.AddRange(categoryDictionary!.Entries);
            }

            foreach (PdfPageResourceEntry resourceEntry in categoryGroup)
            {
                categoryEntries.RemoveAll(existing => string.Equals(existing.Key, resourceEntry.ResourceName, StringComparison.Ordinal));
                categoryEntries.Add(new PdfDictionaryEntry(resourceEntry.ResourceName, new PdfReferenceObject(resourceEntry.ObjectId)));
            }

            updatedResources = ReplaceDictionaryEntries(
                updatedResources,
                new PdfDictionaryEntry(categoryGroup.Key, new PdfDictionaryObject(categoryEntries)));
        }

        return updatedResources;
    }

    private void AddPageShape(int pageIndex, PdfShapeOptions options, Func<PdfShapeResourceNames, string> buildContent)
    {
        ArgumentNullException.ThrowIfNull(buildContent);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);
        PdfPageModel page = _model.Pages[pageIndex];
        PdfShapeResourceAllocation resourceAllocation = CreateShapeResourceAllocation(page, options);
        string shapeContent = buildContent(resourceAllocation.Names);
        string wrappedContent = WrapShapeContentWithMarker(shapeContent, options.ShapeId);
        AddPageRawContent(
            pageIndex,
            wrappedContent,
            resourceAllocation.ObjectsToAdd,
            resourceAllocation.ResourceEntries,
            resourceAllocation.DirtyObjectIds);
    }

    private PdfShapeResourceAllocation CreateShapeResourceAllocation(PdfPageModel page, PdfShapeOptions options)
    {
        List<PdfIndirectObject> objectsToAdd = [];
        List<PdfPageResourceEntry> resourceEntries = [];
        List<PdfObjectId> dirtyObjectIds = [];

        PdfDictionaryObject effectiveResources = ResolveEffectiveResourcesDictionary(page);
        int nextObjectNumber = GetNextObjectNumber(_file.Objects);

        string? graphicsStateName = null;
        if (options.StrokeOpacity is not null || options.FillOpacity is not null || options.BlendMode != PdfBlendMode.Normal)
        {
            PdfObjectId graphicsStateId = new(nextObjectNumber++, 0);
            graphicsStateName = AllocateResourceName(effectiveResources, "ExtGState", "GS");
            PdfDictionaryObject graphicsStateDictionary = CreateShapeGraphicsStateDictionary(options);
            objectsToAdd.Add(new PdfIndirectObject(graphicsStateId, graphicsStateDictionary));
            resourceEntries.Add(new PdfPageResourceEntry("ExtGState", graphicsStateName, graphicsStateId));
            dirtyObjectIds.Add(graphicsStateId);
        }

        string? patternName = null;
        if (options.FillLinearGradient is PdfShapeLinearGradient gradient)
        {
            PdfObjectId patternId = new(nextObjectNumber++, 0);
            patternName = AllocateResourceName(effectiveResources, "Pattern", "Pt");
            PdfDictionaryObject patternDictionary = CreateLinearGradientPatternDictionary(gradient);
            objectsToAdd.Add(new PdfIndirectObject(patternId, patternDictionary));
            resourceEntries.Add(new PdfPageResourceEntry("Pattern", patternName, patternId));
            dirtyObjectIds.Add(patternId);
        }

        return new PdfShapeResourceAllocation(
            new PdfShapeResourceNames(graphicsStateName, patternName),
            objectsToAdd,
            resourceEntries,
            dirtyObjectIds);
    }

    private static PdfDictionaryObject CreateShapeGraphicsStateDictionary(PdfShapeOptions options)
    {
        List<PdfDictionaryEntry> entries = [];
        if (options.StrokeOpacity is double strokeOpacity)
        {
            entries.Add(new PdfDictionaryEntry("CA", new PdfNumberObject(strokeOpacity, isInteger: false)));
        }

        if (options.FillOpacity is double fillOpacity)
        {
            entries.Add(new PdfDictionaryEntry("ca", new PdfNumberObject(fillOpacity, isInteger: false)));
        }

        if (options.BlendMode != PdfBlendMode.Normal)
        {
            entries.Add(new PdfDictionaryEntry("BM", new PdfNameObject(MapBlendModeName(options.BlendMode))));
        }

        return new PdfDictionaryObject(entries);
    }

    private static PdfDictionaryObject CreateLinearGradientPatternDictionary(PdfShapeLinearGradient gradient)
    {
        PdfDictionaryObject interpolationFunction = new(
        [
            new PdfDictionaryEntry("FunctionType", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Domain", new PdfArrayObject([new PdfNumberObject(0, isInteger: true), new PdfNumberObject(1, isInteger: true)])),
            new PdfDictionaryEntry("C0", new PdfArrayObject([new PdfNumberObject(gradient.StartColor.Red, isInteger: false), new PdfNumberObject(gradient.StartColor.Green, isInteger: false), new PdfNumberObject(gradient.StartColor.Blue, isInteger: false)])),
            new PdfDictionaryEntry("C1", new PdfArrayObject([new PdfNumberObject(gradient.EndColor.Red, isInteger: false), new PdfNumberObject(gradient.EndColor.Green, isInteger: false), new PdfNumberObject(gradient.EndColor.Blue, isInteger: false)])),
            new PdfDictionaryEntry("N", new PdfNumberObject(1, isInteger: true)),
        ]);

        PdfDictionaryObject shading = new(
        [
            new PdfDictionaryEntry("ShadingType", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("ColorSpace", new PdfNameObject("DeviceRGB")),
            new PdfDictionaryEntry(
                "Coords",
                new PdfArrayObject(
                [
                    new PdfNumberObject(gradient.StartX, isInteger: false),
                    new PdfNumberObject(gradient.StartY, isInteger: false),
                    new PdfNumberObject(gradient.EndX, isInteger: false),
                    new PdfNumberObject(gradient.EndY, isInteger: false),
                ])),
            new PdfDictionaryEntry("Function", interpolationFunction),
            new PdfDictionaryEntry("Extend", new PdfArrayObject([new PdfBooleanObject(true), new PdfBooleanObject(true)])),
        ]);

        return new PdfDictionaryObject(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Pattern")),
            new PdfDictionaryEntry("PatternType", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Shading", shading),
        ]);
    }

    private static string MapBlendModeName(PdfBlendMode blendMode)
    {
        return blendMode switch
        {
            PdfBlendMode.Normal => "Normal",
            PdfBlendMode.Multiply => "Multiply",
            PdfBlendMode.Screen => "Screen",
            PdfBlendMode.Overlay => "Overlay",
            PdfBlendMode.Darken => "Darken",
            PdfBlendMode.Lighten => "Lighten",
            PdfBlendMode.ColorDodge => "ColorDodge",
            PdfBlendMode.ColorBurn => "ColorBurn",
            PdfBlendMode.HardLight => "HardLight",
            PdfBlendMode.SoftLight => "SoftLight",
            PdfBlendMode.Difference => "Difference",
            PdfBlendMode.Exclusion => "Exclusion",
            _ => throw new ArgumentOutOfRangeException(nameof(blendMode), "BlendMode contains an unsupported value."),
        };
    }

    private static string WrapShapeContentWithMarker(string content, string? shapeId)
    {
        if (string.IsNullOrWhiteSpace(shapeId))
        {
            return content;
        }

        return $"%MP_SHAPE_BEGIN:{shapeId}\n{content}\n%MP_SHAPE_END:{shapeId}\n";
    }

    private PdfObjectId FindShapeContentStreamId(PdfPageModel page, string shapeId)
    {
        foreach (PdfObjectId contentId in EnumerateContentStreamReferences(page.Contents))
        {
            PdfStreamObject stream = RequireStreamObject(contentId, "Page contents");
            string contentText = DecodeContentStreamText(stream, $"Page content stream {contentId}");
            if (contentText.Contains($"%MP_SHAPE_BEGIN:{shapeId}", StringComparison.Ordinal))
            {
                return contentId;
            }
        }

        throw new InvalidOperationException($"Shape '{shapeId}' was not found on page.");
    }

    private static IEnumerable<string> EnumerateShapeMarkers(string content)
    {
        const string token = "%MP_SHAPE_BEGIN:";
        int index = 0;
        while (index < content.Length)
        {
            int markerIndex = content.IndexOf(token, index, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                yield break;
            }

            int valueStart = markerIndex + token.Length;
            int valueEnd = content.IndexOf('\n', valueStart);
            if (valueEnd < 0)
            {
                valueEnd = content.Length;
            }

            if (valueEnd > valueStart)
            {
                yield return content[valueStart..valueEnd].TrimEnd('\r');
            }

            index = valueEnd;
        }
    }

    private static PdfShapeOptions CloneShapeOptions(
        PdfShapeOptions source,
        string? shapeId = null,
        PdfShapeTransform? transform = null,
        IReadOnlyList<PdfPathCommand>? clipPath = null)
    {
        return new PdfShapeOptions
        {
            StrokeColor = source.StrokeColor,
            FillColor = source.FillColor,
            StrokeWidth = source.StrokeWidth,
            StrokeLineCap = source.StrokeLineCap,
            StrokeLineJoin = source.StrokeLineJoin,
            StrokeMiterLimit = source.StrokeMiterLimit,
            StrokeDashPattern = source.StrokeDashPattern,
            FillRule = source.FillRule,
            StrokeOpacity = source.StrokeOpacity,
            FillOpacity = source.FillOpacity,
            BlendMode = source.BlendMode,
            FillLinearGradient = source.FillLinearGradient,
            Transform = transform ?? source.Transform,
            ClipPath = clipPath ?? source.ClipPath,
            ShapeId = shapeId ?? source.ShapeId,
        };
    }

    private static List<PdfPathCommand> BuildArcPathCommands(
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        double startAngleDegrees,
        double endAngleDegrees)
    {
        double sweepDegrees = endAngleDegrees - startAngleDegrees;
        if (Math.Abs(sweepDegrees) < 0.0001d)
        {
            throw new ArgumentException("Arc span must be non-zero.", nameof(endAngleDegrees));
        }

        if (Math.Abs(sweepDegrees) > 360d)
        {
            sweepDegrees = Math.Sign(sweepDegrees) * 360d;
        }

        int segmentCount = (int)Math.Ceiling(Math.Abs(sweepDegrees) / 90d);
        double segmentSweep = sweepDegrees / segmentCount;

        List<PdfPathCommand> commands = [];
        for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            double a0Degrees = startAngleDegrees + (segmentSweep * segmentIndex);
            double a1Degrees = a0Degrees + segmentSweep;
            double a0 = a0Degrees * Math.PI / 180d;
            double a1 = a1Degrees * Math.PI / 180d;
            double k = (4d / 3d) * Math.Tan((a1 - a0) / 4d);

            double cos0 = Math.Cos(a0);
            double sin0 = Math.Sin(a0);
            double cos1 = Math.Cos(a1);
            double sin1 = Math.Sin(a1);

            double p0x = centerX + (radiusX * cos0);
            double p0y = centerY + (radiusY * sin0);
            double p3x = centerX + (radiusX * cos1);
            double p3y = centerY + (radiusY * sin1);
            double c1x = p0x - (k * radiusX * sin0);
            double c1y = p0y + (k * radiusY * cos0);
            double c2x = p3x + (k * radiusX * sin1);
            double c2y = p3y - (k * radiusY * cos1);

            if (segmentIndex == 0)
            {
                commands.Add(new PdfPathMoveTo(p0x, p0y));
            }

            commands.Add(new PdfPathCurveTo(c1x, c1y, c2x, c2y, p3x, p3y));
        }

        return commands;
    }

    private void AddPageRawContent(
        int pageIndex,
        string rawContentStream,
        IReadOnlyList<PdfIndirectObject>? objectsToAdd = null,
        IReadOnlyList<PdfPageResourceEntry>? resourceEntries = null,
        IReadOnlyList<PdfObjectId>? additionalDirtyObjectIds = null)
    {
        ArgumentNullException.ThrowIfNull(rawContentStream);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _model.Pages.Count);

        PdfPageModel page = _model.Pages[pageIndex];
        PdfDictionaryObject pageDictionary = RequireDictionaryObject(page.ObjectId, "Page");

        List<PdfIndirectObject> objects = [.. _file.Objects];
        if (objectsToAdd is { Count: > 0 })
        {
            objects.AddRange(objectsToAdd);
        }

        PdfObject? resourcesReplacement = null;
        PdfObjectId? resourcesId = null;
        if (resourceEntries is { Count: > 0 })
        {
            PdfDictionaryObject effectiveResources = ResolveEffectiveResourcesDictionary(page);
            PdfDictionaryObject mergedResources = BuildResourcesDictionaryWithEntries(effectiveResources, resourceEntries, "content composition");
            resourcesId = new PdfObjectId(GetNextObjectNumber(objects), 0);
            objects.Add(new PdfIndirectObject(resourcesId.Value, mergedResources));
            resourcesReplacement = new PdfReferenceObject(resourcesId.Value);
        }

        PdfObjectId appendedContentId = new(GetNextObjectNumber(objects), 0);
        objects.Add(new PdfIndirectObject(appendedContentId, new PdfStreamObject(new PdfDictionaryObject([]), Encoding.ASCII.GetBytes(rawContentStream))));

        PdfObject updatedContents = ComposeAppendedContentsValue(page.Contents, new PdfReferenceObject(appendedContentId));
        PdfDictionaryObject updatedPage = resourcesReplacement is null
            ? ReplaceDictionaryEntries(
                pageDictionary,
                new PdfDictionaryEntry("Contents", updatedContents))
            : ReplaceDictionaryEntries(
                pageDictionary,
                new PdfDictionaryEntry("Contents", updatedContents),
                new PdfDictionaryEntry("Resources", resourcesReplacement));
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
        MarkDirty(appendedContentId);
        if (resourcesId is PdfObjectId dirtyResourcesId)
        {
            MarkDirty(dirtyResourcesId);
        }

        if (additionalDirtyObjectIds is not null)
        {
            foreach (PdfObjectId objectId in additionalDirtyObjectIds)
            {
                MarkDirty(objectId);
            }
        }
    }

    private static PdfObject ComposeAppendedContentsValue(PdfObject? existingContents, PdfReferenceObject appendedContentReference)
    {
        return existingContents switch
        {
            null => appendedContentReference,
            PdfReferenceObject contentReference => new PdfArrayObject([contentReference, appendedContentReference]),
            PdfArrayObject contentArray => new PdfArrayObject([.. contentArray.Items, appendedContentReference]),
            _ => throw new NotSupportedException("Page /Contents must be a reference or array for content composition."),
        };
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

    private PdfArrayObject RequireArrayObject(PdfObjectId id, string context)
    {
        foreach (PdfIndirectObject indirectObject in _file.Objects)
        {
            if (indirectObject.ObjectId == id)
            {
                return indirectObject.Value as PdfArrayObject
                    ?? throw new PdfFormatException($"{context} object {id} is not an array.");
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

    private static Dictionary<string, PdfFontGlyphWidths> BuildFontWidthMapsForPage(
        PdfObject? resourcesObject,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap)
    {
        if (!TryResolveDictionaryFromMap(resourcesObject, objectMap, out PdfDictionaryObject? resolvedResources)
            || resolvedResources is null)
        {
            return new Dictionary<string, PdfFontGlyphWidths>(StringComparer.Ordinal);
        }

        if (!TryGetDictionaryEntry(resolvedResources, "Font", out PdfObject? fontObject)
            || !TryResolveDictionaryFromMap(fontObject, objectMap, out PdfDictionaryObject? resolvedFonts)
            || resolvedFonts is null)
        {
            return new Dictionary<string, PdfFontGlyphWidths>(StringComparer.Ordinal);
        }

        Dictionary<string, PdfFontGlyphWidths> maps = new(StringComparer.Ordinal);
        foreach (PdfDictionaryEntry fontEntry in resolvedFonts.Entries)
        {
            if (!TryResolveDictionaryFromMap(fontEntry.Value, objectMap, out PdfDictionaryObject? resolvedFontDictionary)
                || resolvedFontDictionary is null)
            {
                continue;
            }

            if (TryCreateFontGlyphWidths(resolvedFontDictionary, objectMap, out PdfFontGlyphWidths? widths))
            {
                maps[fontEntry.Key] = widths;
            }
        }

        return maps;
    }

    private static bool TryCreateFontGlyphWidths(
        PdfDictionaryObject fontDictionary,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        out PdfFontGlyphWidths widths)
    {
        widths = null!;
        if (!TryGetDictionaryEntry(fontDictionary, "Subtype", out PdfObject? subtypeObject)
            || subtypeObject is not PdfNameObject subtypeName)
        {
            return false;
        }

        string subtype = subtypeName.Value;
        if (string.Equals(subtype, "Type0", StringComparison.Ordinal))
        {
            if (!TryGetDictionaryEntry(fontDictionary, "DescendantFonts", out PdfObject? descendantFontsObject)
                || !TryResolveArrayFromMap(descendantFontsObject, objectMap, out PdfArrayObject? descendantFontsArray)
                || descendantFontsArray is null
                || descendantFontsArray.Items.Count == 0
                || !TryResolveDictionaryFromMap(descendantFontsArray.Items[0], objectMap, out PdfDictionaryObject? descendantFont)
                || descendantFont is null)
            {
                return false;
            }

            double defaultWidth = 1000;
            if (TryGetDictionaryEntry(descendantFont, "DW", out PdfObject? defaultWidthObject)
                && defaultWidthObject is not null
                && TryReadNumber(defaultWidthObject, out double parsedDefaultWidth))
            {
                defaultWidth = parsedDefaultWidth;
            }

            Dictionary<int, double> glyphWidths = [];
            if (TryGetDictionaryEntry(descendantFont, "W", out PdfObject? widthsObject)
                && TryResolveArrayFromMap(widthsObject, objectMap, out PdfArrayObject? widthsArray)
                && widthsArray is not null)
            {
                ParseCidWidthArray(widthsArray, objectMap, glyphWidths);
            }

            widths = new PdfFontGlyphWidths(isComposite: true, glyphWidths, defaultWidth);
            return true;
        }

        if (string.Equals(subtype, "Type1", StringComparison.Ordinal)
            || string.Equals(subtype, "TrueType", StringComparison.Ordinal)
            || string.Equals(subtype, "MMType1", StringComparison.Ordinal))
        {
            if (!TryGetDictionaryEntry(fontDictionary, "FirstChar", out PdfObject? firstCharObject)
                || firstCharObject is null
                || !TryReadInteger(firstCharObject, out int firstChar))
            {
                return false;
            }

            if (!TryGetDictionaryEntry(fontDictionary, "Widths", out PdfObject? widthsObject)
                || !TryResolveArrayFromMap(widthsObject, objectMap, out PdfArrayObject? widthsArray)
                || widthsArray is null)
            {
                return false;
            }

            double defaultWidth = 0;
            if (TryGetDictionaryEntry(fontDictionary, "FontDescriptor", out PdfObject? descriptorObject)
                && TryResolveDictionaryFromMap(descriptorObject, objectMap, out PdfDictionaryObject? descriptorDictionary)
                && descriptorDictionary is not null
                && TryGetDictionaryEntry(descriptorDictionary, "MissingWidth", out PdfObject? missingWidthObject)
                && missingWidthObject is not null
                && TryReadNumber(missingWidthObject, out double missingWidth))
            {
                defaultWidth = missingWidth;
            }

            Dictionary<int, double> glyphWidths = [];
            for (int index = 0; index < widthsArray.Items.Count; index++)
            {
                if (!TryReadNumber(widthsArray.Items[index], out double width))
                {
                    continue;
                }

                glyphWidths[firstChar + index] = width;
            }

            widths = new PdfFontGlyphWidths(isComposite: false, glyphWidths, defaultWidth);
            return true;
        }

        return false;
    }

    private static void ParseCidWidthArray(
        PdfArrayObject widthsArray,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        Dictionary<int, double> glyphWidths)
    {
        int index = 0;
        while (index < widthsArray.Items.Count)
        {
            if (!TryReadInteger(widthsArray.Items[index], out int startCid))
            {
                index++;
                continue;
            }

            index++;
            if (index >= widthsArray.Items.Count)
            {
                break;
            }

            if (TryResolveArrayFromMap(widthsArray.Items[index], objectMap, out PdfArrayObject? contiguousWidths)
                && contiguousWidths is not null)
            {
                for (int widthIndex = 0; widthIndex < contiguousWidths.Items.Count; widthIndex++)
                {
                    if (TryReadNumber(contiguousWidths.Items[widthIndex], out double width))
                    {
                        glyphWidths[startCid + widthIndex] = width;
                    }
                }

                index++;
                continue;
            }

            if (TryReadInteger(widthsArray.Items[index], out int endCid)
                && index + 1 < widthsArray.Items.Count
                && TryReadNumber(widthsArray.Items[index + 1], out double rangeWidth))
            {
                for (int cid = startCid; cid <= endCid; cid++)
                {
                    glyphWidths[cid] = rangeWidth;
                }

                index += 2;
                continue;
            }

            index++;
        }
    }

    private static bool TryReadNumber(PdfObject value, out double number)
    {
        if (value is PdfNumberObject numberObject)
        {
            number = numberObject.Value;
            return true;
        }

        number = 0;
        return false;
    }

    private static bool TryReadInteger(PdfObject value, out int integer)
    {
        integer = 0;
        if (!TryReadNumber(value, out double number))
        {
            return false;
        }

        if (!double.IsFinite(number))
        {
            return false;
        }

        if (number < int.MinValue || number > int.MaxValue)
        {
            return false;
        }

        integer = (int)Math.Round(number, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryResolveObjectFromMap(
        PdfObject? source,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        out PdfObject? resolved)
    {
        resolved = source;
        if (resolved is null)
        {
            return false;
        }

        HashSet<PdfObjectId> visited = [];
        while (resolved is PdfReferenceObject reference)
        {
            if (!visited.Add(reference.ObjectId) || !objectMap.TryGetValue(reference.ObjectId, out PdfIndirectObject? indirectObject))
            {
                resolved = null;
                return false;
            }

            resolved = indirectObject.Value;
        }

        return true;
    }

    private static bool TryResolveDictionaryFromMap(
        PdfObject? source,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        out PdfDictionaryObject? dictionary)
    {
        dictionary = null;
        if (!TryResolveObjectFromMap(source, objectMap, out PdfObject? resolved))
        {
            return false;
        }

        dictionary = resolved as PdfDictionaryObject;
        return dictionary is not null;
    }

    private static bool TryResolveArrayFromMap(
        PdfObject? source,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        out PdfArrayObject? array)
    {
        array = null;
        if (!TryResolveObjectFromMap(source, objectMap, out PdfObject? resolved))
        {
            return false;
        }

        array = resolved as PdfArrayObject;
        return array is not null;
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

    private static string DecodeContentStreamText(PdfStreamObject streamObject, string context)
    {
        byte[] decodedContent = PdfFileReader.DecodeStreamDataForExtraction(streamObject, context);
        return ContentStreamEncoding.GetString(decodedContent);
    }

    private static ReadOnlyMemory<byte> EncodeContentStreamText(PdfStreamObject streamObject, string content, string context)
    {
        byte[] encodedContent = ContentStreamEncoding.GetBytes(content);
        List<string> filters = GetContentStreamFilterNames(streamObject.Dictionary);
        for (int index = filters.Count - 1; index >= 0; index--)
        {
            string filter = filters[index];
            if (string.Equals(filter, "FlateDecode", StringComparison.Ordinal)
                || string.Equals(filter, "Fl", StringComparison.Ordinal))
            {
                encodedContent = EncodeFlateData(encodedContent);
                continue;
            }

            throw new PdfFormatException($"Unsupported stream filter '/{filter}' in {context}.");
        }

        return encodedContent;
    }

    private static List<string> GetContentStreamFilterNames(PdfDictionaryObject dictionary)
    {
        if (!TryGetDictionaryEntry(dictionary, "Filter", out PdfObject? filterObject))
        {
            return [];
        }

        switch (filterObject)
        {
            case PdfNameObject filterName:
                return [filterName.Value];
            case PdfArrayObject filterArray:
            {
                List<string> names = [];
                foreach (PdfObject item in filterArray.Items)
                {
                    if (item is not PdfNameObject nameObject)
                    {
                        throw new PdfFormatException("Stream '/Filter' array entries must be names.");
                    }

                    names.Add(nameObject.Value);
                }

                return names;
            }
            default:
                throw new PdfFormatException("Stream '/Filter' must be a name or an array of names.");
        }
    }

    private static byte[] EncodeFlateData(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream stream = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            stream.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private List<PdfTextRegion> ExtractTextRegionsCore(int? pageIndex)
    {
        Dictionary<PdfObjectId, PdfIndirectObject> objectMap = PdfTextExtractor.BuildObjectMap(_file.Objects);
        List<PdfTextRegion> regions = [];
        int startPage = pageIndex ?? 0;
        int endPage = pageIndex is null ? _model.Pages.Count : pageIndex.Value + 1;

        for (int currentPageIndex = startPage; currentPageIndex < endPage; currentPageIndex++)
        {
            PdfPageModel page = _model.Pages[currentPageIndex];
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont =
                PdfTextExtractor.BuildToUnicodeMapsForPage(page.Resources, objectMap);
            IReadOnlyDictionary<string, PdfFontGlyphWidths> fontWidthsByFont =
                BuildFontWidthMapsForPage(page.Resources, objectMap);
            foreach (PdfObjectId streamId in EnumerateContentStreamReferences(page.Contents))
            {
                PdfStreamObject stream = RequireStreamObject(streamId, "Page contents");
                string content = DecodeContentStreamText(stream, $"Page content stream {streamId}");
                regions.AddRange(ExtractTextRegionsFromStream(content, currentPageIndex, streamId, toUnicodeByFont, fontWidthsByFont));
            }
        }

        return regions;
    }

    private static List<PdfTextRegion> ExtractTextRegionsFromStream(
        string content,
        int pageIndex,
        PdfObjectId streamId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont,
        IReadOnlyDictionary<string, PdfFontGlyphWidths> fontWidthsByFont)
    {
        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(ContentStreamEncoding.GetBytes(content));
        List<PdfTextRegion> regions = [];

        bool inTextObject = false;
        IReadOnlyDictionary<int, string>? activeToUnicode = null;
        PdfFontGlyphWidths? activeFontWidths = null;
        double fontSize = 12;
        double textX = 0;
        double textY = 0;
        double characterSpacing = 0;
        double wordSpacing = 0;
        double horizontalScale = 100;
        double lineHeightEstimate = fontSize * 1.2;
        double? lastShownBaselineY = null;
        int index = 0;

        while (index < tokens.Count)
        {
            PdfToken token = tokens[index];
            if (token.Kind == PdfTokenKind.Keyword)
            {
                if (string.Equals(token.Lexeme, "BT", StringComparison.Ordinal))
                {
                    inTextObject = true;
                    textX = 0;
                    textY = 0;
                    characterSpacing = 0;
                    wordSpacing = 0;
                    horizontalScale = 100;
                    lineHeightEstimate = fontSize * 1.2;
                    lastShownBaselineY = null;
                }
                else if (string.Equals(token.Lexeme, "ET", StringComparison.Ordinal))
                {
                    inTextObject = false;
                }
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == PdfTokenKind.Keyword)
            {
                if (string.Equals(tokens[index + 1].Lexeme, "Tc", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedCharacterSpacing))
                    {
                        characterSpacing = parsedCharacterSpacing;
                    }

                    index += 2;
                    continue;
                }

                if (string.Equals(tokens[index + 1].Lexeme, "Tw", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedWordSpacing))
                    {
                        wordSpacing = parsedWordSpacing;
                    }

                    index += 2;
                    continue;
                }

                if (string.Equals(tokens[index + 1].Lexeme, "Tz", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedHorizontalScale) && double.IsFinite(parsedHorizontalScale))
                    {
                        horizontalScale = parsedHorizontalScale;
                    }

                    index += 2;
                    continue;
                }
            }

            if (inTextObject
                && token.Kind == PdfTokenKind.Name
                && index + 2 < tokens.Count
                && TryParseTokenDouble(tokens[index + 1], out double parsedFontSize)
                && tokens[index + 2].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 2].Lexeme, "Tf", StringComparison.Ordinal))
            {
                activeToUnicode = toUnicodeByFont.TryGetValue(token.Lexeme, out IReadOnlyDictionary<int, string>? map)
                    ? map
                    : null;
                activeFontWidths = fontWidthsByFont.TryGetValue(token.Lexeme, out PdfFontGlyphWidths? widths)
                    ? widths
                    : null;
                fontSize = Math.Abs(parsedFontSize);
                lineHeightEstimate = Math.Max(lineHeightEstimate, fontSize * 1.2);
                index += 3;
                continue;
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 2 < tokens.Count
                && IsNumberToken(tokens[index + 1])
                && tokens[index + 2].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 2].Lexeme, "Td", StringComparison.Ordinal))
            {
                if (TryParseTokenDouble(token, out double tx) && TryParseTokenDouble(tokens[index + 1], out double ty))
                {
                    textX += tx;
                    textY += ty;
                    if (Math.Abs(ty) > 0.01)
                    {
                        lineHeightEstimate = Math.Abs(ty);
                    }
                }

                index += 3;
                continue;
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 6 < tokens.Count
                && IsNumberToken(tokens[index + 1])
                && IsNumberToken(tokens[index + 2])
                && IsNumberToken(tokens[index + 3])
                && IsNumberToken(tokens[index + 4])
                && IsNumberToken(tokens[index + 5])
                && tokens[index + 6].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 6].Lexeme, "Tm", StringComparison.Ordinal))
            {
                if (TryParseTokenDouble(tokens[index + 4], out double matrixX)
                    && TryParseTokenDouble(tokens[index + 5], out double matrixY))
                {
                    double deltaY = Math.Abs(matrixY - textY);
                    textX = matrixX;
                    textY = matrixY;
                    if (lastShownBaselineY is not null && deltaY > 0.01)
                    {
                        lineHeightEstimate = deltaY;
                    }
                }

                index += 7;
                continue;
            }

            if (inTextObject
                && PdfTextExtractor.IsTextStringToken(token)
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == PdfTokenKind.Keyword
                && IsSingleStringTextOperator(tokens[index + 1].Lexeme))
            {
                string segment = PdfTextExtractor.DecodeTextToken(token, activeToUnicode);
                if (lastShownBaselineY is double previousBaselineY)
                {
                    double shownDelta = Math.Abs(textY - previousBaselineY);
                    if (shownDelta > 0.01)
                    {
                        lineHeightEstimate = shownDelta;
                    }
                }

                double effectiveLineHeight = ResolveRedactionLineHeight(fontSize, lineHeightEstimate);
                double segmentWidth = MeasureTokenTextWidth(
                    token,
                    segment,
                    activeToUnicode,
                    activeFontWidths,
                    fontSize,
                    characterSpacing,
                    wordSpacing,
                    horizontalScale);
                AddTextRegion(regions, pageIndex, segment, textX, textY, segmentWidth, fontSize, effectiveLineHeight, streamId, index);
                textX += segmentWidth;
                lastShownBaselineY = textY;
                index += 2;
                continue;
            }

            if (inTextObject && token.Kind == PdfTokenKind.StartArray)
            {
                int depth = 1;
                int arrayEndIndex = index + 1;

                while (arrayEndIndex < tokens.Count && depth > 0)
                {
                    PdfToken itemToken = tokens[arrayEndIndex];
                    if (itemToken.Kind == PdfTokenKind.StartArray)
                    {
                        depth++;
                    }
                    else if (itemToken.Kind == PdfTokenKind.EndArray)
                    {
                        depth--;
                    }

                    arrayEndIndex++;
                }

                if (depth != 0)
                {
                    throw new PdfFormatException("Unterminated array in content stream.");
                }

                if (arrayEndIndex < tokens.Count
                    && tokens[arrayEndIndex].Kind == PdfTokenKind.Keyword
                    && string.Equals(tokens[arrayEndIndex].Lexeme, "TJ", StringComparison.Ordinal))
                {
                    int nestedDepth = 1;
                    for (int itemIndex = index + 1; itemIndex < arrayEndIndex; itemIndex++)
                    {
                        PdfToken item = tokens[itemIndex];
                        if (item.Kind == PdfTokenKind.StartArray)
                        {
                            nestedDepth++;
                            continue;
                        }

                        if (item.Kind == PdfTokenKind.EndArray)
                        {
                            nestedDepth--;
                            continue;
                        }

                        if (nestedDepth != 1)
                        {
                            continue;
                        }

                        if (PdfTextExtractor.IsTextStringToken(item))
                        {
                            string segment = PdfTextExtractor.DecodeTextToken(item, activeToUnicode);
                            if (lastShownBaselineY is double previousBaselineY)
                            {
                                double shownDelta = Math.Abs(textY - previousBaselineY);
                                if (shownDelta > 0.01)
                                {
                                    lineHeightEstimate = shownDelta;
                                }
                            }

                            double effectiveLineHeight = ResolveRedactionLineHeight(fontSize, lineHeightEstimate);
                            double segmentWidth = MeasureTokenTextWidth(
                                item,
                                segment,
                                activeToUnicode,
                                activeFontWidths,
                                fontSize,
                                characterSpacing,
                                wordSpacing,
                                horizontalScale);
                            AddTextRegion(regions, pageIndex, segment, textX, textY, segmentWidth, fontSize, effectiveLineHeight, streamId, itemIndex);
                            textX += segmentWidth;
                            lastShownBaselineY = textY;
                        }
                        else if (IsNumberToken(item) && TryParseTokenDouble(item, out double adjustment))
                        {
                            textX -= ResolveTjAdjustmentWidth(adjustment, fontSize, horizontalScale);
                        }
                    }

                    index = arrayEndIndex + 1;
                    continue;
                }
            }

            index++;
        }

        return regions;
    }

    private static void AddTextRegion(
        List<PdfTextRegion> regions,
        int pageIndex,
        string segment,
        double textX,
        double textY,
        double width,
        double fontSize,
        double lineHeight,
        PdfObjectId streamId,
        int stringTokenIndex)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return;
        }

        if (width <= 0)
        {
            return;
        }

        (double y, double height) = ComputeTextRegionVerticalPlacement(textY, fontSize, lineHeight);
        if (height <= 0)
        {
            return;
        }

        regions.Add(new PdfTextRegion(
            pageIndex,
            segment,
            textX,
            y,
            width,
            height,
            streamId.ObjectNumber,
            streamId.GenerationNumber,
            stringTokenIndex,
            fontSize));
    }

    private readonly record struct PdfTextMatchGroup(int PageIndex, List<PdfTextMatch> Segments);

    private static List<PdfTextMatch> BuildTextMatches(IReadOnlyList<PdfTextRegion> regions, Regex regex)
    {
        (List<PdfTextMatch> matches, _) = BuildTextMatchesWithGroups(regions, regex);
        return matches;
    }

    private static (List<PdfTextMatch> Matches, List<PdfTextMatchGroup> Groups) BuildTextMatchesWithGroups(
        IReadOnlyList<PdfTextRegion> regions,
        Regex regex)
    {
        List<PdfTextMatch> matches = [];
        List<PdfTextMatchGroup> groups = [];
        foreach (IGrouping<int, PdfTextRegion> pageGroup in regions.GroupBy(static region => region.PageIndex))
        {
            List<(PdfTextRegion Region, int Start, int Length)> spans = [];
            StringBuilder pageTextBuilder = new();
            foreach (PdfTextRegion region in pageGroup)
            {
                if (string.IsNullOrEmpty(region.Text))
                {
                    continue;
                }

                int start = pageTextBuilder.Length;
                pageTextBuilder.Append(region.Text);
                spans.Add((region, start, region.Text.Length));
            }

            if (spans.Count == 0)
            {
                continue;
            }

            string pageText = pageTextBuilder.ToString();
            MatchCollection pageMatches = regex.Matches(pageText);
            foreach (Match pageMatch in pageMatches)
            {
                if (!pageMatch.Success || pageMatch.Length == 0)
                {
                    continue;
                }

                int matchStart = pageMatch.Index;
                int matchEnd = checked(pageMatch.Index + pageMatch.Length);
                List<PdfTextMatch> groupSegments = [];
                foreach ((PdfTextRegion region, int regionStart, int regionLength) in spans)
                {
                    int regionEnd = checked(regionStart + regionLength);
                    if (regionEnd <= matchStart)
                    {
                        continue;
                    }

                    if (regionStart >= matchEnd)
                    {
                        break;
                    }

                    int overlapStart = Math.Max(matchStart, regionStart);
                    int overlapEnd = Math.Min(matchEnd, regionEnd);
                    if (overlapEnd <= overlapStart)
                    {
                        continue;
                    }

                    int segmentStart = overlapStart - regionStart;
                    int segmentLength = overlapEnd - overlapStart;
                    string segmentText = region.Text.Substring(segmentStart, segmentLength);
                    double matchX = region.X + EstimateRedactionTextWidth(region.Text[..segmentStart], region.FontSize);
                    double matchWidth = EstimateRedactionTextWidth(segmentText, region.FontSize);

                    PdfTextMatch match = new(
                        region.PageIndex,
                        segmentText,
                        matchX,
                        region.Y,
                        matchWidth,
                        region.Height,
                        region.StreamObjectNumber,
                        region.StreamObjectGeneration,
                        region.StringTokenIndex,
                        segmentStart,
                        segmentLength);
                    matches.Add(match);
                    groupSegments.Add(match);
                }

                if (groupSegments.Count > 0)
                {
                    groups.Add(new PdfTextMatchGroup(pageGroup.Key, groupSegments));
                }
            }
        }

        return (matches, groups);
    }

    private Dictionary<PdfTextAnchorKey, List<PdfTextSelectionRange>> BuildRedactionRangesByAnchor(IReadOnlyList<PdfTextMatch> matches)
    {
        Dictionary<PdfTextAnchorKey, List<PdfTextSelectionRange>> rangesByAnchor = [];

        foreach (PdfTextMatch match in matches)
        {
            ArgumentNullException.ThrowIfNull(match);
            if (match.StartIndex < 0 || match.Length <= 0)
            {
                throw new ArgumentException("Each text match must include a positive-length segment.", nameof(matches));
            }

            if (match.PageIndex < 0 || match.PageIndex >= _model.Pages.Count)
            {
                throw new ArgumentException("Text matches must target valid document page indexes.", nameof(matches));
            }

            PdfTextAnchorKey key = new(
                match.StreamObjectNumber,
                match.StreamObjectGeneration,
                match.StringTokenIndex);
            if (!rangesByAnchor.TryGetValue(key, out List<PdfTextSelectionRange>? ranges))
            {
                ranges = [];
                rangesByAnchor.Add(key, ranges);
            }

            ranges.Add(new PdfTextSelectionRange(match.StartIndex, match.Length));
        }

        foreach (List<PdfTextSelectionRange> ranges in rangesByAnchor.Values)
        {
            ranges.Sort(static (left, right) =>
            {
                int startComparison = left.Start.CompareTo(right.Start);
                return startComparison != 0 ? startComparison : left.Length.CompareTo(right.Length);
            });

            if (ranges.Count == 0)
            {
                continue;
            }

            List<PdfTextSelectionRange> normalized = [ranges[0]];
            for (int index = 1; index < ranges.Count; index++)
            {
                PdfTextSelectionRange current = ranges[index];
                PdfTextSelectionRange previous = normalized[^1];

                int previousEnd;
                int currentEnd;
                try
                {
                    previousEnd = checked(previous.Start + previous.Length);
                    currentEnd = checked(current.Start + current.Length);
                }
                catch (OverflowException)
                {
                    throw new ArgumentException("Text match offsets are out of supported range.", nameof(matches));
                }

                if (current.Start <= previousEnd)
                {
                    int mergedEnd = Math.Max(previousEnd, currentEnd);
                    normalized[^1] = new PdfTextSelectionRange(previous.Start, mergedEnd - previous.Start);
                }
                else
                {
                    normalized.Add(current);
                }
            }

            ranges.Clear();
            ranges.AddRange(normalized);
        }

        return rangesByAnchor;
    }

    private static List<PdfRedactionRectangle> BuildPhraseCoverageRectangles(
        IReadOnlyList<PdfTextMatchGroup> groups,
        PdfHardRedactionOptions options)
    {
        List<PdfRedactionRectangle> rectangles = [];
        foreach (PdfTextMatchGroup group in groups)
        {
            if (group.Segments.Count == 0)
            {
                continue;
            }

            List<PdfTextMatch> orderedSegments = [.. group.Segments
                .Where(static segment => segment.Width > 0 && segment.Height > 0)
                .OrderBy(static segment => segment.Y)
                .ThenBy(static segment => segment.X)];
            if (orderedSegments.Count == 0)
            {
                continue;
            }

            double lineTolerance = Math.Max(0.5, orderedSegments.Max(static segment => segment.Height) * 0.35);
            List<PdfTextMatch> currentLine = [];
            double currentLineCenterY = 0;

            void FlushCurrentLine()
            {
                if (currentLine.Count == 0)
                {
                    return;
                }

                double minX = currentLine.Min(static segment => segment.X);
                double maxX = currentLine.Max(static segment => segment.X + segment.Width);
                double minY = currentLine.Min(static segment => segment.Y);
                double maxY = currentLine.Max(static segment => segment.Y + segment.Height);

                double x = minX - options.HorizontalPadding;
                double y = minY - options.VerticalPadding;
                double width = (maxX - minX) + (options.HorizontalPadding * 2);
                double height = (maxY - minY) + (options.VerticalPadding * 2);
                if (width > 0 && height > 0)
                {
                    rectangles.Add(new PdfRedactionRectangle(group.PageIndex, x, y, width, height));
                }

                currentLine.Clear();
            }

            foreach (PdfTextMatch segment in orderedSegments)
            {
                double segmentCenterY = segment.Y + (segment.Height / 2);
                if (currentLine.Count == 0)
                {
                    currentLine.Add(segment);
                    currentLineCenterY = segmentCenterY;
                    continue;
                }

                if (Math.Abs(segmentCenterY - currentLineCenterY) > lineTolerance)
                {
                    FlushCurrentLine();
                    currentLine.Add(segment);
                    currentLineCenterY = segmentCenterY;
                    continue;
                }

                currentLine.Add(segment);
                currentLineCenterY = ((currentLineCenterY * (currentLine.Count - 1)) + segmentCenterY) / currentLine.Count;
            }

            FlushCurrentLine();
        }

        return rectangles;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private int RewriteAnchoredHardRedactionsInContentStreams(
        IReadOnlyDictionary<PdfTextAnchorKey, List<PdfTextSelectionRange>> rangesByAnchor,
        PdfHardRedactionOptions options,
        out List<PdfRedactionRectangle> redactionRectangles)
    {
        Dictionary<PdfObjectId, PdfIndirectObject> objectMap = PdfTextExtractor.BuildObjectMap(_file.Objects);
        List<PdfIndirectObject> objects = [.. _file.Objects];
        HashSet<PdfObjectId> processedStreamIds = [];
        HashSet<PdfObjectId> changedStreamIds = [];
        redactionRectangles = [];
        int totalReplacements = 0;

        for (int pageIndex = 0; pageIndex < _model.Pages.Count; pageIndex++)
        {
            PdfPageModel page = _model.Pages[pageIndex];
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont =
                PdfTextExtractor.BuildToUnicodeMapsForPage(page.Resources, objectMap);
            IReadOnlyDictionary<string, PdfFontGlyphWidths> fontWidthsByFont =
                BuildFontWidthMapsForPage(page.Resources, objectMap);
            foreach (PdfObjectId streamId in EnumerateContentStreamReferences(page.Contents))
            {
                PdfStreamObject stream = RequireStreamObject(streamId, "Page contents");
                string content = DecodeContentStreamText(stream, $"Page content stream {streamId}");

                if (!processedStreamIds.Add(streamId))
                {
                    (_, _, List<PdfRedactionRectangle> duplicateRectangles) = RewriteAnchoredHardRedactionsInStream(
                        content,
                        pageIndex,
                        streamId,
                        rangesByAnchor,
                        options,
                        toUnicodeByFont,
                        fontWidthsByFont);
                    if (duplicateRectangles.Count > 0)
                    {
                        redactionRectangles.AddRange(duplicateRectangles);
                    }

                    continue;
                }

                (string updatedContent, int replacements, List<PdfRedactionRectangle> streamRectangles) = RewriteAnchoredHardRedactionsInStream(
                    content,
                    pageIndex,
                    streamId,
                    rangesByAnchor,
                    options,
                    toUnicodeByFont,
                    fontWidthsByFont);
                totalReplacements += replacements;
                if (streamRectangles.Count > 0)
                {
                    redactionRectangles.AddRange(streamRectangles);
                }

                if (!string.Equals(updatedContent, content, StringComparison.Ordinal))
                {
                    ReplaceObject(objects, streamId, new PdfStreamObject(stream.Dictionary, EncodeContentStreamText(stream, updatedContent, $"Page content stream {streamId}")));
                    changedStreamIds.Add(streamId);
                }
            }
        }

        if (changedStreamIds.Count == 0)
        {
            return totalReplacements;
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

        return totalReplacements;
    }

    private static (string UpdatedContent, int Replacements, List<PdfRedactionRectangle> Rectangles) RewriteAnchoredHardRedactionsInStream(
        string content,
        int pageIndex,
        PdfObjectId streamId,
        IReadOnlyDictionary<PdfTextAnchorKey, List<PdfTextSelectionRange>> rangesByAnchor,
        PdfHardRedactionOptions options,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont,
        IReadOnlyDictionary<string, PdfFontGlyphWidths> fontWidthsByFont)
    {
        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(ContentStreamEncoding.GetBytes(content));
        Dictionary<int, string> rawTokenOverrides = [];
        HashSet<int> removedTokenIndices = [];
        List<PdfRedactionRectangle> rectangles = [];
        int replacements = 0;

        bool inTextObject = false;
        IReadOnlyDictionary<int, string>? activeToUnicode = null;
        PdfFontGlyphWidths? activeFontWidths = null;
        double fontSize = 12;
        double textX = 0;
        double textY = 0;
        double characterSpacing = 0;
        double wordSpacing = 0;
        double horizontalScale = 100;
        double lineHeightEstimate = fontSize * 1.2;
        double? lastShownBaselineY = null;
        int index = 0;

        while (index < tokens.Count)
        {
            PdfToken token = tokens[index];
            if (token.Kind == PdfTokenKind.Keyword)
            {
                if (string.Equals(token.Lexeme, "BT", StringComparison.Ordinal))
                {
                    inTextObject = true;
                    textX = 0;
                    textY = 0;
                    characterSpacing = 0;
                    wordSpacing = 0;
                    horizontalScale = 100;
                    lineHeightEstimate = fontSize * 1.2;
                    lastShownBaselineY = null;
                }
                else if (string.Equals(token.Lexeme, "ET", StringComparison.Ordinal))
                {
                    inTextObject = false;
                }
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == PdfTokenKind.Keyword)
            {
                if (string.Equals(tokens[index + 1].Lexeme, "Tc", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedCharacterSpacing))
                    {
                        characterSpacing = parsedCharacterSpacing;
                    }

                    index += 2;
                    continue;
                }

                if (string.Equals(tokens[index + 1].Lexeme, "Tw", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedWordSpacing))
                    {
                        wordSpacing = parsedWordSpacing;
                    }

                    index += 2;
                    continue;
                }

                if (string.Equals(tokens[index + 1].Lexeme, "Tz", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedHorizontalScale) && double.IsFinite(parsedHorizontalScale))
                    {
                        horizontalScale = parsedHorizontalScale;
                    }

                    index += 2;
                    continue;
                }
            }

            if (inTextObject
                && token.Kind == PdfTokenKind.Name
                && index + 2 < tokens.Count
                && TryParseTokenDouble(tokens[index + 1], out double parsedFontSize)
                && tokens[index + 2].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 2].Lexeme, "Tf", StringComparison.Ordinal))
            {
                activeToUnicode = toUnicodeByFont.TryGetValue(token.Lexeme, out IReadOnlyDictionary<int, string>? map)
                    ? map
                    : null;
                activeFontWidths = fontWidthsByFont.TryGetValue(token.Lexeme, out PdfFontGlyphWidths? widths)
                    ? widths
                    : null;
                fontSize = Math.Abs(parsedFontSize);
                lineHeightEstimate = Math.Max(lineHeightEstimate, fontSize * 1.2);
                index += 3;
                continue;
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 2 < tokens.Count
                && IsNumberToken(tokens[index + 1])
                && tokens[index + 2].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 2].Lexeme, "Td", StringComparison.Ordinal))
            {
                if (TryParseTokenDouble(token, out double tx) && TryParseTokenDouble(tokens[index + 1], out double ty))
                {
                    textX += tx;
                    textY += ty;
                    if (Math.Abs(ty) > 0.01)
                    {
                        lineHeightEstimate = Math.Abs(ty);
                    }
                }

                index += 3;
                continue;
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 6 < tokens.Count
                && IsNumberToken(tokens[index + 1])
                && IsNumberToken(tokens[index + 2])
                && IsNumberToken(tokens[index + 3])
                && IsNumberToken(tokens[index + 4])
                && IsNumberToken(tokens[index + 5])
                && tokens[index + 6].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 6].Lexeme, "Tm", StringComparison.Ordinal))
            {
                if (TryParseTokenDouble(tokens[index + 4], out double matrixX)
                    && TryParseTokenDouble(tokens[index + 5], out double matrixY))
                {
                    double deltaY = Math.Abs(matrixY - textY);
                    textX = matrixX;
                    textY = matrixY;
                    if (lastShownBaselineY is not null && deltaY > 0.01)
                    {
                        lineHeightEstimate = deltaY;
                    }
                }

                index += 7;
                continue;
            }

            if (inTextObject
                && PdfTextExtractor.IsTextStringToken(token)
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == PdfTokenKind.Keyword
                && IsSingleStringTextOperator(tokens[index + 1].Lexeme))
            {
                string segment = PdfTextExtractor.DecodeTextToken(token, activeToUnicode);
                string textOperator = tokens[index + 1].Lexeme;
                if (lastShownBaselineY is double previousBaselineY)
                {
                    double shownDelta = Math.Abs(textY - previousBaselineY);
                    if (shownDelta > 0.01)
                    {
                        lineHeightEstimate = shownDelta;
                    }
                }

                PdfTextAnchorKey anchorKey = new(streamId.ObjectNumber, streamId.GenerationNumber, index);
                double effectiveLineHeight = ResolveRedactionLineHeight(fontSize, lineHeightEstimate);
                if (rangesByAnchor.TryGetValue(anchorKey, out List<PdfTextSelectionRange>? ranges)
                    && (token.Kind == PdfTokenKind.HexString
                        ? TryBuildAnchoredHardRedactionHexParts(
                            token.Lexeme,
                            activeToUnicode,
                            activeFontWidths,
                            segment,
                            ranges,
                            fontSize,
                            characterSpacing,
                            wordSpacing,
                            horizontalScale,
                            textX,
                            textY,
                            effectiveLineHeight,
                            pageIndex,
                            options,
                            out string hardParts,
                            out int rangeReplacements,
                            out List<PdfRedactionRectangle> rangeRectangles)
                        : TryBuildAnchoredHardRedactionParts(
                            segment,
                            ranges,
                            activeFontWidths,
                            fontSize,
                            characterSpacing,
                            wordSpacing,
                            horizontalScale,
                            textX,
                            textY,
                            effectiveLineHeight,
                            pageIndex,
                            options,
                            out hardParts,
                            out rangeReplacements,
                            out rangeRectangles)))
                {
                    replacements += rangeReplacements;
                    if (rangeRectangles.Count > 0)
                    {
                        rectangles.AddRange(rangeRectangles);
                    }

                    if (string.Equals(textOperator, "Tj", StringComparison.Ordinal))
                    {
                        rawTokenOverrides[index] = $"[{hardParts}] TJ";
                        removedTokenIndices.Add(index + 1);
                    }
                    else if (string.Equals(textOperator, "'", StringComparison.Ordinal))
                    {
                        rawTokenOverrides[index] = $"T* [{hardParts}] TJ";
                        removedTokenIndices.Add(index + 1);
                    }
                    else if (string.Equals(textOperator, "\"", StringComparison.Ordinal))
                    {
                        if (index < 2
                            || !IsNumberToken(tokens[index - 2])
                            || !IsNumberToken(tokens[index - 1]))
                        {
                            throw new NotSupportedException("Hard redaction for the '\"' text operator requires preceding word and character spacing operands.");
                        }

                        rawTokenOverrides[index - 2] = $"{tokens[index - 2].Lexeme} Tw {tokens[index - 1].Lexeme} Tc T* [{hardParts}] TJ";
                        removedTokenIndices.Add(index - 1);
                        removedTokenIndices.Add(index);
                        removedTokenIndices.Add(index + 1);
                    }
                }

                textX += MeasureTokenTextWidth(
                    token,
                    segment,
                    activeToUnicode,
                    activeFontWidths,
                    fontSize,
                    characterSpacing,
                    wordSpacing,
                    horizontalScale);
                lastShownBaselineY = textY;
                index += 2;
                continue;
            }

            if (inTextObject && token.Kind == PdfTokenKind.StartArray)
            {
                int depth = 1;
                int arrayEndIndex = index + 1;

                while (arrayEndIndex < tokens.Count && depth > 0)
                {
                    PdfToken itemToken = tokens[arrayEndIndex];
                    if (itemToken.Kind == PdfTokenKind.StartArray)
                    {
                        depth++;
                    }
                    else if (itemToken.Kind == PdfTokenKind.EndArray)
                    {
                        depth--;
                    }

                    arrayEndIndex++;
                }

                if (depth != 0)
                {
                    throw new PdfFormatException("Unterminated array in content stream.");
                }

                if (arrayEndIndex < tokens.Count
                    && tokens[arrayEndIndex].Kind == PdfTokenKind.Keyword
                    && string.Equals(tokens[arrayEndIndex].Lexeme, "TJ", StringComparison.Ordinal))
                {
                    int nestedDepth = 1;
                    for (int itemIndex = index + 1; itemIndex < arrayEndIndex; itemIndex++)
                    {
                        PdfToken item = tokens[itemIndex];
                        if (item.Kind == PdfTokenKind.StartArray)
                        {
                            nestedDepth++;
                            continue;
                        }

                        if (item.Kind == PdfTokenKind.EndArray)
                        {
                            nestedDepth--;
                            continue;
                        }

                        if (nestedDepth != 1)
                        {
                            continue;
                        }

                        if (PdfTextExtractor.IsTextStringToken(item))
                        {
                            string segment = PdfTextExtractor.DecodeTextToken(item, activeToUnicode);
                            if (lastShownBaselineY is double previousBaselineY)
                            {
                                double shownDelta = Math.Abs(textY - previousBaselineY);
                                if (shownDelta > 0.01)
                                {
                                    lineHeightEstimate = shownDelta;
                                }
                            }

                            PdfTextAnchorKey anchorKey = new(streamId.ObjectNumber, streamId.GenerationNumber, itemIndex);
                            double effectiveLineHeight = ResolveRedactionLineHeight(fontSize, lineHeightEstimate);
                            if (rangesByAnchor.TryGetValue(anchorKey, out List<PdfTextSelectionRange>? ranges)
                                && (item.Kind == PdfTokenKind.HexString
                                    ? TryBuildAnchoredHardRedactionHexParts(
                                        item.Lexeme,
                                        activeToUnicode,
                                        activeFontWidths,
                                        segment,
                                        ranges,
                                        fontSize,
                                        characterSpacing,
                                        wordSpacing,
                                        horizontalScale,
                                        textX,
                                        textY,
                                        effectiveLineHeight,
                                        pageIndex,
                                        options,
                                        out string hardParts,
                                        out int rangeReplacements,
                                        out List<PdfRedactionRectangle> rangeRectangles)
                                    : TryBuildAnchoredHardRedactionParts(
                                        segment,
                                        ranges,
                                        activeFontWidths,
                                        fontSize,
                                        characterSpacing,
                                        wordSpacing,
                                        horizontalScale,
                                        textX,
                                        textY,
                                        effectiveLineHeight,
                                        pageIndex,
                                        options,
                                        out hardParts,
                                        out rangeReplacements,
                                        out rangeRectangles)))
                            {
                                replacements += rangeReplacements;
                                if (rangeRectangles.Count > 0)
                                {
                                    rectangles.AddRange(rangeRectangles);
                                }

                                rawTokenOverrides[itemIndex] = hardParts;
                            }

                            textX += MeasureTokenTextWidth(
                                item,
                                segment,
                                activeToUnicode,
                                activeFontWidths,
                                fontSize,
                                characterSpacing,
                                wordSpacing,
                                horizontalScale);
                            lastShownBaselineY = textY;
                        }
                        else if (IsNumberToken(item) && TryParseTokenDouble(item, out double adjustment))
                        {
                            textX -= ResolveTjAdjustmentWidth(adjustment, fontSize, horizontalScale);
                        }
                    }

                    index = arrayEndIndex + 1;
                    continue;
                }
            }

            index++;
        }

        if (rawTokenOverrides.Count == 0 && removedTokenIndices.Count == 0)
        {
            return (content, replacements, rectangles);
        }

        return (SerializeContentTokens(tokens, [], rawTokenOverrides, removedTokenIndices), replacements, rectangles);
    }

    private static bool TryBuildAnchoredHardRedactionParts(
        string segment,
        IReadOnlyList<PdfTextSelectionRange> ranges,
        PdfFontGlyphWidths? fontWidths,
        double fontSize,
        double characterSpacing,
        double wordSpacing,
        double horizontalScale,
        double textX,
        double textY,
        double lineHeight,
        int pageIndex,
        PdfHardRedactionOptions options,
        out string parts,
        out int replacements,
        out List<PdfRedactionRectangle> rectangles)
    {
        StringBuilder builder = new();
        rectangles = [];
        replacements = 0;
        int cursor = 0;

        foreach (PdfTextSelectionRange range in ranges)
        {
            if (range.Start < 0 || range.Length <= 0 || range.Start >= segment.Length)
            {
                continue;
            }

            int clampedLength = Math.Min(range.Length, segment.Length - range.Start);
            int clampedStart = Math.Max(cursor, range.Start);
            int clampedEnd = clampedStart + clampedLength;
            if (clampedEnd <= clampedStart)
            {
                continue;
            }

            if (clampedStart > cursor)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append('(');
                builder.Append(EscapeLiteralString(segment[cursor..clampedStart]));
                builder.Append(')');
            }

            double removedWidth = MeasureLiteralSubstringWidth(
                segment,
                clampedStart,
                clampedEnd - clampedStart,
                fontWidths,
                fontSize,
                characterSpacing,
                wordSpacing,
                horizontalScale);
            if (removedWidth > 0 && fontSize > 0)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                double kerningAdjustment = -(removedWidth * 1000.0 / fontSize);
                builder.Append(kerningAdjustment.ToString("0.###", CultureInfo.InvariantCulture));
            }

            double removedX = textX + MeasureLiteralSubstringWidth(
                segment,
                0,
                clampedStart,
                fontWidths,
                fontSize,
                characterSpacing,
                wordSpacing,
                horizontalScale) - options.HorizontalPadding;
            double removedRectWidth = removedWidth + (options.HorizontalPadding * 2);
            (double removedY, double removedHeight) = ComputeRedactionVerticalPlacement(textY, fontSize, lineHeight, options);
            if (removedRectWidth > 0 && removedHeight > 0)
            {
                rectangles.Add(new PdfRedactionRectangle(pageIndex, removedX, removedY, removedRectWidth, removedHeight));
            }

            replacements++;
            cursor = clampedEnd;
        }

        if (replacements == 0)
        {
            parts = string.Empty;
            rectangles.Clear();
            return false;
        }

        if (cursor < segment.Length)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('(');
            builder.Append(EscapeLiteralString(segment[cursor..]));
            builder.Append(')');
        }

        if (builder.Length == 0)
        {
            builder.Append("()");
        }

        parts = builder.ToString();
        return true;
    }

    private readonly record struct PdfHexTextUnit(string HexSlice, string Text, double WidthUnits, bool AppliesWordSpacing);

    private static bool TryBuildAnchoredHardRedactionHexParts(
        string hexLexeme,
        IReadOnlyDictionary<int, string>? cidToUnicode,
        PdfFontGlyphWidths? fontWidths,
        string segment,
        IReadOnlyList<PdfTextSelectionRange> ranges,
        double fontSize,
        double characterSpacing,
        double wordSpacing,
        double horizontalScale,
        double textX,
        double textY,
        double lineHeight,
        int pageIndex,
        PdfHardRedactionOptions options,
        out string parts,
        out int replacements,
        out List<PdfRedactionRectangle> rectangles)
    {
        if (!TryDecodeHexTokenUnits(hexLexeme, cidToUnicode, fontWidths, out List<PdfHexTextUnit>? units, out string decodedText)
            || !string.Equals(decodedText, segment, StringComparison.Ordinal))
        {
            parts = string.Empty;
            replacements = 0;
            rectangles = [];
            return false;
        }

        return TryBuildHexHardRedactionPartsFromRanges(
            decodedText,
            units,
            ranges,
            fontSize,
            characterSpacing,
            wordSpacing,
            horizontalScale,
            textX,
            textY,
            lineHeight,
            pageIndex,
            options,
            out parts,
            out replacements,
            out rectangles);
    }

    private static bool TryBuildHardRedactionHexParts(
        string hexLexeme,
        IReadOnlyDictionary<int, string>? cidToUnicode,
        PdfFontGlyphWidths? fontWidths,
        string segment,
        string target,
        double fontSize,
        double characterSpacing,
        double wordSpacing,
        double horizontalScale,
        out string parts,
        out int replacements)
    {
        if (!TryDecodeHexTokenUnits(hexLexeme, cidToUnicode, fontWidths, out List<PdfHexTextUnit>? units, out string decodedText)
            || !string.Equals(decodedText, segment, StringComparison.Ordinal))
        {
            parts = string.Empty;
            replacements = 0;
            return false;
        }

        List<PdfTextSelectionRange> ranges = [];
        int searchStart = 0;
        while (searchStart <= decodedText.Length)
        {
            int found = decodedText.IndexOf(target, searchStart, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            ranges.Add(new PdfTextSelectionRange(found, target.Length));
            searchStart = found + target.Length;
        }

        if (ranges.Count == 0)
        {
            parts = string.Empty;
            replacements = 0;
            return false;
        }

        bool built = TryBuildHexHardRedactionPartsFromRanges(
            decodedText,
            units,
            ranges,
            fontSize,
            characterSpacing,
            wordSpacing,
            horizontalScale,
            textX: 0,
            textY: 0,
            lineHeight: 0,
            pageIndex: 0,
            options: null,
            out parts,
            out replacements,
            out _);
        return built;
    }

    private static List<PdfRedactionRectangle> MergeRedactionRectangles(IReadOnlyList<PdfRedactionRectangle> rectangles)
    {
        if (rectangles.Count <= 1)
        {
            return [.. rectangles];
        }

        const double epsilon = 0.01;
        List<PdfRedactionRectangle> merged = rectangles
            .Where(static rectangle => rectangle.Width > 0 && rectangle.Height > 0)
            .OrderBy(static rectangle => rectangle.PageIndex)
            .ThenBy(static rectangle => rectangle.Y)
            .ThenBy(static rectangle => rectangle.X)
            .ToList();

        bool hasMerged;
        do
        {
            hasMerged = false;
            for (int index = 0; index < merged.Count; index++)
            {
                PdfRedactionRectangle current = merged[index];
                for (int candidateIndex = index + 1; candidateIndex < merged.Count; candidateIndex++)
                {
                    PdfRedactionRectangle candidate = merged[candidateIndex];
                    if (!CanMergeRedactionRectangles(current, candidate, epsilon))
                    {
                        continue;
                    }

                    double left = Math.Min(current.X, candidate.X);
                    double bottom = Math.Min(current.Y, candidate.Y);
                    double right = Math.Max(current.X + current.Width, candidate.X + candidate.Width);
                    double top = Math.Max(current.Y + current.Height, candidate.Y + candidate.Height);
                    merged[index] = new PdfRedactionRectangle(current.PageIndex, left, bottom, right - left, top - bottom);
                    merged.RemoveAt(candidateIndex);
                    hasMerged = true;
                    break;
                }

                if (hasMerged)
                {
                    break;
                }
            }
        } while (hasMerged);

        return merged;
    }

    private static bool CanMergeRedactionRectangles(
        PdfRedactionRectangle first,
        PdfRedactionRectangle second,
        double epsilon)
    {
        if (first.PageIndex != second.PageIndex)
        {
            return false;
        }

        double firstRight = first.X + first.Width;
        double secondRight = second.X + second.Width;
        double firstTop = first.Y + first.Height;
        double secondTop = second.Y + second.Height;

        bool horizontallyTouching = first.X <= secondRight + epsilon && second.X <= firstRight + epsilon;
        bool verticallyTouching = first.Y <= secondTop + epsilon && second.Y <= firstTop + epsilon;
        return horizontallyTouching && verticallyTouching;
    }

    private static bool TryBuildHexHardRedactionPartsFromRanges(
        string decodedText,
        IReadOnlyList<PdfHexTextUnit> units,
        IReadOnlyList<PdfTextSelectionRange> ranges,
        double fontSize,
        double characterSpacing,
        double wordSpacing,
        double horizontalScale,
        double textX,
        double textY,
        double lineHeight,
        int pageIndex,
        PdfHardRedactionOptions? options,
        out string parts,
        out int replacements,
        out List<PdfRedactionRectangle> rectangles)
    {
        StringBuilder builder = new();
        rectangles = [];
        replacements = 0;
        int cursorUnit = 0;

        foreach (PdfTextSelectionRange range in ranges)
        {
            if (range.Start < 0 || range.Length <= 0 || range.Start >= decodedText.Length)
            {
                continue;
            }

            int clampedLength = Math.Min(range.Length, decodedText.Length - range.Start);
            int clampedStart = range.Start;
            int clampedEnd = clampedStart + clampedLength;
            if (clampedEnd <= clampedStart)
            {
                continue;
            }

            if (!TryResolveHexUnitRange(units, clampedStart, clampedEnd, out int startUnit, out int endUnitExclusive))
            {
                parts = string.Empty;
                rectangles.Clear();
                replacements = 0;
                return false;
            }

            if (startUnit < cursorUnit)
            {
                startUnit = cursorUnit;
            }

            if (startUnit > cursorUnit)
            {
                AppendHexTokenSlice(builder, units, cursorUnit, startUnit);
            }

            double removedWidth = MeasureHexUnitsWidth(
                units,
                startUnit,
                endUnitExclusive,
                fontSize,
                characterSpacing,
                wordSpacing,
                horizontalScale);
            if (removedWidth > 0 && fontSize > 0)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                double kerningAdjustment = -(removedWidth * 1000.0 / fontSize);
                builder.Append(kerningAdjustment.ToString("0.###", CultureInfo.InvariantCulture));
            }

            if (options is not null)
            {
                double removedX = textX + MeasureHexUnitsWidth(
                    units,
                    0,
                    startUnit,
                    fontSize,
                    characterSpacing,
                    wordSpacing,
                    horizontalScale) - options.HorizontalPadding;
                double removedRectWidth = removedWidth + (options.HorizontalPadding * 2);
                (double removedY, double removedHeight) = ComputeRedactionVerticalPlacement(textY, fontSize, lineHeight, options);
                if (removedRectWidth > 0 && removedHeight > 0)
                {
                    rectangles.Add(new PdfRedactionRectangle(pageIndex, removedX, removedY, removedRectWidth, removedHeight));
                }
            }

            replacements++;
            cursorUnit = endUnitExclusive;
        }

        if (replacements == 0)
        {
            parts = string.Empty;
            rectangles.Clear();
            return false;
        }

        if (cursorUnit < units.Count)
        {
            AppendHexTokenSlice(builder, units, cursorUnit, units.Count);
        }

        if (builder.Length == 0)
        {
            builder.Append("()");
        }

        parts = builder.ToString();
        return true;
    }

    private static bool TryDecodeHexTokenUnits(
        string hexLexeme,
        IReadOnlyDictionary<int, string>? cidToUnicode,
        PdfFontGlyphWidths? fontWidths,
        out List<PdfHexTextUnit> units,
        out string decodedText)
    {
        units = [];
        StringBuilder decodedBuilder = new();

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hexLexeme);
        }
        catch (FormatException)
        {
            decodedText = string.Empty;
            return false;
        }

        if (bytes.Length == 0)
        {
            decodedText = string.Empty;
            return true;
        }

        if ((bytes.Length & 1) == 0)
        {
            for (int byteIndex = 0; byteIndex < bytes.Length; byteIndex += 2)
            {
                string text;
                int cid = (bytes[byteIndex] << 8) | bytes[byteIndex + 1];
                if (cidToUnicode is not null)
                {
                    text = cidToUnicode.TryGetValue(cid, out string? mapped)
                        ? mapped
                        : ((char)cid).ToString();
                }
                else
                {
                    text = Encoding.BigEndianUnicode.GetString(bytes, byteIndex, 2);
                }

                if (text.Length == 0)
                {
                    decodedText = string.Empty;
                    return false;
                }

                string hexSlice = hexLexeme.Substring(byteIndex * 2, 4);
                double widthUnits = fontWidths?.GetGlyphWidthUnits(cid)
                    ?? EstimateRedactionTextUnits(text);
                units.Add(new PdfHexTextUnit(hexSlice, text, widthUnits, AppliesWordSpacing: false));
                decodedBuilder.Append(text);
            }

            decodedText = decodedBuilder.ToString();
            return true;
        }

        for (int byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
        {
            string hexSlice = hexLexeme.Substring(byteIndex * 2, 2);
            string text = ((char)bytes[byteIndex]).ToString();
            int charCode = bytes[byteIndex];
            double widthUnits = fontWidths?.GetGlyphWidthUnits(charCode)
                ?? EstimateRedactionTextUnits(text);
            bool appliesWordSpacing = charCode == 0x20;
            units.Add(new PdfHexTextUnit(hexSlice, text, widthUnits, appliesWordSpacing));
            decodedBuilder.Append(text);
        }

        decodedText = decodedBuilder.ToString();
        return true;
    }

    private static bool TryResolveHexUnitRange(
        IReadOnlyList<PdfHexTextUnit> units,
        int startIndex,
        int endIndex,
        out int startUnit,
        out int endUnitExclusive)
    {
        startUnit = -1;
        endUnitExclusive = -1;
        int cursor = 0;

        for (int unitIndex = 0; unitIndex < units.Count; unitIndex++)
        {
            if (cursor == startIndex)
            {
                startUnit = unitIndex;
            }

            int unitLength = units[unitIndex].Text.Length;
            if (unitLength == 0)
            {
                return false;
            }

            cursor += unitLength;
            if (cursor == endIndex)
            {
                endUnitExclusive = unitIndex + 1;
                break;
            }

            if (cursor > endIndex)
            {
                return false;
            }
        }

        if (startIndex == cursor && startUnit < 0)
        {
            startUnit = units.Count;
        }

        if (endIndex == cursor && endUnitExclusive < 0)
        {
            endUnitExclusive = units.Count;
        }

        return startUnit >= 0 && endUnitExclusive >= startUnit;
    }

    private static void AppendHexTokenSlice(
        StringBuilder builder,
        IReadOnlyList<PdfHexTextUnit> units,
        int startUnit,
        int endUnitExclusive)
    {
        if (startUnit >= endUnitExclusive)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append('<');
        for (int index = startUnit; index < endUnitExclusive; index++)
        {
            builder.Append(units[index].HexSlice);
        }

        builder.Append('>');
    }

    private int RewriteTextInContentStreams(
        Func<string, (string Updated, int Replacements)> rewriteSegment,
        string? hardTarget,
        PdfHardRedactionOptions? hardOptions,
        Func<string, double, (bool Matched, string Parts, int Replacements)>? rewriteWithLayout,
        Func<string, double, double, double, double, int, (bool Matched, string Parts, int Replacements, List<PdfRedactionRectangle> Rectangles)>? rewriteWithLayoutAndRectangles,
        out List<PdfRedactionRectangle> hardRectangles)
    {
        Dictionary<PdfObjectId, PdfIndirectObject> objectMap = PdfTextExtractor.BuildObjectMap(_file.Objects);
        List<PdfIndirectObject> objects = [.. _file.Objects];
        HashSet<PdfObjectId> processedStreamIds = [];
        HashSet<PdfObjectId> changedStreamIds = [];
        hardRectangles = [];
        int totalReplacements = 0;

        for (int pageIndex = 0; pageIndex < _model.Pages.Count; pageIndex++)
        {
            PdfPageModel page = _model.Pages[pageIndex];
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont =
                PdfTextExtractor.BuildToUnicodeMapsForPage(page.Resources, objectMap);
            IReadOnlyDictionary<string, PdfFontGlyphWidths> fontWidthsByFont =
                BuildFontWidthMapsForPage(page.Resources, objectMap);
            foreach (PdfObjectId streamId in EnumerateContentStreamReferences(page.Contents))
            {
                PdfStreamObject stream = RequireStreamObject(streamId, "Page contents");
                string content = DecodeContentStreamText(stream, $"Page content stream {streamId}");

                if (!processedStreamIds.Add(streamId))
                {
                    if (hardTarget is not null && hardOptions is not null)
                    {
                        hardRectangles.AddRange(CollectRedactionRectangles(content, pageIndex, hardTarget, hardOptions, rewriteWithLayoutAndRectangles: null, toUnicodeByFont, fontWidthsByFont));
                    }
                    else if (rewriteWithLayoutAndRectangles is not null)
                    {
                        hardRectangles.AddRange(CollectRedactionRectangles(content, pageIndex, hardTarget: null, hardOptions: null, rewriteWithLayoutAndRectangles, toUnicodeByFont, fontWidthsByFont));
                    }

                    continue;
                }

                (string updatedContent, int replacements, List<PdfRedactionRectangle> rectangles) = RewriteTextOperatorsInStream(
                    content,
                    rewriteSegment,
                    pageIndex,
                    hardTarget,
                    hardOptions,
                    rewriteWithLayout,
                    rewriteWithLayoutAndRectangles,
                    toUnicodeByFont,
                    fontWidthsByFont);

                totalReplacements += replacements;
                if (rectangles.Count > 0)
                {
                    hardRectangles.AddRange(rectangles);
                }

                if (!string.Equals(updatedContent, content, StringComparison.Ordinal))
                {
                    ReplaceObject(objects, streamId, new PdfStreamObject(stream.Dictionary, EncodeContentStreamText(stream, updatedContent, $"Page content stream {streamId}")));
                    changedStreamIds.Add(streamId);
                }
            }
        }

        if (changedStreamIds.Count == 0)
        {
            return totalReplacements;
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

        return totalReplacements;
    }

    private static (string UpdatedContent, int Replacements, List<PdfRedactionRectangle> Rectangles) RewriteTextOperatorsInStream(
        string content,
        Func<string, (string Updated, int Replacements)> rewriteSegment,
        int pageIndex,
        string? hardTarget,
        PdfHardRedactionOptions? hardOptions,
        Func<string, double, (bool Matched, string Parts, int Replacements)>? rewriteWithLayout,
        Func<string, double, double, double, double, int, (bool Matched, string Parts, int Replacements, List<PdfRedactionRectangle> Rectangles)>? rewriteWithLayoutAndRectangles,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont,
        IReadOnlyDictionary<string, PdfFontGlyphWidths> fontWidthsByFont)
    {
        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(ContentStreamEncoding.GetBytes(content));
        Dictionary<int, string> rewrittenStringTokens = [];
        Dictionary<int, string> rawTokenOverrides = [];
        HashSet<int> removedTokenIndices = [];
        List<PdfRedactionRectangle> rectangles = [];
        int replacements = 0;

        bool inTextObject = false;
        IReadOnlyDictionary<int, string>? activeToUnicode = null;
        PdfFontGlyphWidths? activeFontWidths = null;
        double fontSize = 12;
        double textX = 0;
        double textY = 0;
        double characterSpacing = 0;
        double wordSpacing = 0;
        double horizontalScale = 100;
        double lineHeightEstimate = fontSize * 1.2;
        double? lastShownBaselineY = null;
        int index = 0;


        while (index < tokens.Count)
        {
            PdfToken token = tokens[index];
            if (token.Kind == PdfTokenKind.Keyword)
            {
                if (string.Equals(token.Lexeme, "BT", StringComparison.Ordinal))
                {
                    inTextObject = true;
                    textX = 0;
                    textY = 0;
                    characterSpacing = 0;
                    wordSpacing = 0;
                    horizontalScale = 100;
                    lineHeightEstimate = fontSize * 1.2;
                    lastShownBaselineY = null;
                }
                else if (string.Equals(token.Lexeme, "ET", StringComparison.Ordinal))
                {
                    inTextObject = false;
                }
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == PdfTokenKind.Keyword)
            {
                if (string.Equals(tokens[index + 1].Lexeme, "Tc", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedCharacterSpacing))
                    {
                        characterSpacing = parsedCharacterSpacing;
                    }

                    index += 2;
                    continue;
                }

                if (string.Equals(tokens[index + 1].Lexeme, "Tw", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedWordSpacing))
                    {
                        wordSpacing = parsedWordSpacing;
                    }

                    index += 2;
                    continue;
                }

                if (string.Equals(tokens[index + 1].Lexeme, "Tz", StringComparison.Ordinal))
                {
                    if (TryParseTokenDouble(token, out double parsedHorizontalScale) && double.IsFinite(parsedHorizontalScale))
                    {
                        horizontalScale = parsedHorizontalScale;
                    }

                    index += 2;
                    continue;
                }
            }

            if (inTextObject
                && token.Kind == PdfTokenKind.Name
                && index + 2 < tokens.Count
                && TryParseTokenDouble(tokens[index + 1], out double parsedFontSize)
                && tokens[index + 2].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 2].Lexeme, "Tf", StringComparison.Ordinal))
            {
                activeToUnicode = toUnicodeByFont.TryGetValue(token.Lexeme, out IReadOnlyDictionary<int, string>? map)
                    ? map
                    : null;
                activeFontWidths = fontWidthsByFont.TryGetValue(token.Lexeme, out PdfFontGlyphWidths? widths)
                    ? widths
                    : null;
                fontSize = Math.Abs(parsedFontSize);
                lineHeightEstimate = Math.Max(lineHeightEstimate, fontSize * 1.2);
                index += 3;
                continue;
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 2 < tokens.Count
                && IsNumberToken(tokens[index + 1])
                && tokens[index + 2].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 2].Lexeme, "Td", StringComparison.Ordinal))
            {
                if (TryParseTokenDouble(token, out double tx) && TryParseTokenDouble(tokens[index + 1], out double ty))
                {
                    textX += tx;
                    textY += ty;
                    if (Math.Abs(ty) > 0.01)
                    {
                        lineHeightEstimate = Math.Abs(ty);
                    }
                }

                index += 3;
                continue;
            }

            if (inTextObject
                && IsNumberToken(token)
                && index + 6 < tokens.Count
                && IsNumberToken(tokens[index + 1])
                && IsNumberToken(tokens[index + 2])
                && IsNumberToken(tokens[index + 3])
                && IsNumberToken(tokens[index + 4])
                && IsNumberToken(tokens[index + 5])
                && tokens[index + 6].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 6].Lexeme, "Tm", StringComparison.Ordinal))
            {
                if (TryParseTokenDouble(tokens[index + 4], out double matrixX)
                    && TryParseTokenDouble(tokens[index + 5], out double matrixY))
                {
                    double deltaY = Math.Abs(matrixY - textY);
                    textX = matrixX;
                    textY = matrixY;
                    if (lastShownBaselineY is not null && deltaY > 0.01)
                    {
                        lineHeightEstimate = deltaY;
                    }
                }

                index += 7;
                continue;
            }

            if (inTextObject
                && PdfTextExtractor.IsTextStringToken(token)
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == PdfTokenKind.Keyword
                && IsSingleStringTextOperator(tokens[index + 1].Lexeme))
            {
                bool supportsLiteralRewrite = token.Kind == PdfTokenKind.String;
                string segment = PdfTextExtractor.DecodeTextToken(token, activeToUnicode);
                string textOperator = tokens[index + 1].Lexeme;
                if (lastShownBaselineY is double previousBaselineY)
                {
                    double shownDelta = Math.Abs(textY - previousBaselineY);
                    if (shownDelta > 0.01)
                    {
                        lineHeightEstimate = shownDelta;
                    }
                }

                double effectiveLineHeight = ResolveRedactionLineHeight(fontSize, lineHeightEstimate);
                if (hardTarget is not null && hardOptions is not null)
                {
                    AddHardRedactionRectanglesForSegment(
                        rectangles,
                        pageIndex,
                        segment,
                        hardTarget,
                        textX,
                        textY,
                        fontSize,
                        effectiveLineHeight,
                        hardOptions);

                    bool matched = token.Kind == PdfTokenKind.HexString
                        ? TryBuildHardRedactionHexParts(
                            token.Lexeme,
                            activeToUnicode,
                            activeFontWidths,
                            segment,
                            hardTarget,
                            fontSize,
                            characterSpacing,
                            wordSpacing,
                            horizontalScale,
                            out string hardParts,
                            out int hardMatches)
                        : TryBuildHardRedactionParts(segment, hardTarget, fontSize, out hardParts, out hardMatches);
                    if (matched)
                    {
                        replacements += hardMatches;
                        if (string.Equals(textOperator, "Tj", StringComparison.Ordinal))
                        {
                            rawTokenOverrides[index] = $"[{hardParts}] TJ";
                            removedTokenIndices.Add(index + 1);
                        }
                        else if (string.Equals(textOperator, "'", StringComparison.Ordinal))
                        {
                            rawTokenOverrides[index] = $"T* [{hardParts}] TJ";
                            removedTokenIndices.Add(index + 1);
                        }
                        else if (string.Equals(textOperator, "\"", StringComparison.Ordinal))
                        {
                            if (index < 2
                                || !IsNumberToken(tokens[index - 2])
                                || !IsNumberToken(tokens[index - 1]))
                            {
                                throw new NotSupportedException("Hard redaction for the '\"' text operator requires preceding word and character spacing operands.");
                            }

                            rawTokenOverrides[index - 2] = $"{tokens[index - 2].Lexeme} Tw {tokens[index - 1].Lexeme} Tc T* [{hardParts}] TJ";
                            removedTokenIndices.Add(index - 1);
                            removedTokenIndices.Add(index);
                            removedTokenIndices.Add(index + 1);
                        }

                        textX += MeasureTokenTextWidth(
                            token,
                            segment,
                            activeToUnicode,
                            activeFontWidths,
                            fontSize,
                            characterSpacing,
                            wordSpacing,
                            horizontalScale);
                        index += 2;
                        continue;
                    }
                }
                else if (rewriteWithLayout is not null)
                {
                    if (!supportsLiteralRewrite)
                    {
                        textX += MeasureTokenTextWidth(
                            token,
                            segment,
                            activeToUnicode,
                            activeFontWidths,
                            fontSize,
                            characterSpacing,
                            wordSpacing,
                            horizontalScale);
                        lastShownBaselineY = textY;
                        index += 2;
                        continue;
                    }

                    (bool matched, string parts, int layoutReplacements) = rewriteWithLayout(segment, fontSize);
                    if (matched)
                    {
                        replacements += layoutReplacements;
                        if (string.Equals(textOperator, "Tj", StringComparison.Ordinal))
                        {
                            rawTokenOverrides[index] = $"[{parts}] TJ";
                            removedTokenIndices.Add(index + 1);
                        }
                        else if (string.Equals(textOperator, "'", StringComparison.Ordinal))
                        {
                            rawTokenOverrides[index] = $"T* [{parts}] TJ";
                            removedTokenIndices.Add(index + 1);
                        }
                        else if (string.Equals(textOperator, "\"", StringComparison.Ordinal))
                        {
                            if (index < 2
                                || !IsNumberToken(tokens[index - 2])
                                || !IsNumberToken(tokens[index - 1]))
                            {
                                throw new NotSupportedException("Soft redaction for the '\"' text operator requires preceding word and character spacing operands.");
                            }

                            rawTokenOverrides[index - 2] = $"{tokens[index - 2].Lexeme} Tw {tokens[index - 1].Lexeme} Tc T* [{parts}] TJ";
                            removedTokenIndices.Add(index - 1);
                            removedTokenIndices.Add(index);
                            removedTokenIndices.Add(index + 1);
                        }

                        textX += MeasureTokenTextWidth(
                            token,
                            segment,
                            activeToUnicode,
                            activeFontWidths,
                            fontSize,
                            characterSpacing,
                            wordSpacing,
                            horizontalScale);
                        index += 2;
                        continue;
                    }
                }
                else if (rewriteWithLayoutAndRectangles is not null)
                {
                    if (!supportsLiteralRewrite)
                    {
                        textX += MeasureTokenTextWidth(
                            token,
                            segment,
                            activeToUnicode,
                            activeFontWidths,
                            fontSize,
                            characterSpacing,
                            wordSpacing,
                            horizontalScale);
                        lastShownBaselineY = textY;
                        index += 2;
                        continue;
                    }

                    (bool matched, string parts, int layoutReplacements, List<PdfRedactionRectangle> segmentRectangles) =
                        rewriteWithLayoutAndRectangles(segment, fontSize, textX, textY, effectiveLineHeight, pageIndex);
                    if (matched)
                    {
                        replacements += layoutReplacements;
                        if (segmentRectangles.Count > 0)
                        {
                            rectangles.AddRange(segmentRectangles);
                        }

                        if (string.Equals(textOperator, "Tj", StringComparison.Ordinal))
                        {
                            rawTokenOverrides[index] = $"[{parts}] TJ";
                            removedTokenIndices.Add(index + 1);
                        }
                        else if (string.Equals(textOperator, "'", StringComparison.Ordinal))
                        {
                            rawTokenOverrides[index] = $"T* [{parts}] TJ";
                            removedTokenIndices.Add(index + 1);
                        }
                        else if (string.Equals(textOperator, "\"", StringComparison.Ordinal))
                        {
                            if (index < 2
                                || !IsNumberToken(tokens[index - 2])
                                || !IsNumberToken(tokens[index - 1]))
                            {
                                throw new NotSupportedException("Soft redaction for the '\"' text operator requires preceding word and character spacing operands.");
                            }

                            rawTokenOverrides[index - 2] = $"{tokens[index - 2].Lexeme} Tw {tokens[index - 1].Lexeme} Tc T* [{parts}] TJ";
                            removedTokenIndices.Add(index - 1);
                            removedTokenIndices.Add(index);
                            removedTokenIndices.Add(index + 1);
                        }

                        textX += MeasureTokenTextWidth(
                            token,
                            segment,
                            activeToUnicode,
                            activeFontWidths,
                            fontSize,
                            characterSpacing,
                            wordSpacing,
                            horizontalScale);
                        index += 2;
                        continue;
                    }
                }

                if (!supportsLiteralRewrite)
                {
                    textX += MeasureTokenTextWidth(
                        token,
                        segment,
                        activeToUnicode,
                        activeFontWidths,
                        fontSize,
                        characterSpacing,
                        wordSpacing,
                        horizontalScale);
                    lastShownBaselineY = textY;
                    index += 2;
                    continue;
                }

                (string updatedSegment, int segmentReplacements) = rewriteSegment(segment);
                replacements += segmentReplacements;
                if (!string.Equals(updatedSegment, segment, StringComparison.Ordinal))
                {
                    rewrittenStringTokens[index] = updatedSegment;
                }

                textX += MeasureTokenTextWidth(
                    token,
                    segment,
                    activeToUnicode,
                    activeFontWidths,
                    fontSize,
                    characterSpacing,
                    wordSpacing,
                    horizontalScale);
                lastShownBaselineY = textY;
                index += 2;
                continue;
            }

            if (inTextObject && token.Kind == PdfTokenKind.StartArray)
            {
                int depth = 1;
                int arrayEndIndex = index + 1;
                while (arrayEndIndex < tokens.Count && depth > 0)
                {
                    PdfToken item = tokens[arrayEndIndex];
                    if (item.Kind == PdfTokenKind.StartArray)
                    {
                        depth++;
                    }
                    else if (item.Kind == PdfTokenKind.EndArray)
                    {
                        depth--;
                    }

                    arrayEndIndex++;
                }

                if (depth != 0)
                {
                    throw new PdfFormatException("Unterminated array in content stream.");
                }

                if (arrayEndIndex < tokens.Count
                    && tokens[arrayEndIndex].Kind == PdfTokenKind.Keyword
                    && string.Equals(tokens[arrayEndIndex].Lexeme, "TJ", StringComparison.Ordinal))
                {
                    int nestedDepth = 1;
                    for (int itemIndex = index + 1; itemIndex < arrayEndIndex; itemIndex++)
                    {
                        PdfToken item = tokens[itemIndex];
                        if (item.Kind == PdfTokenKind.StartArray)
                        {
                            nestedDepth++;
                            continue;
                        }

                        if (item.Kind == PdfTokenKind.EndArray)
                        {
                            nestedDepth--;
                            continue;
                        }

                        if (nestedDepth != 1)
                        {
                            continue;
                        }

                        if (PdfTextExtractor.IsTextStringToken(item))
                        {
                            bool supportsLiteralRewrite = item.Kind == PdfTokenKind.String;
                            string segment = PdfTextExtractor.DecodeTextToken(item, activeToUnicode);
                            if (lastShownBaselineY is double previousBaselineY)
                            {
                                double shownDelta = Math.Abs(textY - previousBaselineY);
                                if (shownDelta > 0.01)
                                {
                                    lineHeightEstimate = shownDelta;
                                }
                            }

                            double effectiveLineHeight = ResolveRedactionLineHeight(fontSize, lineHeightEstimate);
                            if (hardTarget is not null && hardOptions is not null)
                            {
                                AddHardRedactionRectanglesForSegment(
                                    rectangles,
                                    pageIndex,
                                    segment,
                                    hardTarget,
                                    textX,
                                    textY,
                                    fontSize,
                                    effectiveLineHeight,
                                    hardOptions);

                                bool matched = item.Kind == PdfTokenKind.HexString
                                    ? TryBuildHardRedactionHexParts(
                                        item.Lexeme,
                                        activeToUnicode,
                                        activeFontWidths,
                                        segment,
                                        hardTarget,
                                        fontSize,
                                        characterSpacing,
                                        wordSpacing,
                                        horizontalScale,
                                        out string hardParts,
                                        out int hardMatches)
                                    : TryBuildHardRedactionParts(segment, hardTarget, fontSize, out hardParts, out hardMatches);
                                if (matched)
                                {
                                    replacements += hardMatches;
                                    rawTokenOverrides[itemIndex] = hardParts;
                                    textX += MeasureTokenTextWidth(
                                        item,
                                        segment,
                                        activeToUnicode,
                                        activeFontWidths,
                                        fontSize,
                                        characterSpacing,
                                        wordSpacing,
                                        horizontalScale);
                                    continue;
                                }
                            }
                            else if (rewriteWithLayout is not null)
                            {
                                if (!supportsLiteralRewrite)
                                {
                                    textX += MeasureTokenTextWidth(
                                        item,
                                        segment,
                                        activeToUnicode,
                                        activeFontWidths,
                                        fontSize,
                                        characterSpacing,
                                        wordSpacing,
                                        horizontalScale);
                                    lastShownBaselineY = textY;
                                    continue;
                                }

                                (bool matched, string parts, int layoutReplacements) = rewriteWithLayout(segment, fontSize);
                                if (matched)
                                {
                                    replacements += layoutReplacements;
                                    rawTokenOverrides[itemIndex] = parts;
                                    textX += MeasureTokenTextWidth(
                                        item,
                                        segment,
                                        activeToUnicode,
                                        activeFontWidths,
                                        fontSize,
                                        characterSpacing,
                                        wordSpacing,
                                        horizontalScale);
                                    continue;
                                }
                            }
                            else if (rewriteWithLayoutAndRectangles is not null)
                            {
                                if (!supportsLiteralRewrite)
                                {
                                    textX += MeasureTokenTextWidth(
                                        item,
                                        segment,
                                        activeToUnicode,
                                        activeFontWidths,
                                        fontSize,
                                        characterSpacing,
                                        wordSpacing,
                                        horizontalScale);
                                    lastShownBaselineY = textY;
                                    continue;
                                }

                                (bool matched, string parts, int layoutReplacements, List<PdfRedactionRectangle> segmentRectangles) =
                                    rewriteWithLayoutAndRectangles(segment, fontSize, textX, textY, effectiveLineHeight, pageIndex);
                                if (matched)
                                {
                                    replacements += layoutReplacements;
                                    if (segmentRectangles.Count > 0)
                                    {
                                        rectangles.AddRange(segmentRectangles);
                                    }

                                    rawTokenOverrides[itemIndex] = parts;
                                    textX += MeasureTokenTextWidth(
                                        item,
                                        segment,
                                        activeToUnicode,
                                        activeFontWidths,
                                        fontSize,
                                        characterSpacing,
                                        wordSpacing,
                                        horizontalScale);
                                    continue;
                                }
                            }

                            if (!supportsLiteralRewrite)
                            {
                                textX += MeasureTokenTextWidth(
                                    item,
                                    segment,
                                    activeToUnicode,
                                    activeFontWidths,
                                    fontSize,
                                    characterSpacing,
                                    wordSpacing,
                                    horizontalScale);
                                lastShownBaselineY = textY;
                                continue;
                            }

                            (string updatedSegment, int segmentReplacements) = rewriteSegment(segment);
                            replacements += segmentReplacements;
                            if (!string.Equals(updatedSegment, segment, StringComparison.Ordinal))
                            {
                                rewrittenStringTokens[itemIndex] = updatedSegment;
                            }

                            textX += MeasureTokenTextWidth(
                                item,
                                segment,
                                activeToUnicode,
                                activeFontWidths,
                                fontSize,
                                characterSpacing,
                                wordSpacing,
                                horizontalScale);
                            lastShownBaselineY = textY;
                        }
                        else if (IsNumberToken(item) && TryParseTokenDouble(item, out double adjustment))
                        {
                            textX -= ResolveTjAdjustmentWidth(adjustment, fontSize, horizontalScale);
                        }
                    }

                    index = arrayEndIndex + 1;
                    continue;
                }
            }

            index++;
        }

        if (rewrittenStringTokens.Count == 0
            && rawTokenOverrides.Count == 0
            && removedTokenIndices.Count == 0)
        {
            return (content, replacements, rectangles);
        }

        return (SerializeContentTokens(tokens, rewrittenStringTokens, rawTokenOverrides, removedTokenIndices), replacements, rectangles);
    }

    private static List<PdfRedactionRectangle> CollectRedactionRectangles(
        string content,
        int pageIndex,
        string? hardTarget,
        PdfHardRedactionOptions? hardOptions,
        Func<string, double, double, double, double, int, (bool Matched, string Parts, int Replacements, List<PdfRedactionRectangle> Rectangles)>? rewriteWithLayoutAndRectangles,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont,
        IReadOnlyDictionary<string, PdfFontGlyphWidths> fontWidthsByFont)
    {
        (_, _, List<PdfRedactionRectangle> rectangles) = RewriteTextOperatorsInStream(
            content,
            static segment => (segment, 0),
            pageIndex,
            hardTarget,
            hardOptions,
            rewriteWithLayout: null,
            rewriteWithLayoutAndRectangles: rewriteWithLayoutAndRectangles,
            toUnicodeByFont,
            fontWidthsByFont);
        return rectangles;
    }

    private static void AddHardRedactionRectanglesForSegment(
        List<PdfRedactionRectangle> rectangles,
        int pageIndex,
        string segment,
        string target,
        double textX,
        double textY,
        double fontSize,
        double lineHeight,
        PdfHardRedactionOptions options)
    {
        int index = 0;
        while (index < segment.Length)
        {
            int found = segment.IndexOf(target, index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            double matchX = textX + EstimateRedactionTextWidth(segment[..found], fontSize);
            double matchWidth = EstimateRedactionTextWidth(target, fontSize);
            double x = matchX - options.HorizontalPadding;
            double width = matchWidth + (options.HorizontalPadding * 2);
            (double y, double height) = ComputeRedactionVerticalPlacement(textY, fontSize, lineHeight, options);
            if (width > 0 && height > 0)
            {
                rectangles.Add(new PdfRedactionRectangle(pageIndex, x, y, width, height));
            }

            index = found + target.Length;
        }
    }

    private static bool TryBuildHardRedactionParts(
        string segment,
        string target,
        double fontSize,
        out string parts,
        out int replacements)
    {
        replacements = 0;
        StringBuilder builder = new();
        int cursor = 0;

        while (cursor <= segment.Length)
        {
            int found = segment.IndexOf(target, cursor, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            replacements++;
            if (found > cursor)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append('(');
                builder.Append(EscapeLiteralString(segment[cursor..found]));
                builder.Append(')');
            }

            double removedWidth = EstimateRedactionTextWidth(target, fontSize);
            if (removedWidth > 0 && fontSize > 0)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                double kerningAdjustment = -(removedWidth * 1000.0 / fontSize);
                builder.Append(kerningAdjustment.ToString("0.###", CultureInfo.InvariantCulture));
            }

            cursor = found + target.Length;
        }

        if (replacements == 0)
        {
            parts = string.Empty;
            return false;
        }

        if (cursor < segment.Length)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('(');
            builder.Append(EscapeLiteralString(segment[cursor..]));
            builder.Append(')');
        }

        if (builder.Length == 0)
        {
            builder.Append("()");
        }

        parts = builder.ToString();
        return true;
    }

    private static (bool Matched, string Parts, int Replacements) TryBuildLiteralSoftRedactionParts(
        string segment,
        string target,
        string replacement,
        double fontSize)
    {
        StringBuilder builder = new();
        int replacements = 0;
        int cursor = 0;

        while (cursor <= segment.Length)
        {
            int found = segment.IndexOf(target, cursor, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            replacements++;
            AppendTjArrayStringPart(builder, segment[cursor..found]);
            AppendTjArrayStringPart(builder, replacement);
            AppendTjArrayCompensation(builder, EstimateRedactionTextWidth(target, fontSize), EstimateRedactionTextWidth(replacement, fontSize), fontSize);
            cursor = found + target.Length;
        }

        if (replacements == 0)
        {
            return (false, string.Empty, 0);
        }

        AppendTjArrayStringPart(builder, segment[cursor..]);
        if (builder.Length == 0)
        {
            builder.Append("()");
        }

        return (true, builder.ToString(), replacements);
    }

    private static (bool Matched, string Parts, int Replacements) TryBuildRegexSoftRedactionParts(
        string segment,
        Regex regex,
        MatchEvaluator evaluator,
        double fontSize)
    {
        MatchCollection matches = regex.Matches(segment);
        if (matches.Count == 0)
        {
            return (false, string.Empty, 0);
        }

        StringBuilder builder = new();
        int cursor = 0;
        int replacements = 0;

        foreach (Match match in matches)
        {
            if (!match.Success || match.Length == 0 || match.Index < cursor)
            {
                continue;
            }

            string replacement = evaluator(match) ?? string.Empty;
            replacements++;
            AppendTjArrayStringPart(builder, segment[cursor..match.Index]);
            AppendTjArrayStringPart(builder, replacement);
            AppendTjArrayCompensation(builder, EstimateRedactionTextWidth(match.Value, fontSize), EstimateRedactionTextWidth(replacement, fontSize), fontSize);
            cursor = match.Index + match.Length;
        }

        if (replacements == 0)
        {
            return (false, string.Empty, 0);
        }

        AppendTjArrayStringPart(builder, segment[cursor..]);
        if (builder.Length == 0)
        {
            builder.Append("()");
        }

        return (true, builder.ToString(), replacements);
    }

    private static (bool Matched, string Parts, int Replacements, List<PdfRedactionRectangle> Rectangles) TryBuildDirectiveSoftRedactionParts(
        string segment,
        Regex regex,
        Func<Match, PdfSoftRedactionDirective> directiveSelector,
        PdfHardRedactionOptions options,
        double fontSize,
        double textX,
        double textY,
        double lineHeight,
        int pageIndex)
    {
        MatchCollection matches = regex.Matches(segment);
        if (matches.Count == 0)
        {
            return (false, string.Empty, 0, []);
        }

        StringBuilder builder = new();
        List<PdfRedactionRectangle> rectangles = [];
        int cursor = 0;
        int replacements = 0;

        foreach (Match match in matches)
        {
            if (!match.Success || match.Length == 0 || match.Index < cursor)
            {
                continue;
            }

            PdfSoftRedactionDirective directive = directiveSelector(match)
                ?? throw new ArgumentException("Soft redaction directive selector returned null.", nameof(directiveSelector));
            ValidateSoftRedactionDirective(directive);
            ApplySoftRedactionDirective(
                match.Value,
                directive,
                out string keptPrefixText,
                out string keptSuffixText,
                out string removedText);

            AppendTjArrayStringPart(builder, segment[cursor..match.Index]);
            AppendTjArrayStringPart(builder, keptPrefixText);

            if (removedText.Length > 0)
            {
                replacements++;
                AppendTjArrayCompensation(builder, EstimateRedactionTextWidth(removedText, fontSize), 0, fontSize);

                double matchX = textX + EstimateRedactionTextWidth(segment[..match.Index], fontSize);
                double removedX = matchX + EstimateRedactionTextWidth(keptPrefixText, fontSize) - options.HorizontalPadding;
                double removedWidth = EstimateRedactionTextWidth(removedText, fontSize) + (options.HorizontalPadding * 2);
                (double removedY, double removedHeight) = ComputeRedactionVerticalPlacement(textY, fontSize, lineHeight, options);
                if (removedWidth > 0 && removedHeight > 0)
                {
                    rectangles.Add(new PdfRedactionRectangle(pageIndex, removedX, removedY, removedWidth, removedHeight));
                }
            }

            AppendTjArrayStringPart(builder, keptSuffixText);
            cursor = match.Index + match.Length;
        }

        if (replacements == 0)
        {
            return (false, string.Empty, 0, []);
        }

        AppendTjArrayStringPart(builder, segment[cursor..]);
        if (builder.Length == 0)
        {
            builder.Append("()");
        }

        return (true, builder.ToString(), replacements, rectangles);
    }

    private static void ApplySoftRedactionDirective(
        string value,
        PdfSoftRedactionDirective directive,
        out string keptPrefixText,
        out string keptSuffixText,
        out string removedText)
    {
        List<string> elements = EnumerateTextElements(value);
        int keepPrefix = Math.Min(directive.KeepPrefixCharacters, elements.Count);
        int keepSuffix = Math.Min(directive.KeepSuffixCharacters, Math.Max(0, elements.Count - keepPrefix));
        int removedCount = Math.Max(0, elements.Count - keepPrefix - keepSuffix);

        keptPrefixText = string.Concat(elements.Take(keepPrefix));
        keptSuffixText = string.Concat(elements.Skip(elements.Count - keepSuffix));
        removedText = removedCount > 0
            ? string.Concat(elements.Skip(keepPrefix).Take(removedCount))
            : string.Empty;
    }

    private static void ValidateSoftRedactionDirective(PdfSoftRedactionDirective directive)
    {
        if (directive.KeepPrefixCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(directive), "KeepPrefixCharacters must be greater than or equal to zero.");
        }

        if (directive.KeepSuffixCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(directive), "KeepSuffixCharacters must be greater than or equal to zero.");
        }
    }

    private static void AppendTjArrayStringPart(StringBuilder builder, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append('(');
        builder.Append(EscapeLiteralString(value));
        builder.Append(')');
    }

    private static void AppendTjArrayCompensation(StringBuilder builder, double originalWidth, double replacementWidth, double fontSize)
    {
        if (fontSize <= 0 || !double.IsFinite(originalWidth) || !double.IsFinite(replacementWidth))
        {
            return;
        }

        double widthDelta = originalWidth - replacementWidth;
        if (Math.Abs(widthDelta) < 0.0001)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        double kerningAdjustment = -(widthDelta * 1000.0 / fontSize);
        builder.Append(kerningAdjustment.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static (double Y, double Height) ComputeRedactionVerticalPlacement(
        double baselineY,
        double fontSize,
        double lineHeight,
        PdfHardRedactionOptions options)
    {
        (double textBandY, double textBandHeight) = ComputeTextRegionVerticalPlacement(baselineY, fontSize, lineHeight);
        double boxHeight = textBandHeight + (options.VerticalPadding * 2);
        double y = textBandY - options.VerticalPadding;
        double height = boxHeight;
        return (y, height);
    }

    private static (double Y, double Height) ComputeTextRegionVerticalPlacement(
        double baselineY,
        double fontSize,
        double lineHeight)
    {
        double descent = fontSize * 0.30;
        double textBandHeight = fontSize * 0.80;
        double effectiveLineHeight = Math.Max(lineHeight, textBandHeight);
        double lineBottom = baselineY - descent;
        double centeredOffset = Math.Min((effectiveLineHeight - textBandHeight) / 2, fontSize * 0.20);
        double y = lineBottom + centeredOffset;
        return (y, textBandHeight);
    }

    private static double MeasureTokenTextWidth(
        PdfToken token,
        string decodedText,
        IReadOnlyDictionary<int, string>? cidToUnicode,
        PdfFontGlyphWidths? fontWidths,
        double fontSize,
        double characterSpacing,
        double wordSpacing,
        double horizontalScale)
    {
        if (fontSize <= 0 || string.IsNullOrEmpty(decodedText))
        {
            return 0;
        }

        if (token.Kind == PdfTokenKind.HexString
            && TryDecodeHexTokenUnits(token.Lexeme, cidToUnicode, fontWidths, out List<PdfHexTextUnit>? units, out string mappedText)
            && string.Equals(mappedText, decodedText, StringComparison.Ordinal))
        {
            return MeasureHexUnitsWidth(units, 0, units.Count, fontSize, characterSpacing, wordSpacing, horizontalScale);
        }

        if (token.Kind == PdfTokenKind.String)
        {
            return MeasureLiteralSubstringWidth(
                decodedText,
                0,
                decodedText.Length,
                fontWidths,
                fontSize,
                characterSpacing,
                wordSpacing,
                horizontalScale);
        }

        return MeasureFallbackTextWidth(decodedText, fontSize, characterSpacing, wordSpacing, horizontalScale);
    }

    private static double MeasureLiteralSubstringWidth(
        string segment,
        int start,
        int length,
        PdfFontGlyphWidths? fontWidths,
        double fontSize,
        double characterSpacing,
        double wordSpacing,
        double horizontalScale)
    {
        if (fontSize <= 0 || length <= 0 || start < 0 || start >= segment.Length)
        {
            return 0;
        }

        int end = Math.Min(segment.Length, start + length);
        if (end <= start)
        {
            return 0;
        }

        double widthUnits = 0;
        int glyphCount = 0;
        int spaceCount = 0;
        for (int index = start; index < end; index++)
        {
            int characterCode = segment[index] & 0xFF;
            if (fontWidths is not null)
            {
                widthUnits += fontWidths.GetGlyphWidthUnits(characterCode);
            }
            else
            {
                Rune rune = Rune.TryCreate(segment[index], out Rune parsedRune)
                    ? parsedRune
                    : Rune.ReplacementChar;
                widthUnits += GetApproximateRedactionHelveticaWidth(rune);
            }
            glyphCount++;
            if (characterCode == 0x20)
            {
                spaceCount++;
            }
        }

        double horizontalScaleFactor = horizontalScale / 100.0;
        return ((widthUnits * fontSize / 1000.0) + (glyphCount * characterSpacing) + (spaceCount * wordSpacing)) * horizontalScaleFactor;
    }

    private static double MeasureHexUnitsWidth(
        IReadOnlyList<PdfHexTextUnit> units,
        int startUnit,
        int endUnitExclusive,
        double fontSize,
        double characterSpacing,
        double wordSpacing,
        double horizontalScale)
    {
        if (fontSize <= 0 || endUnitExclusive <= startUnit || startUnit < 0)
        {
            return 0;
        }

        endUnitExclusive = Math.Min(endUnitExclusive, units.Count);
        if (startUnit >= endUnitExclusive)
        {
            return 0;
        }

        double widthUnits = 0;
        int glyphCount = 0;
        int spaceCount = 0;
        for (int index = startUnit; index < endUnitExclusive; index++)
        {
            PdfHexTextUnit unit = units[index];
            widthUnits += unit.WidthUnits;
            glyphCount++;
            if (unit.AppliesWordSpacing)
            {
                spaceCount++;
            }
        }

        double horizontalScaleFactor = horizontalScale / 100.0;
        return ((widthUnits * fontSize / 1000.0) + (glyphCount * characterSpacing) + (spaceCount * wordSpacing)) * horizontalScaleFactor;
    }

    private static double MeasureFallbackTextWidth(
        string text,
        double fontSize,
        double characterSpacing,
        double wordSpacing,
        double horizontalScale)
    {
        if (fontSize <= 0 || string.IsNullOrEmpty(text))
        {
            return 0;
        }

        double baseWidth = EstimateRedactionTextWidth(text, fontSize);
        int glyphCount = text.Length;
        int spaceCount = 0;
        foreach (char character in text)
        {
            if (character == ' ')
            {
                spaceCount++;
            }
        }

        double horizontalScaleFactor = horizontalScale / 100.0;
        return (baseWidth + (glyphCount * characterSpacing) + (spaceCount * wordSpacing)) * horizontalScaleFactor;
    }

    private static double ResolveTjAdjustmentWidth(double adjustment, double fontSize, double horizontalScale)
    {
        double horizontalScaleFactor = horizontalScale / 100.0;
        return adjustment * fontSize * horizontalScaleFactor / 1000.0;
    }

    private static double EstimateRedactionTextUnits(string text)
    {
        double widthUnits = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            widthUnits += GetApproximateRedactionHelveticaWidth(rune);
        }

        return widthUnits;
    }

    private static double ResolveRedactionLineHeight(double fontSize, double lineHeightEstimate)
    {
        if (!double.IsFinite(lineHeightEstimate) || lineHeightEstimate <= 0)
        {
            return fontSize * 1.2;
        }

        return Math.Max(lineHeightEstimate, fontSize * 0.9);
    }

    private static double EstimateRedactionTextWidth(string text, double fontSize)
    {
        double widthUnits = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            widthUnits += GetApproximateRedactionHelveticaWidth(rune);
        }

        return widthUnits * fontSize / 1000.0;
    }

    private static int GetApproximateRedactionHelveticaWidth(Rune rune)
    {
        if (rune.Value <= 0x20)
        {
            return rune.Value == 0x20 ? 278 : 0;
        }

        if (!rune.IsAscii)
        {
            return GetApproximateHelveticaWidth(rune);
        }

        char character = (char)rune.Value;
        return character switch
        {
            >= 'A' and <= 'Z' => 667,
            >= '0' and <= '9' => 556,
            >= 'a' and <= 'z' => character switch
            {
                'r' => 333,
                't' => 278,
                'f' => 278,
                'i' or 'j' or 'l' => 222,
                'm' => 833,
                'w' => 722,
                'c' or 'k' or 's' or 'v' or 'x' or 'y' or 'z' => 500,
                _ => 556,
            },
            '.' or ',' or ':' or ';' => 278,
            '!' => 278,
            '?' => 556,
            '-' => 333,
            '*' => 389,
            '"' => 355,
            '\'' => 191,
            '(' or ')' or '[' or ']' => 333,
            '/' or '\\' => 278,
            '+' or '=' or '<' or '>' => 584,
            '@' => 1015,
            _ => GetApproximateHelveticaWidth(rune),
        };
    }

    private static void ValidateHardRedactionOptions(PdfHardRedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.HorizontalPadding) || options.HorizontalPadding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Hard redaction HorizontalPadding must be a finite number greater than or equal to zero.");
        }

        if (!double.IsFinite(options.VerticalPadding) || options.VerticalPadding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Hard redaction VerticalPadding must be a finite number greater than or equal to zero.");
        }
    }

    private static string SerializeContentTokens(
        IReadOnlyList<PdfToken> tokens,
        Dictionary<int, string> rewrittenStringTokens,
        Dictionary<int, string> rawTokenOverrides,
        HashSet<int> removedTokenIndices)
    {
        StringBuilder builder = new();
        for (int index = 0; index < tokens.Count; index++)
        {
            if (removedTokenIndices.Contains(index))
            {
                continue;
            }

            string? rendered = null;
            if (rawTokenOverrides.TryGetValue(index, out string? rawToken))
            {
                rendered = rawToken;
            }
            else if (rewrittenStringTokens.TryGetValue(index, out string? rewrittenLiteral))
            {
                rendered = $"({EscapeLiteralString(rewrittenLiteral)})";
            }
            else
            {
                rendered = SerializeContentToken(tokens[index]);
            }

            if (string.IsNullOrEmpty(rendered))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(rendered);
        }

        return builder.ToString();
    }

    private static string SerializeContentToken(PdfToken token)
    {
        return token.Kind switch
        {
            PdfTokenKind.Integer or PdfTokenKind.Real => token.Lexeme,
            PdfTokenKind.Name => $"/{token.Lexeme}",
            PdfTokenKind.String => $"({EscapeLiteralString(token.Lexeme)})",
            PdfTokenKind.HexString => $"<{token.Lexeme}>",
            PdfTokenKind.BooleanTrue => "true",
            PdfTokenKind.BooleanFalse => "false",
            PdfTokenKind.Null => "null",
            PdfTokenKind.Keyword => token.Lexeme,
            PdfTokenKind.StartArray => "[",
            PdfTokenKind.EndArray => "]",
            PdfTokenKind.StartDictionary => "<<",
            PdfTokenKind.EndDictionary => ">>",
            _ => throw new PdfFormatException($"Unsupported token kind '{token.Kind}'."),
        };
    }

    private static bool IsSingleStringTextOperator(string lexeme)
    {
        return string.Equals(lexeme, "Tj", StringComparison.Ordinal)
            || string.Equals(lexeme, "'", StringComparison.Ordinal)
            || string.Equals(lexeme, "\"", StringComparison.Ordinal);
    }

    private static bool IsNumberToken(PdfToken token)
    {
        return token.Kind is PdfTokenKind.Integer or PdfTokenKind.Real;
    }

    private static bool TryParseTokenDouble(PdfToken token, out double value)
    {
        if (IsNumberToken(token)
            && double.TryParse(token.Lexeme, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
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
                '*' => 389,
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
            UnicodeCategory.NonSpacingMark
                or UnicodeCategory.EnclosingMark
                or UnicodeCategory.Control
                or UnicodeCategory.Format => 0,
            UnicodeCategory.SpaceSeparator => 278,
            UnicodeCategory.DecimalDigitNumber => 556,
            UnicodeCategory.UppercaseLetter => 667,
            UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter => 556,
            UnicodeCategory.DashPunctuation => 333,
            UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.ConnectorPunctuation => 333,
            UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol
                or UnicodeCategory.OtherSymbol => 584,
            _ => 500,
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

    private static void ValidateImageOptions(PdfImageOptions options)
    {
        if (!double.IsFinite(options.X))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image X position must be finite.");
        }

        if (!double.IsFinite(options.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image Y position must be finite.");
        }

        if (options.Width is double width && (!double.IsFinite(width) || width <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image Width must be a positive finite number when provided.");
        }

        if (options.Height is double height && (!double.IsFinite(height) || height <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image Height must be a positive finite number when provided.");
        }
    }

    private static void ValidateShapeOptions(PdfShapeOptions options, bool requireStroke = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StrokeColor is null && options.FillColor is null && options.FillLinearGradient is null)
        {
            throw new ArgumentException("At least one of StrokeColor, FillColor, or FillLinearGradient must be set.", nameof(options));
        }

        if (requireStroke && options.StrokeColor is null)
        {
            throw new ArgumentException("StrokeColor is required for line drawing.", nameof(options));
        }

        if (options.StrokeColor is not null && (!double.IsFinite(options.StrokeWidth) || options.StrokeWidth <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shape StrokeWidth must be a positive finite number when stroke is enabled.");
        }

        if (!Enum.IsDefined(options.StrokeLineCap))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shape StrokeLineCap contains an unsupported value.");
        }

        if (!Enum.IsDefined(options.StrokeLineJoin))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shape StrokeLineJoin contains an unsupported value.");
        }

        if (!Enum.IsDefined(options.FillRule))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shape FillRule contains an unsupported value.");
        }

        if (!Enum.IsDefined(options.BlendMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shape BlendMode contains an unsupported value.");
        }

        if (options.StrokeColor is not null && (!double.IsFinite(options.StrokeMiterLimit) || options.StrokeMiterLimit <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shape StrokeMiterLimit must be a positive finite number when stroke is enabled.");
        }

        if (options.FillColor is not null && options.FillLinearGradient is not null)
        {
            throw new ArgumentException("Shape fill cannot define both FillColor and FillLinearGradient.", nameof(options));
        }

        if (options.StrokeOpacity is double strokeOpacity && (!double.IsFinite(strokeOpacity) || strokeOpacity < 0 || strokeOpacity > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shape StrokeOpacity must be a finite number in the range [0, 1].");
        }

        if (options.FillOpacity is double fillOpacity && (!double.IsFinite(fillOpacity) || fillOpacity < 0 || fillOpacity > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shape FillOpacity must be a finite number in the range [0, 1].");
        }

        if (options.FillLinearGradient is PdfShapeLinearGradient gradient)
        {
            if (!double.IsFinite(gradient.StartX)
                || !double.IsFinite(gradient.StartY)
                || !double.IsFinite(gradient.EndX)
                || !double.IsFinite(gradient.EndY))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Shape FillLinearGradient coordinates must be finite.");
            }

            if (gradient.StartX == gradient.EndX && gradient.StartY == gradient.EndY)
            {
                throw new ArgumentException("Shape FillLinearGradient start and end points must differ.", nameof(options));
            }
        }

        if (options.ClipPath is { Count: > 0 } clipPath)
        {
            ValidatePathCommands(clipPath);
        }

        if (options.ShapeId is string shapeId)
        {
            if (string.IsNullOrWhiteSpace(shapeId))
            {
                throw new ArgumentException("ShapeId cannot be blank when provided.", nameof(options));
            }

            if (shapeId.Contains('\n', StringComparison.Ordinal) || shapeId.Contains('\r', StringComparison.Ordinal))
            {
                throw new ArgumentException("ShapeId cannot contain newline characters.", nameof(options));
            }
        }

        if (options.StrokeColor is not null && options.StrokeDashPattern is PdfShapeDashPattern dashPattern)
        {
            if (dashPattern.Segments is null)
            {
                throw new ArgumentException("Shape StrokeDashPattern Segments cannot be null.", nameof(options));
            }

            if (!double.IsFinite(dashPattern.Phase) || dashPattern.Phase < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Shape StrokeDashPattern Phase must be a non-negative finite number.");
            }

            bool hasPositiveDashSegment = false;
            for (int index = 0; index < dashPattern.Segments.Count; index++)
            {
                double segment = dashPattern.Segments[index];
                if (!double.IsFinite(segment) || segment < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(options), "Shape StrokeDashPattern segments must be non-negative finite numbers.");
                }

                hasPositiveDashSegment |= segment > 0;
            }

            if (dashPattern.Segments.Count > 0 && !hasPositiveDashSegment)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Shape StrokeDashPattern must contain at least one positive segment.");
            }
        }
    }

    private static void ValidatePathCommands(IReadOnlyList<PdfPathCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            throw new ArgumentException("Path command collection cannot be empty.", nameof(commands));
        }

        bool hasMove = false;
        bool hasPaintableSegment = false;
        for (int index = 0; index < commands.Count; index++)
        {
            PdfPathCommand command = commands[index] ?? throw new ArgumentException($"Path command at index {index} cannot be null.", nameof(commands));
            switch (command)
            {
                case PdfPathMoveTo:
                    hasMove = true;
                    break;
                case PdfPathLineTo:
                case PdfPathCurveTo:
                case PdfPathClosePath:
                    if (!hasMove)
                    {
                        throw new ArgumentException("Path commands must begin with a move command before line/curve/close commands.", nameof(commands));
                    }

                    hasPaintableSegment = true;
                    break;
                default:
                    throw new NotSupportedException($"Path command type '{command.GetType().Name}' is not supported.");
            }
        }

        if (!hasPaintableSegment)
        {
            throw new ArgumentException("Path command collection must include at least one line, curve, or close command.", nameof(commands));
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

    private static void ValidateSignatureValidationOptions(PdfDetachedSignatureValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(options.RevocationCheckMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Signature validation RevocationCheckMode contains an unsupported value.");
        }
    }

    private static void ValidateSignatureOptions(PdfSignatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.FieldName))
        {
            throw new ArgumentException("Signature FieldName is required.", nameof(options));
        }

        if (!double.IsFinite(options.X) || !double.IsFinite(options.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Signature coordinates must be finite.");
        }

        if (!double.IsFinite(options.Width) || options.Width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Signature Width must be a non-negative finite number.");
        }

        if (!double.IsFinite(options.Height) || options.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Signature Height must be a non-negative finite number.");
        }

        if (options.ContentsByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Signature ContentsByteLength must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(options.Filter))
        {
            throw new ArgumentException("Signature Filter is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.SubFilter))
        {
            throw new ArgumentException("Signature SubFilter is required.", nameof(options));
        }

        EnsureAsciiString(options.FieldName, "Signature FieldName");
        EnsureAsciiString(options.Filter, "Signature Filter");
        EnsureAsciiString(options.SubFilter, "Signature SubFilter");
        EnsureAsciiString(options.Name, "Signature Name");
        EnsureAsciiString(options.Reason, "Signature Reason");
        EnsureAsciiString(options.Location, "Signature Location");
        EnsureAsciiString(options.ContactInfo, "Signature ContactInfo");
    }

    private static void EnsureAsciiString(string? value, string context)
    {
        if (value is null)
        {
            return;
        }

        if (!value.All(char.IsAscii))
        {
            throw new ArgumentException($"{context} must contain only ASCII characters.");
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

        if (!Enum.IsDefined(security.Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(security), "Security profile contains an unsupported value.");
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
