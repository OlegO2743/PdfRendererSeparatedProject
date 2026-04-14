namespace PdfCore.Color;

public sealed class PdfDeviceCmykColorSpace : PdfColorSpace
{
    public override int Components => 4;
    public override string Name => "/DeviceCMYK";
}
