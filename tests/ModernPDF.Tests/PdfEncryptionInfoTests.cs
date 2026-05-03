namespace ModernPDF.Tests;

public sealed class PdfEncryptionInfoTests
{
    [Fact]
    public void ConstructorStoresProvidedValues()
    {
        PdfEncryptionInfo info = new(
            filter: "Standard",
            subFilter: "adbe.pkcs7.s4",
            algorithmVersion: 4,
            keyLengthBits: 128);

        Assert.Equal("Standard", info.Filter);
        Assert.Equal("adbe.pkcs7.s4", info.SubFilter);
        Assert.Equal(4, info.AlgorithmVersion);
        Assert.Equal(128, info.KeyLengthBits);
    }
}
