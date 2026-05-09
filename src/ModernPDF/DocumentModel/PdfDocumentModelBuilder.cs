using ModernPDF.Format;
using ModernPDF.Format.Files;
using ModernPDF.Format.Objects;
using ModernPDF.Primitives;
using System.Diagnostics.CodeAnalysis;

namespace ModernPDF.DocumentModel;

/// <summary>
/// Builds a navigable document model from low-level PDF objects.
/// </summary>
internal static class PdfDocumentModelBuilder
{
    /// <summary>
    /// Resolves catalog/page-tree objects and flattens effective page state into <see cref="PdfDocumentModel"/>.
    /// </summary>
    public static PdfDocumentModel Build(PdfFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        Dictionary<PdfObjectId, PdfIndirectObject> objectMap = BuildObjectMap(file.Objects);
        PdfReferenceObject rootReference = RequireTrailerReference(file.Trailer, "Root");
        PdfDictionaryObject catalog = RequireDictionary(objectMap, rootReference.ObjectId, "catalog");
        PdfReferenceObject pagesReference = RequireDictionaryReference(catalog, "Pages");
        PdfObjectId? metadataObjectId = TryGetOptionalReference(catalog, "Metadata")?.ObjectId;
        PdfObjectId? infoObjectId = TryGetOptionalReference(file.Trailer, "Info")?.ObjectId;

        List<PdfPageModel> pages = [];
        HashSet<PdfObjectId> visited = [];
        CollectPages(objectMap, pagesReference.ObjectId, pages, visited, inheritedResources: null);

        return new PdfDocumentModel(rootReference.ObjectId, pagesReference.ObjectId, metadataObjectId, infoObjectId, pages);
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

    private static PdfReferenceObject RequireTrailerReference(PdfDictionaryObject trailer, string key)
    {
        PdfObject value = RequireDictionaryEntry(trailer, key);
        return value as PdfReferenceObject
            ?? throw new PdfFormatException($"Trailer entry '/{key}' must be a reference.");
    }

    private static PdfReferenceObject RequireDictionaryReference(PdfDictionaryObject dictionary, string key)
    {
        PdfObject value = RequireDictionaryEntry(dictionary, key);
        return value as PdfReferenceObject
            ?? throw new PdfFormatException($"Dictionary entry '/{key}' must be a reference.");
    }

    private static PdfDictionaryObject RequireDictionary(
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        PdfObjectId objectId,
        string context)
    {
        if (!objectMap.TryGetValue(objectId, out PdfIndirectObject? indirectObject))
        {
            throw new PdfFormatException($"Missing {context} object {objectId}.");
        }

        return indirectObject.Value as PdfDictionaryObject
            ?? throw new PdfFormatException($"Expected {context} object {objectId} to be a dictionary.");
    }

    private static void CollectPages(
        IReadOnlyDictionary<PdfObjectId, PdfIndirectObject> objectMap,
        PdfObjectId nodeId,
        List<PdfPageModel> pages,
        HashSet<PdfObjectId> visited,
        PdfObject? inheritedResources)
    {
        if (!visited.Add(nodeId))
        {
            throw new PdfFormatException($"Cycle detected in page tree at object {nodeId}.");
        }

        PdfDictionaryObject dictionary = RequireDictionary(objectMap, nodeId, "page-tree");
        string typeName = GetRequiredTypeName(dictionary);
        PdfObject? effectiveResources = TryGetDictionaryEntry(dictionary, "Resources", out PdfObject? localResources)
            ? localResources
            : inheritedResources;

        if (string.Equals(typeName, "Page", StringComparison.Ordinal))
        {
            PdfObject? contents = TryGetDictionaryEntry(dictionary, "Contents", out PdfObject? value) ? value : null;
            pages.Add(new PdfPageModel(nodeId, ParseMediaBox(dictionary), effectiveResources, contents));
            visited.Remove(nodeId);
            return;
        }

        if (!string.Equals(typeName, "Pages", StringComparison.Ordinal))
        {
            throw new PdfFormatException($"Unsupported page-tree node type '/{typeName}'.");
        }

        PdfObject kidsObject = RequireDictionaryEntry(dictionary, "Kids");
        if (kidsObject is not PdfArrayObject kidsArray)
        {
            throw new PdfFormatException("Page-tree '/Kids' must be an array.");
        }

        foreach (PdfObject kid in kidsArray.Items)
        {
            if (kid is not PdfReferenceObject kidReference)
            {
                throw new PdfFormatException("Page-tree '/Kids' entries must be references.");
            }

            CollectPages(objectMap, kidReference.ObjectId, pages, visited, effectiveResources);
        }

        visited.Remove(nodeId);
    }

    private static PdfReferenceObject? TryGetOptionalReference(PdfDictionaryObject dictionary, string key)
    {
        if (!TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            return null;
        }

        return value as PdfReferenceObject
            ?? throw new PdfFormatException($"Dictionary entry '/{key}' must be a reference when present.");
    }

    private static string GetRequiredTypeName(PdfDictionaryObject dictionary)
    {
        PdfObject typeObject = RequireDictionaryEntry(dictionary, "Type");
        if (typeObject is not PdfNameObject nameObject)
        {
            throw new PdfFormatException("Dictionary '/Type' entry must be a name.");
        }

        return nameObject.Value;
    }

    private static PdfRectangle? ParseMediaBox(PdfDictionaryObject dictionary)
    {
        if (!TryGetDictionaryEntry(dictionary, "MediaBox", out PdfObject? mediaBoxObject))
        {
            return null;
        }

        if (mediaBoxObject is not PdfArrayObject mediaBoxArray || mediaBoxArray.Items.Count != 4)
        {
            throw new PdfFormatException("Page '/MediaBox' must be an array of 4 numbers.");
        }

        double[] values = new double[4];
        for (int index = 0; index < 4; index++)
        {
            if (mediaBoxArray.Items[index] is not PdfNumberObject number)
            {
                throw new PdfFormatException("Page '/MediaBox' must only contain numbers.");
            }

            values[index] = number.Value;
        }

        return new PdfRectangle(values[0], values[1], values[2], values[3]);
    }

    private static PdfObject RequireDictionaryEntry(PdfDictionaryObject dictionary, string key)
    {
        if (!TryGetDictionaryEntry(dictionary, key, out PdfObject? value))
        {
            throw new PdfFormatException($"Required dictionary entry '/{key}' was not found.");
        }

        return value;
    }

    private static bool TryGetDictionaryEntry(PdfDictionaryObject dictionary, string key, [NotNullWhen(true)] out PdfObject? value)
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
}
