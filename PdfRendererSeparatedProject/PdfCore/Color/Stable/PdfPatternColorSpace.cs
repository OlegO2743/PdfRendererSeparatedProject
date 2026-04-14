namespace PdfCore.Color;

public sealed class PdfPatternColorSpace : PdfColorSpace
{
    public PdfColorSpace? BaseColorSpace { get; init; }

    public override int Components => BaseColorSpace?.Components ?? 0;
    public override string Name => "/Pattern";

    public override PdfColorSpace GetFallback() => BaseColorSpace ?? new PdfDeviceRgbColorSpace();
}
