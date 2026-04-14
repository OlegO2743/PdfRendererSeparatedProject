namespace PdfCore.Color;

public sealed class PdfIndexedColorSpace : PdfColorSpace
{
    public PdfColorSpace BaseColorSpace { get; init; } = new PdfDeviceRgbColorSpace();
    public int HighValue { get; init; }
    public byte[] Lookup { get; init; } = Array.Empty<byte>();

    public override int Components => 1;
    public override string Name => "/Indexed";
}
