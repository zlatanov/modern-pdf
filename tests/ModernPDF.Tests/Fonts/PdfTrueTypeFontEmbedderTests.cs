using System.Reflection;
using ModernPDF.Fonts;
using ModernPDF.Format;

namespace ModernPDF.Tests;

public sealed class PdfTrueTypeFontEmbedderTests
{
    [Fact]
    public void BuildThrowsWhenFontFileIsMissing()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ttf");

        Assert.Throws<FileNotFoundException>(() => PdfTrueTypeFontEmbedder.Build(missingPath, "abc", subsetFont: true, direction: PdfTextDirection.Auto));
    }

    [Fact]
    public void BuildThrowsForTooShortFontData()
    {
        string path = WriteTempFontFile([1, 2, 3]);
        try
        {
            Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "A", subsetFont: true, direction: PdfTextDirection.Auto));
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsForUnsupportedSfntVersion()
    {
        byte[] bytes = new byte[12];
        WriteUInt32(bytes, 0, 0xDEADBEEF);
        WriteUInt16(bytes, 4, 0);

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "A", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("Unsupported font format", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsForTruncatedTableDirectory()
    {
        byte[] bytes = new byte[12];
        WriteUInt32(bytes, 0, 0x00010000);
        WriteUInt16(bytes, 4, 1);

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "A", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("table directory is truncated", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsWhenTableEntryIsOutOfRange()
    {
        byte[] bytes = new byte[28];
        WriteUInt32(bytes, 0, 0x00010000);
        WriteUInt16(bytes, 4, 1);
        WriteAscii(bytes, 12, "head");
        WriteUInt32(bytes, 20, 1000);
        WriteUInt32(bytes, 24, 54);

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "A", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("is out of range", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsWhenRequiredTableIsMissing()
    {
        byte[] bytes = new byte[220];
        WriteUInt32(bytes, 0, 0x00010000);
        WriteUInt16(bytes, 4, 1);
        WriteAscii(bytes, 12, "head");
        WriteUInt32(bytes, 20, 100);
        WriteUInt32(bytes, 24, 54);

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "A", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("missing required table", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsForUnsupportedIndexToLocFormat()
    {
        byte[] bytes = LoadFixtureFontBytes();
        TableDirectoryEntry head = GetRequiredTable(bytes, "head");
        WriteInt16(bytes, head.Offset + 50, 2);

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "abc", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("indexToLocFormat", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsWhenCmapHasNoSupportedFormat4Subtable()
    {
        byte[] bytes = LoadFixtureFontBytes();
        TableDirectoryEntry cmap = GetRequiredTable(bytes, "cmap");

        ushort cmapTableCount = ReadUInt16(bytes, cmap.Offset + 2);
        for (int index = 0; index < cmapTableCount; index++)
        {
            int recordOffset = cmap.Offset + 4 + index * 8;
            uint subOffset = ReadUInt32(bytes, recordOffset + 4);
            int subtableOffset = checked(cmap.Offset + (int)subOffset);
            if (subtableOffset + 2 <= bytes.Length)
            {
                WriteUInt16(bytes, subtableOffset, 0);
            }
        }

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "abc", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("supported format 4", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsWhenCmapSegmentCountIsInvalid()
    {
        byte[] bytes = LoadFixtureFontBytes();
        TableDirectoryEntry cmap = GetRequiredTable(bytes, "cmap");

        int format4Offset = FindFirstCmapFormat4SubtableOffset(bytes, cmap);
        Assert.True(format4Offset > 0);
        WriteUInt16(bytes, format4Offset + 6, 1);

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "abc", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("segment count", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsWhenCmapSubtableOffsetIsOutOfRange()
    {
        byte[] bytes = LoadFixtureFontBytes();
        TableDirectoryEntry cmap = GetRequiredTable(bytes, "cmap");

        WriteUInt32(bytes, cmap.Offset + 8, (uint)(bytes.Length + 1024));

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "abc", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("cmap subtable exceeded available data", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsWhenHeadTableLengthIsTooShort()
    {
        byte[] bytes = LoadFixtureFontBytes();
        TableDirectoryEntry head = GetRequiredTable(bytes, "head");
        WriteUInt32(bytes, head.RecordOffset + 12, 10);

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "abc", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("head", exception.Message, StringComparison.Ordinal);
            Assert.Contains("shorter than expected", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildSupportsNonBmpCharacters()
    {
        string fontPath = GetFixtureFontPath();
        PdfEmbeddedTrueTypeFont embedded = PdfTrueTypeFontEmbedder.Build(fontPath, "emoji-\U0001F600", subsetFont: true, direction: PdfTextDirection.Auto);

        Assert.NotEmpty(embedded.GlyphRun);
        Assert.NotEmpty(embedded.CidToGlyphId);
        Assert.Contains(embedded.CidToUnicode.Values, static value => string.Equals(value, "\U0001F600", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildUsesSanitizedFileNameWhenNameTableIsMissing()
    {
        byte[] bytes = LoadFixtureFontBytes();
        TableDirectoryEntry name = GetRequiredTable(bytes, "name");
        WriteAscii(bytes, name.RecordOffset, "zzzz");

        string path = WriteTempFontFile(bytes, "Fallback Font#1.ttf");
        try
        {
            PdfEmbeddedTrueTypeFont embedded = PdfTrueTypeFontEmbedder.Build(path, "ABC", subsetFont: false, direction: PdfTextDirection.Auto);
            Assert.Equal("FallbackFont1", embedded.BaseFontName);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildThrowsWhenLocaOffsetsAreDescending()
    {
        byte[] bytes = LoadFixtureFontBytes();
        TableDirectoryEntry head = GetRequiredTable(bytes, "head");
        TableDirectoryEntry loca = GetRequiredTable(bytes, "loca");
        short indexToLocFormat = ReadInt16(bytes, head.Offset + 50);

        if (indexToLocFormat == 0)
        {
            WriteUInt16(bytes, loca.Offset, 10);
            WriteUInt16(bytes, loca.Offset + 2, 5);
        }
        else
        {
            WriteUInt32(bytes, loca.Offset, 100);
            WriteUInt32(bytes, loca.Offset + 4, 20);
        }

        string path = WriteTempFontFile(bytes);
        try
        {
            PdfFormatException exception = Assert.Throws<PdfFormatException>(() => PdfTrueTypeFontEmbedder.Build(path, "A", subsetFont: true, direction: PdfTextDirection.Auto));
            Assert.Contains("Invalid glyph offset ordering", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void BuildWithSubsetProcessesCompositeGlyphCharacters()
    {
        byte[] bytes = LoadFixtureFontBytes();
        List<char> compositeChars = FindCompositeBmpCharacters(bytes, 4);

        Assert.NotEmpty(compositeChars);

        string path = WriteTempFontFile(bytes);
        try
        {
            string text = new(compositeChars.ToArray());
            PdfEmbeddedTrueTypeFont embedded = PdfTrueTypeFontEmbedder.Build(path, text, subsetFont: true, direction: PdfTextDirection.Auto);

            Assert.Contains("+", embedded.BaseFontName, StringComparison.Ordinal);
            Assert.Equal(text.Length, embedded.UnicodeToGlyphId.Count);
            Assert.NotEmpty(embedded.FontProgram);
        }
        finally
        {
            DeleteTempFontFile(path);
        }
    }

    [Fact]
    public void RewriteCompositeGlyphReferencesThrowsForUnknownComponentGlyph()
    {
        MethodInfo rewrite = GetTrueTypePrivateStaticMethod("RewriteCompositeGlyphReferences");
        byte[] compositeGlyph = BuildCompositeGlyph(
            firstFlags: 0x0021,
            firstGlyphId: 3,
            secondFlags: 0x0000,
            secondGlyphId: 7);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => rewrite.Invoke(null, [compositeGlyph, new Dictionary<int, ushort>()]));
        Assert.IsType<PdfFormatException>(exception.InnerException);
    }

    [Fact]
    public void RewriteCompositeGlyphReferencesRewritesGlyphIdsAcrossFlagBranches()
    {
        MethodInfo rewrite = GetTrueTypePrivateStaticMethod("RewriteCompositeGlyphReferences");
        byte[] compositeGlyph = BuildCompositeGlyph(
            firstFlags: 0x0029, // MORE_COMPONENTS + ARG_1_AND_2_ARE_WORDS + WE_HAVE_A_SCALE
            firstGlyphId: 3,
            secondFlags: 0x0080, // WE_HAVE_A_TWO_BY_TWO
            secondGlyphId: 7);

        Dictionary<int, ushort> oldToNew = new()
        {
            [3] = 30,
            [7] = 70,
        };

        byte[] rewritten = (byte[])rewrite.Invoke(null, [compositeGlyph, oldToNew])!;

        Assert.Equal((byte)0x00, rewritten[12]);
        Assert.Equal((byte)0x1E, rewritten[13]);
        Assert.Equal((byte)0x00, rewritten[22]);
        Assert.Equal((byte)0x46, rewritten[23]);
    }

    [Fact]
    public void RewriteCompositeGlyphReferencesReturnsOriginalForShortAndSimpleGlyphs()
    {
        MethodInfo rewrite = GetTrueTypePrivateStaticMethod("RewriteCompositeGlyphReferences");

        byte[] shortGlyph = [1, 2, 3];
        byte[] shortResult = (byte[])rewrite.Invoke(null, [shortGlyph, new Dictionary<int, ushort>()])!;
        Assert.Same(shortGlyph, shortResult);

        byte[] simpleGlyph = new byte[12];
        WriteInt16(simpleGlyph, 0, 1);
        byte[] simpleResult = (byte[])rewrite.Invoke(null, [simpleGlyph, new Dictionary<int, ushort>()])!;
        Assert.Same(simpleGlyph, simpleResult);
    }

    [Fact]
    public void CmapGetGlyphIdHandlesDeltaAndRangeOffsetCases()
    {
        Type cmapType = GetPrivateType("ModernPDF.Fonts.PdfTrueTypeFontEmbedder+CmapFormat4");
        ConstructorInfo ctor = cmapType.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [
                typeof(byte[]),
                typeof(ushort[]),
                typeof(ushort[]),
                typeof(short[]),
                typeof(ushort[]),
                typeof(int),
            ],
            modifiers: null)!;
        MethodInfo getGlyph = cmapType.GetMethod("GetGlyphId", BindingFlags.Public | BindingFlags.Instance)!;

        object deltaMapped = ctor.Invoke(
        [
            new byte[16],
            new ushort[] { 10 },
            new ushort[] { 5 },
            new short[] { 1 },
            new ushort[] { 0 },
            0,
        ]);

        Assert.Equal((ushort)0, (ushort)getGlyph.Invoke(deltaMapped, [4])!);
        Assert.Equal((ushort)7, (ushort)getGlyph.Invoke(deltaMapped, [6])!);
        Assert.Equal((ushort)0, (ushort)getGlyph.Invoke(deltaMapped, [0x110000])!);

        byte[] subtable = new byte[10];
        WriteUInt16(subtable, 4, 3);
        object rangeMapped = ctor.Invoke(
        [
            subtable,
            new ushort[] { 2 },
            new ushort[] { 1 },
            new short[] { 0 },
            new ushort[] { 4 },
            0,
        ]);
        Assert.Equal((ushort)3, (ushort)getGlyph.Invoke(rangeMapped, [1])!);

        object outOfRange = ctor.Invoke(
        [
            new byte[4],
            new ushort[] { 2 },
            new ushort[] { 1 },
            new short[] { 0 },
            new ushort[] { 4 },
            0,
        ]);
        Assert.Equal((ushort)0, (ushort)getGlyph.Invoke(outOfRange, [1])!);
    }

    private static MethodInfo GetTrueTypePrivateStaticMethod(string methodName)
    {
        Type trueTypeType = GetPrivateType("ModernPDF.Fonts.PdfTrueTypeFontEmbedder+TrueTypeFont");
        return trueTypeType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
    }

    private static Type GetPrivateType(string fullName)
    {
        return typeof(PdfTrueTypeFontEmbedder).Assembly.GetType(fullName, throwOnError: true)!;
    }

    private static byte[] BuildCompositeGlyph(ushort firstFlags, ushort firstGlyphId, ushort secondFlags, ushort secondGlyphId)
    {
        List<byte> bytes = [];
        bytes.Add(0xFF);
        bytes.Add(0xFF);
        bytes.AddRange([0, 0, 0, 0, 0, 0, 0, 0]);

        AppendUInt16(bytes, firstFlags);
        AppendUInt16(bytes, firstGlyphId);
        if ((firstFlags & 0x0001) != 0)
        {
            bytes.AddRange([0, 0, 0, 0]);
        }
        else
        {
            bytes.AddRange([0, 0]);
        }

        AppendTransformBytes(bytes, firstFlags);

        AppendUInt16(bytes, secondFlags);
        AppendUInt16(bytes, secondGlyphId);
        if ((secondFlags & 0x0001) != 0)
        {
            bytes.AddRange([0, 0, 0, 0]);
        }
        else
        {
            bytes.AddRange([0, 0]);
        }

        AppendTransformBytes(bytes, secondFlags);
        return bytes.ToArray();
    }

    private static void AppendTransformBytes(List<byte> bytes, ushort flags)
    {
        if ((flags & 0x0008) != 0)
        {
            bytes.AddRange([0, 0]);
            return;
        }

        if ((flags & 0x0040) != 0)
        {
            bytes.AddRange([0, 0, 0, 0]);
            return;
        }

        if ((flags & 0x0080) != 0)
        {
            bytes.AddRange([0, 0, 0, 0, 0, 0, 0, 0]);
        }
    }

    private static void AppendUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static string GetFixtureFontPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fonts", "LiberationSans-Regular.ttf");
        Assert.True(File.Exists(path), $"Expected test font fixture at '{path}'.");
        return path;
    }

    private static byte[] LoadFixtureFontBytes()
    {
        return File.ReadAllBytes(GetFixtureFontPath());
    }

    private static TableDirectoryEntry GetRequiredTable(byte[] bytes, string tag)
    {
        Dictionary<string, TableDirectoryEntry> directory = ReadTableDirectory(bytes);
        Assert.True(directory.TryGetValue(tag, out TableDirectoryEntry entry), $"Expected table '{tag}' in fixture font.");
        return entry;
    }

    private static Dictionary<string, TableDirectoryEntry> ReadTableDirectory(byte[] bytes)
    {
        ushort count = ReadUInt16(bytes, 4);
        Dictionary<string, TableDirectoryEntry> map = new(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            int recordOffset = 12 + index * 16;
            string tag = ReadAscii(bytes, recordOffset, 4);
            int offset = checked((int)ReadUInt32(bytes, recordOffset + 8));
            int length = checked((int)ReadUInt32(bytes, recordOffset + 12));
            map[tag] = new TableDirectoryEntry(tag, recordOffset, offset, length);
        }

        return map;
    }

    private static int FindFirstCmapFormat4SubtableOffset(byte[] bytes, TableDirectoryEntry cmap)
    {
        ushort count = ReadUInt16(bytes, cmap.Offset + 2);
        for (int index = 0; index < count; index++)
        {
            int recordOffset = cmap.Offset + 4 + index * 8;
            int subtableOffset = checked(cmap.Offset + (int)ReadUInt32(bytes, recordOffset + 4));
            if (subtableOffset + 2 > bytes.Length)
            {
                continue;
            }

            if (ReadUInt16(bytes, subtableOffset) == 4)
            {
                return subtableOffset;
            }
        }

        return -1;
    }

    private static List<char> FindCompositeBmpCharacters(byte[] bytes, int maxCount)
    {
        TableDirectoryEntry head = GetRequiredTable(bytes, "head");
        TableDirectoryEntry loca = GetRequiredTable(bytes, "loca");
        TableDirectoryEntry glyf = GetRequiredTable(bytes, "glyf");
        TableDirectoryEntry cmap = GetRequiredTable(bytes, "cmap");
        TableDirectoryEntry maxp = GetRequiredTable(bytes, "maxp");

        ushort glyphCount = ReadUInt16(bytes, maxp.Offset + 4);
        short indexToLocFormat = ReadInt16(bytes, head.Offset + 50);
        uint[] offsets = ReadLocaOffsets(bytes, loca, glyphCount, indexToLocFormat);
        Dictionary<int, ushort> cmapMap = ParseFormat4CmapToGlyphMap(bytes, cmap);

        List<char> result = [];
        foreach ((int codePoint, ushort glyphId) in cmapMap.OrderBy(static item => item.Key))
        {
            if (result.Count >= maxCount)
            {
                break;
            }

            if (codePoint < char.MinValue || codePoint > char.MaxValue)
            {
                continue;
            }

            if (glyphId == 0 || glyphId >= offsets.Length - 1)
            {
                continue;
            }

            uint start = offsets[glyphId];
            uint end = offsets[glyphId + 1];
            if (end <= start)
            {
                continue;
            }

            int absolute = checked(glyf.Offset + (int)start);
            if (absolute + 2 > bytes.Length)
            {
                continue;
            }

            short contours = ReadInt16(bytes, absolute);
            if (contours == -1)
            {
                result.Add((char)codePoint);
            }
        }

        return result;
    }

    private static uint[] ReadLocaOffsets(byte[] bytes, TableDirectoryEntry loca, ushort glyphCount, short indexToLocFormat)
    {
        uint[] offsets = new uint[glyphCount + 1];
        if (indexToLocFormat == 0)
        {
            for (int index = 0; index < offsets.Length; index++)
            {
                offsets[index] = (uint)(ReadUInt16(bytes, loca.Offset + index * 2) * 2);
            }

            return offsets;
        }

        for (int index = 0; index < offsets.Length; index++)
        {
            offsets[index] = ReadUInt32(bytes, loca.Offset + index * 4);
        }

        return offsets;
    }

    private static Dictionary<int, ushort> ParseFormat4CmapToGlyphMap(byte[] bytes, TableDirectoryEntry cmap)
    {
        int format4Offset = FindFirstCmapFormat4SubtableOffset(bytes, cmap);
        if (format4Offset < 0)
        {
            return [];
        }

        ushort segCountX2 = ReadUInt16(bytes, format4Offset + 6);
        int segCount = segCountX2 / 2;
        int endCodeOffset = format4Offset + 14;
        int startCodeOffset = endCodeOffset + segCount * 2 + 2;
        int idDeltaOffset = startCodeOffset + segCount * 2;
        int idRangeOffsetOffset = idDeltaOffset + segCount * 2;

        Dictionary<int, ushort> map = [];
        for (int segment = 0; segment < segCount; segment++)
        {
            int start = ReadUInt16(bytes, startCodeOffset + segment * 2);
            int end = ReadUInt16(bytes, endCodeOffset + segment * 2);
            short delta = ReadInt16(bytes, idDeltaOffset + segment * 2);
            ushort idRangeOffset = ReadUInt16(bytes, idRangeOffsetOffset + segment * 2);

            if (start == 0xFFFF && end == 0xFFFF)
            {
                continue;
            }

            for (int code = start; code <= end; code++)
            {
                ushort glyph;
                if (idRangeOffset == 0)
                {
                    glyph = (ushort)((code + delta) & 0xFFFF);
                }
                else
                {
                    int glyphAddress =
                        idRangeOffsetOffset
                        + segment * 2
                        + idRangeOffset
                        + (code - start) * 2;
                    if (glyphAddress + 2 > bytes.Length)
                    {
                        continue;
                    }

                    glyph = ReadUInt16(bytes, glyphAddress);
                    if (glyph != 0)
                    {
                        glyph = (ushort)((glyph + delta) & 0xFFFF);
                    }
                }

                map[code] = glyph;
            }
        }

        return map;
    }

    private static string WriteTempFontFile(byte[] bytes, string fileName = "temp-font.ttf")
    {
        string directory = Path.Combine(Path.GetTempPath(), "ModernPdfFontTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void DeleteTempFontFile(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24)
            | ((uint)bytes[offset + 1] << 16)
            | ((uint)bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    private static void WriteInt16(byte[] bytes, int offset, short value)
    {
        WriteUInt16(bytes, offset, unchecked((ushort)value));
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private static short ReadInt16(byte[] bytes, int offset)
    {
        return unchecked((short)ReadUInt16(bytes, offset));
    }

    private static void WriteAscii(byte[] bytes, int offset, string text)
    {
        byte[] encoded = System.Text.Encoding.ASCII.GetBytes(text);
        encoded.CopyTo(bytes, offset);
    }

    private static string ReadAscii(byte[] bytes, int offset, int length)
    {
        return System.Text.Encoding.ASCII.GetString(bytes, offset, length);
    }

    private readonly record struct TableDirectoryEntry(string Tag, int RecordOffset, int Offset, int Length);
}
