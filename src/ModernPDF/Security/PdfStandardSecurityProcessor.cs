using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Security;

internal static class PdfStandardSecurityProcessor
{
    private const string SupportedProfilesMessage = "Only Standard security handler profiles V=1/R=2 (40-bit RC4), V=2/R=3 (128-bit RC4), and V=4/R=4 (128-bit AES) are supported.";

    private static readonly byte[] PasswordPadding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static readonly byte[] AesObjectKeySalt = [0x73, 0x41, 0x6C, 0x54];

    public static PdfFile Encrypt(PdfFile file, PdfSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);

        ValidateSecurityOptions(options);

        EncryptionMaterial material = CreateEncryptionMaterial(options);
        int encryptObjectNumber = GetNextObjectNumber(file.Objects);
        PdfObjectId encryptObjectId = new(encryptObjectNumber, 0);

        List<PdfIndirectObject> objects = [];
        foreach (PdfIndirectObject indirectObject in file.Objects)
        {
            PdfObject encryptedValue = EncryptObject(
                indirectObject.Value,
                indirectObject.ObjectId,
                material.FileKey,
                material.StringCipher,
                material.StreamCipher);
            objects.Add(new PdfIndirectObject(indirectObject.ObjectId, encryptedValue));
        }

        PdfDictionaryObject encryptDictionary = BuildEncryptionDictionary(material);
        objects.Add(new PdfIndirectObject(encryptObjectId, encryptDictionary));

        PdfDictionaryObject trailer = UpdateTrailerForEncryption(file.Trailer, encryptObjectId, material.DocumentId);
        return new PdfFile(file.Version, objects, trailer);
    }

    public static PdfFile Decrypt(PdfFile file, string password)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(password);

        if (!TryReadDescriptor(file, out EncryptionDescriptor descriptor))
        {
            throw new PdfFormatException("Encrypted PDF descriptor was not found.");
        }

        if (!TryResolveFileKey(descriptor, password, out byte[]? fileKey) || fileKey is null)
        {
            throw new UnauthorizedAccessException("Invalid PDF password.");
        }

        List<PdfIndirectObject> decryptedObjects = [];
        foreach (PdfIndirectObject item in file.Objects)
        {
            if (item.ObjectId == descriptor.EncryptObjectId)
            {
                continue;
            }

            PdfObject decryptedValue = DecryptObject(
                item.Value,
                item.ObjectId,
                fileKey,
                descriptor.StringCipher,
                descriptor.StreamCipher);
            decryptedObjects.Add(new PdfIndirectObject(item.ObjectId, decryptedValue));
        }

        PdfDictionaryObject trailer = RemoveDictionaryEntries(file.Trailer, "Encrypt");
        return new PdfFile(file.Version, decryptedObjects, trailer);
    }

    public static bool TryReadEncryptionInfo(PdfFile file, out PdfEncryptionInfo? info)
    {
        info = null;

        if (!TryReadDescriptor(file, out EncryptionDescriptor descriptor))
        {
            return false;
        }

        info = new PdfEncryptionInfo(
            filter: descriptor.Filter,
            subFilter: descriptor.SubFilter,
            algorithmVersion: descriptor.V,
            keyLengthBits: descriptor.LengthBits);

        return true;
    }

    public static bool IsSupportedStandardHandler(PdfFile file)
    {
        if (!TryReadDescriptor(file, out EncryptionDescriptor descriptor))
        {
            return false;
        }

        return IsSupportedDescriptor(descriptor);
    }

    private static EncryptionMaterial CreateEncryptionMaterial(PdfSecurityOptions options)
    {
        SecurityProfileDefinition profile = ResolveSecurityProfile(options.Profile);

        string ownerPassword = options.OwnerPassword ?? options.UserPassword;
        byte[] userPadded = PadPassword(options.UserPassword);
        byte[] ownerPadded = PadPassword(ownerPassword);
        byte[] ownerKey = ComputeOwnerKey(ownerPadded, profile.KeyLengthBytes, profile.R);
        byte[] ownerEntry = ComputeOwnerEntry(ownerKey, userPadded, profile.R);

        int permissionValue = BuildPermissionValue(options.Permissions, profile.R);
        byte[] documentId = RandomNumberGenerator.GetBytes(16);
        byte[] fileKey = ComputeFileKey(
            userPadded,
            ownerEntry,
            permissionValue,
            documentId,
            profile.KeyLengthBytes,
            profile.R,
            encryptMetadata: true);
        byte[] userEntry = ComputeUserEntry(fileKey, documentId, profile.R);

        return new EncryptionMaterial(
            OwnerEntry: ownerEntry,
            UserEntry: userEntry,
            FileKey: fileKey,
            DocumentId: documentId,
            PermissionValue: permissionValue,
            V: profile.V,
            R: profile.R,
            KeyLengthBits: profile.KeyLengthBits,
            EncryptMetadata: true,
            StringCipher: profile.Cipher,
            StreamCipher: profile.Cipher,
            UseCryptFilters: profile.UseCryptFilters);
    }

    private static SecurityProfileDefinition ResolveSecurityProfile(PdfSecurityProfile profile)
    {
        return profile switch
        {
            PdfSecurityProfile.Standard40BitRc4 => new SecurityProfileDefinition(
                V: 1,
                R: 2,
                KeyLengthBits: 40,
                KeyLengthBytes: 5,
                Cipher: EncryptionCipher.Rc4,
                UseCryptFilters: false),
            PdfSecurityProfile.Standard128BitRc4 => new SecurityProfileDefinition(
                V: 2,
                R: 3,
                KeyLengthBits: 128,
                KeyLengthBytes: 16,
                Cipher: EncryptionCipher.Rc4,
                UseCryptFilters: false),
            PdfSecurityProfile.Standard128BitAes => new SecurityProfileDefinition(
                V: 4,
                R: 4,
                KeyLengthBits: 128,
                KeyLengthBytes: 16,
                Cipher: EncryptionCipher.AesV2,
                UseCryptFilters: true),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), "Security profile contains an unsupported value."),
        };
    }

    private static PdfDictionaryObject BuildEncryptionDictionary(EncryptionMaterial material)
    {
        List<PdfDictionaryEntry> entries =
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(material.V, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(material.R, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject(material.KeyLengthBits, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(material.PermissionValue, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfByteStringObject(material.OwnerEntry)),
            new PdfDictionaryEntry("U", new PdfByteStringObject(material.UserEntry)),
        ];

        if (material.UseCryptFilters)
        {
            string cfm = material.StreamCipher switch
            {
                EncryptionCipher.AesV2 => "AESV2",
                EncryptionCipher.Rc4 => "V2",
                _ => throw new NotSupportedException("Unsupported crypt filter cipher."),
            };

            PdfDictionaryObject stdCf = new(
            [
                new PdfDictionaryEntry("Type", new PdfNameObject("CryptFilter")),
                new PdfDictionaryEntry("CFM", new PdfNameObject(cfm)),
                new PdfDictionaryEntry("AuthEvent", new PdfNameObject("DocOpen")),
                new PdfDictionaryEntry("Length", new PdfNumberObject(material.KeyLengthBits / 8, isInteger: true)),
            ]);

            PdfDictionaryObject cf = new(
            [
                new PdfDictionaryEntry("StdCF", stdCf),
            ]);

            entries.Add(new PdfDictionaryEntry("CF", cf));
            entries.Add(new PdfDictionaryEntry("StmF", new PdfNameObject("StdCF")));
            entries.Add(new PdfDictionaryEntry("StrF", new PdfNameObject("StdCF")));

            if (!material.EncryptMetadata)
            {
                entries.Add(new PdfDictionaryEntry("EncryptMetadata", new PdfBooleanObject(false)));
            }
        }

        return new PdfDictionaryObject(entries);
    }

    private static PdfDictionaryObject UpdateTrailerForEncryption(PdfDictionaryObject trailer, PdfObjectId encryptObjectId, byte[] documentId)
    {
        PdfArrayObject idArray = new(
        [
            new PdfByteStringObject(documentId),
            new PdfByteStringObject(documentId),
        ]);

        List<PdfDictionaryEntry> entries = [];
        foreach (PdfDictionaryEntry entry in trailer.Entries)
        {
            if (!string.Equals(entry.Key, "Encrypt", StringComparison.Ordinal)
                && !string.Equals(entry.Key, "ID", StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
        }

        entries.Add(new PdfDictionaryEntry("Encrypt", new PdfReferenceObject(encryptObjectId)));
        entries.Add(new PdfDictionaryEntry("ID", idArray));
        return new PdfDictionaryObject(entries);
    }

    private static PdfObject EncryptObject(
        PdfObject value,
        PdfObjectId objectId,
        byte[] fileKey,
        EncryptionCipher stringCipher,
        EncryptionCipher streamCipher)
    {
        return value switch
        {
            PdfNullObject => value,
            PdfBooleanObject => value,
            PdfNumberObject => value,
            PdfNameObject => value,
            PdfReferenceObject => value,
            PdfStringObject stringValue => new PdfByteStringObject(EncryptBytes(Encoding.ASCII.GetBytes(stringValue.Value), objectId, fileKey, stringCipher)),
            PdfByteStringObject bytesValue => new PdfByteStringObject(EncryptBytes(bytesValue.Bytes.Span, objectId, fileKey, stringCipher)),
            PdfArrayObject arrayValue => new PdfArrayObject(arrayValue.Items.Select(item => EncryptObject(item, objectId, fileKey, stringCipher, streamCipher))),
            PdfDictionaryObject dictionaryValue => new PdfDictionaryObject(
                dictionaryValue.Entries.Select(entry => new PdfDictionaryEntry(entry.Key, EncryptObject(entry.Value, objectId, fileKey, stringCipher, streamCipher)))),
            PdfStreamObject streamValue => new PdfStreamObject(
                dictionary: (PdfDictionaryObject)EncryptObject(streamValue.Dictionary, objectId, fileKey, stringCipher, streamCipher),
                data: EncryptBytes(streamValue.Data.Span, objectId, fileKey, streamCipher)),
            _ => throw new PdfFormatException($"Unsupported object type for encryption '{value.GetType().Name}'."),
        };
    }

    private static PdfObject DecryptObject(
        PdfObject value,
        PdfObjectId objectId,
        byte[] fileKey,
        EncryptionCipher stringCipher,
        EncryptionCipher streamCipher)
    {
        return value switch
        {
            PdfNullObject => value,
            PdfBooleanObject => value,
            PdfNumberObject => value,
            PdfNameObject => value,
            PdfReferenceObject => value,
            PdfStringObject stringValue => new PdfStringObject(Encoding.ASCII.GetString(DecryptBytes(Encoding.ASCII.GetBytes(stringValue.Value), objectId, fileKey, stringCipher))),
            PdfByteStringObject bytesValue => new PdfStringObject(Encoding.ASCII.GetString(DecryptBytes(bytesValue.Bytes.Span, objectId, fileKey, stringCipher))),
            PdfArrayObject arrayValue => new PdfArrayObject(arrayValue.Items.Select(item => DecryptObject(item, objectId, fileKey, stringCipher, streamCipher))),
            PdfDictionaryObject dictionaryValue => new PdfDictionaryObject(
                dictionaryValue.Entries.Select(entry => new PdfDictionaryEntry(entry.Key, DecryptObject(entry.Value, objectId, fileKey, stringCipher, streamCipher)))),
            PdfStreamObject streamValue => new PdfStreamObject(
                dictionary: (PdfDictionaryObject)DecryptObject(streamValue.Dictionary, objectId, fileKey, stringCipher, streamCipher),
                data: DecryptBytes(streamValue.Data.Span, objectId, fileKey, streamCipher)),
            _ => throw new PdfFormatException($"Unsupported object type for decryption '{value.GetType().Name}'."),
        };
    }

    private static byte[] EncryptBytes(ReadOnlySpan<byte> bytes, PdfObjectId objectId, byte[] fileKey, EncryptionCipher cipher)
    {
        byte[] objectKey = BuildObjectKey(fileKey, objectId, cipher);
        return cipher switch
        {
            EncryptionCipher.Rc4 => Rc4(objectKey, bytes),
            EncryptionCipher.AesV2 => EncryptAesV2(objectKey, bytes),
            _ => throw new NotSupportedException("Unsupported encryption cipher."),
        };
    }

    private static byte[] DecryptBytes(ReadOnlySpan<byte> bytes, PdfObjectId objectId, byte[] fileKey, EncryptionCipher cipher)
    {
        byte[] objectKey = BuildObjectKey(fileKey, objectId, cipher);
        return cipher switch
        {
            EncryptionCipher.Rc4 => Rc4(objectKey, bytes),
            EncryptionCipher.AesV2 => DecryptAesV2(objectKey, bytes),
            _ => throw new NotSupportedException("Unsupported decryption cipher."),
        };
    }

    private static byte[] BuildObjectKey(byte[] fileKey, PdfObjectId objectId, EncryptionCipher cipher)
    {
        int seedLength = fileKey.Length + 5 + (cipher == EncryptionCipher.AesV2 ? AesObjectKeySalt.Length : 0);
        byte[] seed = new byte[seedLength];
        Buffer.BlockCopy(fileKey, 0, seed, 0, fileKey.Length);
        seed[fileKey.Length + 0] = (byte)(objectId.ObjectNumber & 0xFF);
        seed[fileKey.Length + 1] = (byte)((objectId.ObjectNumber >> 8) & 0xFF);
        seed[fileKey.Length + 2] = (byte)((objectId.ObjectNumber >> 16) & 0xFF);
        seed[fileKey.Length + 3] = (byte)(objectId.GenerationNumber & 0xFF);
        seed[fileKey.Length + 4] = (byte)((objectId.GenerationNumber >> 8) & 0xFF);
        if (cipher == EncryptionCipher.AesV2)
        {
            Buffer.BlockCopy(AesObjectKeySalt, 0, seed, fileKey.Length + 5, AesObjectKeySalt.Length);
        }

        #pragma warning disable CA5351 // Standard security key derivation uses MD5 by specification.
        byte[] digest = MD5.HashData(seed);
        #pragma warning restore CA5351

        int keyLength = Math.Min(fileKey.Length + 5, 16);
        return digest.AsSpan(0, keyLength).ToArray();
    }

    private static bool TryReadDescriptor(PdfFile file, out EncryptionDescriptor descriptor)
    {
        descriptor = default;

        if (!TryGetDictionaryEntry(file.Trailer, "Encrypt", out PdfObject? encryptObject))
        {
            return false;
        }

        PdfObjectId encryptObjectId;
        PdfDictionaryObject dictionary;

        if (encryptObject is PdfReferenceObject reference)
        {
            encryptObjectId = reference.ObjectId;
            dictionary = ResolveDictionaryReference(file.Objects, reference.ObjectId);
        }
        else if (encryptObject is PdfDictionaryObject inlineDictionary)
        {
            encryptObjectId = default;
            dictionary = inlineDictionary;
        }
        else
        {
            throw new PdfFormatException("Trailer /Encrypt must be a dictionary or a dictionary reference.");
        }

        string filter = RequireName(dictionary, "Filter");
        string? subFilter = TryReadName(dictionary, "SubFilter");
        int v = RequireInteger(dictionary, "V");
        int r = RequireInteger(dictionary, "R");
        int lengthBits = TryReadInteger(dictionary, "Length") ?? 40;
        int permissions = RequireInteger(dictionary, "P");
        byte[] o = RequireByteString(dictionary, "O");
        byte[] u = RequireByteString(dictionary, "U");
        byte[] documentId = RequireDocumentId(file.Trailer);
        bool encryptMetadata = TryReadBoolean(dictionary, "EncryptMetadata") ?? true;

        EncryptionCipher stringCipher = EncryptionCipher.Rc4;
        EncryptionCipher streamCipher = EncryptionCipher.Rc4;
        if (v == 4 && !TryResolveV4CryptFilterCiphers(dictionary, out stringCipher, out streamCipher))
        {
            stringCipher = EncryptionCipher.Unsupported;
            streamCipher = EncryptionCipher.Unsupported;
        }

        descriptor = new EncryptionDescriptor(
            EncryptObjectId: encryptObjectId,
            Filter: filter,
            SubFilter: subFilter,
            V: v,
            R: r,
            LengthBits: lengthBits,
            Permissions: permissions,
            O: o,
            U: u,
            DocumentId: documentId,
            EncryptMetadata: encryptMetadata,
            StringCipher: stringCipher,
            StreamCipher: streamCipher);
        return true;
    }

    private static bool TryResolveV4CryptFilterCiphers(PdfDictionaryObject encryptDictionary, out EncryptionCipher stringCipher, out EncryptionCipher streamCipher)
    {
        stringCipher = EncryptionCipher.Unsupported;
        streamCipher = EncryptionCipher.Unsupported;

        if (!TryGetDictionaryEntry(encryptDictionary, "CF", out PdfObject? cryptFiltersObject) || cryptFiltersObject is not PdfDictionaryObject cryptFilters)
        {
            return false;
        }

        string? strF = TryReadName(encryptDictionary, "StrF");
        string? stmF = TryReadName(encryptDictionary, "StmF");
        if (string.IsNullOrWhiteSpace(strF) || string.IsNullOrWhiteSpace(stmF))
        {
            return false;
        }

        if (string.Equals(strF, "Identity", StringComparison.Ordinal) || string.Equals(stmF, "Identity", StringComparison.Ordinal))
        {
            return false;
        }

        return TryResolveCryptFilterCipher(cryptFilters, strF, out stringCipher)
            && TryResolveCryptFilterCipher(cryptFilters, stmF, out streamCipher);
    }

    private static bool TryResolveCryptFilterCipher(PdfDictionaryObject cryptFilters, string filterName, out EncryptionCipher cipher)
    {
        cipher = EncryptionCipher.Unsupported;
        if (!TryGetDictionaryEntry(cryptFilters, filterName, out PdfObject? filterObject) || filterObject is not PdfDictionaryObject filterDictionary)
        {
            return false;
        }

        string? cfm = TryReadName(filterDictionary, "CFM");
        if (string.IsNullOrWhiteSpace(cfm))
        {
            return false;
        }

        cipher = cfm switch
        {
            "V2" => EncryptionCipher.Rc4,
            "AESV2" => EncryptionCipher.AesV2,
            _ => EncryptionCipher.Unsupported,
        };

        return cipher != EncryptionCipher.Unsupported;
    }

    private static bool IsSupportedDescriptor(EncryptionDescriptor descriptor)
    {
        if (!string.Equals(descriptor.Filter, "Standard", StringComparison.Ordinal))
        {
            return false;
        }

        return (descriptor.V, descriptor.R, descriptor.LengthBits, descriptor.StringCipher, descriptor.StreamCipher) switch
        {
            (1, 2, 40, EncryptionCipher.Rc4, EncryptionCipher.Rc4) => true,
            (2, 3, 128, EncryptionCipher.Rc4, EncryptionCipher.Rc4) => true,
            (4, 4, 128, EncryptionCipher.AesV2, EncryptionCipher.AesV2) => true,
            _ => false,
        };
    }

    private static bool TryResolveFileKey(EncryptionDescriptor descriptor, string password, out byte[]? fileKey)
    {
        fileKey = null;

        if (!IsSupportedDescriptor(descriptor))
        {
            throw new NotSupportedException(SupportedProfilesMessage);
        }

        if (descriptor.O.Length != 32 || descriptor.U.Length < 32)
        {
            throw new PdfFormatException("Encryption dictionary O/U entries have invalid lengths.");
        }

        int keyLengthBytes = descriptor.LengthBits / 8;
        if (keyLengthBytes is <= 0 or > 16)
        {
            throw new PdfFormatException("Encryption dictionary key length is invalid.");
        }

        byte[] userPadded = PadPassword(password);
        byte[] keyFromUser = ComputeFileKey(
            userPadded,
            descriptor.O,
            descriptor.Permissions,
            descriptor.DocumentId,
            keyLengthBytes,
            descriptor.R,
            descriptor.EncryptMetadata);
        if (MatchesUserEntry(keyFromUser, descriptor.U, descriptor.DocumentId, descriptor.R))
        {
            fileKey = keyFromUser;
            return true;
        }

        byte[] ownerPadded = PadPassword(password);
        byte[] ownerKey = ComputeOwnerKey(ownerPadded, keyLengthBytes, descriptor.R);
        byte[] recoveredUserPadded = RecoverUserPaddedFromOwnerEntry(ownerKey, descriptor.O, descriptor.R);
        byte[] keyFromOwner = ComputeFileKey(
            recoveredUserPadded,
            descriptor.O,
            descriptor.Permissions,
            descriptor.DocumentId,
            keyLengthBytes,
            descriptor.R,
            descriptor.EncryptMetadata);
        if (MatchesUserEntry(keyFromOwner, descriptor.U, descriptor.DocumentId, descriptor.R))
        {
            fileKey = keyFromOwner;
            return true;
        }

        return false;
    }

    private static bool MatchesUserEntry(byte[] fileKey, byte[] uEntry, byte[] documentId, int revision)
    {
        if (revision == 2)
        {
            byte[] expected = Rc4(fileKey, PasswordPadding);
            return CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 32), uEntry.AsSpan(0, 32));
        }

        byte[] digest = ComputeUserValidationDigest(documentId);
        byte[] encrypted = Rc4(fileKey, digest);
        for (int i = 1; i <= 19; i++)
        {
            encrypted = Rc4(XorKey(fileKey, (byte)i), encrypted);
        }

        return CryptographicOperations.FixedTimeEquals(encrypted.AsSpan(0, 16), uEntry.AsSpan(0, 16));
    }

    private static byte[] PadPassword(string password)
    {
        byte[] source = Encoding.ASCII.GetBytes(password);
        byte[] padded = new byte[32];

        int copyLength = Math.Min(source.Length, 32);
        source.AsSpan(0, copyLength).CopyTo(padded);
        if (copyLength < 32)
        {
            PasswordPadding.AsSpan(0, 32 - copyLength).CopyTo(padded.AsSpan(copyLength));
        }

        return padded;
    }

    private static byte[] ComputeOwnerKey(byte[] ownerPadded, int keyLengthBytes, int revision)
    {
        #pragma warning disable CA5351 // Standard security key derivation uses MD5 by specification.
        byte[] digest = MD5.HashData(ownerPadded);
        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                digest = MD5.HashData(digest);
            }
        }
        #pragma warning restore CA5351

        return digest.AsSpan(0, keyLengthBytes).ToArray();
    }

    private static byte[] ComputeOwnerEntry(byte[] ownerKey, byte[] userPadded, int revision)
    {
        if (revision == 2)
        {
            return Rc4(ownerKey, userPadded);
        }

        byte[] encrypted = Rc4(ownerKey, userPadded);
        for (int i = 1; i <= 19; i++)
        {
            encrypted = Rc4(XorKey(ownerKey, (byte)i), encrypted);
        }

        return encrypted;
    }

    private static byte[] RecoverUserPaddedFromOwnerEntry(byte[] ownerKey, byte[] ownerEntry, int revision)
    {
        if (revision == 2)
        {
            return Rc4(ownerKey, ownerEntry);
        }

        byte[] recovered = ownerEntry.ToArray();
        for (int i = 19; i >= 0; i--)
        {
            recovered = Rc4(XorKey(ownerKey, (byte)i), recovered);
        }

        return recovered;
    }

    private static byte[] ComputeFileKey(
        byte[] userPadded,
        byte[] ownerEntry,
        int permissions,
        byte[] documentId,
        int keyLengthBytes,
        int revision,
        bool encryptMetadata)
    {
        int metadataBytes = revision >= 4 && !encryptMetadata ? 4 : 0;
        byte[] buffer = new byte[userPadded.Length + ownerEntry.Length + 4 + documentId.Length + metadataBytes];
        int offset = 0;

        Buffer.BlockCopy(userPadded, 0, buffer, offset, userPadded.Length);
        offset += userPadded.Length;
        Buffer.BlockCopy(ownerEntry, 0, buffer, offset, ownerEntry.Length);
        offset += ownerEntry.Length;

        buffer[offset + 0] = (byte)(permissions & 0xFF);
        buffer[offset + 1] = (byte)((permissions >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((permissions >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((permissions >> 24) & 0xFF);
        offset += 4;

        Buffer.BlockCopy(documentId, 0, buffer, offset, documentId.Length);
        offset += documentId.Length;

        if (metadataBytes == 4)
        {
            buffer[offset + 0] = 0xFF;
            buffer[offset + 1] = 0xFF;
            buffer[offset + 2] = 0xFF;
            buffer[offset + 3] = 0xFF;
        }

        #pragma warning disable CA5351 // Standard security key derivation uses MD5 by specification.
        byte[] digest = MD5.HashData(buffer);
        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                digest = MD5.HashData(digest.AsSpan(0, keyLengthBytes));
            }
        }
        #pragma warning restore CA5351

        return digest.AsSpan(0, keyLengthBytes).ToArray();
    }

    private static byte[] ComputeUserEntry(byte[] fileKey, byte[] documentId, int revision)
    {
        if (revision == 2)
        {
            return Rc4(fileKey, PasswordPadding);
        }

        byte[] digest = ComputeUserValidationDigest(documentId);
        byte[] encrypted = Rc4(fileKey, digest);
        for (int i = 1; i <= 19; i++)
        {
            encrypted = Rc4(XorKey(fileKey, (byte)i), encrypted);
        }

        byte[] result = new byte[32];
        encrypted.CopyTo(result, 0);
        RandomNumberGenerator.Fill(result.AsSpan(16));
        return result;
    }

    private static byte[] ComputeUserValidationDigest(byte[] documentId)
    {
        byte[] buffer = new byte[PasswordPadding.Length + documentId.Length];
        Buffer.BlockCopy(PasswordPadding, 0, buffer, 0, PasswordPadding.Length);
        Buffer.BlockCopy(documentId, 0, buffer, PasswordPadding.Length, documentId.Length);

        #pragma warning disable CA5351 // Standard security key derivation uses MD5 by specification.
        return MD5.HashData(buffer);
        #pragma warning restore CA5351
    }

    private static byte[] XorKey(byte[] key, byte value)
    {
        byte[] result = new byte[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            result[i] = (byte)(key[i] ^ value);
        }

        return result;
    }

    private static int BuildPermissionValue(PdfPermissions permissions, int revision)
    {
        PdfPermissions supportedPermissions = revision == 2
            ? PdfPermissions.Print | PdfPermissions.Modify | PdfPermissions.Copy | PdfPermissions.Annotate
            : PdfPermissions.All;

        if ((permissions & ~supportedPermissions) != 0)
        {
            throw new NotSupportedException(
                revision == 2
                    ? "Current 40-bit security profile supports only Print, Modify, Copy, and Annotate permissions."
                    : "Current 128-bit security profiles support permissions up to HighQualityPrint.");
        }

        int value = revision == 2
            ? unchecked((int)0xFFFFFFC0)
            : unchecked((int)0xFFFFF0C0);

        if ((permissions & PdfPermissions.Print) != 0)
        {
            value |= 1 << 2;
        }

        if ((permissions & PdfPermissions.Modify) != 0)
        {
            value |= 1 << 3;
        }

        if ((permissions & PdfPermissions.Copy) != 0)
        {
            value |= 1 << 4;
        }

        if ((permissions & PdfPermissions.Annotate) != 0)
        {
            value |= 1 << 5;
        }

        if ((permissions & PdfPermissions.FillForms) != 0)
        {
            value |= 1 << 8;
        }

        if ((permissions & PdfPermissions.Accessibility) != 0)
        {
            value |= 1 << 9;
        }

        if ((permissions & PdfPermissions.AssembleDocument) != 0)
        {
            value |= 1 << 10;
        }

        if ((permissions & PdfPermissions.HighQualityPrint) != 0)
        {
            value |= 1 << 11;
        }

        return value;
    }

    private static byte[] Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        byte[] state = new byte[256];
        for (int i = 0; i < state.Length; i++)
        {
            state[i] = (byte)i;
        }

        int j = 0;
        for (int i = 0; i < state.Length; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        byte[] output = new byte[data.Length];
        int iIndex = 0;
        j = 0;

        for (int k = 0; k < data.Length; k++)
        {
            iIndex = (iIndex + 1) & 0xFF;
            j = (j + state[iIndex]) & 0xFF;
            (state[iIndex], state[j]) = (state[j], state[iIndex]);
            int t = (state[iIndex] + state[j]) & 0xFF;
            byte streamByte = state[t];
            output[k] = (byte)(data[k] ^ streamByte);
        }

        return output;
    }

    private static byte[] EncryptAesV2(byte[] key, ReadOnlySpan<byte> plainBytes)
    {
        if (key.Length != 16)
        {
            throw new PdfFormatException("AES-128 object key must be 16 bytes.");
        }

        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.IV = RandomNumberGenerator.GetBytes(16);

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        byte[] cipher = encryptor.TransformFinalBlock(plainBytes.ToArray(), 0, plainBytes.Length);
        byte[] result = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, result, aes.IV.Length, cipher.Length);
        return result;
    }

    private static byte[] DecryptAesV2(byte[] key, ReadOnlySpan<byte> encryptedBytes)
    {
        if (key.Length != 16)
        {
            throw new PdfFormatException("AES-128 object key must be 16 bytes.");
        }

        if (encryptedBytes.Length < 16)
        {
            throw new PdfFormatException("AES-encrypted object content is too short to include an initialization vector.");
        }

        byte[] iv = encryptedBytes[..16].ToArray();
        byte[] cipher = encryptedBytes[16..].ToArray();

        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        try
        {
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        }
        catch (CryptographicException exception)
        {
            throw new PdfFormatException($"AES-encrypted object content could not be decrypted: {exception.Message}");
        }
    }

    private static PdfDictionaryObject ResolveDictionaryReference(IEnumerable<PdfIndirectObject> objects, PdfObjectId objectId)
    {
        foreach (PdfIndirectObject item in objects)
        {
            if (item.ObjectId == objectId)
            {
                return item.Value as PdfDictionaryObject
                    ?? throw new PdfFormatException($"Object {objectId} is not a dictionary.");
            }
        }

        throw new PdfFormatException($"Object {objectId} was not found.");
    }

    private static byte[] RequireDocumentId(PdfDictionaryObject trailer)
    {
        PdfObject idObject = RequireDictionaryEntry(trailer, "ID");
        if (idObject is not PdfArrayObject idArray || idArray.Items.Count == 0)
        {
            throw new PdfFormatException("Trailer /ID must be an array with at least one entry.");
        }

        return ToByteString(idArray.Items[0], "ID[0]");
    }

    private static string RequireName(PdfDictionaryObject dictionary, string key)
    {
        PdfObject value = RequireDictionaryEntry(dictionary, key);
        return value as PdfNameObject is PdfNameObject nameObject
            ? nameObject.Value
            : throw new PdfFormatException($"Dictionary '/{key}' must be a name.");
    }

    private static int RequireInteger(PdfDictionaryObject dictionary, string key)
    {
        PdfObject value = RequireDictionaryEntry(dictionary, key);
        return value as PdfNumberObject is PdfNumberObject number && number.IsInteger
            ? Convert.ToInt32(number.Value, CultureInfo.InvariantCulture)
            : throw new PdfFormatException($"Dictionary '/{key}' must be an integer.");
    }

    private static byte[] RequireByteString(PdfDictionaryObject dictionary, string key)
    {
        PdfObject value = RequireDictionaryEntry(dictionary, key);
        return ToByteString(value, key);
    }

    private static byte[] ToByteString(PdfObject value, string context)
    {
        return value switch
        {
            PdfByteStringObject byteString => byteString.Bytes.ToArray(),
            PdfStringObject literalString => Encoding.ASCII.GetBytes(literalString.Value),
            _ => throw new PdfFormatException($"Object '{context}' must be a string or hex string."),
        };
    }

    private static string? TryReadName(PdfDictionaryObject dictionary, string key)
    {
        if (!TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            return null;
        }

        return value as PdfNameObject is PdfNameObject nameObject ? nameObject.Value : null;
    }

    private static int? TryReadInteger(PdfDictionaryObject dictionary, string key)
    {
        if (!TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            return null;
        }

        if (value is not PdfNumberObject number || !number.IsInteger)
        {
            return null;
        }

        if (number.Value < int.MinValue || number.Value > int.MaxValue)
        {
            return null;
        }

        return Convert.ToInt32(number.Value, CultureInfo.InvariantCulture);
    }

    private static bool? TryReadBoolean(PdfDictionaryObject dictionary, string key)
    {
        if (!TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            return null;
        }

        return value as PdfBooleanObject is PdfBooleanObject boolean ? boolean.Value : null;
    }

    private static PdfObject RequireDictionaryEntry(PdfDictionaryObject dictionary, string key)
    {
        if (TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            return value ?? throw new PdfFormatException($"Dictionary entry '/{key}' is null.");
        }

        throw new PdfFormatException($"Dictionary entry '/{key}' was not found.");
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

    private static PdfDictionaryObject RemoveDictionaryEntries(PdfDictionaryObject dictionary, params string[] keys)
    {
        HashSet<string> excluded = keys.ToHashSet(StringComparer.Ordinal);
        return new PdfDictionaryObject(dictionary.Entries.Where(entry => !excluded.Contains(entry.Key)));
    }

    private static int GetNextObjectNumber(IEnumerable<PdfIndirectObject> objects)
    {
        int max = 0;
        foreach (PdfIndirectObject item in objects)
        {
            if (item.ObjectId.ObjectNumber > max)
            {
                max = item.ObjectId.ObjectNumber;
            }
        }

        return max + 1;
    }

    private static void ValidateSecurityOptions(PdfSecurityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.UserPassword))
        {
            throw new ArgumentException("Security UserPassword is required.", nameof(options));
        }

        if (options.OwnerPassword is not null && options.OwnerPassword.Length == 0)
        {
            throw new ArgumentException("Security OwnerPassword cannot be empty when provided.", nameof(options));
        }
    }

    private readonly record struct SecurityProfileDefinition(
        int V,
        int R,
        int KeyLengthBits,
        int KeyLengthBytes,
        EncryptionCipher Cipher,
        bool UseCryptFilters);

    private readonly record struct EncryptionMaterial(
        byte[] OwnerEntry,
        byte[] UserEntry,
        byte[] FileKey,
        byte[] DocumentId,
        int PermissionValue,
        int V,
        int R,
        int KeyLengthBits,
        bool EncryptMetadata,
        EncryptionCipher StringCipher,
        EncryptionCipher StreamCipher,
        bool UseCryptFilters);

    private readonly record struct EncryptionDescriptor(
        PdfObjectId EncryptObjectId,
        string Filter,
        string? SubFilter,
        int V,
        int R,
        int LengthBits,
        int Permissions,
        byte[] O,
        byte[] U,
        byte[] DocumentId,
        bool EncryptMetadata,
        EncryptionCipher StringCipher,
        EncryptionCipher StreamCipher);

    private enum EncryptionCipher
    {
        Unsupported = 0,
        Rc4 = 1,
        AesV2 = 2,
    }
}
