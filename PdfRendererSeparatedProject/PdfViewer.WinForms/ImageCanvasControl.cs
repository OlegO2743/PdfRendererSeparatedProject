using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PdfCore;

namespace PdfViewer.WinForms;

public sealed class ImageCanvasControl : Control
{
    private const int HoverTolerancePx = 6;

    private Bitmap? _bitmap;
    private Point _bitmapOffset;
    private IReadOnlyList<PdfRenderObject> _objects = Array.Empty<PdfRenderObject>();
    private bool _objectOverlayEnabled;
    private int _hoveredObjectIndex = -1;

    public Size BitmapSize => _bitmap?.Size ?? Size.Empty;

    public Point BitmapOffset => _bitmapOffset;

    public ImageCanvasControl()
    {
        DoubleBuffered = true;
        BackColor = Color.DimGray;
    }

    public void SetBitmap(Bitmap? bitmap)
        => SetRenderedPage(bitmap, null);

    public void SetRenderedPage(Bitmap? bitmap, IReadOnlyList<PdfRenderObject>? objects)
    {
        var old = _bitmap;
        _bitmap = bitmap;
        _objects = objects ?? Array.Empty<PdfRenderObject>();
        _hoveredObjectIndex = -1;
        Size = _bitmap?.Size ?? new Size(1, 1);
        _bitmapOffset = Point.Empty;
        Invalidate();
        old?.Dispose();
    }

    public void SetObjectOverlayEnabled(bool enabled)
    {
        if (_objectOverlayEnabled == enabled)
            return;

        _objectOverlayEnabled = enabled;
        if (!enabled)
            _hoveredObjectIndex = -1;

        Invalidate();
    }

    public void UpdateHoveredObject(Point canvasPoint)
    {
        if (!_objectOverlayEnabled || _bitmap == null || _objects.Count == 0)
        {
            ClearHoveredObject();
            return;
        }

        int newIndex = FindObjectAt(canvasPoint);
        if (newIndex == _hoveredObjectIndex)
            return;

        _hoveredObjectIndex = newIndex;
        Invalidate();
    }

    public void ClearHoveredObject()
    {
        if (_hoveredObjectIndex < 0)
            return;

        _hoveredObjectIndex = -1;
        Invalidate();
    }

    public void SetViewportSize(Size viewportSize)
    {
        int bitmapWidth = _bitmap?.Width ?? 1;
        int bitmapHeight = _bitmap?.Height ?? 1;
        int width = Math.Max(1, Math.Max(bitmapWidth, viewportSize.Width));
        int height = Math.Max(1, Math.Max(bitmapHeight, viewportSize.Height));

        _bitmapOffset = new Point(
            Math.Max(0, (width - bitmapWidth) / 2),
            Math.Max(0, (height - bitmapHeight) / 2));

        Size = new Size(width, height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_bitmap == null)
            return;

        e.Graphics.DrawImageUnscaled(_bitmap, _bitmapOffset);

        if (!_objectOverlayEnabled || _hoveredObjectIndex < 0 || _hoveredObjectIndex >= _objects.Count)
            return;

        RectangleF highlightBounds = GetClientBounds(_objects[_hoveredObjectIndex].Bounds);
        if (highlightBounds.Width <= 0f || highlightBounds.Height <= 0f)
            return;

        using var fillBrush = new SolidBrush(Color.FromArgb(36, 96, 96, 96));
        using var outlinePen = new Pen(Color.FromArgb(170, 96, 96, 96), 2f);
        e.Graphics.FillRectangle(fillBrush, highlightBounds);
        e.Graphics.DrawRectangle(
            outlinePen,
            highlightBounds.X,
            highlightBounds.Y,
            highlightBounds.Width,
            highlightBounds.Height);
    }

    private int FindObjectAt(Point canvasPoint)
    {
        float bitmapX = canvasPoint.X - _bitmapOffset.X;
        float bitmapY = canvasPoint.Y - _bitmapOffset.Y;

        if (_bitmap == null ||
            bitmapX < 0f ||
            bitmapY < 0f ||
            bitmapX > _bitmap.Width ||
            bitmapY > _bitmap.Height)
        {
            return -1;
        }

        var probe = new PointF(bitmapX, bitmapY);
        var candidates = new List<int>();

        for (int i = 0; i < _objects.Count; i++)
        {
            RectangleF bounds = _objects[i].Bounds;
            bounds.Inflate(HoverTolerancePx, HoverTolerancePx);
            if (!bounds.Contains(probe))
                continue;

            candidates.Add(i);
        }

        if (candidates.Count == 0)
            return -1;

        List<int> filtered = candidates
            .Where(index => !IsDominatedFragment(index, candidates))
            .ToList();
        if (filtered.Count == 0)
            filtered = candidates;

        int bestIndex = -1;
        float bestScore = float.MaxValue;
        foreach (int index in filtered)
        {
            float score = GetHoverScore(index, probe);
            if (score >= bestScore)
                continue;

            bestScore = score;
            bestIndex = index;
        }

        return bestIndex;
    }

    private bool IsDominatedFragment(int candidateIndex, IReadOnlyList<int> candidates)
    {
        PdfRenderObject candidate = _objects[candidateIndex];
        RectangleF candidateBounds = candidate.Bounds;
        float candidateArea = Math.Max(1f, candidateBounds.Width * candidateBounds.Height);
        bool tinyText =
            candidate.Kind == PdfRenderObjectKind.Text &&
            candidateBounds.Width <= 260f &&
            candidateBounds.Height <= 90f &&
            !string.IsNullOrWhiteSpace(candidate.Content) &&
            candidate.Content!.Trim().Length <= 8;
        bool tinyVector =
            candidate.Kind == PdfRenderObjectKind.VectorPath &&
            (candidateBounds.Width <= 26f ||
             candidateBounds.Height <= 26f ||
             candidateArea <= 1500f);

        if (!tinyText && !tinyVector && candidateArea > 900f)
            return false;

        foreach (int otherIndex in candidates)
        {
            if (otherIndex == candidateIndex)
                continue;

            PdfRenderObject other = _objects[otherIndex];
            RectangleF expanded = other.Bounds;
            expanded.Inflate(HoverTolerancePx * 2, HoverTolerancePx * 2);
            if (!expanded.Contains(candidateBounds.Left, candidateBounds.Top) ||
                !expanded.Contains(candidateBounds.Right, candidateBounds.Bottom))
            {
                continue;
            }

            float otherArea = Math.Max(1f, other.Bounds.Width * other.Bounds.Height);
            if (otherArea <= candidateArea * 4f)
                continue;

            if (candidate.Kind == PdfRenderObjectKind.Text &&
                other.Kind == PdfRenderObjectKind.Text &&
                !string.IsNullOrWhiteSpace(other.Content))
            {
                return true;
            }

            if (candidate.Kind == PdfRenderObjectKind.Text &&
                other.Kind == PdfRenderObjectKind.VectorPath &&
                IsTextWrappedByVectorBlock(candidate, other, candidateArea, otherArea))
            {
                return true;
            }

            if (candidate.Kind == PdfRenderObjectKind.VectorPath &&
                other.Kind != PdfRenderObjectKind.Image)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTextWrappedByVectorBlock(
        PdfRenderObject textObject,
        PdfRenderObject vectorObject,
        float textArea,
        float vectorArea)
    {
        RectangleF textBounds = textObject.Bounds;
        RectangleF vectorBounds = vectorObject.Bounds;

        RectangleF expandedVector = vectorBounds;
        expandedVector.Inflate(HoverTolerancePx * 3f, HoverTolerancePx * 3f);
        if (!expandedVector.Contains(textBounds.Left, textBounds.Top) ||
            !expandedVector.Contains(textBounds.Right, textBounds.Bottom))
        {
            return false;
        }

        string content = textObject.Content?.Trim() ?? string.Empty;
        if (content.Length == 0 || content.Length > 180)
            return false;

        bool lineLikeText =
            textBounds.Height <= 96f &&
            (textBounds.Width >= 24f || textBounds.Height <= 42f);
        if (!lineLikeText)
            return false;

        bool substantiallyLarger =
            vectorArea >= textArea * 6f &&
            (vectorBounds.Width >= textBounds.Width * 1.10f ||
             vectorBounds.Height >= textBounds.Height * 1.30f);

        return substantiallyLarger;
    }

    private float GetHoverScore(int objectIndex, PointF probe)
    {
        PdfRenderObject obj = _objects[objectIndex];
        RectangleF bounds = obj.Bounds;
        float area = Math.Max(1f, bounds.Width * bounds.Height);
        float centerX = bounds.Left + bounds.Width / 2f;
        float centerY = bounds.Top + bounds.Height / 2f;
        float normalizedDx = Math.Abs(probe.X - centerX) / Math.Max(20f, bounds.Width / 2f);
        float normalizedDy = Math.Abs(probe.Y - centerY) / Math.Max(20f, bounds.Height / 2f);
        float distanceScore = (normalizedDx * normalizedDx) + (normalizedDy * normalizedDy);
        float areaScore = MathF.Log(area + 1f);
        float edgeInset = Math.Min(
            Math.Min(probe.X - bounds.Left, bounds.Right - probe.X),
            Math.Min(probe.Y - bounds.Top, bounds.Bottom - probe.Y));
        float edgePenalty = edgeInset <= 0f ? 0.75f : 1f / (1f + edgeInset);
        float kindBias = obj.Kind switch
        {
            PdfRenderObjectKind.Image => -0.35f,
            PdfRenderObjectKind.Text when !string.IsNullOrWhiteSpace(obj.Content) => -0.15f,
            _ => 0f
        };

        return (areaScore * 0.55f) + (distanceScore * 0.35f) + (edgePenalty * 0.10f) + kindBias;
    }

    private RectangleF GetClientBounds(RectangleF bitmapBounds)
    {
        return new RectangleF(
            bitmapBounds.X + _bitmapOffset.X,
            bitmapBounds.Y + _bitmapOffset.Y,
            bitmapBounds.Width,
            bitmapBounds.Height);
    }
}
