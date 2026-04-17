using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PdfCore;

namespace PdfViewer.WinForms;

internal enum PdfScrollMode
{
    Smooth,
    PageByPage
}

internal sealed class PdfDocumentView : Panel
{
    private const int PageMargin = 24;
    private const int RenderViewportPadding = 5000;
    private const int ReleaseViewportPadding = 18000;
    private const int SmoothPreloadNeighborRadius = 2;
    private const int SmoothResidentNeighborRadius = 2;
    private static readonly TimeSpan RenderStallTimeout = TimeSpan.FromSeconds(4);
    private const int MaxConcurrentRenderWorkers = 2;

    private readonly List<PageSlot> _pages = new();
    private SimplePdfDocument? _document;
    private float _zoom = 1f;
    private int _rotationDegrees;
    private int _renderGeneration;
    private bool _objectOverlayEnabled;
    private bool _isPanning;
    private Point _panStartMouseScreen;
    private Point _panStartScroll;
    private int _hoveredPageIndex = -1;
    private int _currentPageIndex = -1;
    private int _pageByPageIndex = -1;
    private int _nextRenderRequestId;
    private PdfScrollMode _scrollMode = PdfScrollMode.Smooth;
    private readonly VScrollBar _verticalScrollBar = new();
    private readonly HScrollBar _horizontalScrollBar = new();
    private readonly Queue<RenderWorkItem> _renderQueue = new();
    private int _activeRenderWorkers;
    private Size _documentVirtualSize;
    private Size _viewportSize;
    private Point _scrollPosition;
    private bool _syncingScrollBars;

    public event EventHandler? CurrentPageChanged;
    public event MouseEventHandler? ZoomWheel;

    public int CurrentPageIndex => _currentPageIndex;

    public PdfScrollMode ScrollMode
    {
        get => _scrollMode;
        set
        {
            if (_scrollMode == value)
                return;

            int targetIndex = GetCurrentOrPreferredPageIndex();
            _scrollMode = value;
            _pageByPageIndex = targetIndex;
            ClearQueuedRenders();

            if (_pages.Count == 0)
                return;

            if (_scrollMode == PdfScrollMode.PageByPage)
            {
                LayoutPages();
                SetScrollPosition(0, 0);
                EnsureVisiblePagesRendered();
                UpdateCurrentPageFromViewport();
                UpdateHoveredPageUnderCursor();
                Focus();
                return;
            }

            LayoutPages();
            PageSlot targetSlot = _pages[Math.Clamp(targetIndex, 0, _pages.Count - 1)];
            Point target = GetPageScrollTarget(targetSlot, ShouldAlignPageTop(targetIndex));
            SetScrollPosition(target.X, target.Y);
            EnsureVisiblePagesRendered();
            UpdateCurrentPageFromViewport();
            UpdateHoveredPageUnderCursor();
            Focus();
        }
    }

    public PdfDocumentView()
    {
        DoubleBuffered = true;
        AutoScroll = false;
        BackColor = Color.DimGray;
        TabStop = true;

        _verticalScrollBar.Visible = false;
        _horizontalScrollBar.Visible = false;
        _verticalScrollBar.Scroll += HandleScrollBarMoved;
        _horizontalScrollBar.Scroll += HandleScrollBarMoved;
        Controls.Add(_horizontalScrollBar);
        Controls.Add(_verticalScrollBar);
    }

    public void SetDocument(SimplePdfDocument? document)
    {
        ClearDocument();
        _document = document;
        _renderGeneration++;
        ClearQueuedRenders();

        if (_document == null || _document.Pages.Count == 0)
        {
            LayoutPages();
            UpdateCurrentPageFromViewport();
            return;
        }

        SuspendLayout();
        try
        {
            for (int i = 0; i < _document.Pages.Count; i++)
            {
                var view = new PdfPageViewControl
                {
                    PageIndex = i
                };
                HookPageView(view);

                var slot = new PageSlot
                {
                    Page = _document.Pages[i],
                    View = view
                };
                slot.PixelSize = CalculatePagePixelSize(slot.Page);
                slot.View.SetPagePixelSize(slot.PixelSize);
                _pages.Add(slot);
                Controls.Add(view);
            }

            LayoutPages();
        }
        finally
        {
            ResumeLayout();
        }

        SetObjectOverlayEnabled(_objectOverlayEnabled);
        SetScrollPosition(0, 0);
        EnsureVisiblePagesRendered();
        UpdateCurrentPageFromViewport();
        Focus();
    }

    public void UpdateView(float zoom, int rotationDegrees, Point? viewportAnchor = null)
    {
        ViewAnchor? anchor =
            _scrollMode == PdfScrollMode.Smooth && viewportAnchor.HasValue
                ? TryCaptureAnchor(viewportAnchor.Value)
                : null;

        _zoom = zoom;
        _rotationDegrees = rotationDegrees;
        _renderGeneration++;
        ClearQueuedRenders();

        for (int i = 0; i < _pages.Count; i++)
        {
            PageSlot slot = _pages[i];
            slot.PixelSize = CalculatePagePixelSize(slot.Page);
            slot.IsRendered = false;
            slot.IsRendering = false;
            slot.ActiveRenderRequestId = 0;
            slot.View.SetPagePixelSize(slot.PixelSize);
            slot.View.SetRenderedPage(null, null);
        }

        LayoutPages();

        if (anchor.HasValue)
        {
            RestoreAnchor(anchor.Value);
        }
        else if (_scrollMode == PdfScrollMode.PageByPage)
        {
            _pageByPageIndex = GetCurrentOrPreferredPageIndex();
            SetScrollPosition(0, 0);
        }

        EnsureVisiblePagesRendered();
        UpdateCurrentPageFromViewport();
        UpdateHoveredPageUnderCursor();
    }

    public void SetObjectOverlayEnabled(bool enabled)
    {
        _objectOverlayEnabled = enabled;
        foreach (PageSlot slot in _pages)
            slot.View.SetObjectOverlayEnabled(enabled);

        if (!enabled)
            ClearHoveredPage();
    }

    public void ScrollToPage(int pageIndex, bool alignTop = true)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            return;

        if (_scrollMode == PdfScrollMode.PageByPage)
        {
            _pageByPageIndex = pageIndex;
            LayoutPages();
            SetScrollPosition(0, 0);
            EnsureVisiblePagesRendered();
            UpdateCurrentPageFromViewport();
            UpdateHoveredPageUnderCursor();
            Focus();
            return;
        }

        PageSlot slot = _pages[pageIndex];
        Point target = GetPageScrollTarget(slot, alignTop);
        PerformInternalScroll(target.X, target.Y);
        EnsureVisiblePagesRendered();
        UpdateCurrentPageFromViewport();
        Focus();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutPages();

        if (_scrollMode == PdfScrollMode.PageByPage)
        {
            _pageByPageIndex = GetCurrentOrPreferredPageIndex();
            SetScrollPosition(0, 0);
            EnsureVisiblePagesRendered();
            UpdateCurrentPageFromViewport();
            UpdateHoveredPageUnderCursor();
        }
        else
        {
            EnsureVisiblePagesRendered();
            UpdateCurrentPageFromViewport();
            UpdateHoveredPageUnderCursor();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Focus();

        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            ZoomWheel?.Invoke(this, e);
            return;
        }

        if (_scrollMode == PdfScrollMode.PageByPage)
        {
            int direction = e.Delta < 0 ? 1 : -1;
            ScrollToPage(Math.Clamp(_currentPageIndex + direction, 0, _pages.Count - 1));
            return;
        }

        ScrollSmoothly(e.Delta);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isPanning)
        {
            Point current = Control.MousePosition;
            bool changed = SetScrollPosition(
                _panStartScroll.X - (current.X - _panStartMouseScreen.X),
                _panStartScroll.Y - (current.Y - _panStartMouseScreen.Y));
            if (changed)
                HandleViewportScrolled();
            return;
        }

        if (!_objectOverlayEnabled)
            return;

        ClearHoveredPage();
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        base.OnMouseLeave(eventargs);
        if (_isPanning || !_objectOverlayEnabled)
            return;

        ClearHoveredPage();
    }

    private void ClearDocument()
    {
        ClearHoveredPage();
        _currentPageIndex = -1;
        _pageByPageIndex = -1;
        _renderGeneration++;

        foreach (PageSlot slot in _pages)
        {
            Controls.Remove(slot.View);
            slot.View.Dispose();
        }

        _pages.Clear();
        _documentVirtualSize = Size.Empty;
        _viewportSize = Size.Empty;
        _scrollPosition = Point.Empty;
        UpdateScrollBars(new ViewportMetrics(GetAvailableViewportSize(false, false), false, false));
    }

    private void HookPageView(PdfPageViewControl view)
    {
        view.MouseEnter += (_, _) => Focus();
        view.MouseWheel += (_, e) => HandleWheelFrom(view, e);
        view.MouseDown += (_, e) => HandlePageMouseDown(view, e);
        view.MouseMove += (_, e) => HandlePageMouseMove(view, e);
        view.MouseUp += (_, e) => HandlePageMouseUp(view, e);
        view.MouseLeave += (_, _) => HandlePageMouseLeave(view);
    }

    private void HandleWheelFrom(Control source, MouseEventArgs e)
    {
        // The document view usually already receives WM_MOUSEWHEEL directly
        // because we move focus onto it on mouse enter. Forwarding the same
        // wheel event again from the child page control makes page-by-page
        // mode skip two pages per single wheel notch.
        if (ContainsFocus)
            return;

        Point clientPoint = PointToClient(source.PointToScreen(e.Location));
        var translated = new MouseEventArgs(e.Button, e.Clicks, clientPoint.X, clientPoint.Y, e.Delta);
        OnMouseWheel(translated);
    }

    private void HandlePageMouseDown(Control source, MouseEventArgs e)
    {
        Focus();

        if (e.Button != MouseButtons.Middle)
            return;

        _isPanning = true;
        _panStartMouseScreen = Control.MousePosition;
        _panStartScroll = GetScrollPosition();
        Cursor = Cursors.SizeAll;
        source.Cursor = Cursors.SizeAll;
        source.Capture = true;
    }

    private void HandlePageMouseMove(PdfPageViewControl view, MouseEventArgs e)
    {
        if (_isPanning)
        {
            Point current = Control.MousePosition;
            bool changed = SetScrollPosition(
                _panStartScroll.X - (current.X - _panStartMouseScreen.X),
                _panStartScroll.Y - (current.Y - _panStartMouseScreen.Y));
            if (changed)
                HandleViewportScrolled();
            return;
        }

        if (!_objectOverlayEnabled)
            return;

        int pageIndex = view.PageIndex;
        if (_hoveredPageIndex != pageIndex)
        {
            ClearHoveredPage();
            _hoveredPageIndex = pageIndex;
        }

        view.UpdateHoveredObject(e.Location);
    }

    private void HandlePageMouseUp(Control source, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
            StopPanning(source);
    }

    private void HandlePageMouseLeave(PdfPageViewControl view)
    {
        if (_isPanning || !_objectOverlayEnabled)
            return;

        view.ClearHoveredObject();
        if (_hoveredPageIndex == view.PageIndex)
            _hoveredPageIndex = -1;
    }

    private void StopPanning(Control source)
    {
        _isPanning = false;
        Cursor = Cursors.Default;
        source.Cursor = Cursors.Default;
        source.Capture = false;
    }

    private void ScrollSmoothly(int delta)
    {
        if (delta == 0)
            return;

        int notches = Math.Max(1, Math.Abs(delta) / 120);
        int step = Math.Max(96, SystemInformation.MouseWheelScrollLines * 48) * notches;
        Point scroll = GetScrollPosition();
        int nextY = scroll.Y + (delta > 0 ? -step : step);
        if (SetScrollPosition(scroll.X, nextY))
            HandleViewportScrolled();
    }

    private void HandleScrollBarMoved(object? sender, ScrollEventArgs e)
    {
        if (_syncingScrollBars)
            return;

        int targetX = _horizontalScrollBar.Visible ? _horizontalScrollBar.Value : 0;
        int targetY = _verticalScrollBar.Visible ? _verticalScrollBar.Value : 0;
        if (SetScrollPosition(targetX, targetY))
            HandleViewportScrolled();
    }

    private void LayoutPages()
    {
        if (_pages.Count == 0)
        {
            _documentVirtualSize = Size.Empty;
            UpdateScrollBars(new ViewportMetrics(GetAvailableViewportSize(false, false), false, false));
            UpdatePageViewLocations();
            return;
        }

        if (_scrollMode == PdfScrollMode.PageByPage)
        {
            LayoutSinglePage();
            return;
        }

        int widestPage = _pages.Max(page => page.PixelSize.Width);
        int baseContentWidth = widestPage + (PageMargin * 2);
        int contentHeight = PageMargin;

        foreach (PageSlot slot in _pages)
            contentHeight += slot.PixelSize.Height + PageMargin;

        ViewportMetrics viewportMetrics = ResolveViewportMetrics(baseContentWidth, contentHeight);
        Size viewportSize = viewportMetrics.ViewportSize;
        int virtualWidth = Math.Max(baseContentWidth, viewportSize.Width);
        int virtualHeight = Math.Max(contentHeight, viewportSize.Height);
        int y = PageMargin;

        foreach (PageSlot slot in _pages)
        {
            slot.View.Visible = true;
            int x = (virtualWidth - slot.PixelSize.Width) / 2;
            slot.DocumentLocation = new Point(x, y);
            y += slot.PixelSize.Height + PageMargin;
        }

        _documentVirtualSize = new Size(virtualWidth, virtualHeight);
        UpdateScrollBars(viewportMetrics);
        UpdatePageViewLocations();
    }

    private void LayoutSinglePage()
    {
        int activePageIndex = GetPreferredPageIndex();
        if (activePageIndex < 0 || activePageIndex >= _pages.Count)
        {
            _documentVirtualSize = Size.Empty;
            UpdateScrollBars(new ViewportMetrics(GetAvailableViewportSize(false, false), false, false));
            UpdatePageViewLocations();
            return;
        }

        _pageByPageIndex = activePageIndex;

        PageSlot activeSlot = _pages[activePageIndex];
        int baseContentWidth = activeSlot.PixelSize.Width + (PageMargin * 2);
        int baseContentHeight = activeSlot.PixelSize.Height + (PageMargin * 2);
        ViewportMetrics viewportMetrics = ResolveViewportMetrics(baseContentWidth, baseContentHeight);
        Size viewportSize = viewportMetrics.ViewportSize;
        bool fitsViewportWidth = baseContentWidth <= viewportSize.Width;
        bool fitsViewportHeight = baseContentHeight <= viewportSize.Height;
        bool alignTop = ShouldAlignPageTop(activePageIndex);

        int virtualWidth = Math.Max(baseContentWidth, viewportSize.Width);
        int virtualHeight = Math.Max(baseContentHeight, viewportSize.Height);

        for (int i = 0; i < _pages.Count; i++)
        {
            PageSlot slot = _pages[i];
            bool isActive = i == activePageIndex;
            slot.View.Visible = isActive;

            if (!isActive)
            {
                slot.DocumentLocation = new Point(-10000, -10000);
                slot.View.Location = slot.DocumentLocation;
                continue;
            }

            int x = fitsViewportWidth
                ? Math.Max(PageMargin, (viewportSize.Width - slot.PixelSize.Width) / 2)
                : PageMargin;
            int y = fitsViewportHeight
                ? (alignTop
                    ? PageMargin
                    : Math.Max(PageMargin, (viewportSize.Height - slot.PixelSize.Height) / 2))
                : PageMargin;
            slot.DocumentLocation = new Point(x, y);
        }

        _documentVirtualSize = new Size(virtualWidth, virtualHeight);
        UpdateScrollBars(viewportMetrics);
        UpdatePageViewLocations();
    }

    private void SnapToNearestPage(bool preferCurrentPage)
    {
        if (_pages.Count == 0)
            return;

        int targetIndex = preferCurrentPage && _currentPageIndex >= 0
            ? Math.Clamp(_currentPageIndex, 0, _pages.Count - 1)
            : GetNearestPageIndex();

        ScrollToPage(targetIndex, alignTop: ShouldAlignPageTop(targetIndex));
    }

    private void EnsureVisiblePagesRendered()
    {
        if (_pages.Count == 0)
            return;

        if (_scrollMode == PdfScrollMode.PageByPage)
        {
            int activePageIndex = GetPreferredPageIndex();
            _pageByPageIndex = activePageIndex;
            ClearQueuedRenders(activePageIndex);

            for (int i = 0; i < _pages.Count; i++)
            {
                PageSlot slot = _pages[i];
                NormalizeRenderState(slot);

                if (i == activePageIndex)
                {
                    if (!slot.View.HasRenderedBitmap && !slot.IsRendering)
                        StartRender(slot, i, _renderGeneration);

                    continue;
                }
            }

            return;
        }

        Rectangle viewport = GetViewportRectangle();
        Rectangle renderZone = viewport;
        renderZone.Inflate(0, RenderViewportPadding);
        Rectangle releaseZone = viewport;
        releaseZone.Inflate(0, ReleaseViewportPadding);

        int anchorPageIndex = GetDominantVisiblePageIndex();
        HashSet<int> visiblePageIndices = GetPageIndicesIntersecting(viewport);
        HashSet<int> renderZonePageIndices = GetPageIndicesIntersecting(renderZone);
        HashSet<int> releaseZonePageIndices = GetPageIndicesIntersecting(releaseZone);
        HashSet<int> anchorNeighborhood = ExpandPageIndexSet(new[] { anchorPageIndex }, SmoothPreloadNeighborRadius + 2);

        HashSet<int> preferredPageIndices = ExpandPageIndexSet(renderZonePageIndices, SmoothPreloadNeighborRadius);
        preferredPageIndices.UnionWith(ExpandPageIndexSet(visiblePageIndices, SmoothPreloadNeighborRadius + 1));
        preferredPageIndices.UnionWith(anchorNeighborhood);

        if (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
            preferredPageIndices.UnionWith(ExpandPageIndexSet(new[] { _currentPageIndex }, SmoothPreloadNeighborRadius + 1));

        List<int> renderPriority = GetSmoothRenderPriority(viewport, renderZone, preferredPageIndices).ToList();

        HashSet<int> residentPageIndices = ExpandPageIndexSet(releaseZonePageIndices, SmoothResidentNeighborRadius);
        residentPageIndices.UnionWith(ExpandPageIndexSet(visiblePageIndices, SmoothResidentNeighborRadius + 1));
        residentPageIndices.UnionWith(ExpandPageIndexSet(new[] { anchorPageIndex }, SmoothResidentNeighborRadius + 2));

        if (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
            residentPageIndices.UnionWith(ExpandPageIndexSet(new[] { _currentPageIndex }, SmoothResidentNeighborRadius + 1));

        HashSet<int> mustKeepPageIndices = new(preferredPageIndices);
        mustKeepPageIndices.UnionWith(ExpandPageIndexSet(visiblePageIndices, 1));
        mustKeepPageIndices.UnionWith(ExpandPageIndexSet(new[] { anchorPageIndex }, 2));

        if (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
            mustKeepPageIndices.UnionWith(ExpandPageIndexSet(new[] { _currentPageIndex }, 1));

        PruneAndReorderQueuedRenders(renderPriority);

        foreach (int pageIndex in renderPriority)
        {
            PageSlot slot = _pages[pageIndex];
            NormalizeRenderState(slot);

            if (visiblePageIndices.Contains(pageIndex) && !slot.View.HasRenderedBitmap)
                slot.IsRendered = false;

            if (!slot.View.HasRenderedBitmap && !slot.IsRendering)
                StartRender(slot, pageIndex, _renderGeneration);
        }

        // In smooth mode correctness matters more than aggressive reclamation.
        // We were discarding rendered pages too early, which left white gaps and
        // caused pages to appear "lost" during long scroll sessions. Keep the
        // rendered pages resident for now; once continuous scrolling is stable,
        // we can come back with a safer LRU-style eviction policy.
        return;
    }

    private void StartRender(PageSlot slot, int pageIndex, int generation)
    {
        if (slot.IsRendering)
            return;

        slot.IsRendering = true;
        int requestId = ++_nextRenderRequestId;
        slot.ActiveRenderRequestId = requestId;
        slot.RenderStartedUtc = DateTime.MinValue;
        _renderQueue.Enqueue(new RenderWorkItem(slot, pageIndex, generation, requestId, _zoom, _rotationDegrees));
        TryStartNextRender();
    }

    private void TryStartNextRender()
    {
        if (_activeRenderWorkers >= MaxConcurrentRenderWorkers)
            return;

        bool needsVisibleRenderPass = false;

        while (_renderQueue.Count > 0 && _activeRenderWorkers < MaxConcurrentRenderWorkers)
        {
            RenderWorkItem item = _renderQueue.Dequeue();

            if (IsDisposed ||
                item.Generation != _renderGeneration ||
                item.PageIndex < 0 ||
                item.PageIndex >= _pages.Count ||
                _pages[item.PageIndex] != item.Slot ||
                !item.Slot.IsRendering ||
                item.Slot.ActiveRenderRequestId != item.RequestId)
            {
                ReleaseRenderRequest(item.Slot, item.RequestId);
                needsVisibleRenderPass = true;
                continue;
            }

            _activeRenderWorkers++;
            item.Slot.RenderStartedUtc = DateTime.UtcNow;

            _ = Task.Run(() => RotateRenderResult(SimplePdfRenderer.RenderWithObjects(item.Slot.Page, item.Zoom), item.RotationDegrees))
                .ContinueWith(task =>
                {
                    if (_activeRenderWorkers > 0)
                        _activeRenderWorkers--;

                    if (task.IsFaulted || task.IsCanceled)
                    {
                        HandleDroppedRender(item.Slot, item.RequestId);
                        TryStartNextRender();
                        return;
                    }

                    PdfRenderResult result = task.Result;
                    if (IsDisposed || item.Generation != _renderGeneration || item.PageIndex >= _pages.Count)
                    {
                        HandleDroppedRender(item.Slot, item.RequestId);
                        result.Bitmap.Dispose();
                        TryStartNextRender();
                        return;
                    }

                    if (item.Slot.View.IsDisposed)
                    {
                        if (item.Slot.ActiveRenderRequestId == item.RequestId)
                        {
                            item.Slot.IsRendering = false;
                            item.Slot.IsRendered = false;
                            item.Slot.ActiveRenderRequestId = 0;
                            item.Slot.RenderStartedUtc = DateTime.MinValue;
                        }

                        result.Bitmap.Dispose();
                        TryStartNextRender();
                        return;
                    }

                    if (_pages[item.PageIndex] != item.Slot)
                    {
                        if (item.Slot.ActiveRenderRequestId == item.RequestId)
                        {
                            item.Slot.IsRendering = false;
                            item.Slot.IsRendered = false;
                            item.Slot.ActiveRenderRequestId = 0;
                            item.Slot.RenderStartedUtc = DateTime.MinValue;
                            QueueVisibleRenderPass();
                        }

                        result.Bitmap.Dispose();
                        TryStartNextRender();
                        return;
                    }

                    if (item.Slot.ActiveRenderRequestId != item.RequestId)
                    {
                        result.Bitmap.Dispose();
                        TryStartNextRender();
                        return;
                    }

                    item.Slot.IsRendering = false;
                    item.Slot.ActiveRenderRequestId = 0;
                    item.Slot.RenderStartedUtc = DateTime.MinValue;
                    item.Slot.View.SetRenderedPage(result.Bitmap, result.Objects);
                    item.Slot.View.SetObjectOverlayEnabled(_objectOverlayEnabled);
                    item.Slot.IsRendered = true;
                    QueueVisibleRenderPass();
                    TryStartNextRender();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        if (needsVisibleRenderPass)
            QueueVisibleRenderPass();
    }

    private static void ReleaseRenderRequest(PageSlot slot, int requestId)
    {
        if (slot.ActiveRenderRequestId != requestId)
            return;

        slot.IsRendering = false;
        slot.ActiveRenderRequestId = 0;
        slot.RenderStartedUtc = DateTime.MinValue;
    }

    private void HandleDroppedRender(PageSlot slot, int requestId)
    {
        if (slot.ActiveRenderRequestId != requestId)
            return;

        slot.IsRendering = false;
        slot.IsRendered = false;
        slot.ActiveRenderRequestId = 0;
        slot.RenderStartedUtc = DateTime.MinValue;
        QueueVisibleRenderPass();
    }

    private void QueueVisibleRenderPass()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        BeginInvoke((Action)(() =>
        {
            if (IsDisposed)
                return;

            EnsureVisiblePagesRendered();
            UpdateCurrentPageFromViewport();
            UpdateHoveredPageUnderCursor();
        }));
    }

    private void ClearQueuedRenders(int keepPageIndex = -1)
    {
        HashSet<int> keptRequestIds = new();

        if (_renderQueue.Count > 0)
        {
            if (keepPageIndex >= 0)
            {
                List<RenderWorkItem> preserved = new();
                while (_renderQueue.Count > 0)
                {
                    RenderWorkItem request = _renderQueue.Dequeue();
                    if (request.PageIndex == keepPageIndex)
                    {
                        preserved.Add(request);
                        keptRequestIds.Add(request.RequestId);
                    }
                }

                foreach (RenderWorkItem request in preserved)
                    _renderQueue.Enqueue(request);
            }
            else
            {
                _renderQueue.Clear();
            }
        }

        for (int i = 0; i < _pages.Count; i++)
        {
            PageSlot slot = _pages[i];
            bool waitingInQueue = slot.IsRendering && slot.RenderStartedUtc == DateTime.MinValue;
            if (!waitingInQueue)
                continue;

            if (keptRequestIds.Contains(slot.ActiveRenderRequestId))
                continue;

            slot.IsRendering = false;
            slot.ActiveRenderRequestId = 0;
            slot.RenderStartedUtc = DateTime.MinValue;
        }
    }

    private bool IsRequestQueued(int requestId)
    {
        if (requestId == 0 || _renderQueue.Count == 0)
            return false;

        foreach (RenderWorkItem item in _renderQueue)
        {
            if (item.RequestId == requestId)
                return true;
        }

        return false;
    }

    private HashSet<int> GetPageIndicesIntersecting(Rectangle zone)
    {
        HashSet<int> pageIndices = new();

        for (int i = 0; i < _pages.Count; i++)
        {
            Rectangle bounds = new(_pages[i].DocumentLocation, _pages[i].PixelSize);
            if (!bounds.IntersectsWith(zone))
                continue;

            pageIndices.Add(i);
        }

        return pageIndices;
    }

    private HashSet<int> ExpandPageIndexSet(IEnumerable<int> seedIndices, int radius)
    {
        HashSet<int> expanded = new();

        foreach (int seedIndex in seedIndices)
        {
            if (seedIndex < 0 || seedIndex >= _pages.Count)
                continue;

            int start = Math.Max(0, seedIndex - radius);
            int end = Math.Min(_pages.Count - 1, seedIndex + radius);
            for (int index = start; index <= end; index++)
                expanded.Add(index);
        }

        return expanded;
    }

    private void PruneAndReorderQueuedRenders(IReadOnlyList<int> preferredPageOrder)
    {
        if (_renderQueue.Count == 0)
            return;

        Dictionary<int, int> rankByPage = new(preferredPageOrder.Count);
        for (int i = 0; i < preferredPageOrder.Count; i++)
            rankByPage[preferredPageOrder[i]] = i;

        List<RenderWorkItem> kept = new();
        while (_renderQueue.Count > 0)
        {
            RenderWorkItem item = _renderQueue.Dequeue();
            if (item.Generation != _renderGeneration ||
                item.PageIndex < 0 ||
                item.PageIndex >= _pages.Count ||
                _pages[item.PageIndex] != item.Slot ||
                !rankByPage.ContainsKey(item.PageIndex))
            {
                ReleaseRenderRequest(item.Slot, item.RequestId);
                continue;
            }

            kept.Add(item);
        }

        if (kept.Count == 0)
            return;

        foreach (RenderWorkItem item in kept.OrderBy(item => rankByPage[item.PageIndex]))
            _renderQueue.Enqueue(item);
    }

    private void NormalizeRenderState(PageSlot slot)
    {
        if (slot.IsRendered && !slot.View.HasRenderedBitmap)
            slot.IsRendered = false;

        if (!slot.IsRendering)
            return;

        bool invalidRequest = slot.ActiveRenderRequestId == 0;
        bool orphanedQueuedRequest =
            slot.RenderStartedUtc == DateTime.MinValue &&
            slot.ActiveRenderRequestId != 0 &&
            !IsRequestQueued(slot.ActiveRenderRequestId);
        bool stalled =
            slot.RenderStartedUtc != DateTime.MinValue &&
            (DateTime.UtcNow - slot.RenderStartedUtc) > RenderStallTimeout;

        if (!invalidRequest && !orphanedQueuedRequest && !stalled)
            return;

        slot.IsRendering = false;
        slot.ActiveRenderRequestId = 0;
        slot.RenderStartedUtc = DateTime.MinValue;
    }

    private void UpdateCurrentPageFromViewport()
    {
        if (_pages.Count == 0)
        {
            if (_currentPageIndex != -1)
            {
                _currentPageIndex = -1;
                CurrentPageChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        int bestIndex = _scrollMode == PdfScrollMode.PageByPage
            ? GetPreferredPageIndex()
            : GetDominantVisiblePageIndex();

        if (bestIndex == _currentPageIndex)
            return;

        _currentPageIndex = bestIndex;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    private int GetNearestPageIndex()
    {
        Rectangle viewport = GetViewportRectangle();
        int centerY = viewport.Top + (viewport.Height / 2);

        int bestIndex = 0;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < _pages.Count; i++)
        {
            Rectangle bounds = new(_pages[i].DocumentLocation, _pages[i].PixelSize);
            int candidateCenter = bounds.Top + (bounds.Height / 2);
            int distance = Math.Abs(centerY - candidateCenter);

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestIndex = i;
        }

        return bestIndex;
    }

    private int GetDominantVisiblePageIndex()
    {
        Rectangle viewport = GetViewportRectangle();
        int centerY = viewport.Top + (viewport.Height / 2);

        int bestCenteredIndex = -1;
        long bestCenteredArea = -1;
        int bestCenteredDistance = int.MaxValue;

        int bestFallbackIndex = -1;
        long bestFallbackArea = -1;
        int bestFallbackDistance = int.MaxValue;

        for (int i = 0; i < _pages.Count; i++)
        {
            Rectangle bounds = new(_pages[i].DocumentLocation, _pages[i].PixelSize);
            Rectangle visibleBounds = Rectangle.Intersect(bounds, viewport);
            if (visibleBounds.IsEmpty || visibleBounds.Width <= 0 || visibleBounds.Height <= 0)
                continue;

            long visibleArea = (long)visibleBounds.Width * visibleBounds.Height;
            int pageCenterY = bounds.Top + (bounds.Height / 2);
            int centerDistance = Math.Abs(pageCenterY - centerY);
            bool spansViewportCenter = bounds.Top <= centerY && bounds.Bottom >= centerY;

            if (spansViewportCenter)
            {
                if (visibleArea > bestCenteredArea ||
                    (visibleArea == bestCenteredArea && centerDistance < bestCenteredDistance))
                {
                    bestCenteredIndex = i;
                    bestCenteredArea = visibleArea;
                    bestCenteredDistance = centerDistance;
                }
            }

            if (visibleArea > bestFallbackArea ||
                (visibleArea == bestFallbackArea && centerDistance < bestFallbackDistance))
            {
                bestFallbackIndex = i;
                bestFallbackArea = visibleArea;
                bestFallbackDistance = centerDistance;
            }
        }

        if (bestCenteredIndex >= 0)
            return bestCenteredIndex;

        if (bestFallbackIndex >= 0)
            return bestFallbackIndex;

        return GetNearestPageIndex();
    }

    private IEnumerable<int> GetSmoothRenderPriority(Rectangle viewport, Rectangle renderZone, IReadOnlyCollection<int> preferredPageIndices)
    {
        Point viewportCenter = new(viewport.Left + (viewport.Width / 2), viewport.Top + (viewport.Height / 2));
        HashSet<int> preferredSet = preferredPageIndices as HashSet<int> ?? new HashSet<int>(preferredPageIndices);

        return Enumerable.Range(0, _pages.Count)
            .Select(index =>
            {
                Rectangle bounds = new(_pages[index].DocumentLocation, _pages[index].PixelSize);
                Rectangle viewportIntersection = Rectangle.Intersect(bounds, viewport);
                Rectangle renderIntersection = Rectangle.Intersect(bounds, renderZone);

                long viewportArea = viewportIntersection.IsEmpty || viewportIntersection.Width <= 0 || viewportIntersection.Height <= 0
                    ? 0L
                    : (long)viewportIntersection.Width * viewportIntersection.Height;
                long renderArea = renderIntersection.IsEmpty || renderIntersection.Width <= 0 || renderIntersection.Height <= 0
                    ? 0L
                    : (long)renderIntersection.Width * renderIntersection.Height;

                Point pageCenter = new(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
                int centerDistance = Math.Abs(pageCenter.X - viewportCenter.X) + Math.Abs(pageCenter.Y - viewportCenter.Y);

                return new
                {
                    Index = index,
                    IsPreferred = preferredSet.Contains(index),
                    ViewportArea = viewportArea,
                    RenderArea = renderArea,
                    CenterDistance = centerDistance
                };
            })
            .Where(item => item.RenderArea > 0 || item.IsPreferred)
            .OrderByDescending(item => item.IsPreferred)
            .ThenByDescending(item => item.ViewportArea > 0)
            .ThenByDescending(item => item.ViewportArea)
            .ThenByDescending(item => item.RenderArea)
            .ThenBy(item => item.CenterDistance)
            .Select(item => item.Index);
    }

    private int GetPreferredPageIndex()
    {
        if (_pages.Count == 0)
            return -1;

        if (_pageByPageIndex >= 0 && _pageByPageIndex < _pages.Count)
            return _pageByPageIndex;

        if (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
            return _currentPageIndex;

        return 0;
    }

    private int GetCurrentOrPreferredPageIndex()
    {
        if (_pages.Count == 0)
            return -1;

        if (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
            return _currentPageIndex;

        return GetPreferredPageIndex();
    }

    private ViewAnchor? TryCaptureAnchor(Point viewportPoint)
    {
        Point scroll = GetScrollPosition();
        Point documentPoint = new(scroll.X + viewportPoint.X, scroll.Y + viewportPoint.Y);

        foreach (PageSlot slot in _pages)
        {
            Rectangle bounds = new(slot.DocumentLocation, slot.PixelSize);
            if (!bounds.Contains(documentPoint))
                continue;

            float localX = documentPoint.X - bounds.Left;
            float localY = documentPoint.Y - bounds.Top;

            return new ViewAnchor(
                slot.View.PageIndex,
                localX / Math.Max(1f, slot.PixelSize.Width),
                localY / Math.Max(1f, slot.PixelSize.Height),
                viewportPoint.X,
                viewportPoint.Y);
        }

        return null;
    }

    private void RestoreAnchor(ViewAnchor anchor)
    {
        if (anchor.PageIndex < 0 || anchor.PageIndex >= _pages.Count)
            return;

        PageSlot slot = _pages[anchor.PageIndex];
        int targetX = (int)Math.Round(slot.DocumentLocation.X + (slot.PixelSize.Width * anchor.RelativeX) - anchor.ViewportX);
        int targetY = (int)Math.Round(slot.DocumentLocation.Y + (slot.PixelSize.Height * anchor.RelativeY) - anchor.ViewportY);
        SetScrollPosition(targetX, targetY);
    }

    private void UpdateHoveredPageUnderCursor()
    {
        if (!_objectOverlayEnabled)
        {
            ClearHoveredPage();
            return;
        }

        Point screenPoint = Control.MousePosition;
        if (!RectangleToScreen(ClientRectangle).Contains(screenPoint))
        {
            ClearHoveredPage();
            return;
        }

        Point clientPoint = PointToClient(screenPoint);
        Point scroll = GetScrollPosition();
        Point documentPoint = new(scroll.X + clientPoint.X, scroll.Y + clientPoint.Y);

        foreach (PageSlot slot in _pages)
        {
            Rectangle bounds = new(slot.DocumentLocation, slot.PixelSize);
            if (!bounds.Contains(documentPoint))
                continue;

            if (_hoveredPageIndex != slot.View.PageIndex)
            {
                ClearHoveredPage();
                _hoveredPageIndex = slot.View.PageIndex;
            }

            Point localPoint = new(documentPoint.X - bounds.Left, documentPoint.Y - bounds.Top);
            slot.View.UpdateHoveredObject(localPoint);
            return;
        }

        ClearHoveredPage();
    }

    private void ClearHoveredPage()
    {
        if (_hoveredPageIndex < 0 || _hoveredPageIndex >= _pages.Count)
        {
            _hoveredPageIndex = -1;
            return;
        }

        _pages[_hoveredPageIndex].View.ClearHoveredObject();
        _hoveredPageIndex = -1;
    }

    private Rectangle GetViewportRectangle()
    {
        Point scroll = GetScrollPosition();
        Size viewportSize = GetViewportSize();
        return new Rectangle(scroll, viewportSize);
    }

    private Point GetScrollPosition()
    {
        return _scrollPosition;
    }

    private bool SetScrollPosition(int x, int y)
    {
        Point maxScroll = GetMaxScrollPosition();
        Point clamped = new(
            Math.Clamp(x, 0, maxScroll.X),
            Math.Clamp(y, 0, maxScroll.Y));

        if (clamped == _scrollPosition)
            return false;

        _scrollPosition = clamped;
        SyncScrollBarsToScrollPosition();
        UpdatePageViewLocations();
        Invalidate();
        return true;
    }

    private void PerformInternalScroll(int x, int y)
    {
        SetScrollPosition(x, y);
    }

    private Point GetMaxScrollPosition()
    {
        return new Point(
            Math.Max(0, _documentVirtualSize.Width - GetViewportSize().Width),
            Math.Max(0, _documentVirtualSize.Height - GetViewportSize().Height));
    }

    private Point GetPageScrollTarget(PageSlot slot, bool alignTop)
    {
        Size viewportSize = GetViewportSize();
        bool fitsViewportWidth = slot.PixelSize.Width + (PageMargin * 2) <= viewportSize.Width;
        int centeredX = Math.Max(0, slot.DocumentLocation.X - Math.Max(0, (viewportSize.Width - slot.PixelSize.Width) / 2));
        int leftAlignedX = Math.Max(0, slot.DocumentLocation.X - PageMargin);
        int topAlignedY = Math.Max(0, slot.DocumentLocation.Y - PageMargin);
        int centeredY = Math.Max(0, slot.DocumentLocation.Y - Math.Max(0, (viewportSize.Height - slot.PixelSize.Height) / 2));

        int targetX = fitsViewportWidth ? centeredX : leftAlignedX;
        int targetY = alignTop ? topAlignedY : centeredY;

        return new Point(targetX, targetY);
    }

    private bool ShouldAlignPageTop(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            return true;

        return _pages[pageIndex].PixelSize.Height > GetViewportSize().Height;
    }

    public Point GetViewportCenter()
    {
        Size viewportSize = GetViewportSize();
        return new Point(viewportSize.Width / 2, viewportSize.Height / 2);
    }

    private void HandleViewportScrolled()
    {
        EnsureVisiblePagesRendered();
        UpdateCurrentPageFromViewport();
        UpdateHoveredPageUnderCursor();
    }

    private void UpdatePageViewLocations()
    {
        SuspendLayout();
        try
        {
            foreach (PageSlot slot in _pages)
            {
                if (!slot.View.Visible)
                    continue;

                slot.View.Location = new Point(
                    slot.DocumentLocation.X - _scrollPosition.X,
                    slot.DocumentLocation.Y - _scrollPosition.Y);
            }

            _verticalScrollBar.BringToFront();
            _horizontalScrollBar.BringToFront();
        }
        finally
        {
            ResumeLayout();
        }
    }

    private Size GetViewportSize()
    {
        int width = _viewportSize.Width > 0 ? _viewportSize.Width : ClientSize.Width;
        int height = _viewportSize.Height > 0 ? _viewportSize.Height : ClientSize.Height;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    private Size GetAvailableViewportSize(bool showVerticalScrollBar, bool showHorizontalScrollBar)
    {
        int width = ClientSize.Width - (showVerticalScrollBar ? SystemInformation.VerticalScrollBarWidth : 0);
        int height = ClientSize.Height - (showHorizontalScrollBar ? SystemInformation.HorizontalScrollBarHeight : 0);
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    private ViewportMetrics ResolveViewportMetrics(int minContentWidth, int minContentHeight)
    {
        bool showVerticalScrollBar = false;
        bool showHorizontalScrollBar = false;

        for (int i = 0; i < 4; i++)
        {
            Size viewportSize = GetAvailableViewportSize(showVerticalScrollBar, showHorizontalScrollBar);
            bool needsHorizontalScrollBar = minContentWidth > viewportSize.Width;
            bool needsVerticalScrollBar = minContentHeight > viewportSize.Height;
            if (needsHorizontalScrollBar == showHorizontalScrollBar &&
                needsVerticalScrollBar == showVerticalScrollBar)
            {
                return new ViewportMetrics(viewportSize, showVerticalScrollBar, showHorizontalScrollBar);
            }

            showHorizontalScrollBar = needsHorizontalScrollBar;
            showVerticalScrollBar = needsVerticalScrollBar;
        }

        return new ViewportMetrics(
            GetAvailableViewportSize(showVerticalScrollBar, showHorizontalScrollBar),
            showVerticalScrollBar,
            showHorizontalScrollBar);
    }

    private void UpdateScrollBars(ViewportMetrics viewportMetrics)
    {
        _viewportSize = viewportMetrics.ViewportSize;

        Point maxScroll = new(
            Math.Max(0, _documentVirtualSize.Width - _viewportSize.Width),
            Math.Max(0, _documentVirtualSize.Height - _viewportSize.Height));
        _scrollPosition = new Point(
            Math.Clamp(_scrollPosition.X, 0, maxScroll.X),
            Math.Clamp(_scrollPosition.Y, 0, maxScroll.Y));

        _syncingScrollBars = true;
        try
        {
            _verticalScrollBar.Visible = viewportMetrics.ShowVerticalScrollBar;
            _horizontalScrollBar.Visible = viewportMetrics.ShowHorizontalScrollBar;

            if (_verticalScrollBar.Visible)
            {
                _verticalScrollBar.Bounds = new Rectangle(
                    Math.Max(0, ClientSize.Width - SystemInformation.VerticalScrollBarWidth),
                    0,
                    SystemInformation.VerticalScrollBarWidth,
                    Math.Max(0, _viewportSize.Height));
                ConfigureScrollBar(_verticalScrollBar, maxScroll.Y, _viewportSize.Height, _scrollPosition.Y);
            }

            if (_horizontalScrollBar.Visible)
            {
                _horizontalScrollBar.Bounds = new Rectangle(
                    0,
                    Math.Max(0, ClientSize.Height - SystemInformation.HorizontalScrollBarHeight),
                    Math.Max(0, _viewportSize.Width),
                    SystemInformation.HorizontalScrollBarHeight);
                ConfigureScrollBar(_horizontalScrollBar, maxScroll.X, _viewportSize.Width, _scrollPosition.X);
            }

            if (!_verticalScrollBar.Visible)
                _verticalScrollBar.Value = 0;

            if (!_horizontalScrollBar.Visible)
                _horizontalScrollBar.Value = 0;
        }
        finally
        {
            _syncingScrollBars = false;
        }

        _verticalScrollBar.BringToFront();
        _horizontalScrollBar.BringToFront();
    }

    private void ConfigureScrollBar(ScrollBar scrollBar, int maxScroll, int viewportSpan, int currentValue)
    {
        int largeChange = Math.Max(1, viewportSpan);
        int clampedValue = Math.Clamp(currentValue, 0, maxScroll);

        scrollBar.Minimum = 0;
        scrollBar.SmallChange = Math.Max(16, viewportSpan / 8);
        scrollBar.LargeChange = largeChange;
        scrollBar.Maximum = maxScroll + largeChange - 1;
        scrollBar.Enabled = maxScroll > 0;
        scrollBar.Value = clampedValue;
    }

    private void SyncScrollBarsToScrollPosition()
    {
        _syncingScrollBars = true;
        try
        {
            if (_verticalScrollBar.Visible)
                _verticalScrollBar.Value = Math.Clamp(_scrollPosition.Y, 0, Math.Max(0, _documentVirtualSize.Height - GetViewportSize().Height));

            if (_horizontalScrollBar.Visible)
                _horizontalScrollBar.Value = Math.Clamp(_scrollPosition.X, 0, Math.Max(0, _documentVirtualSize.Width - GetViewportSize().Width));
        }
        finally
        {
            _syncingScrollBars = false;
        }
    }

    private Size CalculatePagePixelSize(SimplePdfPage page)
    {
        float widthPt = _rotationDegrees is 90 or 270 ? page.HeightPt : page.WidthPt;
        float heightPt = _rotationDegrees is 90 or 270 ? page.WidthPt : page.HeightPt;
        return new Size(
            Math.Max(1, (int)Math.Ceiling(widthPt * _zoom)),
            Math.Max(1, (int)Math.Ceiling(heightPt * _zoom)));
    }

    private static PdfRenderResult RotateRenderResult(PdfRenderResult result, int rotationDegrees)
    {
        if (rotationDegrees == 0)
            return result;

        int sourceWidth = result.Bitmap.Width;
        int sourceHeight = result.Bitmap.Height;

        switch (rotationDegrees)
        {
            case 90:
                result.Bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                break;
            case 180:
                result.Bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                break;
            case 270:
                result.Bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                break;
        }

        return new PdfRenderResult(
            result.Bitmap,
            RotateObjects(result.Objects, sourceWidth, sourceHeight, rotationDegrees));
    }

    private static IReadOnlyList<PdfRenderObject> RotateObjects(
        IReadOnlyList<PdfRenderObject> objects,
        int sourceWidth,
        int sourceHeight,
        int rotationDegrees)
    {
        if (objects.Count == 0)
            return Array.Empty<PdfRenderObject>();

        var rotated = new PdfRenderObject[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            rotated[i] = objects[i] with
            {
                Bounds = RotateBounds(objects[i].Bounds, sourceWidth, sourceHeight, rotationDegrees)
            };
        }

        return rotated;
    }

    private static RectangleF RotateBounds(RectangleF bounds, int sourceWidth, int sourceHeight, int rotationDegrees)
    {
        PointF[] points =
        [
            new(bounds.Left, bounds.Top),
            new(bounds.Right, bounds.Top),
            new(bounds.Right, bounds.Bottom),
            new(bounds.Left, bounds.Bottom)
        ];

        float left = float.MaxValue;
        float top = float.MaxValue;
        float right = float.MinValue;
        float bottom = float.MinValue;

        for (int i = 0; i < points.Length; i++)
        {
            PointF rotated = RotatePoint(points[i], sourceWidth, sourceHeight, rotationDegrees);
            left = Math.Min(left, rotated.X);
            top = Math.Min(top, rotated.Y);
            right = Math.Max(right, rotated.X);
            bottom = Math.Max(bottom, rotated.Y);
        }

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static PointF RotatePoint(PointF point, int sourceWidth, int sourceHeight, int rotationDegrees)
    {
        return rotationDegrees switch
        {
            90 => new PointF(sourceHeight - point.Y, point.X),
            180 => new PointF(sourceWidth - point.X, sourceHeight - point.Y),
            270 => new PointF(point.Y, sourceWidth - point.X),
            _ => point
        };
    }

    private sealed class PageSlot
    {
        public required SimplePdfPage Page { get; init; }
        public required PdfPageViewControl View { get; init; }
        public Point DocumentLocation { get; set; }
        public Size PixelSize { get; set; }
        public bool IsRendering { get; set; }
        public bool IsRendered { get; set; }
        public int ActiveRenderRequestId { get; set; }
        public DateTime RenderStartedUtc { get; set; }
    }

    private readonly record struct ViewportMetrics(Size ViewportSize, bool ShowVerticalScrollBar, bool ShowHorizontalScrollBar);

    private readonly record struct ViewAnchor(
        int PageIndex,
        float RelativeX,
        float RelativeY,
        int ViewportX,
        int ViewportY);

    private readonly record struct RenderWorkItem(
        PageSlot Slot,
        int PageIndex,
        int Generation,
        int RequestId,
        float Zoom,
        int RotationDegrees);
}
