namespace PdfCore.Color;

public sealed class PdfIccBasedColorSpace : PdfColorSpace
{
    public int N { get; init; }
    public PdfColorSpace? Alternate { get; init; }
    public byte[] ProfileBytes { get; init; } = Array.Empty<byte>();
    public int? ProfileObjectNumber { get; init; }

    public override int Components => N;
    public override string Name => "/ICCBased";

    public override PdfColorSpace GetFallback()
    {
        if (Alternate != null)
            return Alternate;

        return PdfColorSpaceFactory.CreateDeviceSpaceByComponentCount(N);
    }
}
