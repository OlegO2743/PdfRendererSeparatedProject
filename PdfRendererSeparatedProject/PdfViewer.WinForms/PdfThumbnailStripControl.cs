using System.Drawing;
using System.Windows.Forms;
using PdfCore;

namespace PdfViewer.WinForms;

internal sealed class PdfThumbnailStripControl : ScrollableControl
{
    private const int WmHScroll = 0x0114;
    private const int WmVScroll = 0x0115;
    private const int WmMouseWheel = 0x020A;
    private const int VisibleThumbnailNeighborRadius = 1;
    private const int OuterPadding = 10;
    private const int ItemPadding = 8;
    private const int ItemSpacing = 10;
    private const int CaptionSpacing = 8;
    private const int MinimumCaptionHeight = 28;
    private const int MinimumThumbnailWidth = 96;
    private const float MinimumThumbnailZoom = 0.08f;
    private const float MaximumThumbnailZoom = 1f;
    private const int RenderViewportPadding = 320;
    private static readonly StringFormat CenteredCaptionFormat = CreateCenteredCaptionFormat();

    private readonly List<ThumbnailSlot> _slots = new();
    private SimplePdfDocument? _document;
    private int _rotationDegrees;
    private int _currentPageIndex = -1;
    private int _renderGeneration;

    public event EventHandler<int>? PageSelected;

    public PdfThumbnailStripControl()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = Color.FromArgb(56, 56, 56);
    }

    public void SetDocument(SimplePdfDocument? document, int rotationDegrees)
    {
        ClearThumbnails();
        _document = document;
        _rotationDegrees = rotationDegrees;
        _currentPageIndex = -1;
        _renderGeneration++;

        if (_document == null || _document.Pages.Count == 0)
        {
            AutoScrollMinSize = Size.Empty;
            Invalidate();
            return;
        }

        foreach (SimplePdfPage page in _document.Pages)
        {
            var slot = new ThumbnailSlot
            {
                Page = page
            };
            _slots.Add(slot);
        }

        LayoutSlots();
        EnsureVisibleThumbnailsRendered();
        Invalidate();
    }

    public void SetRotation(int rotationDegrees)
    {
        if (_rotationDegrees == rotationDegrees && _slots.Count > 0)
            return;

        _rotationDegrees = rotationDegrees;
        _renderGeneration++;

        foreach (ThumbnailSlot slot in _slots)
        {
            slot.ThumbnailSize = CalculateThumbnailSize(slot.Page);
            slot.IsRendered = false;
            slot.IsRendering = false;
            slot.Bitmap?.Dispose();
            slot.Bitmap = null;
        }

        LayoutSlots();
        EnsureVisibleThumbnailsRendered();
        Invalidate();
    }

    public void RefreshRenderedThumbnails()
    {
        _renderGeneration++;
        foreach (ThumbnailSlot slot in _slots)
        {
            slot.IsRendered = false;
            slot.IsRendering = false;
            slot.Bitmap?.Dispose();
            slot.Bitmap = null;
        }

        EnsureVisibleThumbnailsRendered();
        Invalidate();
    }

    public void SetCurrentPage(int pageIndex)
    {
        if (pageIndex < -1 || pageIndex >= _slots.Count)
            return;

        if (_currentPageIndex == pageIndex)
            return;

        int old = _currentPageIndex;
        _currentPageIndex = pageIndex;

        if (old >= 0 && old < _slots.Count)
            Invalidate(GetInvalidateBounds(_slots[old].Bounds));
        if (_currentPageIndex >= 0)
        {
            EnsurePageVisible(_currentPageIndex);
            Invalidate(GetInvalidateBounds(_slots[_currentPageIndex].Bounds));
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutSlots();
        EnsureVisibleThumbnailsRendered();
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        EnsureVisibleThumbnailsRendered();
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg is not (WmHScroll or WmVScroll or WmMouseWheel) || IsDisposed || !IsHandleCreated)
            return;

        BeginInvoke((MethodInvoker)(() =>
        {
            if (IsDisposed)
                return;

            EnsureVisibleThumbnailsRendered();
            Invalidate();
        }));
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        Point point = GetDocumentPoint(e.Location);
        for (int i = 0; i < _slots.Count; i++)
        {
            if (!_slots[i].Bounds.Contains(point))
                continue;

            PageSelected?.Invoke(this, i);
            return;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        EnsureVisibleThumbnailsRendered();

        e.Graphics.Clear(BackColor);
        e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        Rectangle viewport = GetViewportRectangle();
        viewport.Inflate(0, RenderViewportPadding);

        foreach (ThumbnailSlot slot in _slots)
        {
            if (!slot.Bounds.IntersectsWith(viewport))
                continue;

            DrawThumbnailSlot(e.Graphics, slot);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (ThumbnailSlot slot in _slots)
                slot.Bitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawThumbnailSlot(Graphics graphics, ThumbnailSlot slot)
    {
        bool isCurrent = _slots.IndexOf(slot) == _currentPageIndex;
        Color itemFill = isCurrent ? Color.FromArgb(74, 90, 118) : Color.FromArgb(72, 72, 72);
        Color borderColor = isCurrent ? Color.FromArgb(154, 193, 255) : Color.FromArgb(96, 96, 96);
        Color captionColor = isCurrent ? Color.White : Color.Gainsboro;

        using var itemBrush = new SolidBrush(itemFill);
        using var itemBorderPen = new Pen(borderColor, isCurrent ? 2f : 1f);
        graphics.FillRectangle(itemBrush, slot.Bounds);
        graphics.DrawRectangle(itemBorderPen, slot.Bounds);

        Rectangle thumbRect = GetThumbnailContentBounds(slot);
        using var pageBrush = new SolidBrush(Color.White);
        using var pageBorderPen = new Pen(Color.FromArgb(168, 168, 168));
        graphics.FillRectangle(pageBrush, thumbRect);
        graphics.DrawRectangle(pageBorderPen, thumbRect);

        if (slot.Bitmap != null)
            graphics.DrawImage(slot.Bitmap, thumbRect);
        else
            DrawThumbnailPlaceholder(graphics, slot, thumbRect);

        Rectangle captionRect = GetCaptionBounds(slot);
        using var captionBrush = new SolidBrush(captionColor);
        graphics.DrawString(
            (slot.PageIndex + 1).ToString(),
            Font,
            captionBrush,
            captionRect,
            CenteredCaptionFormat);
    }

    private void DrawThumbnailPlaceholder(Graphics graphics, ThumbnailSlot slot, Rectangle thumbRect)
    {
        using var placeholderBrush = new SolidBrush(Color.FromArgb(246, 246, 246));
        using var linePen = new Pen(Color.FromArgb(220, 220, 220));
        using var textBrush = new SolidBrush(Color.Gray);
        graphics.FillRectangle(placeholderBrush, thumbRect);
        graphics.DrawRectangle(linePen, thumbRect);

        string text = $"PDF\n{slot.PageIndex + 1}";
        graphics.DrawString(text, Font, textBrush, thumbRect, CenteredCaptionFormat);
    }

    private void LayoutSlots()
    {
        if (_slots.Count == 0)
        {
            AutoScrollMinSize = Size.Empty;
            return;
        }

        int itemWidth = GetItemWidth();
        int clientWidth = itemWidth + (OuterPadding * 2);
        int captionHeight = GetCaptionHeight();
        int y = OuterPadding;
        bool sizeChanged = false;

        for (int i = 0; i < _slots.Count; i++)
        {
            ThumbnailSlot slot = _slots[i];
            Size thumbnailSize = CalculateThumbnailSize(slot.Page, itemWidth);
            if (slot.ThumbnailSize != thumbnailSize)
            {
                sizeChanged = true;
                slot.ThumbnailSize = thumbnailSize;
                slot.IsRendered = false;
                slot.IsRendering = false;
            }

            int itemHeight = (ItemPadding * 2) + slot.ThumbnailSize.Height + CaptionSpacing + captionHeight;
            slot.PageIndex = i;
            slot.Bounds = new Rectangle(OuterPadding, y, itemWidth, itemHeight);
            y += itemHeight + ItemSpacing;
        }

        if (sizeChanged)
            _renderGeneration++;

        Point scroll = GetScrollPosition();
        AutoScrollMinSize = new Size(clientWidth, y);
        AdjustFormScrollbars(true);
        SetScrollPosition(scroll.X, scroll.Y);
    }

    private void EnsureVisibleThumbnailsRendered()
    {
        if (_slots.Count == 0)
            return;

        Rectangle viewport = GetViewportRectangle();
        viewport.Inflate(0, RenderViewportPadding);
        HashSet<int> residentSlotIndices = GetResidentSlotIndices(viewport);

        for (int i = 0; i < _slots.Count; i++)
        {
            ThumbnailSlot slot = _slots[i];
            if (!residentSlotIndices.Contains(i))
                continue;

            if (!slot.IsRendered && !slot.IsRendering)
                StartRender(slot, i, _renderGeneration);
        }

        ReleaseNonResidentThumbnails(residentSlotIndices);
    }

    private void StartRender(ThumbnailSlot slot, int pageIndex, int generation)
    {
        if (slot.IsRendering)
            return;

        slot.IsRendering = true;
        int rotation = _rotationDegrees;
        float zoom = CalculateThumbnailZoom(slot.Page);
        SimplePdfPage page = slot.Page;

        _ = Task.Run(() =>
            {
                Bitmap bitmap = SimplePdfRenderer.Render(page, zoom);
                return RotateBitmap(bitmap, rotation);
            })
            .ContinueWith(task =>
            {
                slot.IsRendering = false;
                if (task.IsFaulted || task.IsCanceled)
                    return;

                Bitmap bitmap = task.Result;
                if (IsDisposed || generation != _renderGeneration || pageIndex >= _slots.Count || _slots[pageIndex] != slot)
                {
                    bitmap.Dispose();
                    return;
                }

                bitmap = DisplayColorProfileTransform.PrepareForScreen(bitmap)!;
                slot.Bitmap?.Dispose();
                slot.Bitmap = bitmap;
                slot.IsRendered = true;
                Invalidate(GetInvalidateBounds(slot.Bounds));
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private float CalculateThumbnailZoom(SimplePdfPage page)
        => CalculateThumbnailZoom(page, GetItemWidth());

    private float CalculateThumbnailZoom(SimplePdfPage page, int itemWidth)
    {
        float widthPt = _rotationDegrees is 90 or 270 ? page.HeightPt : page.WidthPt;
        int availableWidth = Math.Max(MinimumThumbnailWidth, itemWidth - (ItemPadding * 2));
        return Math.Clamp(
            availableWidth / Math.Max(1f, widthPt),
            MinimumThumbnailZoom,
            MaximumThumbnailZoom);
    }

    private Size CalculateThumbnailSize(SimplePdfPage page)
        => CalculateThumbnailSize(page, GetItemWidth());

    private Size CalculateThumbnailSize(SimplePdfPage page, int itemWidth)
    {
        float widthPt = _rotationDegrees is 90 or 270 ? page.HeightPt : page.WidthPt;
        float heightPt = _rotationDegrees is 90 or 270 ? page.WidthPt : page.HeightPt;
        float zoom = CalculateThumbnailZoom(page, itemWidth);
        return new Size(
            Math.Max(1, (int)Math.Ceiling(widthPt * zoom)),
            Math.Max(1, (int)Math.Ceiling(heightPt * zoom)));
    }

    private void EnsurePageVisible(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _slots.Count)
            return;

        Rectangle bounds = _slots[pageIndex].Bounds;
        Point scroll = GetScrollPosition();
        int viewportTop = scroll.Y;
        int viewportBottom = viewportTop + ClientSize.Height;

        if (bounds.Top < viewportTop)
            AutoScrollPosition = new Point(0, bounds.Top - OuterPadding);
        else if (bounds.Bottom > viewportBottom)
            AutoScrollPosition = new Point(0, bounds.Bottom - ClientSize.Height + OuterPadding);
    }

    private Rectangle GetThumbnailContentBounds(ThumbnailSlot slot)
    {
        Rectangle bounds = slot.Bounds;
        int x = bounds.Left + ((bounds.Width - slot.ThumbnailSize.Width) / 2);
        int y = bounds.Top + ItemPadding;
        return new Rectangle(x, y, slot.ThumbnailSize.Width, slot.ThumbnailSize.Height);
    }

    private Rectangle GetCaptionBounds(ThumbnailSlot slot)
    {
        Rectangle bounds = slot.Bounds;
        int captionTop = bounds.Top + ItemPadding + slot.ThumbnailSize.Height + CaptionSpacing;
        return new Rectangle(
            bounds.Left + ItemPadding,
            captionTop,
            bounds.Width - (ItemPadding * 2),
            Math.Max(1, bounds.Bottom - ItemPadding - captionTop));
    }

    private int GetItemWidth()
    {
        int minimumItemWidth = MinimumThumbnailWidth + (ItemPadding * 2);
        int scrollbarAllowance = SystemInformation.VerticalScrollBarWidth + 2;
        int usableClientWidth = Math.Max(
            minimumItemWidth + (OuterPadding * 2),
            ClientSize.Width - scrollbarAllowance);
        return Math.Max(minimumItemWidth, usableClientWidth - (OuterPadding * 2));
    }

    private int GetCaptionHeight()
    {
        Size measured = TextRenderer.MeasureText(
            "999",
            Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        return Math.Max(MinimumCaptionHeight, measured.Height + 6);
    }

    private Rectangle GetViewportRectangle()
    {
        return new Rectangle(GetScrollPosition(), ClientSize);
    }

    private HashSet<int> GetResidentSlotIndices(Rectangle viewport)
    {
        HashSet<int> visibleIndices = new();

        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].Bounds.IntersectsWith(viewport))
                visibleIndices.Add(i);
        }

        if (visibleIndices.Count == 0 && _currentPageIndex >= 0 && _currentPageIndex < _slots.Count)
            visibleIndices.Add(_currentPageIndex);

        HashSet<int> residentIndices = new();
        foreach (int visibleIndex in visibleIndices)
        {
            int start = Math.Max(0, visibleIndex - VisibleThumbnailNeighborRadius);
            int end = Math.Min(_slots.Count - 1, visibleIndex + VisibleThumbnailNeighborRadius);
            for (int i = start; i <= end; i++)
                residentIndices.Add(i);
        }

        return residentIndices;
    }

    private Point GetScrollPosition()
    {
        return new Point(
            Math.Max(0, -AutoScrollPosition.X),
            Math.Max(0, -AutoScrollPosition.Y));
    }

    private void SetScrollPosition(int x, int y)
    {
        Point maxScroll = GetMaxScrollPosition();
        AutoScrollPosition = new Point(
            Math.Clamp(x, 0, maxScroll.X),
            Math.Clamp(y, 0, maxScroll.Y));
    }

    private Point GetMaxScrollPosition()
    {
        return new Point(
            Math.Max(0, AutoScrollMinSize.Width - ClientSize.Width),
            Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height));
    }

    private Point GetDocumentPoint(Point clientPoint)
    {
        Point scroll = GetScrollPosition();
        return new Point(clientPoint.X + scroll.X, clientPoint.Y + scroll.Y);
    }

    private Rectangle GetInvalidateBounds(Rectangle bounds)
    {
        bounds.Inflate(6, 6);
        return new Rectangle(
            bounds.Left + AutoScrollPosition.X,
            bounds.Top + AutoScrollPosition.Y,
            bounds.Width,
            bounds.Height);
    }

    private void ReleaseNonResidentThumbnails(HashSet<int> residentSlotIndices)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (residentSlotIndices.Contains(i))
                continue;

            ThumbnailSlot slot = _slots[i];
            bool renderAlreadyRunning = slot.IsRendering;
            if (renderAlreadyRunning)
                continue;

            if (slot.Bitmap == null && !slot.IsRendered)
                continue;

            slot.Bitmap?.Dispose();
            slot.Bitmap = null;
            slot.IsRendered = false;
        }
    }

    private void ClearThumbnails()
    {
        _renderGeneration++;
        foreach (ThumbnailSlot slot in _slots)
            slot.Bitmap?.Dispose();

        _slots.Clear();
    }

    private static Bitmap RotateBitmap(Bitmap bitmap, int rotationDegrees)
    {
        switch (rotationDegrees)
        {
            case 90:
                bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                break;
            case 180:
                bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                break;
            case 270:
                bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                break;
        }

        return bitmap;
    }

    private static StringFormat CreateCenteredCaptionFormat()
    {
        return new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoClip
        };
    }

    private sealed class ThumbnailSlot
    {
        public required SimplePdfPage Page { get; init; }
        public int PageIndex { get; set; }
        public Rectangle Bounds { get; set; }
        public Size ThumbnailSize { get; set; }
        public Bitmap? Bitmap { get; set; }
        public bool IsRendered { get; set; }
        public bool IsRendering { get; set; }
    }
}
