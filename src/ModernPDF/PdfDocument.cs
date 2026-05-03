using ModernPDF.DocumentModel;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using ModernPDF.Text;

namespace ModernPDF;

public sealed class PdfDocument
{
    private readonly PdfFile _file;
    private readonly PdfDocumentModel _model;

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
}
