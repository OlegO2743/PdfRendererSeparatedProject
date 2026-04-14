using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace PdfCore.Text;

internal sealed class PdfTrueTypeFont
{
    private readonly byte[] _data;
    private readonly int _glyfOffset;
    private readonly int[] _glyphOffsets;
    private readonly ushort[] _advanceWidths;
    private readonly List<CMapSubtable> _cmapSubtables;

    private PdfTrueTypeFont(byte[] data)
    {
        _data = data;
        Dictionary<string, TableRecord> tables = ReadTableDirectory(data);

        TableRecord head = GetTable(tables, "head");
        TableRecord hhea = GetTable(tables, "hhea");
        TableRecord hmtx = GetTable(tables, "hmtx");
        TableRecord maxp = GetTable(tables, "maxp");
        TableRecord loca = GetTable(tables, "loca");
        TableRecord glyf = GetTable(tables, "glyf");

        UnitsPerEm = ReadUInt16(head.Offset + 18);
        if (UnitsPerEm <= 0)
            UnitsPerEm = 1000;

        int glyphCount = ReadUInt16(maxp.Offset + 4);
        int numberOfHMetrics = Math.Min(ReadUInt16(hhea.Offset + 34), glyphCount);
        int indexToLocFormat = ReadInt16(head.Offset + 50);

        _advanceWidths = ReadAdvanceWidths(hmtx.Offset, glyphCount, numberOfHMetrics);
        _glyphOffsets = ReadGlyphOffsets(loca.Offset, glyphCount, indexToLocFormat);
        _glyfOffset = glyf.Offset;
        _cmapSubtables = tables.TryGetValue("cmap", out TableRecord cmap)
            ? ReadCMapSubtables(cmap)
            : new List<CMapSubtable>();
    }

    public int UnitsPerEm { get; }

    public static bool TryCreate(byte[] bytes, out PdfTrueTypeFont? font)
    {
        try
        {
            font = new PdfTrueTypeFont(bytes);
            return true;
        }
        catch
        {
            font = null;
            return false;
        }
    }

    public float GetAdvanceWidth(int glyphId)
    {
        if (glyphId >= 0 && glyphId < _advanceWidths.Length)
            return _advanceWidths[glyphId];

        return UnitsPerEm / 2f;
    }

    public bool TryMapCharacterCode(int code, out int glyphId)
    {
        foreach (CMapSubtable cmap in EnumerateCharacterCodeCMaps())
        {
            if (cmap.Map.TryGetValue(code, out glyphId) && glyphId > 0)
                return true;
        }

        glyphId = 0;
        return false;
    }

    public bool TryMapUnicode(int codePoint, out int glyphId)
    {
        foreach (CMapSubtable cmap in EnumerateUnicodeCMaps())
        {
            if (cmap.Map.TryGetValue(codePoint, out glyphId) && glyphId > 0)
                return true;
        }

        glyphId = 0;
        return false;
    }

    public void AddGlyphPath(GraphicsPath target, int glyphId, float scale, float xOffset)
    {
        using var transform = new Matrix(scale, 0f, 0f, -scale, xOffset, 0f);
        AddGlyphPath(target, glyphId, transform, depth: 0);
    }

    public void AddGlyphPath(GraphicsPath target, int glyphId, Matrix transform)
        => AddGlyphPath(target, glyphId, transform, depth: 0);

    private void AddGlyphPath(GraphicsPath target, int glyphId, Matrix transform, int depth)
    {
        if (glyphId < 0 || glyphId + 1 >= _glyphOffsets.Length)
            return;

        if (depth > 8)
            return;

        int relativeStart = _glyphOffsets[glyphId];
        int relativeEnd = _glyphOffsets[glyphId + 1];
        if (relativeEnd <= relativeStart)
            return;

        int glyphStart = _glyfOffset + relativeStart;
        int glyphEnd = _glyfOffset + relativeEnd;
        if (glyphStart < 0 || glyphEnd > _data.Length || glyphEnd - glyphStart < 10)
            return;

        short contourCount = ReadInt16(glyphStart);
        if (contourCount >= 0)
            AddSimpleGlyphPath(target, glyphStart, contourCount, transform);
        else
            AddCompositeGlyphPath(target, glyphStart, transform, depth);
    }

    private void AddSimpleGlyphPath(GraphicsPath target, int glyphStart, int contourCount, Matrix transform)
    {
        if (contourCount == 0)
            return;

        int endPtsOffset = glyphStart + 10;
        var endPoints = new int[contourCount];
        for (int i = 0; i < contourCount; i++)
            endPoints[i] = ReadUInt16(endPtsOffset + i * 2);

        int pointCount = endPoints[^1] + 1;
        if (pointCount <= 0)
            return;

        int instructionLengthOffset = endPtsOffset + contourCount * 2;
        int instructionLength = ReadUInt16(instructionLengthOffset);
        int pos = instructionLengthOffset + 2 + instructionLength;
        if (pos >= _data.Length)
            return;

        byte[] flags = ReadFlags(ref pos, pointCount);
        int[] xs = ReadCoordinates(ref pos, pointCount, flags, shortVectorFlag: 0x02, sameOrPositiveFlag: 0x10);
        int[] ys = ReadCoordinates(ref pos, pointCount, flags, shortVectorFlag: 0x04, sameOrPositiveFlag: 0x20);

        int contourStart = 0;
        for (int i = 0; i < contourCount; i++)
        {
            int contourEnd = endPoints[i];
            AddContour(target, xs, ys, flags, contourStart, contourEnd, transform);
            contourStart = contourEnd + 1;
        }
    }

    private void AddCompositeGlyphPath(GraphicsPath target, int glyphStart, Matrix transform, int depth)
    {
        const int Arg1And2AreWords = 0x0001;
        const int ArgsAreXyValues = 0x0002;
        const int WeHaveAScale = 0x0008;
        const int MoreComponents = 0x0020;
        const int WeHaveAnXAndYScale = 0x0040;
        const int WeHaveATwoByTwo = 0x0080;

        int pos = glyphStart + 10;
        bool moreComponents;
        do
        {
            int flags = ReadUInt16(pos);
            int componentGlyphId = ReadUInt16(pos + 2);
            pos += 4;

            int arg1;
            int arg2;
            if ((flags & Arg1And2AreWords) != 0)
            {
                arg1 = ReadInt16(pos);
                arg2 = ReadInt16(pos + 2);
                pos += 4;
            }
            else
            {
                arg1 = ReadInt8(pos);
                arg2 = ReadInt8(pos + 1);
                pos += 2;
            }

            float dx = 0f;
            float dy = 0f;
            if ((flags & ArgsAreXyValues) != 0)
            {
                dx = arg1;
                dy = arg2;
            }

            float a = 1f;
            float b = 0f;
            float c = 0f;
            float d = 1f;

            if ((flags & WeHaveAScale) != 0)
            {
                a = d = ReadF2Dot14(pos);
                pos += 2;
            }
            else if ((flags & WeHaveAnXAndYScale) != 0)
            {
                a = ReadF2Dot14(pos);
                d = ReadF2Dot14(pos + 2);
                pos += 4;
            }
            else if ((flags & WeHaveATwoByTwo) != 0)
            {
                a = ReadF2Dot14(pos);
                b = ReadF2Dot14(pos + 2);
                c = ReadF2Dot14(pos + 4);
                d = ReadF2Dot14(pos + 6);
                pos += 8;
            }

            using var componentTransform = new Matrix(a, b, c, d, dx, dy);
            componentTransform.Multiply(transform, MatrixOrder.Append);
            AddGlyphPath(target, componentGlyphId, componentTransform, depth + 1);

            moreComponents = (flags & MoreComponents) != 0;
        }
        while (moreComponents && pos + 4 <= _data.Length);
    }

    private byte[] ReadFlags(ref int pos, int pointCount)
    {
        var flags = new byte[pointCount];
        int index = 0;
        while (index < pointCount && pos < _data.Length)
        {
            byte flag = _data[pos++];
            flags[index++] = flag;

            if ((flag & 0x08) == 0)
                continue;

            if (pos >= _data.Length)
                break;

            int repeatCount = _data[pos++];
            for (int i = 0; i < repeatCount && index < pointCount; i++)
                flags[index++] = flag;
        }

        return flags;
    }

    private int[] ReadCoordinates(ref int pos, int pointCount, byte[] flags, int shortVectorFlag, int sameOrPositiveFlag)
    {
        var values = new int[pointCount];
        int current = 0;

        for (int i = 0; i < pointCount; i++)
        {
            byte flag = flags[i];
            if ((flag & shortVectorFlag) != 0)
            {
                int delta = pos < _data.Length ? _data[pos++] : 0;
                current += (flag & sameOrPositiveFlag) != 0 ? delta : -delta;
            }
            else if ((flag & sameOrPositiveFlag) == 0)
            {
                current += ReadInt16(pos);
                pos += 2;
            }

            values[i] = current;
        }

        return values;
    }

    private void AddContour(
        GraphicsPath target,
        int[] xs,
        int[] ys,
        byte[] flags,
        int start,
        int end,
        Matrix transform)
    {
        if (start > end)
            return;

        var points = new List<GlyphPoint>(end - start + 1);
        for (int i = start; i <= end; i++)
        {
            PointF point = TransformPoint(transform, xs[i], ys[i]);
            points.Add(new GlyphPoint(point, (flags[i] & 0x01) != 0));
        }

        if (points.Count == 0)
            return;

        PointF contourStart;
        List<GlyphPoint> remaining;

        if (points[0].OnCurve)
        {
            contourStart = points[0].Point;
            remaining = points.Skip(1).ToList();
        }
        else if (points[^1].OnCurve)
        {
            contourStart = points[^1].Point;
            remaining = points.Take(points.Count - 1).ToList();
        }
        else
        {
            contourStart = Midpoint(points[^1].Point, points[0].Point);
            remaining = points;
        }

        target.StartFigure();
        PointF current = contourStart;
        int index = 0;
        while (index < remaining.Count)
        {
            GlyphPoint point = remaining[index];
            if (point.OnCurve)
            {
                target.AddLine(current, point.Point);
                current = point.Point;
                index++;
                continue;
            }

            PointF endPoint;
            if (index + 1 < remaining.Count && remaining[index + 1].OnCurve)
            {
                endPoint = remaining[index + 1].Point;
                index += 2;
            }
            else if (index + 1 < remaining.Count)
            {
                endPoint = Midpoint(point.Point, remaining[index + 1].Point);
                index++;
            }
            else
            {
                endPoint = contourStart;
                index++;
            }

            AddQuadratic(target, current, point.Point, endPoint);
            current = endPoint;
        }

        if (!NearlySame(current, contourStart))
            target.AddLine(current, contourStart);

        target.CloseFigure();
    }

    private static void AddQuadratic(GraphicsPath path, PointF start, PointF control, PointF end)
    {
        PointF c1 = new(
            start.X + (control.X - start.X) * 2f / 3f,
            start.Y + (control.Y - start.Y) * 2f / 3f);
        PointF c2 = new(
            end.X + (control.X - end.X) * 2f / 3f,
            end.Y + (control.Y - end.Y) * 2f / 3f);
        path.AddBezier(start, c1, c2, end);
    }

    private static PointF Midpoint(PointF a, PointF b)
        => new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);

    private static bool NearlySame(PointF a, PointF b)
        => Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f;

    private static PointF TransformPoint(Matrix transform, float x, float y)
    {
        PointF[] points = { new(x, y) };
        transform.TransformPoints(points);
        return points[0];
    }

    private ushort[] ReadAdvanceWidths(int hmtxOffset, int glyphCount, int numberOfHMetrics)
    {
        var widths = new ushort[glyphCount];
        ushort lastAdvance = (ushort)(UnitsPerEm / 2);

        for (int i = 0; i < glyphCount; i++)
        {
            if (i < numberOfHMetrics)
            {
                int metricOffset = hmtxOffset + i * 4;
                lastAdvance = ReadUInt16(metricOffset);
                widths[i] = lastAdvance;
            }
            else
            {
                widths[i] = lastAdvance;
            }
        }

        return widths;
    }

    private int[] ReadGlyphOffsets(int locaOffset, int glyphCount, int indexToLocFormat)
    {
        var offsets = new int[glyphCount + 1];
        for (int i = 0; i <= glyphCount; i++)
        {
            offsets[i] = indexToLocFormat == 0
                ? ReadUInt16(locaOffset + i * 2) * 2
                : checked((int)ReadUInt32(locaOffset + i * 4));
        }

        return offsets;
    }

    private List<CMapSubtable> ReadCMapSubtables(TableRecord cmap)
    {
        int tableCount = ReadUInt16(cmap.Offset + 2);
        var subtables = new List<CMapSubtable>();

        for (int i = 0; i < tableCount; i++)
        {
            int recordOffset = cmap.Offset + 4 + i * 8;
            if (recordOffset + 8 > _data.Length)
                break;

            int platformId = ReadUInt16(recordOffset);
            int encodingId = ReadUInt16(recordOffset + 2);
            int subtableOffset = checked((int)ReadUInt32(recordOffset + 4));
            int subtable = cmap.Offset + subtableOffset;
            if (subtable < cmap.Offset || subtable + 2 > cmap.Offset + cmap.Length || subtable + 2 > _data.Length)
                continue;

            int format = ReadUInt16(subtable);
            Dictionary<int, int>? map = format switch
            {
                0 => ReadCMapFormat0(subtable),
                4 => ReadCMapFormat4(subtable),
                6 => ReadCMapFormat6(subtable),
                12 => ReadCMapFormat12(subtable),
                _ => null
            };

            if (map != null && map.Count > 0)
                subtables.Add(new CMapSubtable(platformId, encodingId, format, map));
        }

        return subtables;
    }

    private IEnumerable<CMapSubtable> EnumerateCharacterCodeCMaps()
    {
        foreach (CMapSubtable cmap in _cmapSubtables
                     .OrderByDescending(c => c.PlatformId == 3 && c.EncodingId == 0)
                     .ThenByDescending(c => c.PlatformId == 1)
                     .ThenByDescending(c => c.PlatformId == 0)
                     .ThenByDescending(c => c.PlatformId == 3 && c.EncodingId == 1)
                     .ThenByDescending(c => c.PlatformId == 3 && c.EncodingId == 10))
        {
            yield return cmap;
        }
    }

    private IEnumerable<CMapSubtable> EnumerateUnicodeCMaps()
    {
        foreach (CMapSubtable cmap in _cmapSubtables
                     .OrderByDescending(c => c.PlatformId == 3 && c.EncodingId == 10)
                     .ThenByDescending(c => c.PlatformId == 3 && c.EncodingId == 1)
                     .ThenByDescending(c => c.PlatformId == 0)
                     .ThenByDescending(c => c.PlatformId == 3 && c.EncodingId == 0)
                     .ThenByDescending(c => c.PlatformId == 1))
        {
            yield return cmap;
        }
    }

    private Dictionary<int, int> ReadCMapFormat0(int subtable)
    {
        int length = ReadUInt16(subtable + 2);
        var map = new Dictionary<int, int>();
        int glyphArray = subtable + 6;
        int count = Math.Min(256, Math.Max(0, length - 6));
        for (int code = 0; code < count && glyphArray + code < _data.Length; code++)
        {
            int glyphId = _data[glyphArray + code];
            if (glyphId > 0)
                map[code] = glyphId;
        }

        return map;
    }

    private Dictionary<int, int> ReadCMapFormat6(int subtable)
    {
        int firstCode = ReadUInt16(subtable + 6);
        int entryCount = ReadUInt16(subtable + 8);
        var map = new Dictionary<int, int>();
        int glyphArray = subtable + 10;
        for (int i = 0; i < entryCount && i < 65536; i++)
        {
            int glyphId = ReadUInt16(glyphArray + i * 2);
            if (glyphId > 0)
                map[firstCode + i] = glyphId;
        }

        return map;
    }

    private Dictionary<int, int> ReadCMapFormat4(int subtable)
    {
        int segCount = ReadUInt16(subtable + 6) / 2;
        var map = new Dictionary<int, int>();
        int endCodeOffset = subtable + 14;
        int startCodeOffset = endCodeOffset + segCount * 2 + 2;
        int idDeltaOffset = startCodeOffset + segCount * 2;
        int idRangeOffsetOffset = idDeltaOffset + segCount * 2;

        for (int i = 0; i < segCount; i++)
        {
            int endCode = ReadUInt16(endCodeOffset + i * 2);
            int startCode = ReadUInt16(startCodeOffset + i * 2);
            int idDelta = ReadInt16(idDeltaOffset + i * 2);
            int idRangeOffset = ReadUInt16(idRangeOffsetOffset + i * 2);

            if (startCode == 0xFFFF && endCode == 0xFFFF)
                continue;
            if (endCode < startCode)
                continue;

            for (int code = startCode; code <= endCode && code <= 0xFFFF; code++)
            {
                int glyphId;
                if (idRangeOffset == 0)
                {
                    glyphId = (code + idDelta) & 0xFFFF;
                }
                else
                {
                    int glyphIndexOffset = idRangeOffsetOffset + i * 2 + idRangeOffset + (code - startCode) * 2;
                    glyphId = ReadUInt16(glyphIndexOffset);
                    if (glyphId > 0)
                        glyphId = (glyphId + idDelta) & 0xFFFF;
                }

                if (glyphId > 0)
                    map[code] = glyphId;
            }
        }

        return map;
    }

    private Dictionary<int, int> ReadCMapFormat12(int subtable)
    {
        uint groupCount = ReadUInt32(subtable + 12);
        var map = new Dictionary<int, int>();
        int groupsOffset = subtable + 16;

        for (uint group = 0; group < groupCount && group < 65536; group++)
        {
            int offset = groupsOffset + checked((int)group) * 12;
            uint startCode = ReadUInt32(offset);
            uint endCode = ReadUInt32(offset + 4);
            uint startGlyphId = ReadUInt32(offset + 8);

            if (endCode < startCode || endCode - startCode > 65536)
                continue;

            for (uint code = startCode; code <= endCode; code++)
            {
                uint glyphId = startGlyphId + (code - startCode);
                if (glyphId > 0 && glyphId <= int.MaxValue && code <= int.MaxValue)
                    map[(int)code] = (int)glyphId;
            }
        }

        return map;
    }

    private static Dictionary<string, TableRecord> ReadTableDirectory(byte[] data)
    {
        if (data.Length < 12)
            throw new InvalidDataException("Invalid TrueType font.");

        int tableCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
        var tables = new Dictionary<string, TableRecord>(StringComparer.Ordinal);

        for (int i = 0; i < tableCount; i++)
        {
            int recordOffset = 12 + i * 16;
            if (recordOffset + 16 > data.Length)
                break;

            string tag = System.Text.Encoding.ASCII.GetString(data, recordOffset, 4);
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(recordOffset + 8, 4)));
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(recordOffset + 12, 4)));
            if (offset >= 0 && length >= 0 && offset + length <= data.Length)
                tables[tag] = new TableRecord(offset, length);
        }

        return tables;
    }

    private static TableRecord GetTable(Dictionary<string, TableRecord> tables, string tag)
    {
        if (!tables.TryGetValue(tag, out TableRecord table))
            throw new InvalidDataException("Missing TrueType table " + tag + ".");

        return table;
    }

    private ushort ReadUInt16(int offset)
        => offset >= 0 && offset + 2 <= _data.Length
            ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2))
            : (ushort)0;

    private uint ReadUInt32(int offset)
        => offset >= 0 && offset + 4 <= _data.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4))
            : 0u;

    private short ReadInt16(int offset)
        => offset >= 0 && offset + 2 <= _data.Length
            ? BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset, 2))
            : (short)0;

    private sbyte ReadInt8(int offset)
        => offset >= 0 && offset < _data.Length
            ? unchecked((sbyte)_data[offset])
            : (sbyte)0;

    private float ReadF2Dot14(int offset)
        => ReadInt16(offset) / 16384f;

    private sealed record CMapSubtable(int PlatformId, int EncodingId, int Format, Dictionary<int, int> Map);
    private readonly record struct TableRecord(int Offset, int Length);
    private readonly record struct GlyphPoint(PointF Point, bool OnCurve);
}
