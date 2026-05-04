using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests;

public sealed class PdfDocumentDssOcspRevocationValidationTests
{
    [Fact]
    public void ValidateDetachedSignaturesOfflineRevocationSucceedsWithEmbeddedDssOcspEvidence()
    {
        (X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf) = CreateSigningChainWithRevocationPointers();
        using (root)
        using (intermediate)
        using (leaf)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] signedBytes = CreateSignedPdf(leaf, now);
            byte[] leafOcsp = BuildOcspResponse(
                certificateToCheck: leaf,
                issuerCertificate: intermediate,
                producedAt: now.AddMinutes(-30),
                thisUpdate: now.AddHours(-1),
                nextUpdate: now.AddDays(1),
                status: OcspTestCertStatus.Good);
            byte[] intermediateOcsp = BuildOcspResponse(
                certificateToCheck: intermediate,
                issuerCertificate: root,
                producedAt: now.AddMinutes(-30),
                thisUpdate: now.AddHours(-1),
                nextUpdate: now.AddDays(1),
                status: OcspTestCertStatus.Good);
            byte[] withEvidence = AddDssEvidence(
                signedBytes,
                certificates: [intermediate],
                ocspResponses: [leafOcsp, intermediateOcsp],
                crls: []);

            PdfDetachedSignatureValidationResult result = Assert.Single(
                PdfDocument.Open(withEvidence).ValidateDetachedSignatures(CreateOfflineRevocationOptions(root, now)));

            Assert.True(result.IsValid);
            Assert.True(result.CryptographicallyValid);
            Assert.True(result.TrustChecksPassed);
            Assert.True(result.CertificateChainValid);
            Assert.True(result.RevocationValid);
        }
    }

    [Fact]
    public void ValidateDetachedSignaturesOfflineRevocationRejectsStaleEmbeddedDssOcspEvidence()
    {
        (X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf) = CreateSigningChainWithRevocationPointers();
        using (root)
        using (intermediate)
        using (leaf)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] signedBytes = CreateSignedPdf(leaf, now);
            byte[] staleLeafOcsp = BuildOcspResponse(
                certificateToCheck: leaf,
                issuerCertificate: intermediate,
                producedAt: now.AddDays(-4),
                thisUpdate: now.AddDays(-4),
                nextUpdate: now.AddDays(-2),
                status: OcspTestCertStatus.Good);
            byte[] staleIntermediateOcsp = BuildOcspResponse(
                certificateToCheck: intermediate,
                issuerCertificate: root,
                producedAt: now.AddDays(-4),
                thisUpdate: now.AddDays(-4),
                nextUpdate: now.AddDays(-2),
                status: OcspTestCertStatus.Good);
            byte[] withStaleEvidence = AddDssEvidence(
                signedBytes,
                certificates: [intermediate],
                ocspResponses: [staleLeafOcsp, staleIntermediateOcsp],
                crls: []);

            PdfDetachedSignatureValidationResult result = Assert.Single(
                PdfDocument.Open(withStaleEvidence).ValidateDetachedSignatures(CreateOfflineRevocationOptions(root, now)));

            Assert.False(result.IsValid);
            Assert.True(result.CryptographicallyValid);
            Assert.False(result.RevocationValid);
            Assert.Contains(result.Diagnostics, static item => item.Contains("No fresh, verifiable DSS OCSP response", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ValidateDetachedSignaturesOfflineRevocationFallsBackToCrlWhenOcspMissing()
    {
        (X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf) = CreateSigningChainWithRevocationPointers();
        using (root)
        using (intermediate)
        using (leaf)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] signedBytes = CreateSignedPdf(leaf, now);
            byte[] leafCrl = BuildCrl(intermediate, crlNumber: 1, thisUpdate: now.AddDays(-1), nextUpdate: now.AddDays(2));
            byte[] intermediateCrl = BuildCrl(root, crlNumber: 2, thisUpdate: now.AddDays(-1), nextUpdate: now.AddDays(2));
            byte[] withCrlEvidence = AddDssEvidence(
                signedBytes,
                certificates: [intermediate],
                ocspResponses: [],
                crls: [leafCrl, intermediateCrl]);

            PdfDetachedSignatureValidationResult result = Assert.Single(
                PdfDocument.Open(withCrlEvidence).ValidateDetachedSignatures(CreateOfflineRevocationOptions(root, now)));

            Assert.True(result.IsValid);
            Assert.True(result.CryptographicallyValid);
            Assert.True(result.TrustChecksPassed);
            Assert.True(result.RevocationValid);
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

    private static byte[] CreateSignedPdf(X509Certificate2 signingCertificate, DateTimeOffset signingTime)
    {
        PdfDocument document = PdfDocument.Create();
        document.AddTextPage("dss-ocsp-revocation");
        return document.SaveSignedDetached(
            payload => CreateDetachedCmsSignature(payload.Span, signingCertificate, signingTime),
            new PdfSignatureOptions
            {
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
            "CN=ModernPDF OCSP Root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        X509Certificate2 root = rootRequest.CreateSelfSigned(now.AddDays(-7), now.AddYears(5));

        using RSA intermediateKey = RSA.Create(2048);
        CertificateRequest intermediateRequest = new(
            "CN=ModernPDF OCSP Intermediate",
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
            "CN=ModernPDF OCSP Leaf",
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

    private static byte[] BuildCrl(
        X509Certificate2 issuerCertificate,
        long crlNumber,
        DateTimeOffset thisUpdate,
        DateTimeOffset nextUpdate)
    {
        CertificateRevocationListBuilder builder = new();
        return builder.Build(
            issuerCertificate,
            new BigInteger(crlNumber),
            nextUpdate,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1,
            thisUpdate: thisUpdate);
    }

    private static byte[] AddDssEvidence(
        byte[] sourceBytes,
        IReadOnlyList<X509Certificate2> certificates,
        IReadOnlyList<byte[]> ocspResponses,
        IReadOnlyList<byte[]> crls)
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
        List<PdfObject> certReferences = [];
        foreach (X509Certificate2 certificate in certificates)
        {
            PdfObjectId certObjectId = new(nextObjectNumber++, 0);
            certReferences.Add(new PdfReferenceObject(certObjectId));
            objects.Add(new PdfIndirectObject(certObjectId, new PdfStreamObject(new PdfDictionaryObject([]), certificate.RawData)));
        }

        List<PdfObject> ocspReferences = [];
        foreach (byte[] ocsp in ocspResponses)
        {
            PdfObjectId ocspObjectId = new(nextObjectNumber++, 0);
            ocspReferences.Add(new PdfReferenceObject(ocspObjectId));
            objects.Add(new PdfIndirectObject(ocspObjectId, new PdfStreamObject(new PdfDictionaryObject([]), ocsp)));
        }

        List<PdfObject> crlReferences = [];
        foreach (byte[] crl in crls)
        {
            PdfObjectId crlObjectId = new(nextObjectNumber++, 0);
            crlReferences.Add(new PdfReferenceObject(crlObjectId));
            objects.Add(new PdfIndirectObject(crlObjectId, new PdfStreamObject(new PdfDictionaryObject([]), crl)));
        }

        List<PdfDictionaryEntry> dssEntries =
        [
            new PdfDictionaryEntry("Certs", new PdfArrayObject(certReferences)),
        ];
        if (ocspReferences.Count > 0)
        {
            dssEntries.Add(new PdfDictionaryEntry("OCSPs", new PdfArrayObject(ocspReferences)));
        }

        if (crlReferences.Count > 0)
        {
            dssEntries.Add(new PdfDictionaryEntry("CRLs", new PdfArrayObject(crlReferences)));
        }

        PdfObjectId dssObjectId = new(nextObjectNumber++, 0);
        objects.Add(new PdfIndirectObject(dssObjectId, new PdfDictionaryObject(dssEntries)));

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

        HashSet<PdfObjectId> dirtyObjectIds = [rootReference.ObjectId, dssObjectId];
        foreach (PdfObject certReference in certReferences)
        {
            if (certReference is PdfReferenceObject reference)
            {
                dirtyObjectIds.Add(reference.ObjectId);
            }
        }

        foreach (PdfObject ocspReference in ocspReferences)
        {
            if (ocspReference is PdfReferenceObject reference)
            {
                dirtyObjectIds.Add(reference.ObjectId);
            }
        }

        foreach (PdfObject crlReference in crlReferences)
        {
            if (crlReference is PdfReferenceObject reference)
            {
                dirtyObjectIds.Add(reference.ObjectId);
            }
        }

        return PdfFileWriter.WriteIncremental(updated, dirtyObjectIds, PdfCrossReferenceStyle.Classic);
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
