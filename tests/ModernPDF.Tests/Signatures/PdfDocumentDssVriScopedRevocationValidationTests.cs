using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests;

public sealed class PdfDocumentDssVriScopedRevocationValidationTests
{
    [Fact]
    public void ValidateDetachedSignaturesOfflineRevocationUsesMatchingVriEvidencePerSignature()
    {
        (X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf) = CreateSigningChainWithRevocationPointers();
        using (root)
        using (intermediate)
        using (leaf)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] signedBytes = CreatePdfWithTwoDetachedSignatures(leaf, now);
            SignatureCmsPayload[] signatures = GetSignatureCmsPayloads(signedBytes);
            Assert.Equal(2, signatures.Length);

            string firstSignatureVriKey = ComputeVriDigestKey(signatures[0].CmsBytes);
            string secondSignatureVriKey = ComputeVriDigestKey(signatures[1].CmsBytes);

            byte[] staleLeafOcsp = BuildOcspResponse(
                certificateToCheck: leaf,
                issuerCertificate: intermediate,
                producedAt: now.AddDays(-5),
                thisUpdate: now.AddDays(-5),
                nextUpdate: now.AddDays(-2),
                status: OcspTestCertStatus.Good);
            byte[] staleIntermediateOcsp = BuildOcspResponse(
                certificateToCheck: intermediate,
                issuerCertificate: root,
                producedAt: now.AddDays(-5),
                thisUpdate: now.AddDays(-5),
                nextUpdate: now.AddDays(-2),
                status: OcspTestCertStatus.Good);

            byte[] freshLeafOcsp = BuildOcspResponse(
                certificateToCheck: leaf,
                issuerCertificate: intermediate,
                producedAt: now.AddMinutes(-30),
                thisUpdate: now.AddHours(-1),
                nextUpdate: now.AddDays(1),
                status: OcspTestCertStatus.Good);
            byte[] freshIntermediateOcsp = BuildOcspResponse(
                certificateToCheck: intermediate,
                issuerCertificate: root,
                producedAt: now.AddMinutes(-30),
                thisUpdate: now.AddHours(-1),
                nextUpdate: now.AddDays(1),
                status: OcspTestCertStatus.Good);

            Dictionary<string, DssVriPayload> vriPayloads = new(StringComparer.OrdinalIgnoreCase)
            {
                [firstSignatureVriKey] = new DssVriPayload(
                    OcspResponses: [staleLeafOcsp, staleIntermediateOcsp],
                    Crls: [],
                    Certificates: []),
                [secondSignatureVriKey] = new DssVriPayload(
                    OcspResponses: [freshLeafOcsp, freshIntermediateOcsp],
                    Crls: [],
                    Certificates: []),
            };

            byte[] withEvidence = AddDssEvidenceWithVri(
                signedBytes,
                certificates: [intermediate],
                globalOcspResponses: [],
                globalCrls: [],
                vriPayloads);

            PdfDetachedSignatureValidationResult[] results = PdfDocument
                .Open(withEvidence)
                .ValidateDetachedSignatures(CreateOfflineRevocationOptions(root, now))
                .OrderBy(static item => item.SignatureObjectNumber)
                .ToArray();

            Assert.Equal(2, results.Length);
            Assert.False(results[0].IsValid);
            Assert.True(results[0].CryptographicallyValid);
            Assert.False(results[0].RevocationValid);
            Assert.Contains(results[0].Diagnostics, static item => item.Contains("No fresh, verifiable DSS OCSP response", StringComparison.Ordinal));

            Assert.True(results[1].IsValid);
            Assert.True(results[1].CryptographicallyValid);
            Assert.True(results[1].TrustChecksPassed);
            Assert.True(results[1].RevocationValid);
        }
    }

    [Fact]
    public void ValidateDetachedSignaturesOfflineRevocationDoesNotFallbackToGlobalEvidenceWhenVriExists()
    {
        (X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf) = CreateSigningChainWithRevocationPointers();
        using (root)
        using (intermediate)
        using (leaf)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] signedBytes = CreatePdfWithTwoDetachedSignatures(leaf, now);
            SignatureCmsPayload[] signatures = GetSignatureCmsPayloads(signedBytes);
            Assert.Equal(2, signatures.Length);

            string firstSignatureVriKey = ComputeVriDigestKey(signatures[0].CmsBytes);

            byte[] staleLeafOcsp = BuildOcspResponse(
                certificateToCheck: leaf,
                issuerCertificate: intermediate,
                producedAt: now.AddDays(-5),
                thisUpdate: now.AddDays(-5),
                nextUpdate: now.AddDays(-2),
                status: OcspTestCertStatus.Good);
            byte[] staleIntermediateOcsp = BuildOcspResponse(
                certificateToCheck: intermediate,
                issuerCertificate: root,
                producedAt: now.AddDays(-5),
                thisUpdate: now.AddDays(-5),
                nextUpdate: now.AddDays(-2),
                status: OcspTestCertStatus.Good);

            byte[] freshLeafOcsp = BuildOcspResponse(
                certificateToCheck: leaf,
                issuerCertificate: intermediate,
                producedAt: now.AddMinutes(-30),
                thisUpdate: now.AddHours(-1),
                nextUpdate: now.AddDays(1),
                status: OcspTestCertStatus.Good);
            byte[] freshIntermediateOcsp = BuildOcspResponse(
                certificateToCheck: intermediate,
                issuerCertificate: root,
                producedAt: now.AddMinutes(-30),
                thisUpdate: now.AddHours(-1),
                nextUpdate: now.AddDays(1),
                status: OcspTestCertStatus.Good);

            Dictionary<string, DssVriPayload> vriPayloads = new(StringComparer.OrdinalIgnoreCase)
            {
                [firstSignatureVriKey] = new DssVriPayload(
                    OcspResponses: [staleLeafOcsp, staleIntermediateOcsp],
                    Crls: [],
                    Certificates: []),
            };

            byte[] withEvidence = AddDssEvidenceWithVri(
                signedBytes,
                certificates: [intermediate],
                globalOcspResponses: [freshLeafOcsp, freshIntermediateOcsp],
                globalCrls: [],
                vriPayloads);

            PdfDetachedSignatureValidationResult[] results = PdfDocument
                .Open(withEvidence)
                .ValidateDetachedSignatures(CreateOfflineRevocationOptions(root, now))
                .OrderBy(static item => item.SignatureObjectNumber)
                .ToArray();

            Assert.Equal(2, results.Length);
            Assert.False(results[0].IsValid);
            Assert.False(results[0].RevocationValid);
            Assert.Contains(results[0].Diagnostics, static item => item.Contains("No fresh, verifiable DSS OCSP response", StringComparison.Ordinal));

            Assert.False(results[1].IsValid);
            Assert.False(results[1].RevocationValid);
            Assert.Contains(results[1].Diagnostics, static item => item.Contains("No /DSS /VRI entry matched signature digest key", StringComparison.Ordinal));
        }
    }

    private static PdfDetachedSignatureValidationOptions CreateOfflineRevocationOptions(X509Certificate2 root, DateTimeOffset validationTime)
    {
        return new PdfDetachedSignatureValidationOptions
        {
            VerifyCertificateChain = true,
            RequireRevocationStatus = true,
            RevocationCheckMode = PdfRevocationCheckMode.Offline,
            TrustedRoots = [root],
            ValidationTime = validationTime,
        };
    }

    private static byte[] CreatePdfWithTwoDetachedSignatures(X509Certificate2 signingCertificate, DateTimeOffset now)
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("vri-scope-test");
        byte[] firstSigned = document.SaveSignedDetached(
            payload => CreateDetachedCmsSignature(payload.Span, signingCertificate, now.AddMinutes(-10)),
            new PdfSignatureOptions
            {
                FieldName = "Sig1",
                ContentsByteLength = 8192,
            });

        PdfDocument appendDocument = PdfDocument.Open(firstSigned);
        return appendDocument.SaveSignedDetached(
            payload => CreateDetachedCmsSignature(payload.Span, signingCertificate, now.AddMinutes(-5)),
            new PdfSignatureOptions
            {
                FieldName = "Sig2",
                Y = 24,
                ContentsByteLength = 8192,
            });
    }

    private static byte[] CreateDetachedCmsSignature(ReadOnlySpan<byte> payload, X509Certificate2 signingCertificate, DateTimeOffset signingTime)
    {
        SignedCms cms = new(new ContentInfo(payload.ToArray()), detached: true);
        CmsSigner signer = new(SubjectIdentifierType.IssuerAndSerialNumber, signingCertificate)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    private static (X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Leaf) CreateSigningChainWithRevocationPointers()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        X509Extension crlDistributionPoint = CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["http://modernpdf.test/ca.crl"], critical: false);

        using RSA rootKey = RSA.Create(2048);
        CertificateRequest rootRequest = new(
            "CN=ModernPDF VRI Root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        X509Certificate2 root = rootRequest.CreateSelfSigned(now.AddDays(-7), now.AddYears(5));

        using RSA intermediateKey = RSA.Create(2048);
        CertificateRequest intermediateRequest = new(
            "CN=ModernPDF VRI Intermediate",
            intermediateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
        intermediateRequest.CertificateExtensions.Add(crlDistributionPoint);
        using X509Certificate2 intermediateWithoutKey = intermediateRequest.Create(
            root,
            now.AddDays(-7),
            now.AddYears(3),
            RandomNumberGenerator.GetBytes(16));
        X509Certificate2 intermediate = intermediateWithoutKey.CopyWithPrivateKey(intermediateKey);

        using RSA leafKey = RSA.Create(2048);
        CertificateRequest leafRequest = new(
            "CN=ModernPDF VRI Leaf",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
        leafRequest.CertificateExtensions.Add(crlDistributionPoint);
        using X509Certificate2 leafWithoutKey = leafRequest.Create(
            intermediate,
            now.AddDays(-7),
            now.AddYears(1),
            RandomNumberGenerator.GetBytes(16));
        X509Certificate2 leaf = leafWithoutKey.CopyWithPrivateKey(leafKey);

        return (root, intermediate, leaf);
    }

    private static byte[] BuildOcspResponse(
        X509Certificate2 certificateToCheck,
        X509Certificate2 issuerCertificate,
        DateTimeOffset producedAt,
        DateTimeOffset thisUpdate,
        DateTimeOffset? nextUpdate,
        OcspTestCertStatus status)
    {
        byte[] certSerial = certificateToCheck.GetSerialNumber().Reverse().ToArray();
        byte[] issuerNameHash = SHA256.HashData(issuerCertificate.SubjectName.RawData);
        byte[] issuerKeyHash = SHA256.HashData(issuerCertificate.PublicKey.EncodedKeyValue.RawData);

        AsnWriter singleResponseWriter = new(AsnEncodingRules.DER);
        singleResponseWriter.PushSequence();
        singleResponseWriter.PushSequence();
        singleResponseWriter.PushSequence();
        singleResponseWriter.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
        singleResponseWriter.WriteNull();
        singleResponseWriter.PopSequence();
        singleResponseWriter.WriteOctetString(issuerNameHash);
        singleResponseWriter.WriteOctetString(issuerKeyHash);
        singleResponseWriter.WriteInteger(new BigInteger(certSerial, isUnsigned: true, isBigEndian: true));
        singleResponseWriter.PopSequence();
        switch (status)
        {
            case OcspTestCertStatus.Good:
                singleResponseWriter.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0));
                break;
            case OcspTestCertStatus.Revoked:
                singleResponseWriter.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1));
                singleResponseWriter.WriteGeneralizedTime(producedAt, omitFractionalSeconds: true);
                singleResponseWriter.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 1));
                break;
            default:
                singleResponseWriter.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 2));
                break;
        }

        singleResponseWriter.WriteGeneralizedTime(thisUpdate, omitFractionalSeconds: true);
        if (nextUpdate.HasValue)
        {
            Asn1Tag nextUpdateTag = new(TagClass.ContextSpecific, 0);
            singleResponseWriter.PushSequence(nextUpdateTag);
            singleResponseWriter.WriteGeneralizedTime(nextUpdate.Value, omitFractionalSeconds: true);
            singleResponseWriter.PopSequence(nextUpdateTag);
        }

        singleResponseWriter.PopSequence();
        byte[] singleResponse = singleResponseWriter.Encode();

        AsnWriter tbsResponseDataWriter = new(AsnEncodingRules.DER);
        tbsResponseDataWriter.PushSequence();
        Asn1Tag responderByNameTag = new(TagClass.ContextSpecific, 1);
        tbsResponseDataWriter.PushSequence(responderByNameTag);
        tbsResponseDataWriter.WriteEncodedValue(issuerCertificate.SubjectName.RawData);
        tbsResponseDataWriter.PopSequence(responderByNameTag);
        tbsResponseDataWriter.WriteGeneralizedTime(producedAt, omitFractionalSeconds: true);
        tbsResponseDataWriter.PushSequence();
        tbsResponseDataWriter.WriteEncodedValue(singleResponse);
        tbsResponseDataWriter.PopSequence();
        tbsResponseDataWriter.PopSequence();
        byte[] tbsResponseData = tbsResponseDataWriter.Encode();

        byte[] signature;
        string signatureAlgorithmOid;
        using (RSA? rsa = issuerCertificate.GetRSAPrivateKey())
        {
            if (rsa is null)
            {
                throw new InvalidOperationException("Issuer certificate must provide an RSA private key for OCSP test response generation.");
            }

            signature = rsa.SignData(tbsResponseData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            signatureAlgorithmOid = "1.2.840.113549.1.1.11";
        }

        AsnWriter basicOcspResponseWriter = new(AsnEncodingRules.DER);
        basicOcspResponseWriter.PushSequence();
        basicOcspResponseWriter.WriteEncodedValue(tbsResponseData);
        basicOcspResponseWriter.PushSequence();
        basicOcspResponseWriter.WriteObjectIdentifier(signatureAlgorithmOid);
        basicOcspResponseWriter.WriteNull();
        basicOcspResponseWriter.PopSequence();
        basicOcspResponseWriter.WriteBitString(signature, 0);
        Asn1Tag certsTag = new(TagClass.ContextSpecific, 0);
        basicOcspResponseWriter.PushSequence(certsTag);
        basicOcspResponseWriter.PushSequence();
        basicOcspResponseWriter.WriteEncodedValue(issuerCertificate.RawData);
        basicOcspResponseWriter.PopSequence();
        basicOcspResponseWriter.PopSequence(certsTag);
        basicOcspResponseWriter.PopSequence();
        byte[] basicOcspResponse = basicOcspResponseWriter.Encode();

        AsnWriter ocspResponseWriter = new(AsnEncodingRules.DER);
        ocspResponseWriter.PushSequence();
        ocspResponseWriter.WriteEnumeratedValue(OcspResponseStatus.Successful);
        Asn1Tag responseBytesTag = new(TagClass.ContextSpecific, 0);
        ocspResponseWriter.PushSequence(responseBytesTag);
        ocspResponseWriter.PushSequence();
        ocspResponseWriter.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
        ocspResponseWriter.WriteOctetString(basicOcspResponse);
        ocspResponseWriter.PopSequence();
        ocspResponseWriter.PopSequence(responseBytesTag);
        ocspResponseWriter.PopSequence();
        return ocspResponseWriter.Encode();
    }

    private static SignatureCmsPayload[] GetSignatureCmsPayloads(byte[] sourceBytes)
    {
        PdfFile file = PdfFileReader.Read(sourceBytes);
        List<SignatureCmsPayload> payloads = [];
        foreach (PdfIndirectObject objectItem in file.Objects.OrderBy(static item => item.ObjectId.ObjectNumber))
        {
            if (objectItem.Value is not PdfDictionaryObject dictionary)
            {
                continue;
            }

            if (!IsSignatureDictionary(dictionary))
            {
                continue;
            }

            if (!TryReadCmsBytes(dictionary, out byte[]? cmsBytes))
            {
                continue;
            }

            payloads.Add(new SignatureCmsPayload(objectItem.ObjectId.ObjectNumber, cmsBytes!));
        }

        return payloads.ToArray();
    }

    private static bool IsSignatureDictionary(PdfDictionaryObject dictionary)
    {
        return TryGetDictionaryEntry(dictionary, "Type", out PdfObject? typeValue)
            && typeValue is PdfNameObject typeName
            && string.Equals(typeName.Value, "Sig", StringComparison.Ordinal)
            && TryGetDictionaryEntry(dictionary, "Contents", out _)
            && TryGetDictionaryEntry(dictionary, "ByteRange", out _);
    }

    private static bool TryReadCmsBytes(PdfDictionaryObject dictionary, out byte[]? cmsBytes)
    {
        cmsBytes = null;
        if (!TryGetDictionaryEntry(dictionary, "Contents", out PdfObject? contentsObject) || contentsObject is not PdfByteStringObject byteString)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = byteString.Bytes.Span;
        if (bytes.Length == 0)
        {
            return false;
        }

        if (!TryReadDerEncodedLength(bytes, out int cmsLength))
        {
            return false;
        }

        cmsBytes = bytes[..cmsLength].ToArray();
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

        int lengthOfLength = firstLengthByte & 0x7F;
        if (lengthOfLength == 0 || lengthOfLength > sizeof(int) || bytes.Length < 2 + lengthOfLength)
        {
            return false;
        }

        int contentLengthValue = 0;
        for (int index = 0; index < lengthOfLength; index++)
        {
            contentLengthValue = (contentLengthValue << 8) | bytes[2 + index];
        }

        if (contentLengthValue < 0)
        {
            return false;
        }

        totalLength = checked(2 + lengthOfLength + contentLengthValue);
        return totalLength <= bytes.Length;
    }

    private static string ComputeVriDigestKey(byte[] cmsBytes)
    {
#pragma warning disable CA5350 // DSS /VRI interoperability uses SHA-1 digest keys
        return Convert.ToHexString(SHA1.HashData(cmsBytes));
#pragma warning restore CA5350
    }

    private static byte[] AddDssEvidenceWithVri(
        byte[] sourceBytes,
        IReadOnlyList<X509Certificate2> certificates,
        IReadOnlyList<byte[]> globalOcspResponses,
        IReadOnlyList<byte[]> globalCrls,
        IReadOnlyDictionary<string, DssVriPayload> vriPayloads)
    {
        PdfFile file = PdfFileReader.Read(sourceBytes);
        List<PdfIndirectObject> objects = [.. file.Objects];
        PdfReferenceObject? rootReference = null;
        foreach (PdfDictionaryEntry entry in file.Trailer.Entries)
        {
            if (string.Equals(entry.Key, "Root", StringComparison.Ordinal))
            {
                rootReference = entry.Value as PdfReferenceObject;
                break;
            }
        }

        if (rootReference is null)
        {
            throw new InvalidOperationException("Trailer /Root reference is missing.");
        }

        int catalogIndex = objects.FindIndex(item => item.ObjectId == rootReference.ObjectId);
        if (catalogIndex < 0 || objects[catalogIndex].Value is not PdfDictionaryObject catalogDictionary)
        {
            throw new InvalidOperationException("Catalog dictionary could not be resolved.");
        }

        int nextObjectNumber = objects.Max(static item => item.ObjectId.ObjectNumber) + 1;
        List<PdfObjectId> createdObjectIds = [];

        PdfReferenceObject AddBinaryObject(byte[] data)
        {
            PdfObjectId objectId = new(nextObjectNumber++, 0);
            objects.Add(new PdfIndirectObject(objectId, new PdfStreamObject(new PdfDictionaryObject([]), data)));
            createdObjectIds.Add(objectId);
            return new PdfReferenceObject(objectId);
        }

        List<PdfObject> certificateReferences = [];
        foreach (X509Certificate2 certificate in certificates)
        {
            certificateReferences.Add(AddBinaryObject(certificate.RawData));
        }

        List<PdfObject> globalOcspReferences = [];
        foreach (byte[] ocsp in globalOcspResponses)
        {
            globalOcspReferences.Add(AddBinaryObject(ocsp));
        }

        List<PdfObject> globalCrlReferences = [];
        foreach (byte[] crl in globalCrls)
        {
            globalCrlReferences.Add(AddBinaryObject(crl));
        }

        List<PdfDictionaryEntry> dssEntries =
        [
            new PdfDictionaryEntry("Certs", new PdfArrayObject(certificateReferences)),
        ];
        if (globalOcspReferences.Count > 0)
        {
            dssEntries.Add(new PdfDictionaryEntry("OCSPs", new PdfArrayObject(globalOcspReferences)));
        }

        if (globalCrlReferences.Count > 0)
        {
            dssEntries.Add(new PdfDictionaryEntry("CRLs", new PdfArrayObject(globalCrlReferences)));
        }

        if (vriPayloads.Count > 0)
        {
            List<PdfDictionaryEntry> vriEntries = [];
            foreach ((string vriKey, DssVriPayload payload) in vriPayloads)
            {
                List<PdfDictionaryEntry> vriItemEntries = [];

                List<PdfObject> vriCertificateReferences = [];
                foreach (X509Certificate2 certificate in payload.Certificates)
                {
                    vriCertificateReferences.Add(AddBinaryObject(certificate.RawData));
                }

                if (vriCertificateReferences.Count > 0)
                {
                    vriItemEntries.Add(new PdfDictionaryEntry("Cert", new PdfArrayObject(vriCertificateReferences)));
                }

                List<PdfObject> vriOcspReferences = [];
                foreach (byte[] ocsp in payload.OcspResponses)
                {
                    vriOcspReferences.Add(AddBinaryObject(ocsp));
                }

                if (vriOcspReferences.Count > 0)
                {
                    vriItemEntries.Add(new PdfDictionaryEntry("OCSP", new PdfArrayObject(vriOcspReferences)));
                }

                List<PdfObject> vriCrlReferences = [];
                foreach (byte[] crl in payload.Crls)
                {
                    vriCrlReferences.Add(AddBinaryObject(crl));
                }

                if (vriCrlReferences.Count > 0)
                {
                    vriItemEntries.Add(new PdfDictionaryEntry("CRL", new PdfArrayObject(vriCrlReferences)));
                }

                vriEntries.Add(new PdfDictionaryEntry(vriKey, new PdfDictionaryObject(vriItemEntries)));
            }

            dssEntries.Add(new PdfDictionaryEntry("VRI", new PdfDictionaryObject(vriEntries)));
        }

        PdfObjectId dssObjectId = new(nextObjectNumber++, 0);
        objects.Add(new PdfIndirectObject(dssObjectId, new PdfDictionaryObject(dssEntries)));
        createdObjectIds.Add(dssObjectId);

        PdfDictionaryObject updatedCatalog = UpsertDictionaryEntry(
            catalogDictionary,
            new PdfDictionaryEntry("DSS", new PdfReferenceObject(dssObjectId)));
        objects[catalogIndex] = new PdfIndirectObject(rootReference.ObjectId, updatedCatalog);

        PdfFile updated = new(
            file.Version,
            objects,
            file.Trailer,
            file.SourceBytes,
            file.StartXrefOffset,
            file.XrefEntries);

        HashSet<PdfObjectId> dirtyObjectIds = [rootReference.ObjectId];
        foreach (PdfObjectId objectId in createdObjectIds)
        {
            dirtyObjectIds.Add(objectId);
        }

        return PdfFileWriter.WriteIncremental(updated, dirtyObjectIds, PdfCrossReferenceStyle.Classic);
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

    private static PdfDictionaryObject UpsertDictionaryEntry(PdfDictionaryObject dictionary, PdfDictionaryEntry entry)
    {
        List<PdfDictionaryEntry> entries = [];
        bool replaced = false;
        foreach (PdfDictionaryEntry existing in dictionary.Entries)
        {
            if (string.Equals(existing.Key, entry.Key, StringComparison.Ordinal))
            {
                entries.Add(entry);
                replaced = true;
            }
            else
            {
                entries.Add(existing);
            }
        }

        if (!replaced)
        {
            entries.Add(entry);
        }

        return new PdfDictionaryObject(entries);
    }

    private readonly record struct SignatureCmsPayload(int ObjectNumber, byte[] CmsBytes);

    private readonly record struct DssVriPayload(
        IReadOnlyList<byte[]> OcspResponses,
        IReadOnlyList<byte[]> Crls,
        IReadOnlyList<X509Certificate2> Certificates);

    private enum OcspTestCertStatus
    {
        Good = 0,
        Revoked = 1,
        Unknown = 2,
    }

    private enum OcspResponseStatus
    {
        Successful = 0,
    }
}
