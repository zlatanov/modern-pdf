using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HarfBuzzSharp;
using ModernPDF.Format;

namespace ModernPDF.Fonts;

internal readonly record struct PdfShapedGlyph(
    int Cid,
    int Cluster,
    int XAdvance,
    int YAdvance,
    int XOffset,
    int YOffset);

internal sealed class PdfEmbeddedTrueTypeFont
{
    public required string BaseFontName { get; init; }

    public required byte[] FontProgram { get; init; }

    public required IReadOnlyDictionary<int, ushort> CidToGlyphId { get; init; }

    public required IReadOnlyDictionary<int, int> CidToWidth { get; init; }

    public required IReadOnlyDictionary<int, string> CidToUnicode { get; init; }

    public required IReadOnlyList<PdfShapedGlyph> GlyphRun { get; init; }

    public required ushort UnitsPerEm { get; init; }

    public required IReadOnlyDictionary<int, ushort> UnicodeToGlyphId { get; init; }

    public required IReadOnlyDictionary<int, int> UnicodeToWidth { get; init; }

    public required int Ascent { get; init; }

    public required int Descent { get; init; }

    public required int XMin { get; init; }

    public required int YMin { get; init; }

    public required int XMax { get; init; }

    public required int YMax { get; init; }
}

internal static class PdfTrueTypeFontEmbedder
{
    public static bool CanRenderText(string fontPath, string text, PdfTextDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        ArgumentNullException.ThrowIfNull(text);

        if (!File.Exists(fontPath))
        {
            return false;
        }

        byte[] bytes = File.ReadAllBytes(fontPath);
        TrueTypeFont font = TrueTypeFont.Parse(bytes, fontPath);
        return font.CanRenderText(text, direction);
    }

    public static PdfEmbeddedTrueTypeFont Build(string fontPath, string text, bool subsetFont, PdfTextDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        ArgumentNullException.ThrowIfNull(text);

        if (!File.Exists(fontPath))
        {
            throw new FileNotFoundException($"TrueType font file '{fontPath}' was not found.", fontPath);
        }

        byte[] bytes = File.ReadAllBytes(fontPath);
        TrueTypeFont font = TrueTypeFont.Parse(bytes, fontPath);
        return font.BuildEmbeddedFont(text, subsetFont, direction);
    }

    private sealed class TrueTypeFont
    {
        private readonly byte[] _bytes;
        private readonly Dictionary<string, TableRecord> _tables;
        private readonly int _glyphTableOffset;
        private readonly uint[] _glyphOffsets;
        private readonly ushort[] _advanceWidths;
        private readonly short[] _leftSideBearings;
        private readonly CmapFormat4 _cmap;

        private TrueTypeFont(
            byte[] bytes,
            Dictionary<string, TableRecord> tables,
            CmapFormat4 cmap,
            uint[] glyphOffsets,
            ushort[] advanceWidths,
            short[] leftSideBearings,
            int glyphTableOffset,
            ushort unitsPerEm,
            short ascender,
            short descender,
            short xMin,
            short yMin,
            short xMax,
            short yMax,
            uint sfntVersion,
            string postScriptName)
        {
            _bytes = bytes;
            _tables = tables;
            _cmap = cmap;
            _glyphOffsets = glyphOffsets;
            _advanceWidths = advanceWidths;
            _leftSideBearings = leftSideBearings;
            _glyphTableOffset = glyphTableOffset;
            UnitsPerEm = unitsPerEm;
            Ascender = ascender;
            Descender = descender;
            XMin = xMin;
            YMin = yMin;
            XMax = xMax;
            YMax = yMax;
            SfntVersion = sfntVersion;
            PostScriptName = postScriptName;
        }

        public ushort UnitsPerEm { get; }

        public short Ascender { get; }

        public short Descender { get; }

        public short XMin { get; }

        public short YMin { get; }

        public short XMax { get; }

        public short YMax { get; }

        public uint SfntVersion { get; }

        public string PostScriptName { get; }

        public int GlyphCount => _glyphOffsets.Length - 1;

        public static TrueTypeFont Parse(byte[] bytes, string sourcePath)
        {
            if (bytes.Length < 12)
            {
                throw new PdfFormatException("TrueType font data is too short.");
            }

            uint sfntVersion = ReadUInt32(bytes, 0);
            if (sfntVersion is not 0x00010000 and not 0x4F54544F)
            {
                throw new PdfFormatException("Unsupported font format. Only TrueType/OpenType sfnt data is supported.");
            }

            ushort numTables = ReadUInt16(bytes, 4);
            int tableDirectoryEnd = 12 + numTables * 16;
            if (tableDirectoryEnd > bytes.Length)
            {
                throw new PdfFormatException("TrueType table directory is truncated.");
            }

            Dictionary<string, TableRecord> tables = new(StringComparer.Ordinal);
            for (int index = 0; index < numTables; index++)
            {
                int offset = 12 + index * 16;
                string tag = Encoding.ASCII.GetString(bytes, offset, 4);
                uint checksum = ReadUInt32(bytes, offset + 4);
                uint tableOffset = ReadUInt32(bytes, offset + 8);
                uint length = ReadUInt32(bytes, offset + 12);

                if (tableOffset + length > bytes.Length)
                {
                    throw new PdfFormatException($"TrueType table '{tag}' is out of range.");
                }

                tables[tag] = new TableRecord(tag, checksum, tableOffset, length);
            }

            TableRecord head = RequireTable(tables, "head");
            TableRecord hhea = RequireTable(tables, "hhea");
            TableRecord hmtx = RequireTable(tables, "hmtx");
            TableRecord maxp = RequireTable(tables, "maxp");
            TableRecord loca = RequireTable(tables, "loca");
            TableRecord glyf = RequireTable(tables, "glyf");
            TableRecord cmap = RequireTable(tables, "cmap");

            EnsureTableLength(head, 54, "head");
            EnsureTableLength(hhea, 36, "hhea");
            EnsureTableLength(maxp, 6, "maxp");

            ushort unitsPerEm = ReadUInt16(bytes, (int)head.Offset + 18);
            short xMin = ReadInt16(bytes, (int)head.Offset + 36);
            short yMin = ReadInt16(bytes, (int)head.Offset + 38);
            short xMax = ReadInt16(bytes, (int)head.Offset + 40);
            short yMax = ReadInt16(bytes, (int)head.Offset + 42);
            short indexToLocFormat = ReadInt16(bytes, (int)head.Offset + 50);
            short ascender = ReadInt16(bytes, (int)hhea.Offset + 4);
            short descender = ReadInt16(bytes, (int)hhea.Offset + 6);
            ushort numberOfHMetrics = ReadUInt16(bytes, (int)hhea.Offset + 34);
            ushort numGlyphs = ReadUInt16(bytes, (int)maxp.Offset + 4);

            uint[] glyphOffsets = ParseLocaOffsets(bytes, loca, numGlyphs, indexToLocFormat);
            if (glyphOffsets.Length < 2)
            {
                throw new PdfFormatException("TrueType loca table did not contain glyph offsets.");
            }

            ushort[] advanceWidths = new ushort[numGlyphs];
            short[] leftSideBearings = new short[numGlyphs];
            ParseHorizontalMetrics(bytes, hmtx, numberOfHMetrics, advanceWidths, leftSideBearings);

            CmapFormat4 format4 = CmapFormat4.Parse(bytes, cmap);

            string postScriptName = ParsePostScriptName(bytes, tables)
                ?? Path.GetFileNameWithoutExtension(sourcePath);
            postScriptName = SanitizeBaseFontName(postScriptName);
            if (string.IsNullOrWhiteSpace(postScriptName))
            {
                postScriptName = "EmbeddedFont";
            }

            return new TrueTypeFont(
                bytes,
                tables,
                format4,
                glyphOffsets,
                advanceWidths,
                leftSideBearings,
                (int)glyf.Offset,
                unitsPerEm,
                ascender,
                descender,
                xMin,
                yMin,
                xMax,
                yMax,
                sfntVersion,
                postScriptName);
        }

        public PdfEmbeddedTrueTypeFont BuildEmbeddedFont(string text, bool subsetFont, PdfTextDirection direction)
        {
            IReadOnlyList<ShapedGlyphEntry> shapedGlyphs = ShapeGlyphs(text, _bytes, UnitsPerEm, direction);

            HashSet<int> unicodeSet = [.. text.EnumerateRunes().Select(static rune => rune.Value)];

            Dictionary<int, ushort> unicodeToOriginalGlyph = [];
            foreach (int unicode in unicodeSet)
            {
                unicodeToOriginalGlyph[unicode] = _cmap.GetGlyphId(unicode);
            }

            Dictionary<CidKey, int> cidByKey = new();
            Dictionary<int, ushort> cidToOriginalGlyph = [];
            Dictionary<int, int> cidToOriginalWidth = [];
            Dictionary<int, string> cidToUnicode = [];
            List<PdfShapedGlyph> glyphRun = [];
            Dictionary<int, (int Start, int End)> clusterRanges = BuildClusterRanges(text, shapedGlyphs);
            int nextCid = 1;

            foreach (ShapedGlyphEntry shaped in shapedGlyphs)
            {
                if (shaped.GlyphId >= GlyphCount)
                {
                    throw new PdfFormatException($"Shaping produced glyph id {shaped.GlyphId}, which is outside the font glyph range.");
                }

                if (!clusterRanges.TryGetValue(ClampCluster(shaped.Cluster, text.Length), out (int Start, int End) range))
                {
                    range = (0, text.Length);
                }

                string unicodeSlice = range.End > range.Start
                    ? text.Substring(range.Start, range.End - range.Start)
                    : "\uFFFD";

                CidKey cidKey = new(shaped.GlyphId, unicodeSlice);
                if (!cidByKey.TryGetValue(cidKey, out int cid))
                {
                    cid = nextCid++;
                    cidByKey[cidKey] = cid;
                    cidToOriginalGlyph[cid] = shaped.GlyphId;
                    cidToOriginalWidth[cid] = ScaleToPdfUnits(_advanceWidths[shaped.GlyphId], UnitsPerEm);
                    cidToUnicode[cid] = unicodeSlice;
                }

                glyphRun.Add(new PdfShapedGlyph(
                    cid,
                    shaped.Cluster,
                    shaped.XAdvance,
                    shaped.YAdvance,
                    shaped.XOffset,
                    shaped.YOffset));
            }

            if (nextCid > 0x10000)
            {
                throw new NotSupportedException("Embedded text run produced more than 65535 unique CID entries.");
            }

            byte[] programBytes;
            IReadOnlyDictionary<int, ushort> unicodeToGlyph;
            Dictionary<int, ushort> cidToGlyph;
            if (subsetFont)
            {
                SubsetResult subset = BuildSubset(
                    [.. cidToOriginalGlyph.Values.Distinct()],
                    unicodeToOriginalGlyph);
                unicodeToGlyph = subset.UnicodeToGlyphId;
                programBytes = subset.FontProgram;
                cidToGlyph = cidToOriginalGlyph.ToDictionary(
                    static pair => pair.Key,
                    pair => subset.OldToNewGlyphId.TryGetValue(pair.Value, out ushort remapped) ? remapped : (ushort)0);
            }
            else
            {
                unicodeToGlyph = unicodeToOriginalGlyph;
                programBytes = _bytes.ToArray();
                cidToGlyph = cidToOriginalGlyph;
            }

            Dictionary<int, int> unicodeToWidth = [];
            foreach ((int unicode, ushort glyphId) in unicodeToOriginalGlyph)
            {
                int width = ScaleToPdfUnits(_advanceWidths[glyphId], UnitsPerEm);
                unicodeToWidth[unicode] = width;
            }

            string baseFontName = subsetFont
                ? $"{CreateSubsetPrefix(PostScriptName, unicodeSet)}+{PostScriptName}"
                : PostScriptName;

            return new PdfEmbeddedTrueTypeFont
            {
                BaseFontName = baseFontName,
                FontProgram = programBytes,
                CidToGlyphId = cidToGlyph,
                CidToWidth = cidToOriginalWidth,
                CidToUnicode = cidToUnicode,
                GlyphRun = glyphRun,
                UnitsPerEm = UnitsPerEm,
                UnicodeToGlyphId = unicodeToGlyph,
                UnicodeToWidth = unicodeToWidth,
                Ascent = ScaleToPdfUnits(Ascender, UnitsPerEm),
                Descent = ScaleToPdfUnits(Descender, UnitsPerEm),
                XMin = ScaleToPdfUnits(XMin, UnitsPerEm),
                YMin = ScaleToPdfUnits(YMin, UnitsPerEm),
                XMax = ScaleToPdfUnits(XMax, UnitsPerEm),
                YMax = ScaleToPdfUnits(YMax, UnitsPerEm),
            };
        }

        public bool CanRenderText(string text, PdfTextDirection direction)
        {
            List<ShapedGlyphEntry> shapedGlyphs = ShapeGlyphs(text, _bytes, UnitsPerEm, direction);
            if (shapedGlyphs.Count == 0)
            {
                return true;
            }

            Dictionary<int, (int Start, int End)> ranges = BuildClusterRanges(text, shapedGlyphs);
            foreach (ShapedGlyphEntry glyph in shapedGlyphs)
            {
                if (glyph.GlyphId != 0)
                {
                    continue;
                }

                if (!ranges.TryGetValue(ClampCluster(glyph.Cluster, text.Length), out (int Start, int End) range))
                {
                    return false;
                }

                if (range.End <= range.Start)
                {
                    continue;
                }

                string clusterText = text.Substring(range.Start, range.End - range.Start);
                if (!clusterText.All(char.IsWhiteSpace))
                {
                    return false;
                }
            }

            return true;
        }

        private SubsetResult BuildSubset(
            IReadOnlyCollection<ushort> requiredGlyphIds,
            IReadOnlyDictionary<int, ushort> unicodeToOriginalGlyph)
        {
            HashSet<int> glyphSet = [0];
            foreach (ushort gid in requiredGlyphIds)
            {
                glyphSet.Add(gid);
            }

            AddCompoundGlyphReferences(glyphSet);
            List<int> oldGlyphIds = [.. glyphSet.OrderBy(static value => value)];

            Dictionary<int, ushort> oldToNew = [];
            for (ushort index = 0; index < oldGlyphIds.Count; index++)
            {
                oldToNew[oldGlyphIds[index]] = index;
            }

            byte[] glyf = BuildSubsetGlyf(oldGlyphIds, oldToNew, out uint[] newLocaOffsets);
            byte[] loca = BuildSubsetLoca(newLocaOffsets);
            byte[] hmtx = BuildSubsetHmtx(oldGlyphIds);
            byte[] head = BuildSubsetHead();
            byte[] hhea = BuildSubsetHhea((ushort)oldGlyphIds.Count);
            byte[] maxp = BuildSubsetMaxp((ushort)oldGlyphIds.Count);

            Dictionary<int, ushort> unicodeToNewGlyph = [];
            foreach ((int unicode, ushort oldGlyph) in unicodeToOriginalGlyph)
            {
                unicodeToNewGlyph[unicode] = oldToNew.TryGetValue(oldGlyph, out ushort value) ? value : (ushort)0;
            }

            byte[] cmap = BuildSubsetCmap(unicodeToNewGlyph);
            byte[] fontProgram = BuildSubsetFontProgram(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["head"] = head,
                ["hhea"] = hhea,
                ["maxp"] = maxp,
                ["hmtx"] = hmtx,
                ["loca"] = loca,
                ["glyf"] = glyf,
                ["cmap"] = cmap,
            });

            return new SubsetResult(fontProgram, oldToNew, unicodeToNewGlyph);
        }

        private static List<ShapedGlyphEntry> ShapeGlyphs(string text, byte[] fontBytes, ushort unitsPerEm, PdfTextDirection direction)
        {
            using MemoryStream stream = new(fontBytes, writable: false);
            using Blob blob = Blob.FromStream(stream);
            using Face face = new(blob, index: 0);
            using Font font = new(face);
            font.SetFunctionsOpenType();
            font.SetScale(unitsPerEm, unitsPerEm);

            using HarfBuzzSharp.Buffer buffer = new();
            buffer.AddUtf16(text);
            buffer.GuessSegmentProperties();
            if (direction != PdfTextDirection.Auto)
            {
                buffer.Direction = direction == PdfTextDirection.RightToLeft
                    ? Direction.RightToLeft
                    : Direction.LeftToRight;
            }

            font.Shape(buffer, Array.Empty<Feature>());

            GlyphInfo[] infos = buffer.GlyphInfos;
            GlyphPosition[] positions = buffer.GlyphPositions;
            if (infos.Length != positions.Length)
            {
                throw new PdfFormatException("Shaping produced mismatched glyph info and position arrays.");
            }

            List<ShapedGlyphEntry> shaped = new(infos.Length);
            for (int index = 0; index < infos.Length; index++)
            {
                uint codepoint = infos[index].Codepoint;
                if (codepoint > ushort.MaxValue)
                {
                    throw new NotSupportedException($"Shaping produced glyph id {codepoint}, which exceeds 16-bit CIDFontType2 limits.");
                }

                shaped.Add(new ShapedGlyphEntry(
                    (ushort)codepoint,
                    checked((int)infos[index].Cluster),
                    positions[index].XAdvance,
                    positions[index].YAdvance,
                    positions[index].XOffset,
                    positions[index].YOffset));
            }

            return shaped;
        }

        private static Dictionary<int, (int Start, int End)> BuildClusterRanges(string text, IReadOnlyList<ShapedGlyphEntry> shapedGlyphs)
        {
            SortedSet<int> boundaries = [0, text.Length];
            foreach (ShapedGlyphEntry glyph in shapedGlyphs)
            {
                boundaries.Add(ClampCluster(glyph.Cluster, text.Length));
            }

            List<int> ordered = [.. boundaries];
            Dictionary<int, (int Start, int End)> ranges = new(ordered.Count);
            for (int index = 0; index < ordered.Count - 1; index++)
            {
                ranges[ordered[index]] = (ordered[index], ordered[index + 1]);
            }

            return ranges;
        }

        private static int ClampCluster(int cluster, int textLength)
        {
            if (cluster < 0)
            {
                return 0;
            }

            return cluster > textLength ? textLength : cluster;
        }

        private byte[] BuildSubsetFontProgram(Dictionary<string, byte[]> rebuiltTables)
        {
            HashSet<string> replaced = rebuiltTables.Keys.ToHashSet(StringComparer.Ordinal);
            foreach ((string tag, TableRecord table) in _tables)
            {
                if (!replaced.Contains(tag))
                {
                    rebuiltTables[tag] = _bytes.AsSpan((int)table.Offset, (int)table.Length).ToArray();
                }
            }

            List<string> orderedTags = [.. rebuiltTables.Keys.OrderBy(static value => value, StringComparer.Ordinal)];
            int tableCount = orderedTags.Count;
            int searchRange = 16 * HighestPowerOfTwo(tableCount);
            int entrySelector = Log2(HighestPowerOfTwo(tableCount));
            int rangeShift = 16 * tableCount - searchRange;

            Dictionary<string, uint> checksums = [];
            Dictionary<string, uint> offsets = [];
            Dictionary<string, uint> lengths = [];

            uint directorySize = (uint)(12 + tableCount * 16);
            uint currentOffset = directorySize;
            foreach (string tag in orderedTags)
            {
                byte[] tableBytes = rebuiltTables[tag];
                checksums[tag] = ComputeChecksum(tableBytes);
                offsets[tag] = currentOffset;
                lengths[tag] = (uint)tableBytes.Length;
                currentOffset += Align4((uint)tableBytes.Length);
            }

            byte[] output = new byte[currentOffset];
            WriteUInt32(output, 0, SfntVersion);
            WriteUInt16(output, 4, (ushort)tableCount);
            WriteUInt16(output, 6, (ushort)searchRange);
            WriteUInt16(output, 8, (ushort)entrySelector);
            WriteUInt16(output, 10, (ushort)rangeShift);

            for (int index = 0; index < orderedTags.Count; index++)
            {
                string tag = orderedTags[index];
                int offset = 12 + index * 16;
                Encoding.ASCII.GetBytes(tag).CopyTo(output, offset);
                WriteUInt32(output, offset + 4, checksums[tag]);
                WriteUInt32(output, offset + 8, offsets[tag]);
                WriteUInt32(output, offset + 12, lengths[tag]);
            }

            foreach (string tag in orderedTags)
            {
                byte[] tableBytes = rebuiltTables[tag];
                tableBytes.CopyTo(output, (int)offsets[tag]);
            }

            if (!offsets.TryGetValue("head", out uint headOffset))
            {
                throw new PdfFormatException("Subset font was missing required head table.");
            }

            WriteUInt32(output, (int)headOffset + 8, 0);
            uint checksum = ComputeChecksum(output);
            uint adjustment = unchecked(0xB1B0AFBA - checksum);
            WriteUInt32(output, (int)headOffset + 8, adjustment);
            return output;
        }

        private byte[] BuildSubsetHead()
        {
            TableRecord headRecord = _tables["head"];
            byte[] head = _bytes.AsSpan((int)headRecord.Offset, (int)headRecord.Length).ToArray();
            WriteUInt32(head, 8, 0);
            WriteInt16(head, 50, 1);
            return head;
        }

        private byte[] BuildSubsetHhea(ushort numberOfHMetrics)
        {
            TableRecord hheaRecord = _tables["hhea"];
            byte[] hhea = _bytes.AsSpan((int)hheaRecord.Offset, (int)hheaRecord.Length).ToArray();
            WriteUInt16(hhea, 34, numberOfHMetrics);
            return hhea;
        }

        private byte[] BuildSubsetMaxp(ushort glyphCount)
        {
            TableRecord maxpRecord = _tables["maxp"];
            byte[] maxp = _bytes.AsSpan((int)maxpRecord.Offset, (int)maxpRecord.Length).ToArray();
            WriteUInt16(maxp, 4, glyphCount);
            return maxp;
        }

        private byte[] BuildSubsetHmtx(List<int> oldGlyphIds)
        {
            byte[] output = new byte[oldGlyphIds.Count * 4];
            for (int index = 0; index < oldGlyphIds.Count; index++)
            {
                int glyphId = oldGlyphIds[index];
                WriteUInt16(output, index * 4, _advanceWidths[glyphId]);
                WriteInt16(output, index * 4 + 2, _leftSideBearings[glyphId]);
            }

            return output;
        }

        private static byte[] BuildSubsetLoca(uint[] offsets)
        {
            byte[] output = new byte[offsets.Length * 4];
            for (int index = 0; index < offsets.Length; index++)
            {
                WriteUInt32(output, index * 4, offsets[index]);
            }

            return output;
        }

        private static byte[] BuildSubsetCmap(IReadOnlyDictionary<int, ushort> unicodeToGlyph)
        {
            List<int> codes = [.. unicodeToGlyph.Keys.Where(static code => code <= 0xFFFF).OrderBy(static value => value)];
            int segCount = codes.Count + 1;
            int segCountX2 = segCount * 2;
            int searchRange = 2 * HighestPowerOfTwo(segCount);
            int entrySelector = Log2(HighestPowerOfTwo(segCount));
            int rangeShift = segCountX2 - searchRange;

            int format4Length = 16 + segCount * 8;
            byte[] format4 = new byte[format4Length];
            WriteUInt16(format4, 0, 4);
            WriteUInt16(format4, 2, (ushort)format4Length);
            WriteUInt16(format4, 4, 0);
            WriteUInt16(format4, 6, (ushort)segCountX2);
            WriteUInt16(format4, 8, (ushort)searchRange);
            WriteUInt16(format4, 10, (ushort)entrySelector);
            WriteUInt16(format4, 12, (ushort)rangeShift);

            int endCodeOffset = 14;
            int startCodeOffset = endCodeOffset + segCount * 2 + 2;
            int idDeltaOffset = startCodeOffset + segCount * 2;
            int idRangeOffset = idDeltaOffset + segCount * 2;

            for (int index = 0; index < codes.Count; index++)
            {
                int code = codes[index];
                ushort gid = unicodeToGlyph.TryGetValue(code, out ushort value) ? value : (ushort)0;
                ushort delta = (ushort)((gid - code) & 0xFFFF);

                WriteUInt16(format4, endCodeOffset + index * 2, (ushort)code);
                WriteUInt16(format4, startCodeOffset + index * 2, (ushort)code);
                WriteUInt16(format4, idDeltaOffset + index * 2, delta);
                WriteUInt16(format4, idRangeOffset + index * 2, 0);
            }

            WriteUInt16(format4, endCodeOffset + codes.Count * 2, 0xFFFF);
            WriteUInt16(format4, startCodeOffset + codes.Count * 2, 0xFFFF);
            WriteUInt16(format4, idDeltaOffset + codes.Count * 2, 1);
            WriteUInt16(format4, idRangeOffset + codes.Count * 2, 0);

            byte[] cmap = new byte[12 + format4.Length];
            WriteUInt16(cmap, 0, 0);
            WriteUInt16(cmap, 2, 1);
            WriteUInt16(cmap, 4, 3);
            WriteUInt16(cmap, 6, 1);
            WriteUInt32(cmap, 8, 12);
            format4.CopyTo(cmap, 12);
            return cmap;
        }

        private byte[] BuildSubsetGlyf(
            List<int> oldGlyphIds,
            IReadOnlyDictionary<int, ushort> oldToNew,
            out uint[] locaOffsets)
        {
            MemoryStream stream = new();
            locaOffsets = new uint[oldGlyphIds.Count + 1];

            for (int index = 0; index < oldGlyphIds.Count; index++)
            {
                int oldGlyphId = oldGlyphIds[index];
                locaOffsets[index] = (uint)stream.Position;

                ReadOnlySpan<byte> glyph = GetGlyphBytes(oldGlyphId);
                if (glyph.Length == 0)
                {
                    continue;
                }

                byte[] rewritten = RewriteCompositeGlyphReferences(glyph.ToArray(), oldToNew);
                stream.Write(rewritten);

                int padding = (4 - (int)(stream.Position % 4)) & 3;
                for (int pad = 0; pad < padding; pad++)
                {
                    stream.WriteByte(0);
                }
            }

            locaOffsets[oldGlyphIds.Count] = (uint)stream.Position;
            return stream.ToArray();
        }

        private static byte[] RewriteCompositeGlyphReferences(byte[] glyph, IReadOnlyDictionary<int, ushort> oldToNew)
        {
            if (glyph.Length < 10)
            {
                return glyph;
            }

            short numberOfContours = ReadInt16(glyph, 0);
            if (numberOfContours != -1)
            {
                return glyph;
            }

            int offset = 10;
            int flags;
            do
            {
                EnsureRange(glyph, offset, 4, "Composite glyph component record");
                flags = ReadUInt16(glyph, offset);
                int oldComponentGlyphId = ReadUInt16(glyph, offset + 2);
                if (!oldToNew.TryGetValue(oldComponentGlyphId, out ushort newComponentGlyphId))
                {
                    throw new PdfFormatException($"Composite glyph references unknown component glyph id {oldComponentGlyphId}.");
                }

                WriteUInt16(glyph, offset + 2, newComponentGlyphId);
                offset += 4;

                if ((flags & 0x0001) != 0)
                {
                    offset += 4;
                }
                else
                {
                    offset += 2;
                }

                if ((flags & 0x0008) != 0)
                {
                    offset += 2;
                }
                else if ((flags & 0x0040) != 0)
                {
                    offset += 4;
                }
                else if ((flags & 0x0080) != 0)
                {
                    offset += 8;
                }

                EnsureRange(glyph, offset, 0, "Composite glyph transform");
            }
            while ((flags & 0x0020) != 0);

            return glyph;
        }

        private void AddCompoundGlyphReferences(HashSet<int> glyphSet)
        {
            bool changed;
            do
            {
                changed = false;
                List<int> snapshot = [.. glyphSet];
                foreach (int glyphId in snapshot)
                {
                    ReadOnlySpan<byte> glyph = GetGlyphBytes(glyphId);
                    if (glyph.Length < 10 || ReadInt16(glyph, 0) != -1)
                    {
                        continue;
                    }

                    int offset = 10;
                    int flags;
                    do
                    {
                        EnsureRange(glyph, offset, 4, "Compound glyph component");
                        flags = ReadUInt16(glyph, offset);
                        int componentGlyphId = ReadUInt16(glyph, offset + 2);
                        changed |= glyphSet.Add(componentGlyphId);
                        offset += 4;

                        if ((flags & 0x0001) != 0)
                        {
                            offset += 4;
                        }
                        else
                        {
                            offset += 2;
                        }

                        if ((flags & 0x0008) != 0)
                        {
                            offset += 2;
                        }
                        else if ((flags & 0x0040) != 0)
                        {
                            offset += 4;
                        }
                        else if ((flags & 0x0080) != 0)
                        {
                            offset += 8;
                        }
                    }
                    while ((flags & 0x0020) != 0);
                }
            }
            while (changed);
        }

        private ReadOnlySpan<byte> GetGlyphBytes(int glyphId)
        {
            if (glyphId < 0 || glyphId >= GlyphCount)
            {
                throw new PdfFormatException($"Glyph id {glyphId} is out of range.");
            }

            uint start = _glyphOffsets[glyphId];
            uint end = _glyphOffsets[glyphId + 1];
            if (end < start)
            {
                throw new PdfFormatException("Invalid glyph offset ordering in loca table.");
            }

            int length = checked((int)(end - start));
            int absoluteStart = checked(_glyphTableOffset + (int)start);
            EnsureRange(_bytes, absoluteStart, length, "glyf table glyph data");
            return _bytes.AsSpan(absoluteStart, length);
        }

        private static string? ParsePostScriptName(byte[] bytes, IReadOnlyDictionary<string, TableRecord> tables)
        {
            if (!tables.TryGetValue("name", out TableRecord nameTable))
            {
                return null;
            }

            EnsureTableLength(nameTable, 6, "name");
            int tableOffset = (int)nameTable.Offset;
            ushort count = ReadUInt16(bytes, tableOffset + 2);
            ushort stringOffset = ReadUInt16(bytes, tableOffset + 4);
            int recordStart = tableOffset + 6;
            int stringStorage = tableOffset + stringOffset;

            string? fallback = null;
            for (int index = 0; index < count; index++)
            {
                int recordOffset = recordStart + index * 12;
                EnsureRange(bytes, recordOffset, 12, "name table record");

                ushort platformId = ReadUInt16(bytes, recordOffset);
                ushort encodingId = ReadUInt16(bytes, recordOffset + 2);
                ushort languageId = ReadUInt16(bytes, recordOffset + 4);
                ushort nameId = ReadUInt16(bytes, recordOffset + 6);
                ushort length = ReadUInt16(bytes, recordOffset + 8);
                ushort offset = ReadUInt16(bytes, recordOffset + 10);

                if (nameId != 6 || length == 0)
                {
                    continue;
                }

                int stringStart = stringStorage + offset;
                EnsureRange(bytes, stringStart, length, "name table string storage");
                byte[] nameBytes = bytes.AsSpan(stringStart, length).ToArray();

                string value = platformId switch
                {
                    0 => Encoding.BigEndianUnicode.GetString(nameBytes),
                    3 => Encoding.BigEndianUnicode.GetString(nameBytes),
                    _ => Encoding.ASCII.GetString(nameBytes),
                };

                if (platformId == 3 && (encodingId == 1 || encodingId == 10) && languageId == 0x0409)
                {
                    return value;
                }

                fallback ??= value;
            }

            return fallback;
        }

        private static uint[] ParseLocaOffsets(byte[] bytes, TableRecord loca, ushort numGlyphs, short indexToLocFormat)
        {
            int count = numGlyphs + 1;
            uint[] offsets = new uint[count];

            if (indexToLocFormat == 0)
            {
                EnsureTableLength(loca, count * 2, "loca");
                for (int index = 0; index < count; index++)
                {
                    offsets[index] = (uint)(ReadUInt16(bytes, (int)loca.Offset + index * 2) * 2);
                }
            }
            else if (indexToLocFormat == 1)
            {
                EnsureTableLength(loca, count * 4, "loca");
                for (int index = 0; index < count; index++)
                {
                    offsets[index] = ReadUInt32(bytes, (int)loca.Offset + index * 4);
                }
            }
            else
            {
                throw new PdfFormatException("Unsupported head.indexToLocFormat value.");
            }

            return offsets;
        }

        private static void ParseHorizontalMetrics(
            byte[] bytes,
            TableRecord hmtx,
            ushort numberOfHMetrics,
            ushort[] advanceWidths,
            short[] leftSideBearings)
        {
            int glyphCount = advanceWidths.Length;
            if (glyphCount == 0)
            {
                return;
            }

            int required = numberOfHMetrics * 4 + Math.Max(0, glyphCount - numberOfHMetrics) * 2;
            EnsureTableLength(hmtx, required, "hmtx");

            ushort lastAdvance = 0;
            for (int gid = 0; gid < glyphCount; gid++)
            {
                if (gid < numberOfHMetrics)
                {
                    int offset = (int)hmtx.Offset + gid * 4;
                    ushort advance = ReadUInt16(bytes, offset);
                    short lsb = ReadInt16(bytes, offset + 2);
                    advanceWidths[gid] = advance;
                    leftSideBearings[gid] = lsb;
                    lastAdvance = advance;
                    continue;
                }

                int extraOffset = (int)hmtx.Offset + numberOfHMetrics * 4 + (gid - numberOfHMetrics) * 2;
                advanceWidths[gid] = lastAdvance;
                leftSideBearings[gid] = ReadInt16(bytes, extraOffset);
            }
        }

        private static TableRecord RequireTable(IReadOnlyDictionary<string, TableRecord> tables, string tag)
        {
            if (!tables.TryGetValue(tag, out TableRecord record))
            {
                throw new PdfFormatException($"TrueType font is missing required table '{tag}'.");
            }

            return record;
        }

        private static int ScaleToPdfUnits(int value, ushort unitsPerEm)
        {
            return (int)Math.Round(value * 1000.0 / unitsPerEm, MidpointRounding.AwayFromZero);
        }

        private static string CreateSubsetPrefix(string postScriptName, IEnumerable<int> unicodeSet)
        {
            string seed = $"{postScriptName}|{string.Join(",", unicodeSet.OrderBy(static value => value))}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            Span<char> chars = stackalloc char[6];
            for (int index = 0; index < chars.Length; index++)
            {
                chars[index] = (char)('A' + (hash[index] % 26));
            }

            return new string(chars);
        }
    }

    private readonly record struct SubsetResult(
        byte[] FontProgram,
        IReadOnlyDictionary<int, ushort> OldToNewGlyphId,
        IReadOnlyDictionary<int, ushort> UnicodeToGlyphId);

    private readonly record struct ShapedGlyphEntry(
        ushort GlyphId,
        int Cluster,
        int XAdvance,
        int YAdvance,
        int XOffset,
        int YOffset);

    private readonly record struct CidKey(
        ushort GlyphId,
        string UnicodeText);

    private readonly record struct TableRecord(
        string Tag,
        uint Checksum,
        uint Offset,
        uint Length);

    private sealed class CmapFormat4
    {
        private readonly byte[] _subtable;
        private readonly ushort[] _endCodes;
        private readonly ushort[] _startCodes;
        private readonly short[] _idDeltas;
        private readonly ushort[] _idRangeOffsets;
        private readonly int _idRangeOffsetArrayStart;

        private CmapFormat4(
            byte[] subtable,
            ushort[] endCodes,
            ushort[] startCodes,
            short[] idDeltas,
            ushort[] idRangeOffsets,
            int idRangeOffsetArrayStart)
        {
            _subtable = subtable;
            _endCodes = endCodes;
            _startCodes = startCodes;
            _idDeltas = idDeltas;
            _idRangeOffsets = idRangeOffsets;
            _idRangeOffsetArrayStart = idRangeOffsetArrayStart;
        }

        public static CmapFormat4 Parse(byte[] bytes, TableRecord cmapTable)
        {
            EnsureTableLength(cmapTable, 4, "cmap");
            int offset = (int)cmapTable.Offset;
            ushort tableCount = ReadUInt16(bytes, offset + 2);
            EnsureRange(bytes, offset + 4, tableCount * 8, "cmap encoding records");

            int? bestSubtableOffset = null;
            for (int index = 0; index < tableCount; index++)
            {
                int record = offset + 4 + index * 8;
                ushort platformId = ReadUInt16(bytes, record);
                ushort encodingId = ReadUInt16(bytes, record + 2);
                uint subtableOffset = ReadUInt32(bytes, record + 4);
                int absoluteSubtableOffset = checked(offset + (int)subtableOffset);
                EnsureRange(bytes, absoluteSubtableOffset, 2, "cmap subtable");
                ushort format = ReadUInt16(bytes, absoluteSubtableOffset);

                if (format != 4)
                {
                    continue;
                }

                if (platformId == 3 && encodingId == 1)
                {
                    bestSubtableOffset = absoluteSubtableOffset;
                    break;
                }

                bestSubtableOffset ??= absoluteSubtableOffset;
            }

            if (bestSubtableOffset is null)
            {
                throw new PdfFormatException("TrueType cmap table does not contain a supported format 4 Unicode subtable.");
            }

            int subOffset = bestSubtableOffset.Value;
            EnsureRange(bytes, subOffset, 8, "cmap format 4 header");
            ushort length = ReadUInt16(bytes, subOffset + 2);
            EnsureRange(bytes, subOffset, length, "cmap format 4 bytes");

            byte[] subtable = bytes.AsSpan(subOffset, length).ToArray();
            ushort segCountX2 = ReadUInt16(subtable, 6);
            if ((segCountX2 & 1) != 0 || segCountX2 == 0)
            {
                throw new PdfFormatException("TrueType cmap format 4 contains an invalid segment count.");
            }

            int segCount = segCountX2 / 2;
            int endCodeOffset = 14;
            int reservedPadOffset = endCodeOffset + segCount * 2;
            int startCodeOffset = reservedPadOffset + 2;
            int idDeltaOffset = startCodeOffset + segCount * 2;
            int idRangeOffset = idDeltaOffset + segCount * 2;
            EnsureRange(subtable, idRangeOffset, segCount * 2, "cmap format 4 arrays");

            ushort[] endCodes = new ushort[segCount];
            ushort[] startCodes = new ushort[segCount];
            short[] deltas = new short[segCount];
            ushort[] rangeOffsets = new ushort[segCount];

            for (int index = 0; index < segCount; index++)
            {
                endCodes[index] = ReadUInt16(subtable, endCodeOffset + index * 2);
                startCodes[index] = ReadUInt16(subtable, startCodeOffset + index * 2);
                deltas[index] = ReadInt16(subtable, idDeltaOffset + index * 2);
                rangeOffsets[index] = ReadUInt16(subtable, idRangeOffset + index * 2);
            }

            return new CmapFormat4(subtable, endCodes, startCodes, deltas, rangeOffsets, idRangeOffset);
        }

        public ushort GetGlyphId(int unicode)
        {
            if ((uint)unicode > 0xFFFFu)
            {
                return 0;
            }

            for (int index = 0; index < _endCodes.Length; index++)
            {
                if (unicode > _endCodes[index])
                {
                    continue;
                }

                if (unicode < _startCodes[index])
                {
                    return 0;
                }

                if (_idRangeOffsets[index] == 0)
                {
                    return (ushort)((unicode + _idDeltas[index]) & 0xFFFF);
                }

                int glyphIndexAddress =
                    _idRangeOffsetArrayStart
                    + index * 2
                    + _idRangeOffsets[index]
                    + (unicode - _startCodes[index]) * 2;

                if (glyphIndexAddress < 0 || glyphIndexAddress + 2 > _subtable.Length)
                {
                    return 0;
                }

                ushort glyph = ReadUInt16(_subtable, glyphIndexAddress);
                if (glyph == 0)
                {
                    return 0;
                }

                return (ushort)((glyph + _idDeltas[index]) & 0xFFFF);
            }

            return 0;
        }
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        int paddedLength = (bytes.Length + 3) & ~3;
        for (int offset = 0; offset < paddedLength; offset += 4)
        {
            uint value = 0;
            for (int byteIndex = 0; byteIndex < 4; byteIndex++)
            {
                int source = offset + byteIndex;
                byte b = source < bytes.Length ? bytes[source] : (byte)0;
                value = (value << 8) | b;
            }

            unchecked
            {
                sum += value;
            }
        }

        return sum;
    }

    private static uint Align4(uint value) => (value + 3u) & ~3u;

    private static int HighestPowerOfTwo(int value)
    {
        int power = 1;
        while ((power << 1) <= value)
        {
            power <<= 1;
        }

        return power;
    }

    private static int Log2(int value)
    {
        int result = 0;
        while ((value >>= 1) > 0)
        {
            result++;
        }

        return result;
    }

    private static string SanitizeBaseFontName(string input)
    {
        StringBuilder builder = new(input.Length);
        foreach (char character in input)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void EnsureTableLength(TableRecord table, int requiredLength, string tableName)
    {
        if (table.Length < requiredLength)
        {
            throw new PdfFormatException($"TrueType table '{tableName}' is shorter than expected.");
        }
    }

    private static void EnsureRange(ReadOnlySpan<byte> data, int offset, int length, string context)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
        {
            throw new PdfFormatException($"{context} exceeded available data.");
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        EnsureRange(data, offset, 2, "UInt16 read");
        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset)
    {
        EnsureRange(data, offset, 2, "Int16 read");
        return BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        EnsureRange(data, offset, 4, "UInt32 read");
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    }

    private static void WriteUInt16(Span<byte> data, int offset, ushort value)
    {
        EnsureRange(data, offset, 2, "UInt16 write");
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), value);
    }

    private static void WriteInt16(Span<byte> data, int offset, short value)
    {
        EnsureRange(data, offset, 2, "Int16 write");
        BinaryPrimitives.WriteInt16BigEndian(data.Slice(offset, 2), value);
    }

    private static void WriteUInt32(Span<byte> data, int offset, uint value)
    {
        EnsureRange(data, offset, 4, "UInt32 write");
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(offset, 4), value);
    }
}
