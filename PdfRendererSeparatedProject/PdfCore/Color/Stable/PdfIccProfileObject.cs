namespace PdfCore.Color;

public sealed class PdfIccProfileObject
{
    public int ObjectNumber { get; init; }
    public int N { get; init; }
    public string? AlternateName { get; init; }
    public byte[] ProfileBytes { get; init; } = Array.Empty<byte>();
}
