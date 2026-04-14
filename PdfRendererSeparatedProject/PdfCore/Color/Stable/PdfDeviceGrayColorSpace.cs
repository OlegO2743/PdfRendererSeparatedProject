namespace PdfCore.Color;

public sealed class PdfDeviceGrayColorSpace : PdfColorSpace
{
    public override int Components => 1;
    public override string Name => "/DeviceGray";
}
