using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using PdfCore.Color;
using PdfCore.Images.Jpeg2000;
using DrawingColor = System.Drawing.Color;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PdfCore.Resources;

public sealed class PdfImageXObject
{
    public string ResourceName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerComponent { get; init; } = 8;
    public PdfColorSpace ColorSpace { get; init; } = new PdfDeviceRgbColorSpace();
    public string Filter { get; init; } = string.Empty;
    public byte[] ImageBytes { get; init; } = Array.Empty<byte>();
    public PdfImageXObject? SoftMask { get; init; }
    public bool IsImageMask { get; init; }
    public bool DecodeInverted { get; init; }
    public int CcittK { get; init; }

    public Bitmap CreateBitmap(DrawingColor? maskColor = null)
    {
        PdfColorSpace effectiveColorSpace = ColorSpace;
        if (effectiveColorSpace is PdfIccBasedColorSpace icc)
        {
            if (PdfColorManagementSettings.Mode == PdfColorManagementMode.ExperimentalPhase2Icc &&
                icc.N == 4 && BitsPerComponent == 8)
            {
                Bitmap? experimental = PdfIccColorConverter.TryConvertCmykBytesToBitmap(ImageBytes, Width, Height, icc);
                if (experimental != null)
                    return experimental;
            }

            effectiveColorSpace = icc.GetFallback();
        }

        Bitmap bitmap;
        // Compressed image formats carry their own sample representation.
        // Do not route JPX/JPEG or CCITT streams into the raw Indexed path,
        // otherwise compressed codestream bytes get misread as unpacked indices.
        if (Filter == "/DCTDecode")
        {
            using var ms = new MemoryStream(ImageBytes);
            using var tmp = new Bitmap(ms);
            bitmap = new Bitmap(tmp);
            return ApplySoftMaskIfPresent(bitmap);
        }

        if (Filter == "/JPXDecode")
        {
            bitmap = Jpeg2000Decoder.Decode(ImageBytes);
            if (effectiveColorSpace is PdfIndexedColorSpace indexedColorSpace)
                bitmap = ApplyIndexedPaletteToDecodedBitmap(bitmap, indexedColorSpace);
            return ApplySoftMaskIfPresent(bitmap);
        }

        if (Filter == "/CCITTFaxDecode")
            return CreateCcittBitmap(maskColor ?? DrawingColor.Black);

        if (effectiveColorSpace is PdfIndexedColorSpace indexed)
        {
            bitmap = CreateIndexedBitmap(indexed);
            return ApplySoftMaskIfPresent(bitmap);
        }

        if (IsImageMask && (string.IsNullOrEmpty(Filter) || Filter == "/FlateDecode"))
            return CreateRawImageMaskBitmap(maskColor ?? DrawingColor.Black);

        if (!string.IsNullOrEmpty(Filter) && Filter != "/FlateDecode")
            throw new NotSupportedException("Image Filter " + Filter + " is not supported.");

        bitmap = effectiveColorSpace switch
        {
            PdfDeviceGrayColorSpace => CreateGrayBitmap(),
            PdfDeviceRgbColorSpace => CreateRgbBitmap(),
            PdfDeviceCmykColorSpace => CreateCmykBitmap(),
            _ => throw new NotSupportedException($"Image ColorSpace {effectiveColorSpace} пока не поддержан.")
        };

        return ApplySoftMaskIfPresent(bitmap);
    }

    private Bitmap CreateCcittBitmap(DrawingColor maskColor)
    {
        if (CcittK >= 0)
            throw new NotSupportedException("Only CCITT Group 4 image XObjects are supported.");

        byte[] tiffBytes = BuildCcittGroup4Tiff();
        using var ms = new MemoryStream(tiffBytes);
        using var decoded = new Bitmap(ms);
        using var copy = new Bitmap(decoded);

        if (IsImageMask)
            return CreateImageMaskBitmap(copy, maskColor);

        return new Bitmap(copy);
    }

    private Bitmap CreateImageMaskBitmap(Bitmap decoded, DrawingColor maskColor)
    {
        var bitmap = new Bitmap(Width, Height, DrawingPixelFormat.Format32bppArgb);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                DrawingColor sample = decoded.GetPixel(x, y);
                bool paint = sample.GetBrightness() < 0.5f;
                if (DecodeInverted)
                    paint = !paint;

                bitmap.SetPixel(
                    x,
                    y,
                    paint ? DrawingColor.FromArgb(maskColor.R, maskColor.G, maskColor.B) : DrawingColor.Transparent);
            }
        }

        return bitmap;
    }

    private Bitmap CreateRawImageMaskBitmap(DrawingColor maskColor)
    {
        if (BitsPerComponent != 1)
            throw new NotSupportedException("Only 1-bit image masks are supported.");

        int rowBytes = (Width + 7) / 8;
        int expected = rowBytes * Height;
        if (ImageBytes.Length < expected)
            throw new InvalidOperationException("Not enough data for image mask.");

        var bitmap = new Bitmap(Width, Height, DrawingPixelFormat.Format32bppArgb);
        for (int y = 0; y < Height; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < Width; x++)
            {
                byte packed = ImageBytes[row + x / 8];
                bool bitSet = (packed & (0x80 >> (x & 7))) != 0;
                bool paint = DecodeInverted ? !bitSet : bitSet;
                bitmap.SetPixel(
                    x,
                    y,
                    paint ? DrawingColor.FromArgb(maskColor.R, maskColor.G, maskColor.B) : DrawingColor.Transparent);
            }
        }

        return bitmap;
    }

    private byte[] BuildCcittGroup4Tiff()
    {
        const ushort typeShort = 3;
        const ushort typeLong = 4;
        const ushort typeRational = 5;
        const ushort tagCount = 13;

        int ifdOffset = 8;
        int ifdSize = 2 + tagCount * 12 + 4;
        int xResolutionOffset = ifdOffset + ifdSize;
        int yResolutionOffset = xResolutionOffset + 8;
        int stripOffset = yResolutionOffset + 8;

        using var ms = new MemoryStream(stripOffset + ImageBytes.Length);
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)'I');
        writer.Write((byte)'I');
        WriteUInt16(writer, 42);
        WriteUInt32(writer, (uint)ifdOffset);

        WriteUInt16(writer, tagCount);
        WriteLongTag(writer, 256, (uint)Width); // ImageWidth
        WriteLongTag(writer, 257, (uint)Height); // ImageLength
        WriteShortTag(writer, 258, (ushort)BitsPerComponent); // BitsPerSample
        WriteShortTag(writer, 259, 4); // Compression: CCITT Group 4
        WriteShortTag(writer, 262, 0); // PhotometricInterpretation: WhiteIsZero
        WriteShortTag(writer, 266, 1); // FillOrder
        WriteLongTag(writer, 273, (uint)stripOffset); // StripOffsets
        WriteShortTag(writer, 277, 1); // SamplesPerPixel
        WriteLongTag(writer, 278, (uint)Height); // RowsPerStrip
        WriteLongTag(writer, 279, (uint)ImageBytes.Length); // StripByteCounts
        WriteRationalTag(writer, 282, (uint)xResolutionOffset); // XResolution
        WriteRationalTag(writer, 283, (uint)yResolutionOffset); // YResolution
        WriteShortTag(writer, 296, 2); // ResolutionUnit: inch
        WriteUInt32(writer, 0);

        WriteUInt32(writer, 72);
        WriteUInt32(writer, 1);
        WriteUInt32(writer, 72);
        WriteUInt32(writer, 1);
        writer.Write(ImageBytes);

        return ms.ToArray();

        static void WriteShortTag(BinaryWriter writer, ushort tag, ushort value)
        {
            WriteUInt16(writer, tag);
            WriteUInt16(writer, typeShort);
            WriteUInt32(writer, 1);
            WriteUInt16(writer, value);
            WriteUInt16(writer, 0);
        }

        static void WriteLongTag(BinaryWriter writer, ushort tag, uint value)
        {
            WriteUInt16(writer, tag);
            WriteUInt16(writer, typeLong);
            WriteUInt32(writer, 1);
            WriteUInt32(writer, value);
        }

        static void WriteRationalTag(BinaryWriter writer, ushort tag, uint offset)
        {
            WriteUInt16(writer, tag);
            WriteUInt16(writer, typeRational);
            WriteUInt32(writer, 1);
            WriteUInt32(writer, offset);
        }

        static void WriteUInt16(BinaryWriter writer, ushort value)
        {
            writer.Write(value);
        }

        static void WriteUInt32(BinaryWriter writer, uint value)
        {
            writer.Write(value);
        }
    }

    private Bitmap CreateIndexedBitmap(PdfIndexedColorSpace colorSpace)
    {
        if (BitsPerComponent != 1 &&
            BitsPerComponent != 2 &&
            BitsPerComponent != 4 &&
            BitsPerComponent != 8)
        {
            throw new NotSupportedException("Only BitsPerComponent=1,2,4,8 is supported for Indexed image.");
        }

        int rowBytes = ((Width * BitsPerComponent) + 7) / 8;
        int expected = rowBytes * Height;
        if (ImageBytes.Length < expected)
            throw new InvalidOperationException("Not enough data for Indexed image XObject.");

        PdfColorSpace baseColorSpace = colorSpace.BaseColorSpace.GetFallback();
        int components = baseColorSpace.Components;
        var bmp = new Bitmap(Width, Height, DrawingPixelFormat.Format24bppRgb);
        for (int y = 0; y < Height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < Width; x++)
            {
                int index = Math.Min(ReadPackedSample(ImageBytes, rowStart, x, BitsPerComponent), colorSpace.HighValue);
                int lookupIndex = index * components;
                bmp.SetPixel(x, y, LookupIndexedColor(colorSpace.Lookup, lookupIndex, baseColorSpace));
            }
        }

        return bmp;
    }

    private static int ReadPackedSample(byte[] data, int rowStart, int x, int bitsPerComponent)
    {
        if (bitsPerComponent == 8)
            return data[rowStart + x];

        int bitOffset = x * bitsPerComponent;
        int byteIndex = rowStart + bitOffset / 8;
        int bitInByte = bitOffset % 8;
        int shift = 8 - bitInByte - bitsPerComponent;
        int mask = (1 << bitsPerComponent) - 1;
        return (data[byteIndex] >> shift) & mask;
    }

    private static DrawingColor LookupIndexedColor(byte[] lookup, int index, PdfColorSpace baseColorSpace)
    {
        if (baseColorSpace is PdfDeviceGrayColorSpace)
        {
            byte gray = index < lookup.Length ? lookup[index] : (byte)0;
            return DrawingColor.FromArgb(gray, gray, gray);
        }

        if (baseColorSpace is PdfDeviceRgbColorSpace)
        {
            byte r = index < lookup.Length ? lookup[index] : (byte)0;
            byte g = index + 1 < lookup.Length ? lookup[index + 1] : r;
            byte b = index + 2 < lookup.Length ? lookup[index + 2] : r;
            return DrawingColor.FromArgb(r, g, b);
        }

        if (baseColorSpace is PdfDeviceCmykColorSpace)
        {
            byte c = index < lookup.Length ? lookup[index] : (byte)0;
            byte m = index + 1 < lookup.Length ? lookup[index + 1] : (byte)0;
            byte y = index + 2 < lookup.Length ? lookup[index + 2] : (byte)0;
            byte k = index + 3 < lookup.Length ? lookup[index + 3] : (byte)0;
            return CmykToRgb(c, m, y, k);
        }

        return DrawingColor.Black;
    }

    private static Bitmap ApplyIndexedPaletteToDecodedBitmap(Bitmap source, PdfIndexedColorSpace colorSpace)
    {
        PdfColorSpace baseColorSpace = colorSpace.BaseColorSpace.GetFallback();
        var mapped = new Bitmap(source.Width, source.Height, DrawingPixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                DrawingColor sample = source.GetPixel(x, y);
                int index = Math.Min(sample.R, colorSpace.HighValue);
                int lookupIndex = index * baseColorSpace.Components;
                DrawingColor mappedColor = LookupIndexedColor(colorSpace.Lookup, lookupIndex, baseColorSpace);
                mapped.SetPixel(x, y, DrawingColor.FromArgb(sample.A, mappedColor.R, mappedColor.G, mappedColor.B));
            }
        }

        source.Dispose();
        return mapped;
    }

    private Bitmap CreateGrayBitmap()
    {
        if (BitsPerComponent != 8)
            throw new NotSupportedException("Поддерживается только BitsPerComponent=8 для DeviceGray.");

        int expected = Width * Height;
        if (ImageBytes.Length < expected)
            throw new InvalidOperationException("Недостаточно данных для DeviceGray image XObject.");

        var bmp = new Bitmap(Width, Height, DrawingPixelFormat.Format24bppRgb);
        int i = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int v = ImageBytes[i++];
                bmp.SetPixel(x, y, DrawingColor.FromArgb(v, v, v));
            }
        }
        return bmp;
    }

    private Bitmap CreateRgbBitmap()
    {
        if (BitsPerComponent != 8)
            throw new NotSupportedException("Поддерживается только BitsPerComponent=8 для DeviceRGB.");

        int expected = Width * Height * 3;
        if (ImageBytes.Length < expected)
            throw new InvalidOperationException("Недостаточно данных для DeviceRGB image XObject.");

        var bmp = new Bitmap(Width, Height, DrawingPixelFormat.Format24bppRgb);
        int i = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte r = ImageBytes[i++];
                byte g = ImageBytes[i++];
                byte b = ImageBytes[i++];
                bmp.SetPixel(x, y, DrawingColor.FromArgb(r, g, b));
            }
        }
        return bmp;
    }

    private Bitmap CreateCmykBitmap()
    {
        if (BitsPerComponent != 8)
            throw new NotSupportedException("Поддерживается только BitsPerComponent=8 для DeviceCMYK.");

        int expected = Width * Height * 4;
        if (ImageBytes.Length < expected)
            throw new InvalidOperationException("Недостаточно данных для DeviceCMYK image XObject.");

        var bmp = new Bitmap(Width, Height, DrawingPixelFormat.Format24bppRgb);
        int i = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte c = ImageBytes[i++];
                byte m = ImageBytes[i++];
                byte yy = ImageBytes[i++];
                byte k = ImageBytes[i++];
                bmp.SetPixel(x, y, CmykToRgb(c, m, yy, k));
            }
        }
        return bmp;
    }

    private static DrawingColor CmykToRgb(byte cByte, byte mByte, byte yByte, byte kByte)
    {
        float c = cByte / 255f;
        float m = mByte / 255f;
        float y = yByte / 255f;
        float k = kByte / 255f;

        int r = ClampToByte(255f * (1f - Math.Min(1f, c + k)));
        int g = ClampToByte(255f * (1f - Math.Min(1f, m + k)));
        int b = ClampToByte(255f * (1f - Math.Min(1f, y + k)));

        return DrawingColor.FromArgb(r, g, b);
    }

    private Bitmap ApplySoftMaskIfPresent(Bitmap source)
    {
        if (SoftMask == null ||
            SoftMask.BitsPerComponent != 8 ||
            SoftMask.Width != Width ||
            SoftMask.Height != Height)
        {
            return source;
        }

        PdfColorSpace softMaskColorSpace = SoftMask.ColorSpace;
        if (softMaskColorSpace is PdfIccBasedColorSpace softMaskIcc)
            softMaskColorSpace = softMaskIcc.GetFallback();

        if (softMaskColorSpace is not PdfDeviceGrayColorSpace)
            return source;

        Bitmap? decodedMask = null;
        try
        {
            decodedMask = SoftMask.CreateBitmap();
        }
        catch (NotSupportedException)
        {
            return source;
        }
        catch (InvalidOperationException)
        {
            return source;
        }

        if (decodedMask.Width != Width || decodedMask.Height != Height)
        {
            decodedMask.Dispose();
            return source;
        }

        var withAlpha = new Bitmap(Width, Height, DrawingPixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    DrawingColor color = source.GetPixel(x, y);
                    DrawingColor maskSample = decodedMask.GetPixel(x, y);
                    int maskAlpha = maskSample.R;
                    int alpha = (color.A * maskAlpha + 127) / 255;
                    withAlpha.SetPixel(x, y, DrawingColor.FromArgb(alpha, color.R, color.G, color.B));
                }
            }
        }
        finally
        {
            decodedMask.Dispose();
        }

        source.Dispose();
        return withAlpha;
    }

    private static int ClampToByte(float value)
    {
        if (value < 0f) return 0;
        if (value > 255f) return 255;
        return (int)Math.Round(value);
    }
}
