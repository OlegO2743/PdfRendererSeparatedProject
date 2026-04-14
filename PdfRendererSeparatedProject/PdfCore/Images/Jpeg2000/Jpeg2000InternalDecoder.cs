using System.Drawing;
using System.Drawing.Imaging;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PdfCore.Images.Jpeg2000;

internal static class Jpeg2000InternalDecoder
{
    private static readonly bool DisableMctForDiagnostics = string.Equals(
        Environment.GetEnvironmentVariable("JPX_DISABLE_MCT"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool DumpStatsForDiagnostics = string.Equals(
        Environment.GetEnvironmentVariable("JPX_DUMP_STATS"),
        "1",
        StringComparison.Ordinal);

    public static bool CanDecode(Jpeg2000Codestream codestream)
    {
        if (codestream.Size.Components.Count is not (1 or 3))
            return false;

        if (codestream.CodingStyle.Transform is not (0 or 1))
            return false;

        if (codestream.CodingStyle.CodeBlockStyle != 0)
            return false;

        if (codestream.Quantization.Style is not (0 or 2))
            return false;

        foreach (Jpeg2000ComponentSize component in codestream.Size.Components)
        {
            if (component.XRsiz != 1 || component.YRsiz != 1)
                return false;
        }

        return true;
    }

    public static Bitmap Decode(Jpeg2000Codestream codestream)
    {
        if (!CanDecode(codestream))
            throw new NotSupportedException(
                "Internal JPX decoder currently supports only non-subsampled 1- or 3-component images with codeBlockStyle=0x00 and qstyle 0/2.");

        if (codestream.Size.Components.Count == 1 &&
            codestream.CodingStyle.Transform == 1 &&
            codestream.Quantization.Style == 0)
        {
            return DecodeSingleComponentReversible(codestream);
        }

        return DecodeGeneral(codestream);
    }

    private static Bitmap DecodeSingleComponentReversible(Jpeg2000Codestream codestream)
    {
        int width = codestream.Size.Width;
        int height = codestream.Size.Height;
        int precision = codestream.Size.Components[0].Precision;
        bool isSigned = codestream.Size.Components[0].IsSigned;
        int levelShift = isSigned ? 0 : 1 << Math.Max(0, precision - 1);
        int maxValue = (1 << precision) - 1;

        int[] image = new int[width * height];
        for (int tileIndex = 0; tileIndex < codestream.Size.TileCount; tileIndex++)
        {
            Jpeg2000TileGeometry geometry = Jpeg2000TileGeometryBuilder.Build(codestream, tileIndex);
            if (geometry.Components.Count != 1)
                throw new NotSupportedException("Internal JPX decoder currently supports single-component tiles only.");

            Jpeg2000PacketTile packetTile = BuildAggregateTile(codestream, geometry);
            Jpeg2000TileComponentGeometry componentGeometry = geometry.Components[0];
            int[] coefficients = DecodeTileComponentCoefficients(codestream, packetTile.Components[0], componentGeometry);

            Jpeg2000InverseWavelet.Transform53(
                coefficients,
                componentGeometry.GlobalBounds.X0,
                componentGeometry.GlobalBounds.Y0,
                componentGeometry.Bounds.Width,
                componentGeometry.Bounds.Height,
                codestream.CodingStyle.DecompositionLevels);

            CopyTileToImage(
                codestream,
                geometry.TileBounds,
                componentGeometry.Bounds.Width,
                componentGeometry.Bounds.Height,
                coefficients,
                image);
        }

        var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
        BitmapData locked = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            DrawingPixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[locked.Stride * height];
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * width;
                int rowOffset = y * locked.Stride;
                for (int x = 0; x < width; x++)
                {
                    int value = Clamp(image[rowStart + x] + levelShift, 0, maxValue);
                    byte b = precision == 8
                        ? (byte)value
                        : (byte)((value * 255 + (maxValue / 2)) / maxValue);
                    int i = rowOffset + x * 4;
                    pixels[i + 0] = b;
                    pixels[i + 1] = b;
                    pixels[i + 2] = b;
                    pixels[i + 3] = 255;
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        return bitmap;
    }

    private static Bitmap DecodeGeneral(Jpeg2000Codestream codestream)
    {
        int width = codestream.Size.Width;
        int height = codestream.Size.Height;
        int componentCount = codestream.Size.Components.Count;
        var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
        BitmapData locked = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            DrawingPixelFormat.Format32bppArgb);
        byte[] pixels = new byte[locked.Stride * height];

        for (int tileIndex = 0; tileIndex < codestream.Size.TileCount; tileIndex++)
        {
            Jpeg2000TileGeometry geometry = Jpeg2000TileGeometryBuilder.Build(codestream, tileIndex);
            Jpeg2000PacketTile packetTile = BuildAggregateTile(codestream, geometry);
            float[][] componentSamples = new float[componentCount][];
            for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                Jpeg2000TileComponentGeometry componentGeometry = geometry.Components[componentIndex];
                if (codestream.CodingStyle.Transform == 0)
                {
                    float[] coefficients = DecodeTileComponentCoefficientsFloat(
                        codestream,
                        packetTile.Components[componentIndex],
                        componentGeometry);
                    Jpeg2000InverseWavelet.Transform97(
                        coefficients,
                        componentGeometry.GlobalBounds.X0,
                        componentGeometry.GlobalBounds.Y0,
                        componentGeometry.Bounds.Width,
                        componentGeometry.Bounds.Height,
                        codestream.CodingStyle.DecompositionLevels);
                    componentSamples[componentIndex] = coefficients;
                }
                else
                {
                    int[] coefficients = DecodeTileComponentCoefficients(codestream, packetTile.Components[componentIndex], componentGeometry);
                    Jpeg2000InverseWavelet.Transform53(
                        coefficients,
                        componentGeometry.GlobalBounds.X0,
                        componentGeometry.GlobalBounds.Y0,
                        componentGeometry.Bounds.Width,
                        componentGeometry.Bounds.Height,
                        codestream.CodingStyle.DecompositionLevels);
                    componentSamples[componentIndex] = Array.ConvertAll(coefficients, static v => (float)v);
                }

                if (DumpStatsForDiagnostics)
                {
                    LogComponentStats(
                        geometry.TileIndex,
                        componentIndex,
                        geometry.TileBounds,
                        componentGeometry.Bounds.Width,
                        componentGeometry.Bounds.Height,
                        componentSamples[componentIndex]);
                }
            }

            CopyTileToPixels(codestream, geometry, componentSamples, pixels, locked.Stride);
        }

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        return bitmap;
    }

    private static Jpeg2000PacketTile BuildAggregateTile(Jpeg2000Codestream codestream, Jpeg2000TileGeometry geometry)
    {
        Jpeg2000PacketTile aggregate = Jpeg2000PacketTile.FromGeometry(geometry);
        foreach (Jpeg2000TilePart tilePart in codestream.TileParts.Where(tp => tp.TileIndex == geometry.TileIndex))
        {
            Jpeg2000PacketTile partial = Jpeg2000PacketParser.ParseTile(codestream, tilePart);
            MergeTiles(aggregate, partial);
        }

        return aggregate;
    }

    private static void MergeTiles(Jpeg2000PacketTile target, Jpeg2000PacketTile source)
    {
        for (int componentIndex = 0; componentIndex < target.Components.Count; componentIndex++)
        {
            Jpeg2000PacketComponent targetComponent = target.Components[componentIndex];
            Jpeg2000PacketComponent sourceComponent = source.Components[componentIndex];
            for (int resolutionIndex = 0; resolutionIndex < targetComponent.Resolutions.Count; resolutionIndex++)
            {
                Jpeg2000PacketResolution targetResolution = targetComponent.Resolutions[resolutionIndex];
                Jpeg2000PacketResolution sourceResolution = sourceComponent.Resolutions[resolutionIndex];
                for (int subbandIndex = 0; subbandIndex < targetResolution.Subbands.Count; subbandIndex++)
                {
                    Jpeg2000PacketSubband targetSubband = targetResolution.Subbands[subbandIndex];
                    Jpeg2000PacketSubband sourceSubband = sourceResolution.Subbands[subbandIndex];
                    for (int codeBlockIndex = 0; codeBlockIndex < targetSubband.CodeBlocks.Count; codeBlockIndex++)
                    {
                        Jpeg2000PacketCodeBlock targetCodeBlock = targetSubband.CodeBlocks[codeBlockIndex];
                        Jpeg2000PacketCodeBlock sourceCodeBlock = sourceSubband.CodeBlocks[codeBlockIndex];
                        if (!sourceCodeBlock.Included)
                            continue;

                        if (!targetCodeBlock.Included)
                        {
                            targetCodeBlock.Included = true;
                            targetCodeBlock.ZeroBitPlanes = sourceCodeBlock.ZeroBitPlanes;
                        }

                        targetCodeBlock.CodingPasses += sourceCodeBlock.CodingPasses;
                        targetCodeBlock.SegmentLengths.AddRange(sourceCodeBlock.SegmentLengths);
                        targetCodeBlock.DataSegments.AddRange(sourceCodeBlock.DataSegments);
                    }
                }
            }
        }
    }

    private static int[] DecodeTileComponentCoefficients(
        Jpeg2000Codestream codestream,
        Jpeg2000PacketComponent component,
        Jpeg2000TileComponentGeometry geometry)
    {
        int width = geometry.Bounds.Width;
        int height = geometry.Bounds.Height;
        int[] coefficients = new int[width * height];
        Jpeg2000Quantization quantization = codestream.GetQuantization(component.Geometry.ComponentIndex);

        foreach (Jpeg2000PacketResolution resolution in component.Resolutions)
        {
            foreach (Jpeg2000PacketSubband subband in resolution.Subbands)
            {
                Jpeg2000QuantizationStep step = quantization.Steps[subband.Geometry.QuantizationIndex];
                int magnitudeBitPlanes = step.Exponent + quantization.GuardBits - 1;
                foreach (Jpeg2000PacketCodeBlock codeBlock in subband.CodeBlocks)
                {
                    if (!codeBlock.Included || codeBlock.CodingPasses == 0)
                        continue;

                    int nonZeroBitPlanes = magnitudeBitPlanes - codeBlock.ZeroBitPlanes;
                    if (nonZeroBitPlanes <= 0)
                        continue;

                    int[] block = Jpeg2000Tier1Decoder.DecodeCodeBlock(
                        codeBlock,
                        subband.Geometry.Kind,
                        nonZeroBitPlanes);

                    WriteCodeBlock(coefficients, width, geometry.Bounds, codeBlock.Geometry.Bounds, block);
                }
            }
        }

        return coefficients;
    }

    private static float[] DecodeTileComponentCoefficientsFloat(
        Jpeg2000Codestream codestream,
        Jpeg2000PacketComponent component,
        Jpeg2000TileComponentGeometry geometry)
    {
        int width = geometry.Bounds.Width;
        int height = geometry.Bounds.Height;
        float[] coefficients = new float[width * height];
        Jpeg2000Quantization quantization = codestream.GetQuantization(component.Geometry.ComponentIndex);
        Jpeg2000ComponentSize componentInfo = codestream.Size.Components[component.Geometry.ComponentIndex];

        foreach (Jpeg2000PacketResolution resolution in component.Resolutions)
        {
            foreach (Jpeg2000PacketSubband subband in resolution.Subbands)
            {
                Jpeg2000QuantizationStep step = quantization.Steps[subband.Geometry.QuantizationIndex];
                int magnitudeBitPlanes = step.Exponent + quantization.GuardBits - 1;
                float stepSize = GetIrreversibleStepSize(quantization, step, subband.Geometry.Kind, componentInfo.Precision);
                foreach (Jpeg2000PacketCodeBlock codeBlock in subband.CodeBlocks)
                {
                    if (!codeBlock.Included || codeBlock.CodingPasses == 0)
                        continue;

                    int nonZeroBitPlanes = magnitudeBitPlanes - codeBlock.ZeroBitPlanes;
                    if (nonZeroBitPlanes <= 0)
                        continue;

                    int[] block = Jpeg2000Tier1Decoder.DecodeCodeBlock(
                        codeBlock,
                        subband.Geometry.Kind,
                        nonZeroBitPlanes);

                    WriteCodeBlock(coefficients, width, geometry.Bounds, codeBlock.Geometry.Bounds, block, stepSize);
                }
            }
        }

        return coefficients;
    }

    private static void WriteCodeBlock(
        int[] target,
        int targetWidth,
        Jpeg2000BandBounds componentBounds,
        Jpeg2000BandBounds codeBlockBounds,
        int[] block)
    {
        int blockWidth = codeBlockBounds.Width;
        int blockHeight = codeBlockBounds.Height;
        int offsetX = codeBlockBounds.X0 - componentBounds.X0;
        int offsetY = codeBlockBounds.Y0 - componentBounds.Y0;
        for (int y = 0; y < blockHeight; y++)
        {
            int srcRow = y * blockWidth;
            int dstRow = (offsetY + y) * targetWidth + offsetX;
            Array.Copy(block, srcRow, target, dstRow, blockWidth);
        }
    }

    private static void WriteCodeBlock(
        float[] target,
        int targetWidth,
        Jpeg2000BandBounds componentBounds,
        Jpeg2000BandBounds codeBlockBounds,
        int[] block,
        float scale)
    {
        int blockWidth = codeBlockBounds.Width;
        int blockHeight = codeBlockBounds.Height;
        int offsetX = codeBlockBounds.X0 - componentBounds.X0;
        int offsetY = codeBlockBounds.Y0 - componentBounds.Y0;
        int targetHeight = targetWidth == 0 ? 0 : target.Length / targetWidth;

        try
        {
            if (offsetX < 0 || offsetY < 0 || offsetX + blockWidth > targetWidth || offsetY + blockHeight > targetHeight)
            {
                throw new InvalidOperationException(
                    $"JPX codeblock does not fit component buffer. comp=[{componentBounds.X0},{componentBounds.Y0}..{componentBounds.X1},{componentBounds.Y1}] " +
                    $"cb=[{codeBlockBounds.X0},{codeBlockBounds.Y0}..{codeBlockBounds.X1},{codeBlockBounds.Y1}] " +
                    $"offset=({offsetX},{offsetY}) block={blockWidth}x{blockHeight} target={targetWidth}x{targetHeight}");
            }

            for (int y = 0; y < blockHeight; y++)
            {
                int srcRow = y * blockWidth;
                int dstRow = (offsetY + y) * targetWidth + offsetX;
                for (int x = 0; x < blockWidth; x++)
                    target[dstRow + x] = block[srcRow + x] * scale;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"JPX WriteCodeBlock failed. comp=[{componentBounds.X0},{componentBounds.Y0}..{componentBounds.X1},{componentBounds.Y1}] " +
                $"cb=[{codeBlockBounds.X0},{codeBlockBounds.Y0}..{codeBlockBounds.X1},{codeBlockBounds.Y1}] " +
                $"offset=({offsetX},{offsetY}) block={blockWidth}x{blockHeight} target={targetWidth}x{targetHeight} " +
                $"blockLen={block.Length} scale={scale}",
                ex);
        }
    }

    private static void CopyTileToImage(
        Jpeg2000Codestream codestream,
        Jpeg2000TileBounds tileBounds,
        int tileWidth,
        int tileHeight,
        int[] tileData,
        int[] image)
    {
        int imageWidth = codestream.Size.Width;
        int dstX = tileBounds.X0 - codestream.Size.XOsiz;
        int dstY = tileBounds.Y0 - codestream.Size.YOsiz;
        for (int y = 0; y < tileHeight; y++)
        {
            int srcRow = y * tileWidth;
            int dstRow = (dstY + y) * imageWidth + dstX;
            Array.Copy(tileData, srcRow, image, dstRow, tileWidth);
        }
    }

    private static void CopyTileToPixels(
        Jpeg2000Codestream codestream,
        Jpeg2000TileGeometry geometry,
        float[][] componentSamples,
        byte[] pixels,
        int stride)
    {
        int tileWidth = geometry.Components[0].Bounds.Width;
        int tileHeight = geometry.Components[0].Bounds.Height;
        int dstX = geometry.TileBounds.X0 - codestream.Size.XOsiz;
        int dstY = geometry.TileBounds.Y0 - codestream.Size.YOsiz;
        int componentCount = codestream.Size.Components.Count;

        for (int y = 0; y < tileHeight; y++)
        {
            int rowOffset = (dstY + y) * stride;
            int componentRow = y * tileWidth;
            for (int x = 0; x < tileWidth; x++)
            {
                float r;
                float g;
                float b;

                if (componentCount == 1)
                {
                    r = g = b = componentSamples[0][componentRow + x];
                }
                else if (!DisableMctForDiagnostics &&
                         codestream.CodingStyle.MultipleComponentTransform != 0 &&
                         componentCount >= 3)
                {
                    float c0 = componentSamples[0][componentRow + x];
                    float c1 = componentSamples[1][componentRow + x];
                    float c2 = componentSamples[2][componentRow + x];
                    if (codestream.CodingStyle.Transform == 0)
                    {
                        r = c0 + 1.402f * c2;
                        g = c0 - 0.34413f * c1 - 0.71414f * c2;
                        b = c0 + 1.772f * c1;
                    }
                    else
                    {
                        int yv = RoundToInt(c0);
                        int u = RoundToInt(c1);
                        int v = RoundToInt(c2);
                        int gg = yv - ((u + v) >> 2);
                        r = v + gg;
                        g = gg;
                        b = u + gg;
                    }
                }
                else
                {
                    r = componentSamples[0][componentRow + x];
                    g = componentCount > 1 ? componentSamples[1][componentRow + x] : r;
                    b = componentCount > 2 ? componentSamples[2][componentRow + x] : r;
                }

                byte rb = ToByte(r, codestream.Size.Components[0]);
                byte gb = ToByte(g, codestream.Size.Components[Math.Min(1, codestream.Size.Components.Count - 1)]);
                byte bb = ToByte(b, codestream.Size.Components[Math.Min(2, codestream.Size.Components.Count - 1)]);

                int i = rowOffset + (dstX + x) * 4;
                pixels[i + 0] = bb;
                pixels[i + 1] = gb;
                pixels[i + 2] = rb;
                pixels[i + 3] = 255;
            }
        }
    }

    private static byte ToByte(float value, Jpeg2000ComponentSize component)
    {
        int precision = component.Precision;
        int maxValue = (1 << precision) - 1;
        float shifted = component.IsSigned ? value : value + (1 << Math.Max(0, precision - 1));
        int clamped = Clamp(RoundToInt(shifted), 0, maxValue);
        if (precision == 8)
            return (byte)clamped;

        return (byte)((clamped * 255 + (maxValue / 2)) / maxValue);
    }

    private static void LogComponentStats(
        int tileIndex,
        int componentIndex,
        Jpeg2000TileBounds tileBounds,
        int width,
        int height,
        float[] values)
    {
        if (values.Length == 0)
        {
            Console.WriteLine(
                $"JPX stats tile={tileIndex} comp={componentIndex} bounds=[{tileBounds.X0},{tileBounds.Y0}..{tileBounds.X1},{tileBounds.Y1}] size={width}x{height} empty");
            return;
        }

        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        double sum = 0;
        foreach (float value in values)
        {
            if (value < min)
                min = value;
            if (value > max)
                max = value;
            sum += value;
        }

        double mean = sum / values.Length;
        Console.WriteLine(
            $"JPX stats tile={tileIndex} comp={componentIndex} bounds=[{tileBounds.X0},{tileBounds.Y0}..{tileBounds.X1},{tileBounds.Y1}] size={width}x{height} min={min:F4} max={max:F4} mean={mean:F4}");
    }

    private static float GetIrreversibleStepSize(
        Jpeg2000Quantization quantization,
        Jpeg2000QuantizationStep step,
        Jpeg2000SubbandKind kind,
        int precision)
    {
        if (quantization.Style == 0)
            return 1f;

        int gain = kind switch
        {
            Jpeg2000SubbandKind.HL or Jpeg2000SubbandKind.LH => 1,
            Jpeg2000SubbandKind.HH => 2,
            _ => 0
        };

        return MathF.Pow(2f, precision + gain - step.Exponent) * (1f + step.Mantissa / 2048f);
    }

    private static int RoundToInt(float value)
        => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    private static int Clamp(int value, int minValue, int maxValue)
    {
        if (value < minValue)
            return minValue;
        if (value > maxValue)
            return maxValue;
        return value;
    }
}

internal static class Jpeg2000Tier1Decoder
{
    public static int[] DecodeCodeBlock(
        Jpeg2000PacketCodeBlock codeBlock,
        Jpeg2000SubbandKind subbandKind,
        int nonZeroBitPlanes)
    {
        int width = codeBlock.Geometry.Bounds.Width;
        int height = codeBlock.Geometry.Bounds.Height;
        int[] data = new int[width * height];
        bool[] significant = new bool[width * height];
        bool[] visited = new bool[width * height];
        bool[] refined = new bool[width * height];
        bool[] negative = new bool[width * height];

        byte[] stream = ConcatenateSegments(codeBlock.DataSegments, codeBlock.SegmentLengths);
        if (stream.Length == 0)
            return data;

        var mq = new Jpeg2000MqDecoder(stream);
        int remainingPasses = codeBlock.CodingPasses;
        int passType = 2;
        int bitPlaneBase = nonZeroBitPlanes - 1;

        while (remainingPasses-- > 0)
        {
            int plane = bitPlaneBase + 1;
            switch (passType)
            {
                case 0:
                    DecodeSignificancePass(mq, data, significant, visited, refined, negative, width, height, plane, subbandKind);
                    break;

                case 1:
                    DecodeRefinementPass(mq, data, significant, visited, refined, negative, width, height, plane);
                    break;

                case 2:
                    DecodeCleanupPass(mq, data, significant, visited, refined, negative, width, height, plane, subbandKind);
                    Array.Clear(visited, 0, visited.Length);
                    break;
            }

            passType++;
            if (passType == 3)
            {
                passType = 0;
                bitPlaneBase--;
            }
        }

        return data;
    }

    private static byte[] ConcatenateSegments(IReadOnlyList<byte[]> segments, IReadOnlyList<int> lengths)
    {
        int total = 0;
        for (int i = 0; i < lengths.Count; i++)
            total += lengths[i];

        byte[] result = new byte[total];
        int offset = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            byte[] segment = segments[i];
            Array.Copy(segment, 0, result, offset, segment.Length);
            offset += segment.Length;
        }

        return result;
    }

    private static void DecodeSignificancePass(
        Jpeg2000MqDecoder mq,
        int[] data,
        bool[] significant,
        bool[] visited,
        bool[] refined,
        bool[] negative,
        int width,
        int height,
        int bitPlane,
        Jpeg2000SubbandKind subbandKind)
    {
        int mask = 3 << (bitPlane - 1);
        for (int stripeY = 0; stripeY < height; stripeY += 4)
        {
            for (int x = 0; x < width; x++)
            {
                int yMax = Math.Min(height, stripeY + 4);
                for (int y = stripeY; y < yMax; y++)
                {
                    int index = y * width + x;
                    if (significant[index] || visited[index] || !HasSignificantNeighbor(significant, width, height, x, y))
                        continue;

                    int ctx = GetSignificanceContext(significant, width, height, x, y, subbandKind);
                    if (mq.Decode(ctx) != 0)
                    {
                        (int signCtx, int xorBit) = GetSignContext(significant, negative, width, height, x, y);
                        bool isNegative = (mq.Decode(signCtx) ^ xorBit) != 0;
                        data[index] = isNegative ? -mask : mask;
                        significant[index] = true;
                        negative[index] = isNegative;
                    }

                    visited[index] = true;
                }
            }
        }
    }

    private static void DecodeRefinementPass(
        Jpeg2000MqDecoder mq,
        int[] data,
        bool[] significant,
        bool[] visited,
        bool[] refined,
        bool[] negative,
        int width,
        int height,
        int bitPlane)
    {
        int half = 1 << (bitPlane - 1);
        for (int stripeY = 0; stripeY < height; stripeY += 4)
        {
            for (int x = 0; x < width; x++)
            {
                int yMax = Math.Min(height, stripeY + 4);
                for (int y = stripeY; y < yMax; y++)
                {
                    int index = y * width + x;
                    if (!significant[index] || visited[index])
                        continue;

                    int ctx = GetRefinementContext(significant, refined, width, height, x, y);
                    int delta = mq.Decode(ctx) != 0 ? half : -half;
                    data[index] += negative[index] ? -delta : delta;
                    refined[index] = true;
                }
            }
        }
    }

    private static void DecodeCleanupPass(
        Jpeg2000MqDecoder mq,
        int[] data,
        bool[] significant,
        bool[] visited,
        bool[] refined,
        bool[] negative,
        int width,
        int height,
        int bitPlane,
        Jpeg2000SubbandKind subbandKind)
    {
        int mask = 3 << (bitPlane - 1);
        for (int stripeY = 0; stripeY < height; stripeY += 4)
        {
            for (int x = 0; x < width; x++)
            {
                int yMax = Math.Min(height, stripeY + 4);
                bool runMode = yMax - stripeY == 4 && CanUseRunLength(significant, visited, width, height, x, stripeY);
                int runLength = 0;
                bool decoded = false;

                if (runMode)
                {
                    if (mq.Decode(17) == 0)
                        continue;

                    runLength = (mq.Decode(18) << 1) | mq.Decode(18);
                    decoded = true;
                }

                for (int y = stripeY + runLength; y < yMax; y++)
                {
                    int index = y * width + x;
                    if (!decoded && !significant[index] && !visited[index])
                    {
                        int ctx = GetSignificanceContext(significant, width, height, x, y, subbandKind);
                        decoded = mq.Decode(ctx) != 0;
                    }

                    if (decoded)
                    {
                        (int signCtx, int xorBit) = GetSignContext(significant, negative, width, height, x, y);
                        bool isNegative = (mq.Decode(signCtx) ^ xorBit) != 0;
                        data[index] = isNegative ? -mask : mask;
                        significant[index] = true;
                        negative[index] = isNegative;
                    }

                    decoded = false;
                }
            }
        }
    }

    private static bool CanUseRunLength(
        bool[] significant,
        bool[] visited,
        int width,
        int height,
        int x,
        int stripeY)
    {
        for (int y = stripeY; y < stripeY + 4; y++)
        {
            int index = y * width + x;
            if (significant[index] || visited[index] || HasSignificantNeighbor(significant, width, height, x, y))
                return false;
        }

        return true;
    }

    private static bool HasSignificantNeighbor(bool[] significant, int width, int height, int x, int y)
        => CountHorizontal(significant, width, height, x, y) +
           CountVertical(significant, width, height, x, y) +
           CountDiagonal(significant, width, height, x, y) > 0;

    private static int GetSignificanceContext(
        bool[] significant,
        int width,
        int height,
        int x,
        int y,
        Jpeg2000SubbandKind kind)
    {
        int h = CountHorizontal(significant, width, height, x, y);
        int v = CountVertical(significant, width, height, x, y);
        int d = CountDiagonal(significant, width, height, x, y);

        int label = kind switch
        {
            Jpeg2000SubbandKind.LL or Jpeg2000SubbandKind.LH => GetSignificanceLabelOrient0(h, v, d),
            Jpeg2000SubbandKind.HL => GetSignificanceLabelOrient1(h, v, d),
            Jpeg2000SubbandKind.HH => GetSignificanceLabelOrient2(h, v, d),
            _ => 0
        };

        return label;
    }

    private static int GetSignificanceLabelOrient0(int h, int v, int d)
    {
        if (h == 0)
        {
            if (v == 0)
                return d == 0 ? 0 : d == 1 ? 1 : 2;
            return v == 1 ? 3 : 4;
        }

        if (h == 1)
        {
            if (v == 0)
                return d == 0 ? 5 : 6;
            return 7;
        }

        return 8;
    }

    private static int GetSignificanceLabelOrient1(int h, int v, int d)
    {
        if (v == 0)
        {
            if (h == 0)
                return d == 0 ? 0 : d == 1 ? 1 : 2;
            return h == 1 ? 3 : 4;
        }

        if (v == 1)
        {
            if (h == 0)
                return d == 0 ? 5 : 6;
            return 7;
        }

        return 8;
    }

    private static int GetSignificanceLabelOrient2(int h, int v, int d)
    {
        if (d == 0)
        {
            if (h == 0 && v == 0)
                return 0;
            return h < 2 && v < 2 ? 1 : 2;
        }

        if (d == 1)
        {
            if (h == 0 && v == 0)
                return 3;
            return h < 2 && v < 2 ? 4 : 5;
        }

        if (d == 2)
            return h < 2 && v < 2 ? 6 : 7;

        return 8;
    }

    private static (int Context, int XorBit) GetSignContext(
        bool[] significant,
        bool[] negative,
        int width,
        int height,
        int x,
        int y)
    {
        int h = NeighborContribution(significant, negative, width, height, x - 1, y) +
                NeighborContribution(significant, negative, width, height, x + 1, y);
        int v = NeighborContribution(significant, negative, width, height, x, y - 1) +
                NeighborContribution(significant, negative, width, height, x, y + 1);

        h = h < 0 ? -1 : h > 0 ? 1 : 0;
        v = v < 0 ? -1 : v > 0 ? 1 : 0;

        return (h, v) switch
        {
            (1, 1) => (13, 0),
            (1, 0) => (12, 0),
            (1, -1) => (11, 0),
            (0, 1) => (10, 0),
            (0, 0) => (9, 0),
            (0, -1) => (10, 1),
            (-1, 1) => (11, 1),
            (-1, 0) => (12, 1),
            (-1, -1) => (13, 1),
            _ => (9, 0)
        };
    }

    private static int GetRefinementContext(
        bool[] significant,
        bool[] refined,
        int width,
        int height,
        int x,
        int y)
    {
        if (refined[y * width + x])
            return 16;

        return HasSignificantNeighbor(significant, width, height, x, y) ? 15 : 14;
    }

    private static int NeighborContribution(
        bool[] significant,
        bool[] negative,
        int width,
        int height,
        int x,
        int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            return 0;

        int index = y * width + x;
        if (!significant[index])
            return 0;

        return negative[index] ? -1 : 1;
    }

    private static int CountHorizontal(bool[] significant, int width, int height, int x, int y)
    {
        int count = 0;
        if (x > 0 && significant[y * width + (x - 1)])
            count++;
        if (x + 1 < width && significant[y * width + (x + 1)])
            count++;
        return count;
    }

    private static int CountVertical(bool[] significant, int width, int height, int x, int y)
    {
        int count = 0;
        if (y > 0 && significant[(y - 1) * width + x])
            count++;
        if (y + 1 < height && significant[(y + 1) * width + x])
            count++;
        return count;
    }

    private static int CountDiagonal(bool[] significant, int width, int height, int x, int y)
    {
        int count = 0;
        if (x > 0 && y > 0 && significant[(y - 1) * width + (x - 1)])
            count++;
        if (x + 1 < width && y > 0 && significant[(y - 1) * width + (x + 1)])
            count++;
        if (x > 0 && y + 1 < height && significant[(y + 1) * width + (x - 1)])
            count++;
        if (x + 1 < width && y + 1 < height && significant[(y + 1) * width + (x + 1)])
            count++;
        return count;
    }
}

internal sealed class Jpeg2000MqDecoder
{
    private static readonly ushort[] Qe = [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801,
        0x3001, 0x2401, 0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401,
        0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101,
        0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085,
        0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601
    ];

    private static readonly byte[] Nmps = [
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10,
        11, 12, 13, 29, 15, 16, 17, 18, 19, 20,
        21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
        31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
        41, 42, 43, 44, 45, 45, 46
    ];

    private static readonly byte[] Nlps = [
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14,
        17, 18, 20, 21, 14, 14, 15, 16, 17, 18,
        19, 19, 20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 30, 31, 32, 33, 34, 35, 36, 37,
        38, 39, 40, 41, 42, 43, 46
    ];

    private static readonly bool[] Switch = [
        true, false, false, false, false, false, true, false, false, false,
        false, false, false, false, true, false, false, false, false, false,
        false, false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false
    ];

    private readonly byte[] _data;
    private readonly int[] _stateIndex = new int[19];
    private readonly int[] _mps = new int[19];
    private int _bp;
    private uint _a;
    private uint _c;
    private int _ct;

    public Jpeg2000MqDecoder(byte[] data)
    {
        _data = data;
        ResetContexts();
        InitializeDecoder();
    }

    public int Decode(int context)
    {
        uint qe = Qe[_stateIndex[context]];
        _a -= qe;
        int decoded;
        if ((_c >> 16) < qe)
        {
            decoded = LpsExchange(context);
            Renormalize();
        }
        else
        {
            _c -= qe << 16;
            if ((_a & 0x8000) == 0)
            {
                decoded = MpsExchange(context);
                Renormalize();
            }
            else
            {
                decoded = _mps[context];
            }
        }

        return decoded;
    }

    private void ResetContexts()
    {
        Array.Clear(_stateIndex, 0, _stateIndex.Length);
        Array.Clear(_mps, 0, _mps.Length);
        _stateIndex[0] = 4;
        _stateIndex[17] = 3;
        _stateIndex[18] = 46;
    }

    private void InitializeDecoder()
    {
        _bp = 0;
        _c = (uint)((_data.Length == 0 ? 0xFF : _data[0]) << 16);
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    private int MpsExchange(int context)
    {
        if (_a < Qe[_stateIndex[context]])
        {
            int decoded = 1 - _mps[context];
            if (Switch[_stateIndex[context]])
                _mps[context] ^= 1;
            _stateIndex[context] = Nlps[_stateIndex[context]];
            return decoded;
        }

        int result = _mps[context];
        _stateIndex[context] = Nmps[_stateIndex[context]];
        return result;
    }

    private int LpsExchange(int context)
    {
        if (_a < Qe[_stateIndex[context]])
        {
            _a = Qe[_stateIndex[context]];
            int result = _mps[context];
            _stateIndex[context] = Nmps[_stateIndex[context]];
            return result;
        }

        _a = Qe[_stateIndex[context]];
        int decoded = 1 - _mps[context];
        if (Switch[_stateIndex[context]])
            _mps[context] ^= 1;
        _stateIndex[context] = Nlps[_stateIndex[context]];
        return decoded;
    }

    private void Renormalize()
    {
        do
        {
            if (_ct == 0)
                ByteIn();

            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while (_a < 0x8000);
    }

    private void ByteIn()
    {
        uint next = _bp + 1 < _data.Length ? _data[_bp + 1] : 0xFFu;
        uint current = _bp < _data.Length ? _data[_bp] : 0xFFu;
        if (current == 0xFF)
        {
            if (next > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
            }
            else
            {
                _bp++;
                _c += next << 9;
                _ct = 7;
            }
        }
        else
        {
            _bp++;
            _c += next << 8;
            _ct = 8;
        }
    }
}

internal static class Jpeg2000InverseWavelet
{
    public static void Transform53(int[] data, int globalX0, int globalY0, int width, int height, int levels)
    {
        if (levels <= 0)
            return;

        for (int resolutionIndex = 1; resolutionIndex <= levels; resolutionIndex++)
        {
            int reducedBy = levels - resolutionIndex;
            int currentX0 = CeilingDivPow2(globalX0, reducedBy);
            int currentY0 = CeilingDivPow2(globalY0, reducedBy);
            int currentX1 = CeilingDivPow2(globalX0 + width, reducedBy);
            int currentY1 = CeilingDivPow2(globalY0 + height, reducedBy);
            int currentWidth = currentX1 - currentX0;
            int currentHeight = currentY1 - currentY0;

            Inverse53Horizontal(data, width, currentWidth, currentHeight, (currentX0 & 1) != 0);
            Inverse53Vertical(data, width, currentWidth, currentHeight, (currentY0 & 1) != 0);
        }
    }

    private static void Inverse53Horizontal(int[] data, int stride, int width, int height, bool lowStartsOnOdd)
    {
        int[] temp = new int[width];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            Interleave53(data, rowOffset, 1, width, lowStartsOnOdd, temp);

            int evenStart = lowStartsOnOdd ? 1 : 0;
            int oddStart = lowStartsOnOdd ? 0 : 1;
            int lowLength = CountInterleavedSamples(width, evenStart);
            int highLength = CountInterleavedSamples(width, oddStart);

            for (int i = 0, pos = evenStart; i < lowLength; i++, pos += 2)
            {
                int leftHigh = pos - 1 >= 0 ? temp[pos - 1] : (pos + 1 < width ? temp[pos + 1] : 0);
                int rightHigh = pos + 1 < width ? temp[pos + 1] : leftHigh;
                temp[pos] -= (leftHigh + rightHigh + 2) >> 2;
            }

            for (int i = 0, pos = oddStart; i < highLength; i++, pos += 2)
            {
                int leftLow = temp[pos - 1];
                int rightLow = pos + 1 < width ? temp[pos + 1] : leftLow;
                temp[pos] += (leftLow + rightLow) >> 1;
            }

            Array.Copy(temp, 0, data, rowOffset, width);
        }
    }

    private static void Inverse53Vertical(int[] data, int stride, int width, int height, bool lowStartsOnOdd)
    {
        int[] temp = new int[height];
        for (int x = 0; x < width; x++)
        {
            Interleave53(data, x, stride, height, lowStartsOnOdd, temp);

            int evenStart = lowStartsOnOdd ? 1 : 0;
            int oddStart = lowStartsOnOdd ? 0 : 1;
            int lowLength = CountInterleavedSamples(height, evenStart);
            int highLength = CountInterleavedSamples(height, oddStart);

            for (int i = 0, pos = evenStart; i < lowLength; i++, pos += 2)
            {
                int upperHigh = pos - 1 >= 0 ? temp[pos - 1] : (pos + 1 < height ? temp[pos + 1] : 0);
                int lowerHigh = pos + 1 < height ? temp[pos + 1] : upperHigh;
                temp[pos] -= (upperHigh + lowerHigh + 2) >> 2;
            }

            for (int i = 0, pos = oddStart; i < highLength; i++, pos += 2)
            {
                int upperLow = temp[pos - 1];
                int lowerLow = pos + 1 < height ? temp[pos + 1] : upperLow;
                temp[pos] += (upperLow + lowerLow) >> 1;
            }

            for (int y = 0; y < height; y++)
                data[y * stride + x] = temp[y];
        }
    }

    public static void Transform97(float[] data, int globalX0, int globalY0, int width, int height, int levels)
    {
        if (levels <= 0)
            return;

        for (int resolutionIndex = 1; resolutionIndex <= levels; resolutionIndex++)
        {
            int reducedBy = levels - resolutionIndex;
            int currentX0 = CeilingDivPow2(globalX0, reducedBy);
            int currentY0 = CeilingDivPow2(globalY0, reducedBy);
            int currentX1 = CeilingDivPow2(globalX0 + width, reducedBy);
            int currentY1 = CeilingDivPow2(globalY0 + height, reducedBy);
            int currentWidth = currentX1 - currentX0;
            int currentHeight = currentY1 - currentY0;

            Inverse97Horizontal(data, width, currentWidth, currentHeight, (currentX0 & 1) != 0);
            Inverse97Vertical(data, width, currentWidth, currentHeight, (currentY0 & 1) != 0);
        }
    }

    private static void Inverse97Horizontal(float[] data, int stride, int width, int height, bool lowStartsOnOdd)
    {
        float[] temp = new float[width];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            Interleave97(data, rowOffset, 1, width, lowStartsOnOdd, temp);
            Synthesize97(temp, 0, width);
            Array.Copy(temp, 0, data, rowOffset, width);
        }
    }

    private static void Inverse97Vertical(float[] data, int stride, int width, int height, bool lowStartsOnOdd)
    {
        float[] temp = new float[height];
        for (int x = 0; x < width; x++)
        {
            Interleave97(data, x, stride, height, lowStartsOnOdd, temp);
            Synthesize97(temp, 0, height);
            for (int y = 0; y < height; y++)
                data[y * stride + x] = temp[y];
        }
    }

    private static void Interleave97(float[] source, int offset, int step, int length, bool lowStartsOnOdd, float[] target)
    {
        int lowStart = lowStartsOnOdd ? 1 : 0;
        int highStart = lowStartsOnOdd ? 0 : 1;
        int lowLength = CountInterleavedSamples(length, lowStart);
        int highLength = CountInterleavedSamples(length, highStart);
        for (int i = 0; i < lowLength; i++)
            target[lowStart + (i << 1)] = source[offset + i * step];
        for (int i = 0; i < highLength; i++)
            target[highStart + (i << 1)] = source[offset + (lowLength + i) * step];
    }

    private static void Interleave53(int[] source, int offset, int step, int length, bool lowStartsOnOdd, int[] target)
    {
        int lowStart = lowStartsOnOdd ? 1 : 0;
        int highStart = lowStartsOnOdd ? 0 : 1;
        int lowLength = CountInterleavedSamples(length, lowStart);
        int highLength = CountInterleavedSamples(length, highStart);
        for (int i = 0; i < lowLength; i++)
            target[lowStart + (i << 1)] = source[offset + i * step];
        for (int i = 0; i < highLength; i++)
            target[highStart + (i << 1)] = source[offset + (lowLength + i) * step];
    }

    private static void Synthesize97(float[] p, int i0, int i1)
    {
        // JPEG2000 irreversible 9/7 lifting coefficients. Alpha and Beta are
        // negative in the forward transform, so the inverse step must subtract
        // those signed values (which effectively becomes addition).
        const float Alpha = -1.586134342059924f;
        const float Beta = -0.052980118572961f;
        const float Gamma = 0.882911075530934f;
        const float Delta = 0.443506852043971f;
        const float K = 1.230174104914001f;
        const float X = 1.0f / K;

        if (i1 <= i0)
            return;

        if (i1 == i0 + 1)
        {
            p[i0] *= X;
            return;
        }

        for (int i = i0 + 1; i < i1; i += 2)
            p[i] *= K;

        for (int i = i0; i < i1; i += 2)
            p[i] *= X;

        // Use symmetric extension at both ends. This keeps the edge handling
        // explicit and avoids parity mistakes for the final odd/even samples.
        for (int i = i0; i < i1; i += 2)
        {
            float left = i - 1 >= i0 ? p[i - 1] : (i + 1 < i1 ? p[i + 1] : 0f);
            float right = i + 1 < i1 ? p[i + 1] : left;
            p[i] -= Delta * (left + right);
        }

        for (int i = i0 + 1; i < i1; i += 2)
        {
            float left = p[i - 1];
            float right = i + 1 < i1 ? p[i + 1] : left;
            p[i] -= Gamma * (left + right);
        }

        for (int i = i0; i < i1; i += 2)
        {
            float left = i - 1 >= i0 ? p[i - 1] : (i + 1 < i1 ? p[i + 1] : 0f);
            float right = i + 1 < i1 ? p[i + 1] : left;
            p[i] -= Beta * (left + right);
        }

        for (int i = i0 + 1; i < i1; i += 2)
        {
            float left = p[i - 1];
            float right = i + 1 < i1 ? p[i + 1] : left;
            p[i] -= Alpha * (left + right);
        }
    }

    private static int CountInterleavedSamples(int length, int startIndex)
    {
        if (length <= startIndex)
            return 0;

        return ((length - startIndex) + 1) >> 1;
    }

    private static int CeilingDivPow2(int value, int power)
        => power <= 0 ? value : (value + (1 << power) - 1) >> power;
}
