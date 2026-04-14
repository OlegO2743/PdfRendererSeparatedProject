namespace PdfCore.Resources;

public sealed class PdfTilingPattern
{
    public string ResourceName { get; init; } = string.Empty;
    public int PaintType { get; init; } = 1;
    public int TilingType { get; init; } = 1;
    public float[] BBox { get; init; } = { 0f, 0f, 0f, 0f };
    public float[] MatrixValues { get; init; } = { 1f, 0f, 0f, 1f, 0f, 0f };
    public float XStep { get; init; }
    public float YStep { get; init; }
    public string ContentStream { get; init; } = string.Empty;
    public PdfResourceSet Resources { get; init; } = new();
}
