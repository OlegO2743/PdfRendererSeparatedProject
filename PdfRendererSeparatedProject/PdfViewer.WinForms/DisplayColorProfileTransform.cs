using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PdfViewer.WinForms;

internal static class DisplayColorProfileTransform
{
    private static readonly Lazy<string?> DisplayProfilePath = new(ResolveDisplayProfilePath, isThreadSafe: true);

    public static Bitmap? PrepareForScreen(Bitmap? source)
    {
        if (source == null)
            return null;

        if (string.Equals(Environment.GetEnvironmentVariable("PDF_VIEWER_DISABLE_DISPLAY_ICC"), "1", StringComparison.Ordinal))
            return source;

        string? profilePathValue = DisplayProfilePath.Value;
        if (string.IsNullOrWhiteSpace(profilePathValue) || !File.Exists(profilePathValue))
            return source;

        string profilePath = profilePathValue;
        Bitmap? converted = TryConvertSrgbToDisplayProfile(source, profilePath);
        if (converted == null)
            return source;

        source.Dispose();
        return converted;
    }

    private static Bitmap? TryConvertSrgbToDisplayProfile(Bitmap source, string profilePath)
    {
        try
        {
            using Bitmap working = source.PixelFormat == DrawingPixelFormat.Format32bppArgb
                ? source.Clone(new Rectangle(0, 0, source.Width, source.Height), DrawingPixelFormat.Format32bppArgb)
                : CloneTo32bppArgb(source);

            byte[] src = CopyBitmapBytes(working, out int stride);
            var srcContext = new ColorContext(PixelFormats.Bgra32);
            var dstContext = new ColorContext(new Uri(profilePath));

            BitmapSource srcBitmap = BitmapSource.Create(
                working.Width,
                working.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                src,
                stride);

            var converted = new ColorConvertedBitmap(srcBitmap, srcContext, dstContext, PixelFormats.Bgra32);

            byte[] dst = new byte[working.Height * stride];
            converted.CopyPixels(dst, stride, 0);

            var bitmap = new Bitmap(working.Width, working.Height, DrawingPixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, working.Width, working.Height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(dst, 0, data.Scan0, dst.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap CloneTo32bppArgb(Bitmap source)
    {
        var clone = new Bitmap(source.Width, source.Height, DrawingPixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(clone);
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        return clone;
    }

    private static byte[] CopyBitmapBytes(Bitmap bitmap, out int stride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            int byteCount = Math.Abs(data.Stride) * bitmap.Height;
            byte[] bytes = new byte[byteCount];
            Marshal.Copy(data.Scan0, bytes, 0, byteCount);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static string? ResolveDisplayProfilePath()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return null;

        try
        {
            uint size = 260;
            var builder = new StringBuilder((int)size);
            if (!GetICMProfile(hdc, ref size, builder))
            {
                builder = new StringBuilder((int)size);
                if (!GetICMProfile(hdc, ref size, builder))
                    return null;
            }

            string path = builder.ToString();
            return File.Exists(path) ? path : null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetICMProfile(IntPtr hdc, ref uint lpcbName, StringBuilder lpszFilename);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
