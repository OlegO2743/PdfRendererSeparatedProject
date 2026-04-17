namespace PdfCore.Images.Jpeg2000;

internal static class Jpeg2000PacketParser
{
    private static readonly Lazy<Jpeg2000PacketTraceConfig> TraceConfig = new(Jpeg2000PacketTraceConfig.Create);

    public static Jpeg2000PacketTile ParseTile(Jpeg2000Codestream codestream, Jpeg2000TilePart tilePart)
    {
        if (tilePart.CodingStyle == null)
            throw new InvalidOperationException("JPEG2000 tile-part has no active coding style.");

        if (tilePart.Quantization == null)
            throw new InvalidOperationException("JPEG2000 tile-part has no active quantization.");

        Jpeg2000TileGeometry geometry = Jpeg2000TileGeometryBuilder.Build(codestream, tilePart.TileIndex);
        Jpeg2000PacketTile packetTile = Jpeg2000PacketTile.FromGeometry(geometry);

        var reader = new Jpeg2000PacketBitReader(codestream.Source, tilePart.DataStart, tilePart.DataStart + tilePart.DataLength);
        switch (tilePart.CodingStyle.ProgressionOrder)
        {
            case 0:
                ParseLrcp(packetTile, tilePart.CodingStyle, ref reader);
                break;

            case 1:
                if (ShouldParseRlcpAsSingleResolutionTilePart(tilePart, tilePart.CodingStyle))
                    ParseRlcpResolutionTilePart(packetTile, tilePart, tilePart.CodingStyle, ref reader);
                else
                    ParseRlcp(packetTile, tilePart.CodingStyle, ref reader);
                break;

            default:
                throw new NotSupportedException($"JPEG2000 progression order {tilePart.CodingStyle.ProgressionOrder} is not supported yet.");
        }

        return packetTile;
    }

    private static void ParseLrcp(Jpeg2000PacketTile tile, Jpeg2000CodingStyle codingStyle, ref Jpeg2000PacketBitReader reader)
    {
        for (int layer = 0; layer < codingStyle.Layers; layer++)
        {
            for (int resolution = 0; resolution <= codingStyle.DecompositionLevels; resolution++)
            {
                foreach (Jpeg2000PacketComponent component in tile.Components)
                    ReadPacket(tile, component, resolution, layer, ref reader);
            }
        }
    }

    private static void ParseRlcp(Jpeg2000PacketTile tile, Jpeg2000CodingStyle codingStyle, ref Jpeg2000PacketBitReader reader)
    {
        for (int resolution = 0; resolution <= codingStyle.DecompositionLevels; resolution++)
        {
            for (int layer = 0; layer < codingStyle.Layers; layer++)
            {
                foreach (Jpeg2000PacketComponent component in tile.Components)
                    ReadPacket(tile, component, resolution, layer, ref reader);
            }
        }
    }

    private static bool ShouldParseRlcpAsSingleResolutionTilePart(Jpeg2000TilePart tilePart, Jpeg2000CodingStyle codingStyle)
    {
        int resolutionCount = codingStyle.DecompositionLevels + 1;
        if (resolutionCount <= 1)
            return false;

        if (codingStyle.Layers != 1)
            return false;

        if (tilePart.TilePartCount != resolutionCount)
            return false;

        return tilePart.TilePartIndex >= 0 &&
               tilePart.TilePartIndex < resolutionCount;
    }

    private static void ParseRlcpResolutionTilePart(
        Jpeg2000PacketTile tile,
        Jpeg2000TilePart tilePart,
        Jpeg2000CodingStyle codingStyle,
        ref Jpeg2000PacketBitReader reader)
    {
        int resolution = tilePart.TilePartIndex;
        if (resolution < 0 || resolution > codingStyle.DecompositionLevels)
            return;

        for (int layer = 0; layer < codingStyle.Layers; layer++)
        {
            foreach (Jpeg2000PacketComponent component in tile.Components)
                ReadPacket(tile, component, resolution, layer, ref reader);
        }
    }

    private static void ReadPacket(
        Jpeg2000PacketTile tile,
        Jpeg2000PacketComponent component,
        int resolutionIndex,
        int layer,
        ref Jpeg2000PacketBitReader reader)
    {
        if (resolutionIndex < 0 || resolutionIndex >= component.Resolutions.Count)
            return;

        Jpeg2000PacketResolution resolution = component.Resolutions[resolutionIndex];
        if (resolution.Subbands.Count == 0)
            return;

        bool trace = TraceConfig.Value.ShouldTrace(tile.TileIndex, component.Geometry.ComponentIndex, resolutionIndex, layer);
        int packetPresent = reader.ReadBit();
        if (trace)
        {
            Console.WriteLine(
                $"JPX TRACE packet tile={tile.TileIndex} comp={component.Geometry.ComponentIndex} res={resolutionIndex} layer={layer} " +
                $"startPos={reader.Position} present={packetPresent}");
        }

        if (packetPresent == 0)
        {
            return;
        }

        var includedInPacket = new List<(Jpeg2000PacketCodeBlock CodeBlock, int Length)>();
        foreach (Jpeg2000PacketSubband subband in resolution.Subbands)
        {
            foreach (Jpeg2000PacketCodeBlock codeBlock in subband.CodeBlocks)
            {
                bool included;
                if (!codeBlock.Included)
                {
                    included = subband.InclusionTree.Decode(
                        codeBlock.Geometry.GridX,
                        codeBlock.Geometry.GridY,
                        layer + 1,
                        ref reader);

                    if (included)
                    {
                        codeBlock.Included = true;
                        codeBlock.ZeroBitPlanes = ReadZeroBitPlanes(
                            subband.ZeroBitPlaneTree,
                            ref reader,
                            codeBlock.Geometry.GridX,
                            codeBlock.Geometry.GridY);
                    }

                    if (!included)
                    {
                        if (trace)
                        {
                            Console.WriteLine(
                                $"JPX TRACE   sb={subband.Geometry.Kind} cb={codeBlock.Geometry.Index} grid=({codeBlock.Geometry.GridX},{codeBlock.Geometry.GridY}) not included");
                        }
                        continue;
                    }
                }
                else
                {
                    included = reader.ReadBit() != 0;
                    if (!included)
                    {
                        if (trace)
                        {
                            Console.WriteLine(
                                $"JPX TRACE   sb={subband.Geometry.Kind} cb={codeBlock.Geometry.Index} grid=({codeBlock.Geometry.GridX},{codeBlock.Geometry.GridY}) skipped-by-include-bit");
                        }
                        continue;
                    }
                }

                int passes = ReadCodingPassCount(ref reader);
                while (reader.ReadBit() != 0)
                    codeBlock.LBlock++;

                int lengthBitCount = codeBlock.LBlock + Log2Floor(passes);
                int length = reader.ReadBits(lengthBitCount);
                if (trace)
                {
                    Console.WriteLine(
                        $"JPX TRACE   sb={subband.Geometry.Kind} cb={codeBlock.Geometry.Index} grid=({codeBlock.Geometry.GridX},{codeBlock.Geometry.GridY}) " +
                        $"zbp={codeBlock.ZeroBitPlanes} passes={passes} lblock={codeBlock.LBlock} lenBits={lengthBitCount} len={length} pos={reader.Position}");
                }

                codeBlock.CodingPasses += passes;
                codeBlock.SegmentLengths.Add(length);
                includedInPacket.Add((codeBlock, length));
            }
        }

        reader.AlignToByte();
        foreach ((Jpeg2000PacketCodeBlock codeBlock, int length) in includedInPacket)
        {
            if (reader.Position + length > reader.EndPosition)
            {
                throw new InvalidOperationException(
                    "JPEG2000 packet data length points outside tile-part data. " +
                    $"tile={tile.TileIndex}, component={component.Geometry.ComponentIndex}, resolution={resolutionIndex}, layer={layer}, " +
                    $"codeblock={codeBlock.Geometry.Index}, grid=({codeBlock.Geometry.GridX},{codeBlock.Geometry.GridY}), " +
                    $"requested={length}, position={reader.Position}, end={reader.EndPosition}, remaining={reader.EndPosition - reader.Position}");
            }

            byte[] bytes = reader.ReadBytes(length);
            codeBlock.DataSegments.Add(bytes);
        }
    }

    private static int ReadZeroBitPlanes(Jpeg2000TagTree tree, ref Jpeg2000PacketBitReader reader, int x, int y)
    {
        for (int threshold = 1; threshold < 512; threshold++)
        {
            if (tree.Decode(x, y, threshold, ref reader))
                return threshold - 1;
        }

        throw new InvalidOperationException("JPEG2000 zero-bitplane tag-tree threshold exceeded sane range.");
    }

    private static int ReadCodingPassCount(ref Jpeg2000PacketBitReader reader)
    {
        if (reader.ReadBit() == 0)
            return 1;

        if (reader.ReadBit() == 0)
            return 2;

        int twoBits = reader.ReadBits(2);
        if (twoBits < 3)
            return 3 + twoBits;

        int fiveBits = reader.ReadBits(5);
        if (fiveBits < 31)
            return 6 + fiveBits;

        return 37 + reader.ReadBits(7);
    }

    private static int Log2Floor(int value)
    {
        int result = 0;
        while (value > 1)
        {
            value >>= 1;
            result++;
        }

        return result;
    }
}

internal sealed class Jpeg2000PacketTraceConfig
{
    private Jpeg2000PacketTraceConfig(bool enabled, int? tileIndex, int? componentIndex, int? resolutionIndex, int? layerIndex)
    {
        Enabled = enabled;
        TileIndex = tileIndex;
        ComponentIndex = componentIndex;
        ResolutionIndex = resolutionIndex;
        LayerIndex = layerIndex;
    }

    public bool Enabled { get; }
    public int? TileIndex { get; }
    public int? ComponentIndex { get; }
    public int? ResolutionIndex { get; }
    public int? LayerIndex { get; }

    public bool ShouldTrace(int tileIndex, int componentIndex, int resolutionIndex, int layerIndex)
    {
        if (!Enabled)
            return false;

        if (TileIndex.HasValue && TileIndex.Value != tileIndex)
            return false;

        if (ComponentIndex.HasValue && ComponentIndex.Value != componentIndex)
            return false;

        if (ResolutionIndex.HasValue && ResolutionIndex.Value != resolutionIndex)
            return false;

        if (LayerIndex.HasValue && LayerIndex.Value != layerIndex)
            return false;

        return true;
    }

    public static Jpeg2000PacketTraceConfig Create()
    {
        string? raw = Environment.GetEnvironmentVariable("JPX_TRACE_PACKET");
        if (string.IsNullOrWhiteSpace(raw))
            return new Jpeg2000PacketTraceConfig(false, null, null, null, null);

        string[] parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        int? ParsePart(int index)
        {
            if (index >= parts.Length)
                return null;

            return int.TryParse(parts[index], out int value) ? value : null;
        }

        return new Jpeg2000PacketTraceConfig(
            enabled: true,
            tileIndex: ParsePart(0),
            componentIndex: ParsePart(1),
            resolutionIndex: ParsePart(2),
            layerIndex: ParsePart(3));
    }
}

internal sealed class Jpeg2000PacketTile
{
    private Jpeg2000PacketTile(int tileIndex, Jpeg2000TileBounds bounds, IReadOnlyList<Jpeg2000PacketComponent> components)
    {
        TileIndex = tileIndex;
        Bounds = bounds;
        Components = components;
    }

    public int TileIndex { get; }
    public Jpeg2000TileBounds Bounds { get; }
    public IReadOnlyList<Jpeg2000PacketComponent> Components { get; }

    public static Jpeg2000PacketTile FromGeometry(Jpeg2000TileGeometry geometry)
    {
        var components = new List<Jpeg2000PacketComponent>(geometry.Components.Count);
        foreach (Jpeg2000TileComponentGeometry component in geometry.Components)
        {
            var resolutions = new List<Jpeg2000PacketResolution>(component.Resolutions.Count);
            foreach (Jpeg2000ResolutionGeometry resolution in component.Resolutions)
            {
                var subbands = new List<Jpeg2000PacketSubband>(resolution.Subbands.Count);
                foreach (Jpeg2000SubbandGeometry subband in resolution.Subbands)
                {
                    subbands.Add(new Jpeg2000PacketSubband(
                        subband,
                        subband.CodeBlocks.Select(cb => new Jpeg2000PacketCodeBlock(cb)).ToList(),
                        new Jpeg2000InclusionTree(subband.CodeBlockColumns, subband.CodeBlockRows),
                        new Jpeg2000TagTree(subband.CodeBlockColumns, subband.CodeBlockRows)));
                }

                resolutions.Add(new Jpeg2000PacketResolution(resolution, subbands));
            }

            components.Add(new Jpeg2000PacketComponent(component, resolutions));
        }

        return new Jpeg2000PacketTile(geometry.TileIndex, geometry.TileBounds, components);
    }
}

internal sealed record Jpeg2000PacketComponent(
    Jpeg2000TileComponentGeometry Geometry,
    IReadOnlyList<Jpeg2000PacketResolution> Resolutions);

internal sealed record Jpeg2000PacketResolution(
    Jpeg2000ResolutionGeometry Geometry,
    IReadOnlyList<Jpeg2000PacketSubband> Subbands);

internal sealed record Jpeg2000PacketSubband(
    Jpeg2000SubbandGeometry Geometry,
    IReadOnlyList<Jpeg2000PacketCodeBlock> CodeBlocks,
    Jpeg2000InclusionTree InclusionTree,
    Jpeg2000TagTree ZeroBitPlaneTree);

internal sealed class Jpeg2000PacketCodeBlock
{
    public Jpeg2000PacketCodeBlock(Jpeg2000CodeBlockGeometry geometry)
    {
        Geometry = geometry;
    }

    public Jpeg2000CodeBlockGeometry Geometry { get; }
    public bool Included { get; set; }
    public int ZeroBitPlanes { get; set; }
    public int LBlock { get; set; } = 3;
    public int CodingPasses { get; set; }
    public List<int> SegmentLengths { get; } = new();
    public List<byte[]> DataSegments { get; } = new();
}

internal sealed class Jpeg2000TagTree
{
    private readonly Jpeg2000TagTreeLevel[] _levels;
    private readonly int[] _pathIndices;

    public Jpeg2000TagTree(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            _levels = [new Jpeg2000TagTreeLevel(1, 1)];
            _pathIndices = new int[_levels.Length];
            return;
        }

        var levels = new List<Jpeg2000TagTreeLevel>();
        int w = width;
        int h = height;
        while (true)
        {
            levels.Add(new Jpeg2000TagTreeLevel(w, h));
            if (w == 1 && h == 1)
                break;

            w = (w + 1) >> 1;
            h = (h + 1) >> 1;
        }

        _levels = levels.ToArray();
        _pathIndices = new int[_levels.Length];
    }

    public bool Decode(int x, int y, int threshold, ref Jpeg2000PacketBitReader reader)
    {
        int currentX = x;
        int currentY = y;
        for (int levelIndex = 0; levelIndex < _levels.Length; levelIndex++)
        {
            Jpeg2000TagTreeLevel level = _levels[levelIndex];
            int clampedX = Math.Clamp(currentX, 0, level.Width - 1);
            int clampedY = Math.Clamp(currentY, 0, level.Height - 1);
            _pathIndices[levelIndex] = clampedX + clampedY * level.Width;
            currentX >>= 1;
            currentY >>= 1;
        }

        int low = 0;
        for (int levelIndex = _levels.Length - 1; levelIndex >= 0; levelIndex--)
        {
            Jpeg2000TagTreeLevel level = _levels[levelIndex];
            ref Jpeg2000TagTreeNode node = ref level.Items[_pathIndices[levelIndex]];
            if (low > node.Low)
                node.Low = low;

            while (node.Low < threshold && node.Low < node.Value)
            {
                if (reader.ReadBit() != 0)
                    node.Value = node.Low;
                else
                    node.Low++;
            }

            low = node.Low;
        }

        Jpeg2000TagTreeNode leaf = _levels[0].Items[_pathIndices[0]];
        return leaf.Value < threshold;
    }
}

internal sealed class Jpeg2000TagTreeLevel
{
    public Jpeg2000TagTreeLevel(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Items = new Jpeg2000TagTreeNode[Width * Height];
        for (int i = 0; i < Items.Length; i++)
            Items[i] = new Jpeg2000TagTreeNode();
    }

    public int Width { get; }
    public int Height { get; }
    public Jpeg2000TagTreeNode[] Items { get; }
}

internal sealed class Jpeg2000TagTreeNode
{
    public int Value { get; set; } = int.MaxValue;
    public int Low { get; set; }
}

internal sealed class Jpeg2000InclusionTree
{
    private readonly Jpeg2000TagTree _tree;

    public Jpeg2000InclusionTree(int width, int height)
    {
        _tree = new Jpeg2000TagTree(width, height);
    }

    public bool Decode(int x, int y, int threshold, ref Jpeg2000PacketBitReader reader)
        => _tree.Decode(x, y, threshold, ref reader);
}

internal ref struct Jpeg2000PacketBitReader
{
    private static readonly int TraceBitsLimit = ParseTraceBitsLimit();
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _end;
    private int _currentByte;
    private int _bitIndex;
    private int _bitsInCurrentByte;
    private int _traceBitsEmitted;

    public Jpeg2000PacketBitReader(byte[] data, int offset, int end)
    {
        _data = data;
        Position = offset;
        _end = end;
        _currentByte = 0;
        _bitIndex = 8;
        _bitsInCurrentByte = 8;
    }

    public int Position { get; private set; }
    public int EndPosition => _end;

    public int ReadBit()
    {
        if (_bitIndex >= _bitsInCurrentByte)
            LoadByte();

        int bitPosition = _bitsInCurrentByte == 7 ? 6 - _bitIndex : 7 - _bitIndex;
        int bit = (_currentByte >> bitPosition) & 1;
        if (_traceBitsEmitted < TraceBitsLimit)
        {
            Console.WriteLine(
                $"JPX BIT pos={Position} byte=0x{_currentByte:X2} bits={_bitsInCurrentByte} bitIndex={_bitIndex} bitPos={bitPosition} -> {bit}");
            _traceBitsEmitted++;
        }

        _bitIndex++;
        return bit;
    }

    public int ReadBits(int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | ReadBit();

        return value;
    }

    public void AlignToByte()
    {
        _bitIndex = _bitsInCurrentByte;
    }

    public byte[] ReadBytes(int count)
    {
        AlignToByte();
        if (count < 0 || Position + count > _end)
            throw new InvalidOperationException("JPEG2000 packet data length points outside tile-part data.");

        byte[] result = _data.Slice(Position, count).ToArray();
        Position += count;
        return result;
    }

    private void LoadByte()
    {
        if (Position >= _end)
            throw new InvalidOperationException("Unexpected end of JPEG2000 packet header.");

        bool previousWasFF = _currentByte == 0xFF;
        _currentByte = _data[Position++];
        _bitsInCurrentByte = previousWasFF ? 7 : 8;
        _bitIndex = 0;
    }

    private static int ParseTraceBitsLimit()
    {
        string? raw = Environment.GetEnvironmentVariable("JPX_TRACE_BITS");
        return int.TryParse(raw, out int value) && value > 0 ? value : 0;
    }
}
