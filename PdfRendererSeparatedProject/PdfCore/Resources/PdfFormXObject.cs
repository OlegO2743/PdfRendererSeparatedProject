using System.Drawing.Drawing2D;

namespace PdfCore.Resources;

public sealed class PdfFormXObject
{
    public string ResourceName { get; init; } = string.Empty;
    public string ContentStream { get; init; } = string.Empty;
    public float[] BBox { get; init; } = new float[] { 0, 0, 0, 0 };
    public float[] MatrixValues { get; init; } = new float[] { 1, 0, 0, 1, 0, 0 };
    public PdfResourceSet Resources { get; init; } = new();

    public Matrix CreateMatrix()
    {
        float[] m = MatrixValues.Length == 6 ? MatrixValues : new float[] { 1, 0, 0, 1, 0, 0 };
        return new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]);
    }
}
