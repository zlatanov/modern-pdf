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

    [Fact]
    public void EncryptAndDecryptRoundTripWithStandard128BitRc4Profile()
    {
        PdfFile file = CreatePlainFileWithStringObject("Hello-128-RC4");
        PdfSecurityOptions options = new()
        {
            UserPassword = "user-pass",
            OwnerPassword = "owner-pass",
            Profile = PdfSecurityProfile.Standard128BitRc4,
            Permissions = PdfPermissions.Print | PdfPermissions.Copy | PdfPermissions.FillForms | PdfPermissions.Accessibility,
        };

        PdfFile encrypted = PdfStandardSecurityProcessor.Encrypt(file, options);
        PdfFile decrypted = PdfStandardSecurityProcessor.Decrypt(encrypted, "user-pass");

        PdfStringObject text = Assert.IsType<PdfStringObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 3).Value);
        Assert.Equal("Hello-128-RC4", text.Value);

        Assert.True(PdfStandardSecurityProcessor.TryReadEncryptionInfo(encrypted, out PdfEncryptionInfo? info));
        Assert.NotNull(info);
        Assert.Equal(2, info.AlgorithmVersion);
        Assert.Equal(128, info.KeyLengthBits);
    }

    [Fact]
    public void EncryptAndDecryptRoundTripWithStandard128BitAesProfile()
    {
        PdfFile file = CreatePlainFileWithStringObject("Hello-128-AES");
        PdfSecurityOptions options = new()
        {
            UserPassword = "user-pass",
            OwnerPassword = "owner-pass",
            Profile = PdfSecurityProfile.Standard128BitAes,
            Permissions = PdfPermissions.Print | PdfPermissions.Copy | PdfPermissions.FillForms | PdfPermissions.HighQualityPrint,
        };

        PdfFile encrypted = PdfStandardSecurityProcessor.Encrypt(file, options);
        PdfFile decrypted = PdfStandardSecurityProcessor.Decrypt(encrypted, "owner-pass");

        PdfStringObject text = Assert.IsType<PdfStringObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 3).Value);
        Assert.Equal("Hello-128-AES", text.Value);

        Assert.True(PdfStandardSecurityProcessor.TryReadEncryptionInfo(encrypted, out PdfEncryptionInfo? info));
        Assert.NotNull(info);
        Assert.Equal(4, info.AlgorithmVersion);
        Assert.Equal(128, info.KeyLengthBits);
    }

    [Fact]
    public void EncryptWithAesProfileWritesCryptFilterEntries()
    {
        PdfFile file = CreatePlainFileWithStringObject("cf-check");
        PdfFile encrypted = PdfStandardSecurityProcessor.Encrypt(
            file,
            new PdfSecurityOptions
            {
                UserPassword = "pw",
                Profile = PdfSecurityProfile.Standard128BitAes,
            });

        PdfDictionaryObject encryptDictionary = Assert.IsType<PdfDictionaryObject>(
            Assert.Single(
                encrypted.Objects,
                item => item.Value is PdfDictionaryObject dictionary
                    && dictionary.Entries.Any(entry => string.Equals(entry.Key, "Filter", StringComparison.Ordinal))).Value);

        PdfNameObject filter = Assert.IsType<PdfNameObject>(encryptDictionary.Entries.Single(entry => entry.Key == "Filter").Value);
        Assert.Equal("Standard", filter.Value);
        Assert.IsType<PdfDictionaryObject>(encryptDictionary.Entries.Single(entry => entry.Key == "CF").Value);
        Assert.Equal("StdCF", Assert.IsType<PdfNameObject>(encryptDictionary.Entries.Single(entry => entry.Key == "StrF").Value).Value);
        Assert.Equal("StdCF", Assert.IsType<PdfNameObject>(encryptDictionary.Entries.Single(entry => entry.Key == "StmF").Value).Value);
    }

    [Fact]
    public void EncryptAndDecryptHandlePrimitiveAndCompositeObjects()
    {
        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), new PdfDictionaryObject([new PdfDictionaryEntry("Type", new PdfNameObject("Catalog"))])),
                new PdfIndirectObject(new PdfObjectId(3, 0), PdfNullObject.Instance),
                new PdfIndirectObject(new PdfObjectId(4, 0), new PdfBooleanObject(true)),
                new PdfIndirectObject(new PdfObjectId(5, 0), new PdfNumberObject(7, isInteger: true)),
                new PdfIndirectObject(new PdfObjectId(6, 0), new PdfNameObject("N")),
                new PdfIndirectObject(new PdfObjectId(7, 0), new PdfReferenceObject(new PdfObjectId(1, 0))),
                new PdfIndirectObject(new PdfObjectId(8, 0), new PdfByteStringObject(Encoding.ASCII.GetBytes("ABC"))),
                new PdfIndirectObject(new PdfObjectId(9, 0), new PdfArrayObject([new PdfNumberObject(1, isInteger: true), new PdfStringObject("X")])),
                new PdfIndirectObject(new PdfObjectId(10, 0), new PdfDictionaryObject([new PdfDictionaryEntry("K", new PdfStringObject("V"))])),
                new PdfIndirectObject(new PdfObjectId(11, 0), new PdfStreamObject(new PdfDictionaryObject([]), Encoding.ASCII.GetBytes("DATA"))),
            ],
            new PdfDictionaryObject([new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0)))]));

        PdfFile encrypted = PdfStandardSecurityProcessor.Encrypt(file, new PdfSecurityOptions { UserPassword = "pw" });
        PdfFile decrypted = PdfStandardSecurityProcessor.Decrypt(encrypted, "pw");

        Assert.IsType<PdfNullObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 3).Value);
        Assert.True(Assert.IsType<PdfBooleanObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 4).Value).Value);
        Assert.Equal(7, Assert.IsType<PdfNumberObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 5).Value).Value);
        Assert.Equal("N", Assert.IsType<PdfNameObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 6).Value).Value);
        Assert.Equal(1, Assert.IsType<PdfReferenceObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 7).Value).ObjectId.ObjectNumber);
        Assert.Equal("ABC", Assert.IsType<PdfStringObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 8).Value).Value);
        Assert.Equal("DATA", Encoding.ASCII.GetString(Assert.IsType<PdfStreamObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 11).Value).Data.Span));
    }

    [Fact]
    public void EncryptThrowsForUnsupportedObjectType()
    {
        PdfFile file = new(
            "2.0",
            [
                new PdfIndirectObject(new PdfObjectId(1, 0), new PdfDictionaryObject([new PdfDictionaryEntry("Type", new PdfNameObject("Catalog"))])),
                new PdfIndirectObject(new PdfObjectId(3, 0), new UnsupportedPdfObject()),
            ],
            new PdfDictionaryObject([new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0)))]));

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.Encrypt(file, new PdfSecurityOptions { UserPassword = "pw" }));
    }

    [Fact]
    public void DecryptThrowsForUnsupportedObjectType()
    {
        PdfFile plain = CreatePlainFileWithStringObject("Hello");
        PdfFile encrypted = PdfStandardSecurityProcessor.Encrypt(plain, new PdfSecurityOptions { UserPassword = "pw" });
        List<PdfIndirectObject> tamperedObjects = encrypted.Objects
            .Select(item => item.ObjectId.ObjectNumber == 3
                ? new PdfIndirectObject(item.ObjectId, new UnsupportedPdfObject())
                : item)
            .ToList();
        PdfFile tampered = new(encrypted.Version, tamperedObjects, encrypted.Trailer);

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.Decrypt(tampered, "pw"));
    }

    [Fact]
    public void DecryptHandlesLiteralStringObjectsInEncryptedBody()
    {
        PdfFile plain = CreatePlainFileWithStringObject("Hello");
        PdfFile encrypted = PdfStandardSecurityProcessor.Encrypt(plain, new PdfSecurityOptions { UserPassword = "pw" });
        List<PdfIndirectObject> tamperedObjects = encrypted.Objects
            .Select(item => item.ObjectId.ObjectNumber == 3
                ? new PdfIndirectObject(item.ObjectId, new PdfStringObject("ABC"))
                : item)
            .ToList();
        PdfFile tampered = new(encrypted.Version, tamperedObjects, encrypted.Trailer);

        PdfFile decrypted = PdfStandardSecurityProcessor.Decrypt(tampered, "pw");

        PdfStringObject value = Assert.IsType<PdfStringObject>(Assert.Single(decrypted.Objects, x => x.ObjectId.ObjectNumber == 3).Value);
        Assert.NotNull(value.Value);
    }

    [Fact]
    public void DecryptThrowsWhenTrailerIdArrayIsEmpty()
    {
        PdfDictionaryObject encrypt = CreateEncryptDictionary(filter: "Standard", v: 1, r: 2, length: 40);
        PdfDictionaryObject trailer = new(
        [
            new PdfDictionaryEntry("Root", new PdfReferenceObject(new PdfObjectId(1, 0))),
            new PdfDictionaryEntry("Encrypt", encrypt),
            new PdfDictionaryEntry("ID", new PdfArrayObject([])),
        ]);
        PdfFile file = new(
            "2.0",
            [new PdfIndirectObject(new PdfObjectId(1, 0), new PdfDictionaryObject([new PdfDictionaryEntry("Type", new PdfNameObject("Catalog"))]))],
            trailer);

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.Decrypt(file, "pw"));
    }

    [Fact]
    public void DecryptThrowsWhenOwnerEntryIsNotString()
    {
        PdfDictionaryObject encrypt = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject(40, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("U", new PdfByteStringObject(new byte[32])),
        ]);
        PdfFile file = CreateFileWithEncrypt(encrypt, []);

        Assert.Throws<PdfFormatException>(() => PdfStandardSecurityProcessor.Decrypt(file, "pw"));
    }

    [Fact]
    public void TryReadEncryptionInfoDefaultsLengthWhenOptionalEntriesAreInvalid()
    {
        PdfDictionaryObject encrypt = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("SubFilter", new PdfNumberObject(7, isInteger: true)),
            new PdfDictionaryEntry("V", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject((double)int.MaxValue + 1, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfByteStringObject(new byte[32])),
            new PdfDictionaryEntry("U", new PdfByteStringObject(new byte[32])),
        ]);
        PdfFile file = CreateFileWithEncrypt(encrypt, []);

        bool found = PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out PdfEncryptionInfo? info);

        Assert.True(found);
        Assert.NotNull(info);
        Assert.Equal(40, info.KeyLengthBits);
        Assert.True(PdfStandardSecurityProcessor.IsSupportedStandardHandler(file));
    }

    [Fact]
    public void TryReadEncryptionInfoDefaultsLengthWhenLengthEntryIsMissing()
    {
        PdfDictionaryObject encrypt = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfByteStringObject(new byte[32])),
            new PdfDictionaryEntry("U", new PdfByteStringObject(new byte[32])),
        ]);
        PdfFile file = CreateFileWithEncrypt(encrypt, []);

        bool found = PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out PdfEncryptionInfo? info);

        Assert.True(found);
        Assert.NotNull(info);
        Assert.Equal(40, info.KeyLengthBits);
    }

    [Fact]
    public void TryReadEncryptionInfoDefaultsLengthWhenLengthEntryIsNonInteger()
    {
        PdfDictionaryObject encrypt = new(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfStringObject("forty")),
            new PdfDictionaryEntry("P", new PdfNumberObject(-4, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfByteStringObject(new byte[32])),
            new PdfDictionaryEntry("U", new PdfByteStringObject(new byte[32])),
        ]);
        PdfFile file = CreateFileWithEncrypt(encrypt, []);

        bool found = PdfStandardSecurityProcessor.TryReadEncryptionInfo(file, out PdfEncryptionInfo? info);

        Assert.True(found);
        Assert.NotNull(info);
        Assert.Equal(40, info.KeyLengthBits);
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

    private sealed class UnsupportedPdfObject : PdfObject
    {
    }
}
