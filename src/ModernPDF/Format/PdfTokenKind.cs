namespace ModernPDF.Format;

internal enum PdfTokenKind
{
    Integer = 0,
    Real = 1,
    Name = 2,
    String = 3,
    HexString = 4,
    BooleanTrue = 5,
    BooleanFalse = 6,
    Null = 7,
    Keyword = 8,
    StartArray = 9,
    EndArray = 10,
    StartDictionary = 11,
    EndDictionary = 12,
}
