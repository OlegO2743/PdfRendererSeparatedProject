namespace PdfCore.Color;

public abstract class PdfColorSpace
{
    public abstract int Components { get; }
    public abstract string Name { get; }
    public virtual PdfColorSpace GetFallback() => this;
    public override string ToString() => Name;
}
