namespace PdfCore.Graphics;

public sealed class PdfPageContext
{
    public float WidthPt { get; init; }
    public float HeightPt { get; init; }
    public float Zoom { get; init; }
    internal PdfPageObjectCollector? ObjectCollector { get; init; }
    public int WidthPx => (int)Math.Ceiling(WidthPt * Zoom);
    public int HeightPx => (int)Math.Ceiling(HeightPt * Zoom);
}
