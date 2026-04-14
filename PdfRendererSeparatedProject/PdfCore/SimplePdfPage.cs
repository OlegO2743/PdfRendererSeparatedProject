using PdfCore.Resources;

namespace PdfCore;

public sealed class SimplePdfPage
{
    public float WidthPt { get; init; }
    public float HeightPt { get; init; }
    public string ContentStream { get; init; } = string.Empty;
    public PdfResourceSet Resources { get; init; } = new();
}
