using System.Text;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using ModernPDF.Security;

namespace ModernPDF.Tests.Security;

public sealed class PdfStandardSecurityProcessorTests
{
    [Fact]
    public void TryReadEncryptionInfoReturnsFalseForUnencryptedFile()
    {
        PdfFile file = CreatePlainFile();

        bool found = PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out PdfEncryptionInfo? info);

        Assert.False(found);
        Assert.Null(info);
        Assert.False(PdfStandardSecurityProcessor.IsSupportedStandardHandler(file));
    }

    [Fact]
    public void TryReadEncryptionInfoThrowsWhenEncryptEntryHasInvalidType()
    {
        PdfFile file = CreateFileWithEncrypt(new PdfNumberObject(1, isInteger: true), []);

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out _));
    }

    [Fact]
    public void IsSupportedStandardHandlerReturnsFalseForUnsupportedFilter()
    {
        PdfDictionaryObject encrypt = CreateEncryptDictionary(filter: "Custom", v: 1, r: 2, length: 40);
        PdfFile file = CreateFileWithEncrypt(encrypt, []);

        bool supported = PdfStandardSecurityProcessor.IsSupportedStandardHandler(file);

        Assert.False(supported);
    }

    [Fact]
    public void DecryptThrowsWhenDescriptorIsMissing()
    {
        PdfFile file = CreatePlainFile();

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.Decrypt(file, "password"));
    }

    [Fact]
    public void DecryptThrowsForUnsupportedDescriptorVersion()
    {
        PdfDictionaryObject encrypt = CreateEncryptDictionary(filter: "Standard", v: 2, r: 2, length: 40);
        PdfFile file = CreateFileWithEncrypt(encrypt, []);

        Assert.Throws<NotSupportedException>(() => PdfStandardSecurityProcessor.Decrypt(file, "password"));
    }

    [Fact]
    public void DecryptThrowsForInvalidOwnerOrUserEntryLengths()
    {
        PdfDictionaryObject encrypt = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject(40, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfByteStringObject(new byte[] { 0x01, 0x02 })),
            new PdfDictionaryEntry("U", new PdfByteStringObject(new byte[] { 0x03, 0x04 })),
        ]);
        PdfFile file = CreateFileWithEncrypt(encrypt, []);

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.Decrypt(file, "password"));
    }

    [Fact]
    public void DecryptThrowsWhenEncryptReferenceIsMissing()
    {
        PdfReferenceObject encryptReference = new(new PdfObjectId(9, 0));
        PdfFile file = CreateFileWithEncrypt(encryptReference, []);

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.Decrypt(file, "password"));
    }

    [Fact]
    public void DecryptThrowsWhenEncryptReferenceTargetIsNotDictionary()
    {
        PdfReferenceObject encryptReference = new(new PdfObjectId(9, 0));
        List<PdfIndirectObject> objects =
        [
            new PdfIndirectObject(new PdfObjectId(9, 0), new PdfStringObject("not-dictionary")),
        ];
        PdfFile file = CreateFileWithEncrypt(encryptReference, objects);

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.Decrypt(file, "password"));
    }

    [Fact]
    public void EncryptValidatesSecurityOptionInputs()
    {
        PdfFile file = CreatePlainFile();

        Assert.Throws<ArgumentException>(() => PdfStandardSecurityProcessor.Encrypt(file, new PdfSecurityOptions { UserPassword = "" }));
        Assert.Throws<ArgumentException>(() => PdfStandardSecurityProcessor.Encrypt(file, new PdfSecurityOptions { UserPassword = "u", OwnerPassword = "" }));
        Assert.Throws<NotSupportedException>(() => PdfStandardSecurityProcessor.Encrypt(file, new PdfSecurityOptions { UserPassword = "u", Permissions = PdfPermissions.FillForms }));
    }

    [Fact]
    public void EncryptAndDecryptRoundTripWithOwnerPassword()
    {
        PdfFile file = CreatePlainFileWithStringObject("Hello");
        PdfSecurityOptions options = new()
        {
            UserPassword = "user-pass",
            OwnerPassword = "owner-pass",
            Permissions = PdfPermissions.Print | PdfPermissions.Copy,
        };

        PdfFile encrypted = PdfStandardSecurityProcessor.Encrypt(file, options);
        PdfFile decrypted = PdfStandardSecurityProcessor.Decrypt(encrypted, "owner-pass");

        PdfIndirectObject valueObject = Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 3);
        PdfStringObject text = Assert.IsType<PdfStringObject>(valueObject.Value);
        Assert.Equal("Hello", text.Value);
    }

    private static PdfFile CreatePlainFile()
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
        ]);
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        return new PdfFile(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
            ],
            trailer);
    }

    private static PdfFile CreatePlainFileWithStringObject(string text)
    {
        PdfDictionaryObject catalog = new(
        [
            new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
        ]);
        PdfStringObject value = new(text);
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
        ]);

        return new PdfFile(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), catalog),
                new PdfIndirectObject(new PdfObjectId(3, 0), value),
            ],
            trailer);
    }

    private static PdfDictionaryObject CreateEncryptDictionary(string filter, int v, int r, int length)
    {
        byte[] o = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] u = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();

        return new PdfDictionaryObject(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject(filter)),
            new PdfDictionaryEntry("V", new PdfNumberObject(v, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(r, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject(length, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfByteStringObject(o)),
            new PdfDictionaryEntry("U", new PdfByteStringObject(u)),
        ]);
    }

    private static PdfFile CreateFileWithEncrypt(PdfObject encryptEntryValue, IEnumerable<PdfIndirectObject> extraObjects)
    {
        List<PdfIndirectObject> objects =
        [
            new PdfIndirectObject(
                new PdfObjectId(1, 0),
                new PdfDictionaryObject(
                [
                    new PdfDictionaryEntry("Type", new PdfNameObject("Catalog")),
                ])),
            .. extraObjects,
        ];

        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Encrypt", encryptEntryValue),
            new PdfDictionaryEntry(
                "ID",
                new PdfArrayObject(
                [
                    new PdfByteStringObject(Encoding.ASCII.GetBytes("0123456789ABCDEF")),
                ])),
        ]);

        return new PdfFile("2.0", objects, trailer);
    }
}
