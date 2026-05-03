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
    private static readonly byte[] PasswordPadding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

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
            PdfObject encryptedValue = EncryptObject(indirectObject.Value, indirectObject.ObjectId, material.FileKey);
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

            PdfObject decryptedValue = DecryptObject(item.Value, item.ObjectId, fileKey);
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
        string ownerPassword = options.OwnerPassword ?? options.UserPassword;
        byte[] userPadded = PadPassword(options.UserPassword);
        byte[] ownerPadded = PadPassword(ownerPassword);
        byte[] ownerKey = ComputeOwnerKey(ownerPadded);
        byte[] ownerEntry = Rc4(ownerKey, userPadded);

        int permissionValue = BuildPermissionValue(options.Permissions);
        byte[] documentId = RandomNumberGenerator.GetBytes(16);
        byte[] fileKey = ComputeFileKey(userPadded, ownerEntry, permissionValue, documentId);
        byte[] userEntry = Rc4(fileKey, PasswordPadding);

        return new EncryptionMaterial(ownerEntry, userEntry, fileKey, documentId, permissionValue);
    }

    private static PdfDictionaryObject BuildEncryptionDictionary(EncryptionMaterial material)
    {
        return new PdfDictionaryObject(
        [
            new PdfDictionaryEntry("Filter", new PdfNameObject("Standard")),
            new PdfDictionaryEntry("V", new PdfNumberObject(1, isInteger: true)),
            new PdfDictionaryEntry("R", new PdfNumberObject(2, isInteger: true)),
            new PdfDictionaryEntry("Length", new PdfNumberObject(40, isInteger: true)),
            new PdfDictionaryEntry("P", new PdfNumberObject(material.PermissionValue, isInteger: true)),
            new PdfDictionaryEntry("O", new PdfByteStringObject(material.OwnerEntry)),
            new PdfDictionaryEntry("U", new PdfByteStringObject(material.UserEntry)),
        ]);
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

    private static PdfObject EncryptObject(PdfObject value, PdfObjectId objectId, byte[] fileKey)
    {
        return value switch
        {
            PdfNullObject => value,
            PdfBooleanObject => value,
            PdfNumberObject => value,
            PdfNameObject => value,
            PdfReferenceObject => value,
            PdfStringObject stringValue => new PdfByteStringObject(EncryptBytes(Encoding.ASCII.GetBytes(stringValue.Value), objectId, fileKey)),
            PdfByteStringObject bytesValue => new PdfByteStringObject(EncryptBytes(bytesValue.Bytes.Span, objectId, fileKey)),
            PdfArrayObject arrayValue => new PdfArrayObject(arrayValue.Items.Select(item => EncryptObject(item, objectId, fileKey))),
            PdfDictionaryObject dictionaryValue => new PdfDictionaryObject(
                dictionaryValue.Entries.Select(entry => new PdfDictionaryEntry(entry.Key, EncryptObject(entry.Value, objectId, fileKey)))),
            PdfStreamObject streamValue => new PdfStreamObject(
                dictionary: (PdfDictionaryObject)EncryptObject(streamValue.Dictionary, objectId, fileKey),
                data: EncryptBytes(streamValue.Data.Span, objectId, fileKey)),
            _ => throw new PdfFormatException($"Unsupported object type for encryption '{value.GetType().Name}'."),
        };
    }

    private static PdfObject DecryptObject(PdfObject value, PdfObjectId objectId, byte[] fileKey)
    {
        return value switch
        {
            PdfNullObject => value,
            PdfBooleanObject => value,
            PdfNumberObject => value,
            PdfNameObject => value,
            PdfReferenceObject => value,
            PdfStringObject stringValue => new PdfStringObject(Encoding.ASCII.GetString(DecryptBytes(Encoding.ASCII.GetBytes(stringValue.Value), objectId, fileKey))),
            PdfByteStringObject bytesValue => new PdfStringObject(Encoding.ASCII.GetString(DecryptBytes(bytesValue.Bytes.Span, objectId, fileKey))),
            PdfArrayObject arrayValue => new PdfArrayObject(arrayValue.Items.Select(item => DecryptObject(item, objectId, fileKey))),
            PdfDictionaryObject dictionaryValue => new PdfDictionaryObject(
                dictionaryValue.Entries.Select(entry => new PdfDictionaryEntry(entry.Key, DecryptObject(entry.Value, objectId, fileKey)))),
            PdfStreamObject streamValue => new PdfStreamObject(
                dictionary: (PdfDictionaryObject)DecryptObject(streamValue.Dictionary, objectId, fileKey),
                data: DecryptBytes(streamValue.Data.Span, objectId, fileKey)),
            _ => throw new PdfFormatException($"Unsupported object type for decryption '{value.GetType().Name}'."),
        };
    }

    private static byte[] EncryptBytes(ReadOnlySpan<byte> bytes, PdfObjectId objectId, byte[] fileKey)
    {
        byte[] objectKey = BuildObjectKey(fileKey, objectId);
        return Rc4(objectKey, bytes);
    }

    private static byte[] DecryptBytes(ReadOnlySpan<byte> bytes, PdfObjectId objectId, byte[] fileKey)
    {
        byte[] objectKey = BuildObjectKey(fileKey, objectId);
        return Rc4(objectKey, bytes);
    }

    private static byte[] BuildObjectKey(byte[] fileKey, PdfObjectId objectId)
    {
        byte[] seed = new byte[fileKey.Length + 5];
        Buffer.BlockCopy(fileKey, 0, seed, 0, fileKey.Length);
        seed[fileKey.Length + 0] = (byte)(objectId.ObjectNumber & 0xFF);
        seed[fileKey.Length + 1] = (byte)((objectId.ObjectNumber >> 8) & 0xFF);
        seed[fileKey.Length + 2] = (byte)((objectId.ObjectNumber >> 16) & 0xFF);
        seed[fileKey.Length + 3] = (byte)(objectId.GenerationNumber & 0xFF);
        seed[fileKey.Length + 4] = (byte)((objectId.GenerationNumber >> 8) & 0xFF);

        #pragma warning disable CA5351 // PDF Standard Security Handler R2 requires MD5.
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
            DocumentId: documentId);
        return true;
    }

    private static bool IsSupportedDescriptor(EncryptionDescriptor descriptor)
    {
        return string.Equals(descriptor.Filter, "Standard", StringComparison.Ordinal)
            && descriptor.V == 1
            && descriptor.R == 2
            && descriptor.LengthBits == 40;
    }

    private static bool TryResolveFileKey(EncryptionDescriptor descriptor, string password, out byte[]? fileKey)
    {
        fileKey = null;

        if (!IsSupportedDescriptor(descriptor))
        {
            throw new NotSupportedException("Only Standard security handler V=1 R=2 (40-bit) is supported.");
        }

        if (descriptor.O.Length != 32 || descriptor.U.Length < 32)
        {
            throw new PdfFormatException("Encryption dictionary O/U entries have invalid lengths.");
        }

        byte[] userPadded = PadPassword(password);
        byte[] keyFromUser = ComputeFileKey(userPadded, descriptor.O, descriptor.Permissions, descriptor.DocumentId);
        if (MatchesUserEntry(keyFromUser, descriptor.U))
        {
            fileKey = keyFromUser;
            return true;
        }

        byte[] ownerPadded = PadPassword(password);
        byte[] ownerKey = ComputeOwnerKey(ownerPadded);
        byte[] recoveredUserPadded = Rc4(ownerKey, descriptor.O);
        byte[] keyFromOwner = ComputeFileKey(recoveredUserPadded, descriptor.O, descriptor.Permissions, descriptor.DocumentId);
        if (MatchesUserEntry(keyFromOwner, descriptor.U))
        {
            fileKey = keyFromOwner;
            return true;
        }

        return false;
    }

    private static bool MatchesUserEntry(byte[] fileKey, byte[] uEntry)
    {
        byte[] expected = Rc4(fileKey, PasswordPadding);
        return CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 32), uEntry.AsSpan(0, 32));
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

    private static byte[] ComputeOwnerKey(byte[] ownerPadded)
    {
        #pragma warning disable CA5351 // PDF Standard Security Handler R2 requires MD5.
        byte[] digest = MD5.HashData(ownerPadded);
        #pragma warning restore CA5351
        return digest.AsSpan(0, 5).ToArray();
    }

    private static byte[] ComputeFileKey(byte[] userPadded, byte[] ownerEntry, int permissions, byte[] documentId)
    {
        byte[] buffer = new byte[userPadded.Length + ownerEntry.Length + 4 + documentId.Length];
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
        #pragma warning disable CA5351 // PDF Standard Security Handler R2 requires MD5.
        byte[] digest = MD5.HashData(buffer);
        #pragma warning restore CA5351
        return digest.AsSpan(0, 5).ToArray();
    }

    private static int BuildPermissionValue(PdfPermissions permissions)
    {
        int supportedMask = (int)(PdfPermissions.Print | PdfPermissions.Modify | PdfPermissions.Copy | PdfPermissions.Annotate);
        if (((int)permissions & ~supportedMask) != 0)
        {
            throw new NotSupportedException("Current security profile supports only Print, Modify, Copy, and Annotate permissions.");
        }

        int value = unchecked((int)0xFFFFFFC0);
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

    private readonly record struct EncryptionMaterial(
        byte[] OwnerEntry,
        byte[] UserEntry,
        byte[] FileKey,
        byte[] DocumentId,
        int PermissionValue);

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
        byte[] DocumentId);
}
