using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PdfCore.Images.Jpeg2000;

public static class Jpeg2000Decoder
{
    public static string Describe(byte[] data, bool parsePackets = false)
    {
        Jpeg2000Codestream codestream = Jpeg2000Codestream.Parse(data);
        var sb = new StringBuilder();
        sb.Append($"JPX {codestream.Size.Width}x{codestream.Size.Height}, comps={codestream.Size.Components.Count}, ");
        sb.Append($"tiles={codestream.Size.TileCount}, tileParts={codestream.TileParts.Count}, ");
        sb.Append($"levels={codestream.CodingStyle.DecompositionLevels}, layers={codestream.CodingStyle.Layers}, ");
        sb.Append($"prog={codestream.CodingStyle.ProgressionOrder}, cb={codestream.CodingStyle.CodeBlockWidth}x{codestream.CodingStyle.CodeBlockHeight}, ");
        sb.Append($"transform={codestream.CodingStyle.Transform}, qstyle={codestream.Quantization.Style}, qsteps={codestream.Quantization.Steps.Count}, ");
        sb.Append($"scod=0x{codestream.CodingStyle.Scod:X2}, precincts={((codestream.CodingStyle.Scod & 0x01) != 0 ? "yes" : "no")}, ");
        sb.Append($"sop={((codestream.CodingStyle.Scod & 0x02) != 0 ? "yes" : "no")}, eph={((codestream.CodingStyle.Scod & 0x04) != 0 ? "yes" : "no")}");

        if (parsePackets)
        {
            int codeBlocks = 0;
            int included = 0;
            int bytes = 0;
            int passes = 0;
            foreach (Jpeg2000TilePart tilePart in codestream.TileParts)
            {
                Jpeg2000PacketTile packetTile = Jpeg2000PacketParser.ParseTile(codestream, tilePart);
                foreach (Jpeg2000PacketComponent component in packetTile.Components)
                foreach (Jpeg2000PacketResolution resolution in component.Resolutions)
                foreach (Jpeg2000PacketSubband subband in resolution.Subbands)
                foreach (Jpeg2000PacketCodeBlock codeBlock in subband.CodeBlocks)
                {
                    codeBlocks++;
                    if (codeBlock.Included)
                    {
                        included++;
                        passes += codeBlock.CodingPasses;
                        bytes += codeBlock.SegmentLengths.Sum();
                    }
                }
            }

            sb.Append($", codeblocks={codeBlocks}, included={included}, passes={passes}, codedBytes={bytes}");
        }

        return sb.ToString();
    }

    public static string DescribeGeometry(byte[] data, int tileIndex = 0)
    {
        Jpeg2000Codestream codestream = Jpeg2000Codestream.Parse(data);
        if (tileIndex < 0 || tileIndex >= codestream.Size.TileCount)
            throw new ArgumentOutOfRangeException(nameof(tileIndex));

        Jpeg2000TileGeometry tile = Jpeg2000TileGeometryBuilder.Build(codestream, tileIndex);
        var sb = new StringBuilder();
        sb.AppendLine(
            $"tile={tile.TileIndex}, bounds=[{tile.TileBounds.X0},{tile.TileBounds.Y0}..{tile.TileBounds.X1},{tile.TileBounds.Y1}], " +
            $"components={tile.Components.Count}");

        foreach (Jpeg2000TileComponentGeometry component in tile.Components)
        {
            sb.AppendLine(
                $"  comp={component.ComponentIndex}, bounds=[{component.Bounds.X0},{component.Bounds.Y0}..{component.Bounds.X1},{component.Bounds.Y1}], " +
                $"resolutions={component.Resolutions.Count}");

            foreach (Jpeg2000ResolutionGeometry resolution in component.Resolutions)
            {
                sb.AppendLine(
                    $"    res={resolution.ResolutionIndex}, bounds=[{resolution.Bounds.X0},{resolution.Bounds.Y0}..{resolution.Bounds.X1},{resolution.Bounds.Y1}], " +
                    $"subbands={resolution.Subbands.Count}");

                foreach (Jpeg2000SubbandGeometry subband in resolution.Subbands)
                {
                    sb.AppendLine(
                        $"      sb={subband.Kind}, q={subband.QuantizationIndex}, bounds=[{subband.Bounds.X0},{subband.Bounds.Y0}..{subband.Bounds.X1},{subband.Bounds.Y1}], " +
                        $"grid={subband.CodeBlockColumns}x{subband.CodeBlockRows}, codeblocks={subband.CodeBlocks.Count}");

                    foreach (Jpeg2000CodeBlockGeometry codeBlock in subband.CodeBlocks)
                    {
                        sb.AppendLine(
                            $"        cb={codeBlock.Index}, grid=({codeBlock.GridX},{codeBlock.GridY}), " +
                            $"bounds=[{codeBlock.Bounds.X0},{codeBlock.Bounds.Y0}..{codeBlock.Bounds.X1},{codeBlock.Bounds.Y1}]");
                    }
                }
            }
        }

        return sb.ToString();
    }

    public static Bitmap Decode(byte[] data)
    {
        if (data.Length == 0)
            throw new InvalidOperationException("Empty JPX image stream.");

        Jpeg2000Codestream codestream = Jpeg2000Codestream.Parse(data);
        if (Jpeg2000InternalDecoder.CanDecode(codestream))
            return Jpeg2000InternalDecoder.Decode(codestream);

        if (TryDecodeWithWic(data, out Bitmap? bitmap, out string? wicError))
            return bitmap;

        Jpeg2000Info info = Jpeg2000Info.FromCodestream(codestream);
        throw new NotSupportedException(
            "JPXDecode/JPEG2000 image was recognized, but this Windows installation does not provide a JPEG2000 WIC codec " +
            "and the internal JPEG2000 decoder does not yet support this codestream profile. " +
            $"{info}. WIC error: {wicError}");
    }

    private static bool TryDecodeWithWic(byte[] data, out Bitmap bitmap, out string? error)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            BitmapSource frame = decoder.Frames[0];
            BitmapSource converted = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            bitmap = new Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
            BitmapData locked = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                DrawingPixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    IntPtr dest = locked.Scan0 + y * locked.Stride;
                    Marshal.Copy(pixels, y * stride, dest, stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(locked);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException ||
                                   ex is FileFormatException ||
                                   ex is IOException ||
                                   ex is InvalidOperationException ||
                                   ex is COMException)
        {
            bitmap = null!;
            error = ex.Message;
            return false;
        }
    }
}

internal sealed record Jpeg2000Info(
    bool IsJp2Container,
    int Width,
    int Height,
    int Components,
    int TileWidth,
    int TileHeight,
    int Levels,
    int Layers,
    int ProgressionOrder,
    int MultipleComponentTransform,
    int Transform,
    byte CodeBlockStyle)
{
    public static Jpeg2000Info Parse(byte[] data)
    {
        Jpeg2000Codestream codestream = Jpeg2000Codestream.Parse(data);
        return FromCodestream(codestream);
    }

    public static Jpeg2000Info FromCodestream(Jpeg2000Codestream codestream)
    {
        return new Jpeg2000Info(
            codestream.IsJp2Container,
            codestream.Size.Width,
            codestream.Size.Height,
            codestream.Size.Components.Count,
            codestream.Size.XTsiz,
            codestream.Size.YTsiz,
            codestream.CodingStyle.DecompositionLevels,
            codestream.CodingStyle.Layers,
            codestream.CodingStyle.ProgressionOrder,
            codestream.CodingStyle.MultipleComponentTransform,
            codestream.CodingStyle.Transform,
            codestream.CodingStyle.CodeBlockStyle);
    }

    public override string ToString()
    {
        string transformName = Transform switch
        {
            0 => "9/7 irreversible",
            1 => "5/3 reversible",
            _ => $"unknown transform {Transform}"
        };

        string progression = ProgressionOrder switch
        {
            0 => "LRCP",
            1 => "RLCP",
            2 => "RPCL",
            3 => "PCRL",
            4 => "CPRL",
            _ => ProgressionOrder.ToString()
        };

        return $"container={(IsJp2Container ? "JP2" : "raw codestream")}, {Width}x{Height}, comps={Components}, " +
               $"tile={TileWidth}x{TileHeight}, levels={Levels}, layers={Layers}, progression={progression}, " +
               $"mct={MultipleComponentTransform}, transform={transformName}, codeBlockStyle=0x{CodeBlockStyle:X2}";
    }

    private static int FindCodestreamOffset(byte[] data, out bool isJp2)
    {
        isJp2 = false;
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F)
            return 0;

        int offset = 0;
        for (int guard = 0; guard < 128 && offset + 8 <= data.Length; guard++)
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

internal ref struct BigEndianReader
{
    private readonly ReadOnlySpan<byte> _data;

    public BigEndianReader(byte[] data, int position)
    {
        _data = data;
        Position = position;
    }

    public int Position { get; set; }

    public bool HasBytes(int count) => Position + count <= _data.Length;

    public byte ReadByte() => _data[Position++];

    public ushort ReadUInt16()
    {
        ushort value = (ushort)((_data[Position] << 8) | _data[Position + 1]);
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        uint value = ((uint)_data[Position] << 24) |
                     ((uint)_data[Position + 1] << 16) |
                     ((uint)_data[Position + 2] << 8) |
                     _data[Position + 3];
        Position += 4;
        return value;
    }

    public void ExpectMarker(byte marker)
    {
        if (!TryReadMarker(out byte actual) || actual != marker)
            throw new InvalidOperationException($"Expected JPEG2000 marker FF{marker:X2}.");
    }

    public bool TryReadMarker(out byte marker)
    {
        marker = 0;
        while (HasBytes(1) && _data[Position] != 0xFF)
            Position++;

        if (!HasBytes(2))
            return false;

        while (HasBytes(1) && _data[Position] == 0xFF)
            Position++;

        if (!HasBytes(1))
            return false;

        marker = _data[Position++];
        return marker != 0x00;
    }
}
