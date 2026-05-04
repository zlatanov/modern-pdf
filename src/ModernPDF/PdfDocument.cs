using ModernPDF.DocumentModel;
using ModernPDF.Fonts;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using ModernPDF.Security;
using ModernPDF.Text;
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ModernPDF;

public sealed class PdfDocument
{
    private static readonly HashSet<string> SupportedCmsSubFilters =
    [
        "adbe.pkcs7.detached",
        "ETSI.CAdES.detached",
        "adbe.pkcs7.sha1",
        "ETSI.RFC3161",
    ];

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

    public void Save(string path, PdfSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, Save(options));
    }

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

    public IReadOnlyList<PdfDetachedSignatureValidationResult> ValidateDetachedSignatures(bool verifyCertificateChain = false)
    {
        return ValidateDetachedSignatures(
            new PdfDetachedSignatureValidationOptions
            {
                VerifyCertificateChain = verifyCertificateChain,
            });
    }

    public IReadOnlyList<PdfDetachedSignatureValidationResult> ValidateDetachedSignatures(PdfDetachedSignatureValidationOptions? options)
    {
        PdfDetachedSignatureValidationOptions effectiveOptions = options ?? new PdfDetachedSignatureValidationOptions();
        ValidateSignatureValidationOptions(effectiveOptions);
        List<PdfDetachedSignatureValidationResult> results = [];
        (List<X509Certificate2> dssCertificates, string? dssDiagnostic) = TryReadDssCertificates();
        try
        {
            foreach (PdfIndirectObject indirectObject in _file.Objects.OrderBy(static objectItem => objectItem.ObjectId.ObjectNumber))
            {
                if (indirectObject.Value is not PdfDictionaryObject dictionary || !IsSignatureDictionary(dictionary))
                {
                    continue;
                }

                results.Add(ValidateDetachedSignature(indirectObject.ObjectId.ObjectNumber, dictionary, effectiveOptions, dssCertificates, dssDiagnostic));
            }
        }
        finally
        {
            foreach (X509Certificate2 certificate in dssCertificates)
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
        IReadOnlyList<X509Certificate2> dssCertificates,
        string? dssDiagnostic)
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
            if (!string.IsNullOrWhiteSpace(dssDiagnostic))
            {
                diagnostics.Add(dssDiagnostic);
            }
            DateTimeOffset? signingTime = timestampSigningTime ?? TryReadFirstSigningTime(signedCms);
            bool? signingTimeValid = null;
            bool? certificateChainValid = null;
            bool? revocationValid = null;
            bool? certificatePolicyValid = null;
            bool trustChecksPassed = true;

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
                (bool chainIsValid, bool revocationIsValid, List<string> chainDiagnostics) = EvaluateSignerChains(signedCms, options, signingTime, dssCertificates);
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
        IReadOnlyList<X509Certificate2> dssCertificates)
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
            chainValid &= signerChainValid;
            if (!signerChainValid)
            {
                diagnostics.AddRange(chain.ChainStatus.Select(static status => $"Certificate chain status: {status.Status} ({status.StatusInformation.Trim()})"));
            }

            if (options.RequireRevocationStatus)
            {
                bool hasRevocationPointers = certificate.Extensions["2.5.29.31"] is not null
                    || certificate.Extensions["1.3.6.1.5.5.7.1.1"] is not null;
                bool signerRevocationValid = signerChainValid
                    && hasRevocationPointers
                    && !chain.ChainStatus.Any(static status =>
                        status.Status is X509ChainStatusFlags.Revoked
                            or X509ChainStatusFlags.RevocationStatusUnknown
                            or X509ChainStatusFlags.OfflineRevocation
                            or X509ChainStatusFlags.NoIssuanceChainPolicy);

                revocationValid &= signerRevocationValid;
                if (!signerRevocationValid)
                {
                    diagnostics.Add(hasRevocationPointers
                        ? options.RevocationCheckMode == PdfRevocationCheckMode.Online
                            ? "Revocation status could not be established for all certificates in the signer chain using online OCSP/CRL retrieval."
                            : "Revocation status could not be established for all certificates in the signer chain."
                        : "Revocation status validation requires CRL or AIA certificate extensions.");
                }
            }
        }

        return (chainValid, revocationValid, diagnostics);
    }

    private (List<X509Certificate2> Certificates, string? Diagnostic) TryReadDssCertificates()
    {
        if (!TryGetDictionaryEntry(_file.Trailer, "Root", out PdfObject? rootObject))
        {
            return ([], "Document trailer is missing /Root entry; DSS certificates could not be loaded.");
        }

        if (!TryResolveDictionaryObject(rootObject!, out PdfDictionaryObject? catalog))
        {
            return ([], "Document catalog could not be resolved; DSS certificates could not be loaded.");
        }

        PdfDictionaryObject resolvedCatalog = catalog!;
        if (!TryGetDictionaryEntry(resolvedCatalog, "DSS", out PdfObject? dssObject))
        {
            return ([], null);
        }

        if (!TryResolveDictionaryObject(dssObject!, out PdfDictionaryObject? dssDictionary))
        {
            return ([], "Document /DSS entry is not a dictionary; DSS certificates were ignored.");
        }

        PdfDictionaryObject resolvedDssDictionary = dssDictionary!;
        if (!TryGetDictionaryEntry(resolvedDssDictionary, "Certs", out PdfObject? certsObject))
        {
            return ([], null);
        }

        if (!TryResolveArrayObject(certsObject!, out PdfArrayObject? certsArray))
        {
            return ([], "Document /DSS /Certs entry is not an array; DSS certificates were ignored.");
        }

        List<X509Certificate2> certificates = [];
        List<string> errors = [];
        PdfArrayObject resolvedCertsArray = certsArray!;
        for (int index = 0; index < resolvedCertsArray.Items.Count; index++)
        {
            if (!TryResolveCertificateBytes(resolvedCertsArray.Items[index], out byte[]? rawCertificate, out string? error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    errors.Add(error);
                }

                continue;
            }

            try
            {
                certificates.Add(X509CertificateLoader.LoadCertificate(rawCertificate!));
            }
            catch (CryptographicException exception)
            {
                errors.Add($"DSS certificate at index {index} is invalid: {exception.Message}");
            }
        }

        string? diagnostic = errors.Count == 0
            ? null
            : $"Some DSS certificates could not be loaded: {string.Join(" | ", errors)}";
        return (certificates, diagnostic);
    }

    private bool TryResolveCertificateBytes(PdfObject value, out byte[]? rawCertificate, out string? error)
    {
        rawCertificate = null;
        error = null;
        if (!TryResolveObject(value, out PdfObject? resolved))
        {
            error = "A DSS certificate reference could not be resolved.";
            return false;
        }

        switch (resolved)
        {
            case PdfStreamObject stream:
                rawCertificate = stream.Data.ToArray();
                return true;
            case PdfByteStringObject byteString:
                rawCertificate = byteString.Bytes.ToArray();
                return true;
            case PdfStringObject stringObject:
                rawCertificate = Encoding.ASCII.GetBytes(stringObject.Value);
                return true;
            default:
                error = resolved is null
                    ? "Unsupported DSS certificate object type '<null>'."
                    : $"Unsupported DSS certificate object type '{resolved.GetType().Name}'.";
                return false;
        }
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
