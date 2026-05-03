using System.Text;

namespace ModernPDF.Format;

internal static class PdfTokenizer
{
    public static IReadOnlyList<PdfToken> Tokenize(ReadOnlySpan<byte> data)
    {
        List<PdfToken> tokens = [];
        int position = 0;

        while (position < data.Length)
        {
            byte current = data[position];

            if (IsWhiteSpace(current))
            {
                position++;
                continue;
            }

            if (current == (byte)'%')
            {
                SkipComment(data, ref position);
                continue;
            }

            switch (current)
            {
                case (byte)'[':
                    tokens.Add(new PdfToken(PdfTokenKind.StartArray));
                    position++;
                    continue;
                case (byte)']':
                    tokens.Add(new PdfToken(PdfTokenKind.EndArray));
                    position++;
                    continue;
                case (byte)'<':
                    if (TryReadDoubleCharacterToken(data, ref position, (byte)'<', (byte)'<'))
                    {
                        tokens.Add(new PdfToken(PdfTokenKind.StartDictionary));
                        continue;
                    }

                    throw new PdfFormatException("Hex strings are not supported in the current tokenizer slice.");
                case (byte)'>':
                    if (TryReadDoubleCharacterToken(data, ref position, (byte)'>', (byte)'>'))
                    {
                        tokens.Add(new PdfToken(PdfTokenKind.EndDictionary));
                        continue;
                    }

                    throw new PdfFormatException("Unexpected '>' token.");
                case (byte)'/':
                    tokens.Add(ReadNameToken(data, ref position));
                    continue;
                case (byte)'(':
                    tokens.Add(ReadStringToken(data, ref position));
                    continue;
                default:
                    if (IsNumberStart(current))
                    {
                        tokens.Add(ReadNumberToken(data, ref position));
                        continue;
                    }

                    tokens.Add(ReadKeywordOrLiteralToken(data, ref position));
                    continue;
            }
        }

        return tokens;
    }

    private static bool IsWhiteSpace(byte value)
    {
        return value is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
    }

    private static bool IsDelimiter(byte value)
    {
        return value is (byte)'(' or (byte)')'
            or (byte)'<' or (byte)'>'
            or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}'
            or (byte)'/' or (byte)'%';
    }

    private static bool IsNumberStart(byte value)
    {
        return value is (byte)'-' or (byte)'+' or (byte)'.' || char.IsAsciiDigit((char)value);
    }

    private static void SkipComment(ReadOnlySpan<byte> data, ref int position)
    {
        while (position < data.Length)
        {
            byte value = data[position];
            position++;

            if (value is (byte)'\r' or (byte)'\n')
            {
                break;
            }
        }
    }

    private static bool TryReadDoubleCharacterToken(ReadOnlySpan<byte> data, ref int position, byte first, byte second)
    {
        if (position + 1 >= data.Length)
        {
            return false;
        }

        if (data[position] == first && data[position + 1] == second)
        {
            position += 2;
            return true;
        }

        return false;
    }

    private static PdfToken ReadNameToken(ReadOnlySpan<byte> data, ref int position)
    {
        position++;
        int start = position;

        while (position < data.Length && !IsWhiteSpace(data[position]) && !IsDelimiter(data[position]))
        {
            position++;
        }

        string name = Encoding.ASCII.GetString(data.Slice(start, position - start));
        return new PdfToken(PdfTokenKind.Name, name);
    }

    private static PdfToken ReadStringToken(ReadOnlySpan<byte> data, ref int position)
    {
        position++;
        StringBuilder builder = new();
        int depth = 1;

        while (position < data.Length)
        {
            char current = (char)data[position];
            position++;

            if (current == '\\')
            {
                if (position >= data.Length)
                {
                    throw new PdfFormatException("Unterminated escape sequence in literal string.");
                }

                char escaped = (char)data[position];
                position++;
                builder.Append(DecodeEscapedCharacter(escaped));
                continue;
            }

            if (current == '(')
            {
                depth++;
                builder.Append(current);
                continue;
            }

            if (current == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return new PdfToken(PdfTokenKind.String, builder.ToString());
                }

                builder.Append(current);
                continue;
            }

            builder.Append(current);
        }

        throw new PdfFormatException("Unterminated literal string.");
    }

    private static char DecodeEscapedCharacter(char value)
    {
        return value switch
        {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'b' => '\b',
            'f' => '\f',
            '(' => '(',
            ')' => ')',
            '\\' => '\\',
            _ => value,
        };
    }

    private static PdfToken ReadNumberToken(ReadOnlySpan<byte> data, ref int position)
    {
        int start = position;
        bool hasDigit = false;
        bool hasDecimalPoint = false;

        if (data[position] is (byte)'+' or (byte)'-')
        {
            position++;
        }

        while (position < data.Length)
        {
            char current = (char)data[position];

            if (char.IsAsciiDigit(current))
            {
                hasDigit = true;
                position++;
                continue;
            }

            if (current == '.' && !hasDecimalPoint)
            {
                hasDecimalPoint = true;
                position++;
                continue;
            }

            break;
        }

        if (!hasDigit)
        {
            throw new PdfFormatException("Invalid numeric token.");
        }

        string lexeme = Encoding.ASCII.GetString(data.Slice(start, position - start));
        return hasDecimalPoint
            ? new PdfToken(PdfTokenKind.Real, lexeme)
            : new PdfToken(PdfTokenKind.Integer, lexeme);
    }

    private static PdfToken ReadKeywordOrLiteralToken(ReadOnlySpan<byte> data, ref int position)
    {
        int start = position;

        while (position < data.Length && !IsWhiteSpace(data[position]) && !IsDelimiter(data[position]))
        {
            position++;
        }

        if (position == start)
        {
            throw new PdfFormatException("Could not tokenize input.");
        }

        string lexeme = Encoding.ASCII.GetString(data.Slice(start, position - start));

        return lexeme switch
        {
            "true" => new PdfToken(PdfTokenKind.BooleanTrue),
            "false" => new PdfToken(PdfTokenKind.BooleanFalse),
            "null" => new PdfToken(PdfTokenKind.Null),
            _ => new PdfToken(PdfTokenKind.Keyword, lexeme),
        };
    }
}
