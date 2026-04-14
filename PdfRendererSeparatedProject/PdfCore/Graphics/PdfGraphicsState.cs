using System.Drawing;
using System.Drawing.Drawing2D;
using PdfCore.Color;
using DrawingColor = System.Drawing.Color;

namespace PdfCore.Graphics;

public sealed class PdfGraphicsState
{
    public Matrix Ctm { get; set; } = new();
    public PdfColorSpace StrokeColorSpace { get; set; } = new PdfDeviceRgbColorSpace();
    public PdfColorSpace FillColorSpace { get; set; } = new PdfDeviceRgbColorSpace();
    public DrawingColor StrokeColor { get; set; } = DrawingColor.Black;
    public DrawingColor FillColor { get; set; } = DrawingColor.Black;
    public string? StrokePatternName { get; set; }
    public string? FillPatternName { get; set; }
    public float LineWidth { get; set; } = 1f;
    public int LineCap { get; set; }
    public int LineJoin { get; set; }
    public float MiterLimit { get; set; } = 10f;
    public float[] DashArray { get; set; } = Array.Empty<float>();
    public float DashPhase { get; set; }

    public PdfGraphicsState Clone()
    {
        return new PdfGraphicsState
        {
            Ctm = Ctm.Clone(),
            StrokeColorSpace = StrokeColorSpace,
            FillColorSpace = FillColorSpace,
            StrokeColor = StrokeColor,
            FillColor = FillColor,
            StrokePatternName = StrokePatternName,
            FillPatternName = FillPatternName,
            LineWidth = LineWidth,
            LineCap = LineCap,
            LineJoin = LineJoin,
            MiterLimit = MiterLimit,
            DashArray = (float[])DashArray.Clone(),
            DashPhase = DashPhase
        };
    }
}
