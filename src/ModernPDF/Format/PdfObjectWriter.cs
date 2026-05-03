using System.Globalization;
using System.Text;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Format;

internal static class PdfObjectWriter
{
    public static byte[] Write(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        ByteBufferWriter writer = new();
        WriteObject(writer, value);
        return writer.ToArray();
    }

    private static void WriteObject(ByteBufferWriter writer, PdfObject value)
    {
        switch (value)
        {
            case PdfNullObject:
                WriteAscii(writer, "null");
                return;
            case PdfBooleanObject booleanValue:
                WriteAscii(writer, booleanValue.Value ? "true" : "false");
                return;
            case PdfNumberObject numberValue:
                WriteNumber(writer, numberValue);
                return;
            case PdfNameObject nameValue:
                WriteName(writer, nameValue.Value);
                return;
            case PdfStringObject stringValue:
                WriteLiteralString(writer, stringValue.Value);
                return;
            case PdfByteStringObject byteStringValue:
                WriteHexString(writer, byteStringValue.Bytes.Span);
                return;
            case PdfReferenceObject referenceValue:
                WriteAscii(writer, $"{referenceValue.ObjectId.ObjectNumber} {referenceValue.ObjectId.GenerationNumber} R");
                return;
            case PdfArrayObject arrayValue:
                WriteArray(writer, arrayValue);
                return;
            case PdfDictionaryObject dictionaryValue:
                WriteDictionary(writer, dictionaryValue);
                return;
            default:
                throw new PdfFormatException($"Unsupported object type '{value.GetType().Name}'.");
        }
    }

    private static void WriteNumber(ByteBufferWriter writer, PdfNumberObject numberValue)
    {
        string text = numberValue.IsInteger
            ? Convert.ToInt64(numberValue.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : numberValue.Value.ToString("0.################", CultureInfo.InvariantCulture);

        WriteAscii(writer, text);
    }

    private static void WriteName(ByteBufferWriter writer, string name)
    {
        writer.WriteByte((byte)'/');

        foreach (char character in name)
        {
            if (RequiresNameEscape(character))
            {
                WriteAscii(writer, $"#{(int)character:X2}");
                continue;
            }

            if (!char.IsAscii(character))
            {
                throw new PdfFormatException("Non-ASCII name characters are not supported in the current writer slice.");
            }

            writer.WriteByte((byte)character);
        }
    }

    private static bool RequiresNameEscape(char character)
    {
        return char.IsWhiteSpace(character)
            || character is '#' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';
    }

    private static void WriteLiteralString(ByteBufferWriter writer, string value)
    {
        writer.WriteByte((byte)'(');

        foreach (char character in value)
        {
            switch (character)
            {
                case '\\':
                    WriteAscii(writer, "\\\\");
                    break;
                case '(':
                    WriteAscii(writer, "\\(");
                    break;
                case ')':
                    WriteAscii(writer, "\\)");
                    break;
                case '\n':
                    WriteAscii(writer, "\\n");
                    break;
                case '\r':
                    WriteAscii(writer, "\\r");
                    break;
                case '\t':
                    WriteAscii(writer, "\\t");
                    break;
                default:
                    if (!char.IsAscii(character))
                    {
                        throw new PdfFormatException("Non-ASCII literal strings are not supported in the current writer slice.");
                    }

                    writer.WriteByte((byte)character);
                    break;
            }
        }

        writer.WriteByte((byte)')');
    }

    private static void WriteHexString(ByteBufferWriter writer, ReadOnlySpan<byte> bytes)
    {
        writer.WriteByte((byte)'<');
        WriteAscii(writer, Convert.ToHexString(bytes));
        writer.WriteByte((byte)'>');
    }

    private static void WriteArray(ByteBufferWriter writer, PdfArrayObject value)
    {
        writer.WriteByte((byte)'[');

        for (int index = 0; index < value.Items.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteByte((byte)' ');
            }

            WriteObject(writer, value.Items[index]);
        }

        writer.WriteByte((byte)']');
    }

    private static void WriteDictionary(ByteBufferWriter writer, PdfDictionaryObject value)
    {
        WriteAscii(writer, "<<");

        for (int index = 0; index < value.Entries.Count; index++)
        {
            PdfDictionaryEntry entry = value.Entries[index];
            writer.WriteByte((byte)' ');
            WriteName(writer, entry.Key);
            writer.WriteByte((byte)' ');
            WriteObject(writer, entry.Value);
        }

        if (value.Entries.Count > 0)
        {
            writer.WriteByte((byte)' ');
        }

        WriteAscii(writer, ">>");
    }

    private static void WriteAscii(ByteBufferWriter writer, string text)
    {
        writer.Write(Encoding.ASCII.GetBytes(text));
    }
}
