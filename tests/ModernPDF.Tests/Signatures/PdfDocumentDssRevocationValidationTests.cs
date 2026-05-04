using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Tests;

public sealed class PdfDocumentDssRevocationValidationTests
{
    [Fact]
    public void ValidateDetachedSignaturesOfflineRevocationFailsWithoutEmbeddedEvidence()
    {
        (X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf) = CreateSigningChainWithCrlPointers();
        using (root)
        using (intermediate)
        using (leaf)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] signedBytes = CreateSignedPdf(leaf, now);
            PdfDetachedSignatureValidationResult result = Assert.Single(
                PdfDocument.Open(signedBytes).ValidateDetachedSignatures(CreateOfflineRevocationOptions(root, now)));

            Assert.False(result.IsValid);
            Assert.True(result.CryptographicallyValid);
            Assert.False(result.RevocationValid);
        }
    }

    [Fact]
    public void ValidateDetachedSignaturesOfflineRevocationSucceedsWithEmbeddedDssCrlEvidence()
    {
        (X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf) = CreateSigningChainWithCrlPointers();
        using (root)
        using (intermediate)
        using (leaf)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] signedBytes = CreateSignedPdf(leaf, now);
            byte[] leafCrl = BuildCrl(intermediate, crlNumber: 1, thisUpdate: now.AddDays(-1), nextUpdate: now.AddDays(2));
            byte[] intermediateCrl = BuildCrl(root, crlNumber: 2, thisUpdate: now.AddDays(-1), nextUpdate: now.AddDays(2));
            byte[] withDssEvidence = AddDssEvidence(signedBytes, [intermediate], [leafCrl, intermediateCrl]);

            PdfDetachedSignatureValidationResult result = Assert.Single(
                PdfDocument.Open(withDssEvidence).ValidateDetachedSignatures(CreateOfflineRevocationOptions(root, now)));

            Assert.True(result.IsValid);
            Assert.True(result.CryptographicallyValid);
            Assert.True(result.TrustChecksPassed);
            Assert.True(result.CertificateChainValid);
            Assert.True(result.RevocationValid);
        }
    }

    [Fact]
    public void ValidateDetachedSignaturesOfflineRevocationRejectsStaleEmbeddedDssCrlEvidence()
    {
        (X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf) = CreateSigningChainWithCrlPointers();
        using (root)
        using (intermediate)
        using (leaf)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] signedBytes = CreateSignedPdf(leaf, now);
            byte[] staleLeafCrl = BuildCrl(intermediate, crlNumber: 1, thisUpdate: now.AddDays(-10), nextUpdate: now.AddDays(-3));
            byte[] staleIntermediateCrl = BuildCrl(root, crlNumber: 2, thisUpdate: now.AddDays(-10), nextUpdate: now.AddDays(-3));
            byte[] withStaleEvidence = AddDssEvidence(signedBytes, [intermediate], [staleLeafCrl, staleIntermediateCrl]);

            PdfDetachedSignatureValidationResult result = Assert.Single(
                PdfDocument.Open(withStaleEvidence).ValidateDetachedSignatures(CreateOfflineRevocationOptions(root, now)));

            Assert.False(result.IsValid);
            Assert.True(result.CryptographicallyValid);
            Assert.False(result.RevocationValid);
            Assert.Contains(result.Diagnostics, static message => message.Contains("No fresh, verifiable DSS CRL", StringComparison.Ordinal));
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
        document.AddTextPage("dss-revocation");
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

    private static (X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Leaf) CreateSigningChainWithCrlPointers()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        X509Extension crlDistributionPoint = CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["http://modernpdf.test/ca.crl"], critical: false);

        using RSA rootKey = RSA.Create(2048);
        CertificateRequest rootRequest = new(
            "CN=ModernPDF LTV Root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        X509Certificate2 root = rootRequest.CreateSelfSigned(now.AddDays(-7), now.AddYears(5));

        using RSA intermediateKey = RSA.Create(2048);
        CertificateRequest intermediateRequest = new(
            "CN=ModernPDF LTV Intermediate",
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
            "CN=ModernPDF LTV Leaf",
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
            objects.Add(new PdfIndirectObject(
                certObjectId,
                new PdfStreamObject(
                    new PdfDictionaryObject([]),
                    certificate.RawData)));
        }

        List<PdfObject> crlReferences = [];
        foreach (byte[] crl in crls)
        {
            PdfObjectId crlObjectId = new(nextObjectNumber++, 0);
            crlReferences.Add(new PdfReferenceObject(crlObjectId));
            objects.Add(new PdfIndirectObject(
                crlObjectId,
                new PdfStreamObject(
                    new PdfDictionaryObject([]),
                    crl)));
        }

        PdfObjectId dssObjectId = new(nextObjectNumber++, 0);
        PdfDictionaryObject dssDictionary = new(
        [
            new PdfDictionaryEntry("Certs", new PdfArrayObject(certReferences)),
            new PdfDictionaryEntry("CRLs", new PdfArrayObject(crlReferences)),
        ]);
        objects.Add(new PdfIndirectObject(dssObjectId, dssDictionary));

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

        HashSet<PdfObjectId> dirtyObjectIds =
        [
            rootReference.ObjectId,
            dssObjectId,
        ];
        foreach (PdfObject certReference in certReferences)
        {
            if (certReference is PdfReferenceObject reference)
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
}
