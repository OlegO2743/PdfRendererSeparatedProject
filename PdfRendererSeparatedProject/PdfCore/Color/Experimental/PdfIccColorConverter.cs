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
        catch
        {
            return null;
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
}
