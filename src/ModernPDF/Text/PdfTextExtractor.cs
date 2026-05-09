using System.Text;
using ModernPDF.DocumentModel;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using System.Globalization;

namespace ModernPDF.Text;

/// <summary>
/// Extracts textual content from page content streams using a tokenizer-based operator scan.
/// </summary>
internal static class PdfTextExtractor
{
    /// <summary>
    /// Extracts text from every page in reading order.
    /// </summary>
    public static string ExtractAll(PdfFile file, PdfDocumentModel model)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(model);

        Dictionary<PdfObjectId, PdfIndirectObject> objectMap = BuildObjectMap(file.Objects);
        StringBuilder builder = new();

        for (int pageIndex = 0; pageIndex < model.Pages.Count; pageIndex++)
        {
            string pageText = ExtractPageCore(model.Pages[pageIndex], objectMap);
            if (pageText.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(pageText);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Extracts text from a specific page.
    /// </summary>
    public static string ExtractPage(PdfFile file, PdfDocumentModel model, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, model.Pages.Count);

        Dictionary<PdfObjectId, PdfIndirectObject> objectMap = BuildObjectMap(file.Objects);
        return ExtractPageCore(model.Pages[pageIndex], objectMap);
    }

    private static string ExtractPageCore(PdfPageModel page, IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap)
    {
        StringBuilder builder = new();
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont =
            BuildToUnicodeMaps(page.Resources, objectMap);

        foreach (PdfStreamObject streamObject in ResolveContentStreams(page.Contents, objectMap))
        {
            builder.Append(ExtractTextFromContentStream(streamObject.Data.Span, toUnicodeByFont));
        }

        return builder.ToString();
    }

    private static IEnumerable<PdfStreamObject> ResolveContentStreams(
        PdfObject? contents,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap)
    {
        if (contents is null)
        {
            yield break;
        }

        if (contents is PdfReferenceObject reference)
        {
            yield return ResolveReferencedStream(reference, objectMap);
            yield break;
        }

        if (contents is PdfArrayObject array)
        {
            foreach (PdfObject entry in array.Items)
            {
                if (entry is not PdfReferenceObject itemReference)
                {
                    throw new PdfFormatException("Page /Contents array must contain only references.");
                }

                yield return ResolveReferencedStream(itemReference, objectMap);
            }

            yield break;
        }

        throw new PdfFormatException("Page /Contents must be a reference or an array of references.");
    }

    private static PdfStreamObject ResolveReferencedStream(
        PdfReferenceObject reference,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap)
    {
        if (!objectMap.TryGetValue(reference.ObjectId, out PdfIndirectObject? indirectObject))
        {
            throw new PdfFormatException($"Missing /Contents stream object {reference.ObjectId}.");
        }

        return indirectObject.Value as PdfStreamObject
            ?? throw new PdfFormatException($"Referenced /Contents object {reference.ObjectId} is not a stream.");
    }

    private static Dictionary<PdfObjectId, PdfIndirectObject> BuildObjectMap(IEnumerable<PdfIndirectObject> objects)
    {
        Dictionary<PdfObjectId, PdfIndirectObject> map = [];

        foreach (PdfIndirectObject item in objects)
        {
            map[item.ObjectId] = item;
        }

        return map;
    }

    private static string ExtractTextFromContentStream(
        ReadOnlySpan<byte> content,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> toUnicodeByFont)
    {
        // This is intentionally operator-driven (Tf/Tj/TJ) rather than a full graphics-state interpreter.
        // It keeps extraction predictable for common text streams while remaining resilient to malformed content.
        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(content);
        StringBuilder builder = new();
        IReadOnlyDictionary<int, string>? activeToUnicode = null;
        int index = 0;

        while (index < tokens.Count)
        {
            PdfToken token = tokens[index];

            if (token.Kind == PdfTokenKind.Name
                && index + 2 < tokens.Count
                && IsNumberToken(tokens[index + 1])
                && tokens[index + 2].Kind == PdfTokenKind.Keyword
                && string.Equals(tokens[index + 2].Lexeme, "Tf", StringComparison.Ordinal))
            {
                activeToUnicode = toUnicodeByFont.TryGetValue(token.Lexeme, out IReadOnlyDictionary<int, string>? map)
                    ? map
                    : null;
                index += 3;
                continue;
            }

            if (IsTextStringToken(token)
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == PdfTokenKind.Keyword
                && IsSingleStringTextOperator(tokens[index + 1].Lexeme))
            {
                builder.Append(DecodeTextToken(token, activeToUnicode));
                index += 2;
                continue;
            }

            if (token.Kind == PdfTokenKind.StartArray)
            {
                List<string> strings = [];
                int depth = 1;
                index++;

                while (index < tokens.Count && depth > 0)
                {
                    PdfToken itemToken = tokens[index];

                    if (itemToken.Kind == PdfTokenKind.StartArray)
                    {
                        depth++;
                    }
                    else if (itemToken.Kind == PdfTokenKind.EndArray)
                    {
                        depth--;
                    }
                    else if (depth == 1 && IsTextStringToken(itemToken))
                    {
                        strings.Add(DecodeTextToken(itemToken, activeToUnicode));
                    }

                    index++;
                }

                if (depth != 0)
                {
                    throw new PdfFormatException("Unterminated array in content stream.");
                }

                if (index < tokens.Count
                    && tokens[index].Kind == PdfTokenKind.Keyword
                    && string.Equals(tokens[index].Lexeme, "TJ", StringComparison.Ordinal))
                {
                    foreach (string value in strings)
                    {
                        builder.Append(value);
                    }

                    index++;
                }

                continue;
            }

            index++;
        }

        return builder.ToString();
    }

    private static bool IsSingleStringTextOperator(string lexeme)
    {
        return string.Equals(lexeme, "Tj", StringComparison.Ordinal)
            || string.Equals(lexeme, "'", StringComparison.Ordinal)
            || string.Equals(lexeme, "\"", StringComparison.Ordinal);
    }

    private static bool IsTextStringToken(PdfToken token)
    {
        return token.Kind is PdfTokenKind.String or PdfTokenKind.HexString;
    }

    private static bool IsNumberToken(PdfToken token)
    {
        return token.Kind is PdfTokenKind.Integer or PdfTokenKind.Real;
    }

    private static string DecodeTextToken(PdfToken token, IReadOnlyDictionary<int, string>? cidToUnicode)
    {
        if (token.Kind == PdfTokenKind.String)
        {
            return token.Lexeme;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(token.Lexeme);
        }
        catch (FormatException)
        {
            return string.Empty;
        }

        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (cidToUnicode is not null && (bytes.Length & 1) == 0)
        {
            StringBuilder decoded = new();
            for (int offset = 0; offset < bytes.Length; offset += 2)
            {
                int cid = (bytes[offset] << 8) | bytes[offset + 1];
                if (cidToUnicode.TryGetValue(cid, out string? mapped))
                {
                    decoded.Append(mapped);
                }
                else
                {
                    decoded.Append((char)cid);
                }
            }

            return decoded.ToString();
        }

        if ((bytes.Length & 1) == 0)
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static Dictionary<string, IReadOnlyDictionary<int, string>> BuildToUnicodeMaps(
        PdfObject? resourcesObject,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap)
    {
        if (!TryResolveDictionary(resourcesObject, objectMap, out PdfDictionaryObject? resolvedResources)
            || resolvedResources is null)
        {
            return new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.Ordinal);
        }

        if (!TryGetDictionaryEntry(resolvedResources, "Font", out PdfObject? fontObject)
            || !TryResolveDictionary(fontObject, objectMap, out PdfDictionaryObject? resolvedFonts)
            || resolvedFonts is null)
        {
            return new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.Ordinal);
        }

        Dictionary<string, IReadOnlyDictionary<int, string>> maps = new(StringComparer.Ordinal);
        foreach (PdfDictionaryEntry fontEntry in resolvedFonts.Entries)
        {
            if (!TryResolveDictionary(fontEntry.Value, objectMap, out PdfDictionaryObject? resolvedFontDictionary)
                || resolvedFontDictionary is null)
            {
                continue;
            }

            if (!TryGetDictionaryEntry(resolvedFontDictionary, "ToUnicode", out PdfObject? toUnicodeObject)
                || !TryResolveStream(toUnicodeObject, objectMap, out PdfStreamObject? resolvedToUnicodeStream)
                || resolvedToUnicodeStream is null)
            {
                continue;
            }

            maps[fontEntry.Key] = ParseToUnicodeMap(resolvedToUnicodeStream.Data.Span);
        }

        return maps;
    }

    private static Dictionary<int, string> ParseToUnicodeMap(ReadOnlySpan<byte> bytes)
    {
        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(bytes);
        Dictionary<int, string> map = [];
        bool inBfChar = false;

        for (int index = 0; index < tokens.Count; index++)
        {
            PdfToken token = tokens[index];
            if (token.Kind == PdfTokenKind.Keyword && string.Equals(token.Lexeme, "beginbfchar", StringComparison.Ordinal))
            {
                inBfChar = true;
                continue;
            }

            if (token.Kind == PdfTokenKind.Keyword && string.Equals(token.Lexeme, "endbfchar", StringComparison.Ordinal))
            {
                inBfChar = false;
                continue;
            }

            if (!inBfChar
                || token.Kind != PdfTokenKind.HexString
                || index + 1 >= tokens.Count
                || tokens[index + 1].Kind != PdfTokenKind.HexString)
            {
                continue;
            }

            if (!int.TryParse(token.Lexeme, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cid))
            {
                continue;
            }

            map[cid] = DecodeToUnicodeHexString(tokens[index + 1].Lexeme);
            index++;
        }

        return map;
    }

    private static string DecodeToUnicodeHexString(string hex)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return string.Empty;
        }

        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        return (bytes.Length & 1) == 0
            ? Encoding.BigEndianUnicode.GetString(bytes)
            : Encoding.ASCII.GetString(bytes);
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

    private static bool TryResolveDictionary(
        PdfObject? source,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        out PdfDictionaryObject? dictionary)
    {
        dictionary = null;
        if (source is null)
        {
            return false;
        }

        if (!TryResolveObject(source, objectMap, out PdfObject? resolved))
        {
            return false;
        }

        dictionary = resolved as PdfDictionaryObject;
        return dictionary is not null;
    }

    private static bool TryResolveStream(
        PdfObject? source,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        out PdfStreamObject? stream)
    {
        stream = null;
        if (source is null)
        {
            return false;
        }

        if (!TryResolveObject(source, objectMap, out PdfObject? resolved))
        {
            return false;
        }

        stream = resolved as PdfStreamObject;
        return stream is not null;
    }

    private static bool TryResolveObject(
        PdfObject source,
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        out PdfObject? resolved)
    {
        if (source is PdfReferenceObject reference)
        {
            if (!objectMap.TryGetValue(reference.ObjectId, out PdfIndirectObject? indirect))
            {
                resolved = null;
                return false;
            }

            resolved = indirect.Value;
            return true;
        }

        resolved = source;
        return true;
    }
}
