using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingColor = System.Drawing.Color;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using MediaPixelFormat = System.Windows.Media.PixelFormat;

namespace PdfCore.Color;

public static class PdfIccColorConverter
{
    public static Bitmap? TryConvertCmykBytesToBitmap(byte[] cmykBytes, int width, int height, PdfIccBasedColorSpace colorSpace)
    {
        try
        {
            if (colorSpace.ProfileBytes == null || colorSpace.ProfileBytes.Length == 0)
                return null;

            string profilePath = PersistTemporaryProfile(colorSpace.ProfileBytes);

            var srcContext = new ColorContext(new Uri(profilePath));
            var dstContext = new ColorContext(PixelFormats.Bgra32);

            int stride = width * 4;

            BitmapSource src = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Cmyk32,
                null,
                cmykBytes,
                stride);

            var converted = new ColorConvertedBitmap(src, srcContext, dstContext, PixelFormats.Bgra32);

            int dstStride = width * 4;
            byte[] dst = new byte[height * dstStride];
            converted.CopyPixels(dst, dstStride, 0);

            var bmp = new Bitmap(width, height, DrawingPixelFormat.Format32bppPArgb);
            var rect = new Rectangle(0, 0, width, height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppPArgb);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(dst, 0, data.Scan0, dst.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }
        catch (Exception ex)
        {
            Trace("CMYK", colorSpace, ex.Message);
            return null;
        }
    }

    public static Bitmap? TryConvertBitmapToSrgb(Bitmap source, PdfIccBasedColorSpace colorSpace)
    {
        try
        {
            if (colorSpace.ProfileBytes == null || colorSpace.ProfileBytes.Length == 0)
                return null;

            if (colorSpace.N != 3)
                return null;

            string profilePath = PersistTemporaryProfile(colorSpace.ProfileBytes);

            using var rgbBitmap = source.PixelFormat == DrawingPixelFormat.Format24bppRgb
                ? source.Clone(new Rectangle(0, 0, source.Width, source.Height), DrawingPixelFormat.Format24bppRgb)
                : CloneTo24bppRgb(source);

            byte[] src = CopyBitmapBytes(rgbBitmap, DrawingPixelFormat.Format24bppRgb, out int srcStride);

            var srcContext = new ColorContext(new Uri(profilePath));
            var dstContext = new ColorContext(PixelFormats.Bgra32);

            BitmapSource srcBitmap = BitmapSource.Create(
                rgbBitmap.Width,
                rgbBitmap.Height,
                96,
                96,
                PixelFormats.Bgr24,
                null,
                src,
                srcStride);

            var converted = new ColorConvertedBitmap(srcBitmap, srcContext, dstContext, PixelFormats.Bgra32);

            int dstStride = rgbBitmap.Width * 4;
            byte[] dst = new byte[rgbBitmap.Height * dstStride];
            converted.CopyPixels(dst, dstStride, 0);

            var bmp = new Bitmap(rgbBitmap.Width, rgbBitmap.Height, DrawingPixelFormat.Format32bppPArgb);
            var rect = new Rectangle(0, 0, rgbBitmap.Width, rgbBitmap.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppPArgb);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(dst, 0, data.Scan0, dst.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            Trace(
                "RGB",
                colorSpace,
                $"ok profile={Path.GetFileName(profilePath)} src={source.Width}x{source.Height} srcFmt={source.PixelFormat} workFmt={rgbBitmap.PixelFormat}");
            return bmp;
        }
        catch (Exception ex)
        {
            Trace("RGB", colorSpace, ex.Message);
            return null;
        }
    }

    private static Bitmap CloneTo24bppRgb(Bitmap source)
    {
        var clone = new Bitmap(source.Width, source.Height, DrawingPixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(clone);
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        return clone;
    }

    private static byte[] CopyBitmapBytes(Bitmap bitmap, DrawingPixelFormat pixelFormat, out int stride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, pixelFormat);
        try
        {
            stride = data.Stride;
            int byteCount = Math.Abs(data.Stride) * bitmap.Height;
            byte[] bytes = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, byteCount);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static string PersistTemporaryProfile(byte[] bytes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "PdfCoreIccProfiles");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "profile_" + BitConverter.ToString(System.Security.Cryptography.SHA1.HashData(bytes)).Replace("-", "").ToLowerInvariant() + ".icc");
        if (!File.Exists(file))
            File.WriteAllBytes(file, bytes);
        return file;
    }

    private static void Trace(string channel, PdfIccBasedColorSpace colorSpace, string message)
    {
        string? flag = Environment.GetEnvironmentVariable("PDF_ICC_TRACE");
        if (!string.Equals(flag, "1", StringComparison.Ordinal))
            return;

        Console.Error.WriteLine(
            $"[ICC {channel}] N={colorSpace.N} Bytes={colorSpace.ProfileBytes.Length} Obj={colorSpace.ProfileObjectNumber?.ToString() ?? "-"} {message}");
    }
}
