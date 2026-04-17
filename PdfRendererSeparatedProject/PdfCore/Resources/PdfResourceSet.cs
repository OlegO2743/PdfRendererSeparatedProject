using PdfCore.Color;

namespace PdfCore.Resources;

public sealed class PdfResourceSet
{
    public Dictionary<string, PdfFontResource> Fonts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PdfFormXObject> Forms { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PdfImageXObject> Images { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PdfColorSpace> ColorSpaces { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PdfTilingPattern> Patterns { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PdfExtGraphicsState> ExtGraphicsStates { get; } = new(StringComparer.Ordinal);
}
