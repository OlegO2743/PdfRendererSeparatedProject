using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using PdfCore;

namespace PdfViewer.WinForms;

internal static class PdfPrintService
{
    public static void Print(IWin32Window owner, SimplePdfDocument document, int rotationDegrees, int currentPageIndex, string? documentName)
    {
        if (document.Pages.Count == 0)
            return;

        using var printDocument = new PrintDocument
        {
            DocumentName = string.IsNullOrWhiteSpace(documentName) ? "PDF Document" : documentName
        };

        printDocument.PrinterSettings.MinimumPage = 1;
        printDocument.PrinterSettings.MaximumPage = document.Pages.Count;
        printDocument.PrinterSettings.FromPage = Math.Clamp(currentPageIndex + 1, 1, document.Pages.Count);
        printDocument.PrinterSettings.ToPage = Math.Clamp(currentPageIndex + 1, 1, document.Pages.Count);

        using var dialog = new PrintDialog
        {
            AllowCurrentPage = true,
            AllowSelection = false,
            AllowSomePages = true,
            UseEXDialog = true,
            Document = printDocument
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return;

        List<int> pagesToPrint = BuildPageSequence(dialog.PrinterSettings, document.Pages.Count, currentPageIndex);
        if (pagesToPrint.Count == 0)
            return;

        int pageCursor = 0;
        printDocument.PrintPage += (_, e) =>
        {
            int pageIndex = pagesToPrint[pageCursor];
            Bitmap bitmap = RenderPageForPrint(document.Pages[pageIndex], e, rotationDegrees);
            try
            {
                if (e.Graphics is null)
                {
                    pageCursor++;
                    e.HasMorePages = pageCursor < pagesToPrint.Count;
                    return;
                }

                Rectangle destination = CalculateDestinationRectangle(e.MarginBounds, bitmap.Size);
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(bitmap, destination);
            }
            finally
            {
                bitmap.Dispose();
            }

            pageCursor++;
            e.HasMorePages = pageCursor < pagesToPrint.Count;
        };

        printDocument.Print();
    }

    private static List<int> BuildPageSequence(PrinterSettings printerSettings, int pageCount, int currentPageIndex)
    {
        var pages = new List<int>();
        switch (printerSettings.PrintRange)
        {
            case PrintRange.CurrentPage:
                pages.Add(Math.Clamp(currentPageIndex, 0, pageCount - 1));
                break;

            case PrintRange.SomePages:
                int from = Math.Clamp(printerSettings.FromPage, 1, pageCount);
                int to = Math.Clamp(printerSettings.ToPage, from, pageCount);
                for (int page = from; page <= to; page++)
                    pages.Add(page - 1);
                break;

            default:
                for (int page = 0; page < pageCount; page++)
                    pages.Add(page);
                break;
        }

        return pages;
    }

    private static Bitmap RenderPageForPrint(SimplePdfPage page, PrintPageEventArgs e, int rotationDegrees)
    {
        float widthPt = rotationDegrees is 90 or 270 ? page.HeightPt : page.WidthPt;
        float heightPt = rotationDegrees is 90 or 270 ? page.WidthPt : page.HeightPt;
        float zoom = Math.Clamp(
            Math.Min(e.MarginBounds.Width / Math.Max(1f, widthPt), e.MarginBounds.Height / Math.Max(1f, heightPt)),
            0.1f,
            20f);

        Bitmap bitmap = SimplePdfRenderer.Render(page, zoom);
        switch (rotationDegrees)
        {
            case 90:
                bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                break;
            case 180:
                bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                break;
            case 270:
                bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                break;
        }

        return bitmap;
    }

    private static Rectangle CalculateDestinationRectangle(Rectangle marginBounds, Size imageSize)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0)
            return marginBounds;

        float scale = Math.Min(
            marginBounds.Width / (float)imageSize.Width,
            marginBounds.Height / (float)imageSize.Height);

        int width = Math.Max(1, (int)Math.Round(imageSize.Width * scale));
        int height = Math.Max(1, (int)Math.Round(imageSize.Height * scale));
        int x = marginBounds.Left + ((marginBounds.Width - width) / 2);
        int y = marginBounds.Top + ((marginBounds.Height - height) / 2);
        return new Rectangle(x, y, width, height);
    }
}
