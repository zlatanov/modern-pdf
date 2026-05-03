namespace ModernPDF.Format;

internal enum PdfTokenKind
{
    Integer = 0,
    Real = 1,
    Name = 2,
    String = 3,
    BooleanTrue = 4,
    BooleanFalse = 5,
    Null = 6,
    Keyword = 7,
    StartArray = 8,
    EndArray = 9,
    StartDictionary = 10,
    EndDictionary = 11,
}
