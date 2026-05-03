using System.Globalization;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Format;

internal static class PdfObjectParser
{
    public static PdfObject Parse(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(data);
        if (tokens.Count == 0)
        {
            throw new PdfFormatException("No tokens were found in the input.");
        }

        int index = 0;
        PdfObject parsed = ParseValue(tokens, ref index);

        if (index != tokens.Count)
        {
            throw new PdfFormatException("Unexpected trailing tokens after parsing object.");
        }

        return parsed;
    }

    public static PdfObject ParseAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(System.Text.Encoding.ASCII.GetBytes(text));
    }

    private static PdfObject ParseValue(IReadOnlyList<PdfToken> tokens, ref int index)
    {
        if (TryParseReference(tokens, ref index, out PdfReferenceObject? reference))
        {
            return reference ?? throw new PdfFormatException("Reference token parsing failed.");
        }

        PdfToken token = ReadToken(tokens, ref index);

        return token.Kind switch
        {
            PdfTokenKind.Null => PdfNullObject.Instance,
            PdfTokenKind.BooleanTrue => new PdfBooleanObject(true),
            PdfTokenKind.BooleanFalse => new PdfBooleanObject(false),
            PdfTokenKind.Integer => ParseInteger(token),
            PdfTokenKind.Real => ParseReal(token),
            PdfTokenKind.Name => new PdfNameObject(token.Lexeme),
            PdfTokenKind.String => new PdfStringObject(token.Lexeme),
            PdfTokenKind.StartArray => ParseArray(tokens, ref index),
            PdfTokenKind.StartDictionary => ParseDictionary(tokens, ref index),
            PdfTokenKind.Keyword => throw new PdfFormatException($"Unsupported keyword token '{token.Lexeme}'."),
            _ => throw new PdfFormatException($"Unexpected token '{token.Kind}'."),
        };
    }

    private static PdfToken ReadToken(IReadOnlyList<PdfToken> tokens, ref int index)
    {
        if (index >= tokens.Count)
        {
            throw new PdfFormatException("Unexpected end of tokens.");
        }

        PdfToken token = tokens[index];
        index++;
        return token;
    }

    private static PdfNumberObject ParseInteger(PdfToken token)
    {
        if (!double.TryParse(token.Lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out double value))
        {
            throw new PdfFormatException($"Invalid integer token '{token.Lexeme}'.");
        }

        return new PdfNumberObject(value, isInteger: true);
    }

    private static PdfNumberObject ParseReal(PdfToken token)
    {
        if (!double.TryParse(token.Lexeme, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new PdfFormatException($"Invalid real token '{token.Lexeme}'.");
        }

        return new PdfNumberObject(value, isInteger: false);
    }

    private static PdfArrayObject ParseArray(IReadOnlyList<PdfToken> tokens, ref int index)
    {
        List<PdfObject> items = [];

        while (index < tokens.Count && tokens[index].Kind != PdfTokenKind.EndArray)
        {
            items.Add(ParseValue(tokens, ref index));
        }

        if (index >= tokens.Count || tokens[index].Kind != PdfTokenKind.EndArray)
        {
            throw new PdfFormatException("Array was not terminated with ']'.");
        }

        index++;
        return new PdfArrayObject(items);
    }

    private static PdfDictionaryObject ParseDictionary(IReadOnlyList<PdfToken> tokens, ref int index)
    {
        List<PdfDictionaryEntry> entries = [];

        while (index < tokens.Count && tokens[index].Kind != PdfTokenKind.EndDictionary)
        {
            PdfToken keyToken = ReadToken(tokens, ref index);
            if (keyToken.Kind != PdfTokenKind.Name)
            {
                throw new PdfFormatException("Dictionary keys must be name tokens.");
            }

            PdfObject value = ParseValue(tokens, ref index);
            entries.Add(new PdfDictionaryEntry(keyToken.Lexeme, value));
        }

        if (index >= tokens.Count || tokens[index].Kind != PdfTokenKind.EndDictionary)
        {
            throw new PdfFormatException("Dictionary was not terminated with '>>'.");
        }

        index++;
        return new PdfDictionaryObject(entries);
    }

    private static bool TryParseReference(IReadOnlyList<PdfToken> tokens, ref int index, out PdfReferenceObject? reference)
    {
        reference = null;

        if (index + 2 >= tokens.Count)
        {
            return false;
        }

        PdfToken first = tokens[index];
        PdfToken second = tokens[index + 1];
        PdfToken third = tokens[index + 2];

        if (first.Kind != PdfTokenKind.Integer
            || second.Kind != PdfTokenKind.Integer
            || third.Kind != PdfTokenKind.Keyword
            || !string.Equals(third.Lexeme, "R", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(first.Lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out int objectNumber))
        {
            throw new PdfFormatException($"Invalid object number in reference: '{first.Lexeme}'.");
        }

        if (!int.TryParse(second.Lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out int generationNumber))
        {
            throw new PdfFormatException($"Invalid generation number in reference: '{second.Lexeme}'.");
        }

        reference = new PdfReferenceObject(new PdfObjectId(objectNumber, generationNumber));
        index += 3;
        return true;
    }
}
