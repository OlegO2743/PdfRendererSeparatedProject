namespace PdfCore.Images.Jpeg2000;

internal static class Jpeg2000TileGeometryBuilder
{
    public static Jpeg2000TileGeometry Build(Jpeg2000Codestream codestream, int tileIndex)
    {
        Jpeg2000TileBounds tileBounds = codestream.Size.GetTileBounds(tileIndex);
        var components = new List<Jpeg2000TileComponentGeometry>(codestream.Size.Components.Count);
        for (int componentIndex = 0; componentIndex < codestream.Size.Components.Count; componentIndex++)
        {
            Jpeg2000ComponentSize componentSize = codestream.Size.Components[componentIndex];
            components.Add(BuildComponent(codestream, tileBounds, componentIndex, componentSize));
        }

        return new Jpeg2000TileGeometry(tileIndex, tileBounds, components);
    }

    private static Jpeg2000TileComponentGeometry BuildComponent(
        Jpeg2000Codestream codestream,
        Jpeg2000TileBounds tileBounds,
        int componentIndex,
        Jpeg2000ComponentSize componentSize)
    {
        Jpeg2000CodingStyle codingStyle = codestream.CodingStyle;
        int levels = codingStyle.DecompositionLevels;

        int globalX0 = CeilingDiv(tileBounds.X0, componentSize.XRsiz);
        int globalY0 = CeilingDiv(tileBounds.Y0, componentSize.YRsiz);
        int globalX1 = CeilingDiv(tileBounds.X1, componentSize.XRsiz);
        int globalY1 = CeilingDiv(tileBounds.Y1, componentSize.YRsiz);

        int width = globalX1 - globalX0;
        int height = globalY1 - globalY0;
        Jpeg2000BandBounds globalBounds = new(globalX0, globalY0, globalX1, globalY1);

        var resolutions = new List<Jpeg2000ResolutionGeometry>(levels + 1);
        int quantizationIndex = 0;
        for (int resolutionIndex = 0; resolutionIndex <= levels; resolutionIndex++)
        {
            int reducedBy = levels - resolutionIndex;
            int currentX0 = CeilingDivPow2(globalX0, reducedBy);
            int currentY0 = CeilingDivPow2(globalY0, reducedBy);
            int currentX1 = CeilingDivPow2(globalX1, reducedBy);
            int currentY1 = CeilingDivPow2(globalY1, reducedBy);
            int currentWidth = currentX1 - currentX0;
            int currentHeight = currentY1 - currentY0;
            var resolutionBounds = new Jpeg2000BandBounds(
                0,
                0,
                currentWidth,
                currentHeight);

            var subbands = new List<Jpeg2000SubbandGeometry>(resolutionIndex == 0 ? 1 : 3);
            if (resolutionIndex == 0)
            {
                subbands.Add(BuildSubband(
                    Jpeg2000SubbandKind.LL,
                    resolutionIndex,
                    quantizationIndex++,
                    resolutionBounds,
                    codingStyle));
            }
            else
            {
                int lowWidth = CeilingDiv(currentX1, 2) - CeilingDiv(currentX0, 2);
                int lowHeight = CeilingDiv(currentY1, 2) - CeilingDiv(currentY0, 2);
                int highWidth = FloorDiv(currentX1, 2) - FloorDiv(currentX0, 2);
                int highHeight = FloorDiv(currentY1, 2) - FloorDiv(currentY0, 2);
                subbands.Add(BuildWaveletSubband(Jpeg2000SubbandKind.HL, resolutionIndex, quantizationIndex++, lowWidth, lowHeight, highWidth, highHeight, codingStyle));
                subbands.Add(BuildWaveletSubband(Jpeg2000SubbandKind.LH, resolutionIndex, quantizationIndex++, lowWidth, lowHeight, highWidth, highHeight, codingStyle));
                subbands.Add(BuildWaveletSubband(Jpeg2000SubbandKind.HH, resolutionIndex, quantizationIndex++, lowWidth, lowHeight, highWidth, highHeight, codingStyle));
            }

            resolutions.Add(new Jpeg2000ResolutionGeometry(resolutionIndex, resolutionBounds, subbands));
        }

        return new Jpeg2000TileComponentGeometry(
            componentIndex,
            new Jpeg2000BandBounds(0, 0, width, height),
            globalBounds,
            resolutions);
    }

    private static Jpeg2000SubbandGeometry BuildWaveletSubband(
        Jpeg2000SubbandKind kind,
        int resolutionIndex,
        int quantizationIndex,
        int lowWidth,
        int lowHeight,
        int highWidth,
        int highHeight,
        Jpeg2000CodingStyle codingStyle)
    {
        Jpeg2000BandBounds bounds = kind switch
        {
            Jpeg2000SubbandKind.HL => new Jpeg2000BandBounds(lowWidth, 0, lowWidth + highWidth, lowHeight),
            Jpeg2000SubbandKind.LH => new Jpeg2000BandBounds(0, lowHeight, lowWidth, lowHeight + highHeight),
            Jpeg2000SubbandKind.HH => new Jpeg2000BandBounds(lowWidth, lowHeight, lowWidth + highWidth, lowHeight + highHeight),
            _ => throw new InvalidOperationException($"Unexpected wavelet subband kind {kind}.")
        };

        return BuildSubband(kind, resolutionIndex, quantizationIndex, bounds, codingStyle);
    }

    private static Jpeg2000SubbandGeometry BuildSubband(
        Jpeg2000SubbandKind kind,
        int resolutionIndex,
        int quantizationIndex,
        Jpeg2000BandBounds bounds,
        Jpeg2000CodingStyle codingStyle)
    {
        int codeBlockWidth = codingStyle.CodeBlockWidth;
        int codeBlockHeight = codingStyle.CodeBlockHeight;
        int localWidth = bounds.Width;
        int localHeight = bounds.Height;

        // JPEG2000 packet headers enumerate code-blocks in the local subband grid.
        // The code-block partition starts at the subband origin, not at the absolute
        // coefficient-space coordinate inside the full tile/component buffer.
        //
        // We still keep absolute bounds for coefficient placement during inverse DWT,
        // but the grid dimensions and code-block enumeration must be computed from the
        // subband-local width/height, otherwise partial edge subbands create phantom
        // 1px-wide code-blocks and packet parsing drifts out of sync.
        int gridX0 = 0;
        int gridY0 = 0;
        int gridX1 = CeilingDiv(localWidth, codeBlockWidth);
        int gridY1 = CeilingDiv(localHeight, codeBlockHeight);

        var codeBlocks = new List<Jpeg2000CodeBlockGeometry>();
        for (int gy = gridY0; gy < gridY1; gy++)
        {
            for (int gx = gridX0; gx < gridX1; gx++)
            {
                int x0 = bounds.X0 + gx * codeBlockWidth;
                int y0 = bounds.Y0 + gy * codeBlockHeight;
                int x1 = Math.Min(bounds.X1, bounds.X0 + (gx + 1) * codeBlockWidth);
                int y1 = Math.Min(bounds.Y1, bounds.Y0 + (gy + 1) * codeBlockHeight);
                if (x1 > x0 && y1 > y0)
                {
                    codeBlocks.Add(new Jpeg2000CodeBlockGeometry(
                        codeBlocks.Count,
                        gx,
                        gy,
                        new Jpeg2000BandBounds(x0, y0, x1, y1)));
                }
            }
        }

        int columns = Math.Max(0, gridX1);
        int rows = Math.Max(0, gridY1);
        return new Jpeg2000SubbandGeometry(
            kind,
            resolutionIndex,
            quantizationIndex,
            bounds,
            columns,
            rows,
            codeBlocks);
    }

    private static int CeilingDivPow2(int value, int power) => CeilingDiv(value, 1 << power);
    private static int FloorDiv(int value, int divisor) => value / divisor;

    private static int CeilingDiv(int value, int divisor)
    {
        if (value >= 0)
            return (value + divisor - 1) / divisor;

        return -((-value) / divisor);
    }
}

internal sealed record Jpeg2000TileGeometry(
    int TileIndex,
    Jpeg2000TileBounds TileBounds,
    IReadOnlyList<Jpeg2000TileComponentGeometry> Components);

internal sealed record Jpeg2000TileComponentGeometry(
    int ComponentIndex,
    Jpeg2000BandBounds Bounds,
    Jpeg2000BandBounds GlobalBounds,
    IReadOnlyList<Jpeg2000ResolutionGeometry> Resolutions);

internal sealed record Jpeg2000ResolutionGeometry(
    int ResolutionIndex,
    Jpeg2000BandBounds Bounds,
    IReadOnlyList<Jpeg2000SubbandGeometry> Subbands);

internal sealed record Jpeg2000SubbandGeometry(
    Jpeg2000SubbandKind Kind,
    int ResolutionIndex,
    int QuantizationIndex,
    Jpeg2000BandBounds Bounds,
    int CodeBlockColumns,
    int CodeBlockRows,
    IReadOnlyList<Jpeg2000CodeBlockGeometry> CodeBlocks);

internal sealed record Jpeg2000CodeBlockGeometry(
    int Index,
    int GridX,
    int GridY,
    Jpeg2000BandBounds Bounds);

internal sealed record Jpeg2000BandBounds(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;
}

internal enum Jpeg2000SubbandKind
{
    LL,
    HL,
    LH,
    HH
}
