using System.Text;

namespace PdfCore.Images.Jpeg2000;

internal sealed class Jpeg2000Codestream
{
    private Jpeg2000Codestream(
        byte[] source,
        int codestreamOffset,
        int codestreamLength,
        bool isJp2Container,
        Jpeg2000Size size,
        Jpeg2000CodingStyle codingStyle,
        Jpeg2000Quantization quantization,
        IReadOnlyDictionary<int, Jpeg2000Quantization> componentQuantizations,
        IReadOnlyList<Jpeg2000TilePart> tileParts)
    {
        Source = source;
        CodestreamOffset = codestreamOffset;
        CodestreamLength = codestreamLength;
        IsJp2Container = isJp2Container;
        Size = size;
        CodingStyle = codingStyle;
        Quantization = quantization;
        ComponentQuantizations = componentQuantizations;
        TileParts = tileParts;
    }

    public byte[] Source { get; }
    public int CodestreamOffset { get; }
    public int CodestreamLength { get; }
    public bool IsJp2Container { get; }
    public Jpeg2000Size Size { get; }
    public Jpeg2000CodingStyle CodingStyle { get; }
    public Jpeg2000Quantization Quantization { get; }
    public IReadOnlyDictionary<int, Jpeg2000Quantization> ComponentQuantizations { get; }
    public IReadOnlyList<Jpeg2000TilePart> TileParts { get; }

    public static Jpeg2000Codestream Parse(byte[] data)
    {
        int codestreamOffset = FindCodestreamOffset(data, out int codestreamLength, out bool isJp2);
        if (codestreamOffset < 0 || codestreamLength < 2)
            throw new InvalidOperationException("JPX stream does not contain a JPEG2000 codestream.");

        int codestreamEnd = codestreamOffset + codestreamLength;
        var reader = new Jpeg2000BigEndianReader(data, codestreamOffset, codestreamEnd);
        reader.ExpectMarker(0x4F); // SOC

        Jpeg2000Size? size = null;
        Jpeg2000CodingStyle? defaultCodingStyle = null;
        Jpeg2000Quantization? defaultQuantization = null;
        var componentQuantizations = new Dictionary<int, Jpeg2000Quantization>();
        var tileParts = new List<Jpeg2000TilePart>();

        while (reader.HasBytes(2))
        {
            int markerStart = reader.Position;
            byte marker = reader.ReadMarker();

            if (marker == 0xD9) // EOC
                break;

            if (marker == 0x90) // SOT
            {
                tileParts.Add(ReadTilePart(data, ref reader, codestreamEnd, markerStart, defaultCodingStyle, defaultQuantization));
                continue;
            }

            Jpeg2000MarkerSegment segment = reader.ReadMarkerSegment(marker, markerStart);
            switch (marker)
            {
                case 0x51:
                    size = ReadSize(segment);
                    break;

                case 0x52:
                    defaultCodingStyle = ReadCodingStyle(segment);
                    break;

                case 0x5C:
                    defaultQuantization = ReadQuantization(segment, hasComponentIndex: false, componentCount: size?.Components.Count ?? 0).Quantization;
                    break;

                case 0x5D:
                    Jpeg2000ComponentQuantization componentQuantization = ReadQuantization(
                        segment,
                        hasComponentIndex: true,
                        componentCount: size?.Components.Count ?? 0);
                    componentQuantizations[componentQuantization.ComponentIndex] = componentQuantization.Quantization;
                    break;
            }
        }

        if (size == null)
            throw new InvalidOperationException("JPEG2000 codestream does not contain SIZ marker.");

        if (defaultCodingStyle == null)
            throw new InvalidOperationException("JPEG2000 codestream does not contain COD marker.");

        if (defaultQuantization == null)
            throw new InvalidOperationException("JPEG2000 codestream does not contain QCD marker.");

        return new Jpeg2000Codestream(
            data,
            codestreamOffset,
            codestreamLength,
            isJp2,
            size,
            defaultCodingStyle,
            defaultQuantization,
            componentQuantizations,
            tileParts);
    }

    public Jpeg2000Quantization GetQuantization(int componentIndex)
        => ComponentQuantizations.TryGetValue(componentIndex, out Jpeg2000Quantization? quantization)
            ? quantization
            : Quantization;

    private static Jpeg2000TilePart ReadTilePart(
        byte[] data,
        ref Jpeg2000BigEndianReader reader,
        int codestreamEnd,
        int markerStart,
        Jpeg2000CodingStyle? defaultCodingStyle,
        Jpeg2000Quantization? defaultQuantization)
    {
        Jpeg2000MarkerSegment sot = reader.ReadMarkerSegment(0x90, markerStart);
        var sotReader = new Jpeg2000SegmentReader(sot);
        ushort tileIndex = sotReader.ReadUInt16();
        uint tilePartLength = sotReader.ReadUInt32();
        byte tilePartIndex = sotReader.ReadByte();
        byte tilePartCount = sotReader.ReadByte();

        int declaredTilePartEnd = tilePartLength == 0
            ? -1
            : checked(markerStart + (int)tilePartLength);

        if (declaredTilePartEnd > codestreamEnd || (declaredTilePartEnd >= 0 && declaredTilePartEnd < reader.Position))
            throw new InvalidOperationException("JPEG2000 tile-part length points outside codestream.");

        Jpeg2000CodingStyle? tileCodingStyle = null;
        Jpeg2000Quantization? tileQuantization = null;
        var tileComponentQuantizations = new Dictionary<int, Jpeg2000Quantization>();

        int headerStart = reader.Position;
        int dataStart = -1;
        while (reader.Position < (declaredTilePartEnd >= 0 ? declaredTilePartEnd : codestreamEnd))
        {
            int tileMarkerStart = reader.Position;
            byte marker = reader.ReadMarker();
            if (marker == 0x93) // SOD
            {
                dataStart = reader.Position;
                break;
            }

            Jpeg2000MarkerSegment segment = reader.ReadMarkerSegment(marker, tileMarkerStart);
            switch (marker)
            {
                case 0x52:
                    tileCodingStyle = ReadCodingStyle(segment);
                    break;

                case 0x5C:
                    tileQuantization = ReadQuantization(segment, hasComponentIndex: false, componentCount: 0).Quantization;
                    break;

                case 0x5D:
                    Jpeg2000ComponentQuantization componentQuantization = ReadQuantization(segment, hasComponentIndex: true, componentCount: 0);
                    tileComponentQuantizations[componentQuantization.ComponentIndex] = componentQuantization.Quantization;
                    break;
            }
        }

        if (dataStart < 0)
            throw new InvalidOperationException("JPEG2000 tile-part does not contain SOD marker.");

        int tilePartEnd = ResolveTilePartEnd(
            data,
            dataStart,
            codestreamEnd,
            declaredTilePartEnd);

        int dataLength = tilePartEnd - dataStart;
        if (dataLength < 0)
            throw new InvalidOperationException("JPEG2000 tile-part has invalid data length.");

        reader.Position = tilePartEnd;

        return new Jpeg2000TilePart(
            tileIndex,
            tilePartIndex,
            tilePartCount,
            markerStart,
            tilePartEnd - markerStart,
            headerStart,
            dataStart,
            dataLength,
            tileCodingStyle ?? defaultCodingStyle,
            tileQuantization ?? defaultQuantization,
            tileComponentQuantizations);
    }

    private static int ResolveTilePartEnd(byte[] data, int dataStart, int codestreamEnd, int declaredTilePartEnd)
    {
        if (declaredTilePartEnd >= 0 &&
            declaredTilePartEnd <= codestreamEnd &&
            (declaredTilePartEnd == codestreamEnd || IsTileBoundaryMarker(data, declaredTilePartEnd, codestreamEnd)))
        {
            return declaredTilePartEnd;
        }

        int nextTileBoundary = FindNextTileBoundaryMarker(data, dataStart, codestreamEnd);
        if (nextTileBoundary >= 0)
            return nextTileBoundary;

        if (declaredTilePartEnd >= 0 && declaredTilePartEnd <= codestreamEnd)
            return declaredTilePartEnd;

        return codestreamEnd;
    }

    private static bool IsTileBoundaryMarker(byte[] data, int offset, int end)
    {
        if (offset < 0 || offset + 1 >= end || data[offset] != 0xFF)
            return false;

        int markerOffset = offset + 1;
        while (markerOffset < end && data[markerOffset] == 0xFF)
            markerOffset++;

        if (markerOffset >= end)
            return false;

        byte marker = data[markerOffset];
        return marker == 0x90 || marker == 0xD9;
    }

    private static int FindNextTileBoundaryMarker(byte[] data, int start, int end)
    {
        for (int i = Math.Max(0, start); i + 1 < end; i++)
        {
            if (data[i] != 0xFF)
                continue;

            int markerOffset = i + 1;
            while (markerOffset < end && data[markerOffset] == 0xFF)
                markerOffset++;

            if (markerOffset >= end)
                return -1;

            byte marker = data[markerOffset];
            if (marker == 0x00)
            {
                i = markerOffset;
                continue;
            }

            if (marker == 0x90 || marker == 0xD9)
                return i;
        }

        return -1;
    }

    private static Jpeg2000Size ReadSize(Jpeg2000MarkerSegment segment)
    {
        var reader = new Jpeg2000SegmentReader(segment);
        ushort capabilities = reader.ReadUInt16();
        uint xsiz = reader.ReadUInt32();
        uint ysiz = reader.ReadUInt32();
        uint xosiz = reader.ReadUInt32();
        uint yosiz = reader.ReadUInt32();
        uint xtsiz = reader.ReadUInt32();
        uint ytsiz = reader.ReadUInt32();
        uint xtosiz = reader.ReadUInt32();
        uint ytosiz = reader.ReadUInt32();
        ushort components = reader.ReadUInt16();

        var componentSizes = new List<Jpeg2000ComponentSize>(components);
        for (int i = 0; i < components; i++)
        {
            byte ssiz = reader.ReadByte();
            byte xrsiz = reader.ReadByte();
            byte yrsiz = reader.ReadByte();
            componentSizes.Add(new Jpeg2000ComponentSize(
                (ssiz & 0x7F) + 1,
                (ssiz & 0x80) != 0,
                xrsiz,
                yrsiz));
        }

        return new Jpeg2000Size(
            capabilities,
            checked((int)xsiz),
            checked((int)ysiz),
            checked((int)xosiz),
            checked((int)yosiz),
            checked((int)xtsiz),
            checked((int)ytsiz),
            checked((int)xtosiz),
            checked((int)ytosiz),
            componentSizes);
    }

    private static Jpeg2000CodingStyle ReadCodingStyle(Jpeg2000MarkerSegment segment)
    {
        var reader = new Jpeg2000SegmentReader(segment);
        byte scod = reader.ReadByte();
        byte progressionOrder = reader.ReadByte();
        ushort layers = reader.ReadUInt16();
        byte multipleComponentTransform = reader.ReadByte();
        byte decompositionLevels = reader.ReadByte();
        byte codeBlockWidthExponentOffset = reader.ReadByte();
        byte codeBlockHeightExponentOffset = reader.ReadByte();
        byte codeBlockStyle = reader.ReadByte();
        byte transform = reader.ReadByte();

        int resolutionCount = decompositionLevels + 1;
        int[] precinctWidths = new int[resolutionCount];
        int[] precinctHeights = new int[resolutionCount];

        if ((scod & 0x01) != 0)
        {
            for (int i = 0; i < resolutionCount; i++)
            {
                byte precinct = reader.ReadByte();
                precinctWidths[i] = 1 << (precinct & 0x0F);
                precinctHeights[i] = 1 << ((precinct >> 4) & 0x0F);
            }
        }
        else
        {
            Array.Fill(precinctWidths, 1 << 15);
            Array.Fill(precinctHeights, 1 << 15);
        }

        return new Jpeg2000CodingStyle(
            scod,
            progressionOrder,
            layers,
            multipleComponentTransform,
            decompositionLevels,
            1 << (codeBlockWidthExponentOffset + 2),
            1 << (codeBlockHeightExponentOffset + 2),
            codeBlockStyle,
            transform,
            precinctWidths,
            precinctHeights);
    }

    private static Jpeg2000ComponentQuantization ReadQuantization(
        Jpeg2000MarkerSegment segment,
        bool hasComponentIndex,
        int componentCount)
    {
        var reader = new Jpeg2000SegmentReader(segment);
        int componentIndex = -1;
        if (hasComponentIndex)
            componentIndex = componentCount > 256 ? reader.ReadUInt16() : reader.ReadByte();

        byte sqcd = reader.ReadByte();
        int guardBits = sqcd >> 5;
        int style = sqcd & 0x1F;

        var steps = new List<Jpeg2000QuantizationStep>();
        if (style == 0)
        {
            while (reader.HasBytes(1))
            {
                byte value = reader.ReadByte();
                steps.Add(new Jpeg2000QuantizationStep(value >> 3, 0));
            }
        }
        else
        {
            while (reader.HasBytes(2))
            {
                ushort value = reader.ReadUInt16();
                steps.Add(new Jpeg2000QuantizationStep(value >> 11, value & 0x7FF));
            }
        }

        return new Jpeg2000ComponentQuantization(
            componentIndex,
            new Jpeg2000Quantization(style, guardBits, steps));
    }

    private static int FindCodestreamOffset(byte[] data, out int codestreamLength, out bool isJp2)
    {
        codestreamLength = 0;
        isJp2 = false;
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F)
        {
            codestreamLength = data.Length;
            return 0;
        }

        int offset = 0;
        for (int guard = 0; guard < 256 && offset + 8 <= data.Length; guard++)
        {
            long length = ReadUInt32(data, offset);
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            int headerLength = 8;
            if (length == 1)
            {
                if (offset + 16 > data.Length)
                    return -1;

                length = checked((long)ReadUInt64(data, offset + 8));
                headerLength = 16;
            }
            else if (length == 0)
            {
                length = data.Length - offset;
            }

            if (length < headerLength || offset + length > data.Length)
                return -1;

            if (type == "jp2c")
            {
                isJp2 = true;
                codestreamLength = checked((int)length - headerLength);
                return offset + headerLength;
            }

            offset += checked((int)length);
        }

        return -1;
    }

    private static uint ReadUInt32(byte[] data, int offset)
        => ((uint)data[offset] << 24) |
           ((uint)data[offset + 1] << 16) |
           ((uint)data[offset + 2] << 8) |
           data[offset + 3];

    private static ulong ReadUInt64(byte[] data, int offset)
        => ((ulong)ReadUInt32(data, offset) << 32) | ReadUInt32(data, offset + 4);
}

internal sealed record Jpeg2000Size(
    int Capabilities,
    int Xsiz,
    int Ysiz,
    int XOsiz,
    int YOsiz,
    int XTsiz,
    int YTsiz,
    int XTOsiz,
    int YTOsiz,
    IReadOnlyList<Jpeg2000ComponentSize> Components)
{
    public int Width => Xsiz - XOsiz;
    public int Height => Ysiz - YOsiz;
    public int TileColumns => CeilingDiv(Xsiz - XTOsiz, XTsiz);
    public int TileRows => CeilingDiv(Ysiz - YTOsiz, YTsiz);
    public int TileCount => TileColumns * TileRows;

    public Jpeg2000TileBounds GetTileBounds(int tileIndex)
    {
        int tx = tileIndex % TileColumns;
        int ty = tileIndex / TileColumns;
        int x0 = Math.Max(XTOsiz + tx * XTsiz, XOsiz);
        int y0 = Math.Max(YTOsiz + ty * YTsiz, YOsiz);
        int x1 = Math.Min(XTOsiz + (tx + 1) * XTsiz, Xsiz);
        int y1 = Math.Min(YTOsiz + (ty + 1) * YTsiz, Ysiz);
        return new Jpeg2000TileBounds(x0, y0, x1, y1);
    }

    private static int CeilingDiv(int value, int divisor) => (value + divisor - 1) / divisor;
}

internal sealed record Jpeg2000ComponentSize(int Precision, bool IsSigned, int XRsiz, int YRsiz);

internal sealed record Jpeg2000TileBounds(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;
}

internal sealed record Jpeg2000CodingStyle(
    byte Scod,
    int ProgressionOrder,
    int Layers,
    int MultipleComponentTransform,
    int DecompositionLevels,
    int CodeBlockWidth,
    int CodeBlockHeight,
    byte CodeBlockStyle,
    int Transform,
    IReadOnlyList<int> PrecinctWidths,
    IReadOnlyList<int> PrecinctHeights);

internal sealed record Jpeg2000Quantization(int Style, int GuardBits, IReadOnlyList<Jpeg2000QuantizationStep> Steps);

internal readonly record struct Jpeg2000QuantizationStep(int Exponent, int Mantissa);

internal sealed record Jpeg2000ComponentQuantization(int ComponentIndex, Jpeg2000Quantization Quantization);

internal sealed record Jpeg2000TilePart(
    int TileIndex,
    int TilePartIndex,
    int TilePartCount,
    int Start,
    int Length,
    int HeaderStart,
    int DataStart,
    int DataLength,
    Jpeg2000CodingStyle? CodingStyle,
    Jpeg2000Quantization? Quantization,
    IReadOnlyDictionary<int, Jpeg2000Quantization> ComponentQuantizations);

internal readonly record struct Jpeg2000MarkerSegment(byte Marker, int MarkerStart, int PayloadStart, int PayloadLength, byte[] Data);

internal ref struct Jpeg2000BigEndianReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _end;

    public Jpeg2000BigEndianReader(byte[] data, int position, int end)
    {
        _data = data;
        _end = end;
        Position = position;
    }

    public int Position { get; set; }

    public bool HasBytes(int count) => Position + count <= _end;

    public void ExpectMarker(byte marker)
    {
        byte actual = ReadMarker();
        if (actual != marker)
            throw new InvalidOperationException($"Expected JPEG2000 marker FF{marker:X2}, got FF{actual:X2}.");
    }

    public byte ReadMarker()
    {
        while (HasBytes(1) && _data[Position] != 0xFF)
            Position++;

        if (!HasBytes(2))
            throw new InvalidOperationException("Unexpected end of JPEG2000 marker stream.");

        while (HasBytes(1) && _data[Position] == 0xFF)
            Position++;

        if (!HasBytes(1))
            throw new InvalidOperationException("Unexpected end of JPEG2000 marker stream.");

        byte marker = _data[Position++];
        if (marker == 0x00)
            return ReadMarker();

        return marker;
    }

    public Jpeg2000MarkerSegment ReadMarkerSegment(byte marker, int markerStart)
    {
        ushort segmentLength = ReadUInt16();
        int payloadStart = Position;
        int payloadLength = segmentLength - 2;
        if (payloadLength < 0 || payloadStart + payloadLength > _end)
            throw new InvalidOperationException($"Invalid JPEG2000 marker segment FF{marker:X2}.");

        Position += payloadLength;
        return new Jpeg2000MarkerSegment(marker, markerStart, payloadStart, payloadLength, _data.ToArray());
    }

    private ushort ReadUInt16()
    {
        if (!HasBytes(2))
            throw new InvalidOperationException("Unexpected end of JPEG2000 marker segment.");

        ushort value = (ushort)((_data[Position] << 8) | _data[Position + 1]);
        Position += 2;
        return value;
    }
}

internal ref struct Jpeg2000SegmentReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _end;

    public Jpeg2000SegmentReader(Jpeg2000MarkerSegment segment)
    {
        _data = segment.Data;
        Position = segment.PayloadStart;
        _end = segment.PayloadStart + segment.PayloadLength;
    }

    private int Position { get; set; }

    public bool HasBytes(int count) => Position + count <= _end;

    public byte ReadByte()
    {
        if (!HasBytes(1))
            throw new InvalidOperationException("Unexpected end of JPEG2000 marker payload.");

        return _data[Position++];
    }

    public ushort ReadUInt16()
    {
        if (!HasBytes(2))
            throw new InvalidOperationException("Unexpected end of JPEG2000 marker payload.");

        ushort value = (ushort)((_data[Position] << 8) | _data[Position + 1]);
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        if (!HasBytes(4))
            throw new InvalidOperationException("Unexpected end of JPEG2000 marker payload.");

        uint value = ((uint)_data[Position] << 24) |
                     ((uint)_data[Position + 1] << 16) |
                     ((uint)_data[Position + 2] << 8) |
                     _data[Position + 3];
        Position += 4;
        return value;
    }
}
