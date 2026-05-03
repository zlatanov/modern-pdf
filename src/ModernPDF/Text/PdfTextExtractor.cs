using System.Text;
using ModernPDF.DocumentModel;
using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;

namespace ModernPDF.Text;

internal static class PdfTextExtractor
{
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

        foreach (PdfStreamObject streamObject in ResolveContentStreams(page.Contents, objectMap))
        {
            builder.Append(ExtractTextFromContentStream(streamObject.Data.Span));
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

    private static string ExtractTextFromContentStream(ReadOnlySpan<byte> content)
    {
        IReadOnlyList<PdfToken> tokens = PdfTokenizer.Tokenize(content);
        StringBuilder builder = new();
        int index = 0;

        while (index < tokens.Count)
        {
            PdfToken token = tokens[index];

            if (IsTextStringToken(token)
                && index + 1 < tokens.Count
                && tokens[index + 1].Kind == PdfTokenKind.Keyword
                && IsSingleStringTextOperator(tokens[index + 1].Lexeme))
            {
                builder.Append(DecodeTextToken(token));
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
                        strings.Add(DecodeTextToken(itemToken));
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

    private static string DecodeTextToken(PdfToken token)
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

        if ((bytes.Length & 1) == 0)
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        return Encoding.ASCII.GetString(bytes);
    }
}
