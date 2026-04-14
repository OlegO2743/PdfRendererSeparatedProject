namespace PdfCore.Color;

public sealed class PdfDeviceRgbColorSpace : PdfColorSpace
{
    public override int Components => 3;
    public override string Name => "/DeviceRGB";
}
