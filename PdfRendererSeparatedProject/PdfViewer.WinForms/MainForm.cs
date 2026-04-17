using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PdfCore;
using PdfCore.Color;
using PdfCore.Parsing;

namespace PdfViewer.WinForms;

public sealed class MainForm : Form
{
    private enum ViewFitMode
    {
        None,
        Auto,
        FitHeight,
        FitWidth
    }

    private readonly PdfCanvasHostPanel _canvasHost = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        BackColor = Color.DimGray
    };

    private readonly ImageCanvasControl _canvas = new();

    private readonly ToolStrip _toolStrip = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripButton _openButton = new("Открыть PDF");
    private readonly ToolStripButton _firstPageButton = new("|◀");
    private readonly ToolStripButton _prevButton = new("◀");
    private readonly ToolStripButton _nextButton = new("▶");
    private readonly ToolStripButton _lastPageButton = new("▶|");
    private readonly ToolStripButton _zoomOutButton = new("−");
    private readonly ToolStripButton _zoomInButton = new("+");
    private readonly ToolStripButton _rotateClockwiseButton = new("\u21bb");
    private readonly ToolStripButton _fitHeightButton = new("↕");
    private readonly ToolStripButton _fitWidthButton = new("↔");
    private readonly ToolStripLabel _zoomLabel = new("100%");
    private readonly ToolStripLabel _pageLabel = new("0 / 0");
    private readonly ToolStripLabel _loadStatusLabel = new();
    private readonly ToolStripProgressBar _loadProgressBar = new()
    {
        AutoSize = false,
        Width = 160,
        Visible = false
    };
    private readonly ToolStripComboBox _modeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _objectOverlayCheckBox = new()
    {
        AutoSize = true,
        Text = "\u041e\u0431\u044a\u0435\u043a\u0442\u044b"
    };
    private readonly ToolStripControlHost _objectOverlayHost;

    private SimplePdfDocument? _document;
    private string? _currentPath;
    private int _pageIndex;
    private float _zoom = 1f;
    private ViewFitMode _fitMode = ViewFitMode.None;
    private int _rotationDegrees;
    private bool _isPanning;
    private Point _panStartMouseScreen;
    private Point _panStartScroll;
    private bool _isHandlingResize;
    private bool _isLoadingDocument;

    public MainForm()
    {
        Text = "Simple PDF Renderer — separated stable / experimental";
        Width = 1200;
        Height = 900;

        _canvasHost.Controls.Add(_canvas);
        _canvasHost.TabStop = true;
        _canvas.TabStop = true;
        _objectOverlayHost = new ToolStripControlHost(_objectOverlayCheckBox)
        {
            Margin = new Padding(4, 0, 4, 0),
            Padding = Padding.Empty,
            ToolTipText = "\u041f\u043e\u0434\u0441\u0432\u0435\u0442\u043a\u0430 \u0433\u0440\u0430\u043d\u0438\u0446 \u0442\u0435\u043a\u0441\u0442\u0430 \u0438 \u0438\u0437\u043e\u0431\u0440\u0430\u0436\u0435\u043d\u0438\u0439 \u043f\u043e\u0434 \u043c\u044b\u0448\u043a\u043e\u0439"
        };

        _modeCombo.Items.Add("Stable phase 1");
        _modeCombo.Items.Add("Experimental ICC phase 2");
        _modeCombo.SelectedIndex = 0;
        ConfigureTooltips();

        _toolStrip.Items.Add(_openButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_firstPageButton);
        _toolStrip.Items.Add(_prevButton);
        _toolStrip.Items.Add(_pageLabel);
        _toolStrip.Items.Add(_nextButton);
        _toolStrip.Items.Add(_lastPageButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_zoomOutButton);
        _toolStrip.Items.Add(_zoomLabel);
        _toolStrip.Items.Add(_zoomInButton);
        _toolStrip.Items.Add(_fitHeightButton);
        _toolStrip.Items.Add(_fitWidthButton);
        _toolStrip.Items.Add(_rotateClockwiseButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(new ToolStripLabel("Цветовой режим:"));
        _toolStrip.Items.Add(_modeCombo);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_objectOverlayHost);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_loadStatusLabel);
        _toolStrip.Items.Add(_loadProgressBar);

        Controls.Add(_canvasHost);
        Controls.Add(_toolStrip);

        _openButton.Click += (_, _) => OpenPdf();
        _firstPageButton.Click += (_, _) => GoToPage(0);
        _prevButton.Click += (_, _) => ChangePage(-1);
        _nextButton.Click += (_, _) => ChangePage(1);
        _lastPageButton.Click += (_, _) => GoToLastPage();
        _zoomInButton.Click += (_, _) => ChangeZoom(1.25f);
        _zoomOutButton.Click += (_, _) => ChangeZoom(1f / 1.25f);
        _fitHeightButton.Click += (_, _) => FitHeight();
        _fitWidthButton.Click += (_, _) => FitWidth();
        _rotateClockwiseButton.Click += (_, _) => RotateClockwise();
        _objectOverlayCheckBox.CheckedChanged += (_, _) =>
        {
            _canvas.SetObjectOverlayEnabled(_objectOverlayCheckBox.Checked);
            UpdateObjectHoverUnderCursor();
        };
        _loadStatusLabel.Visible = false;
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            PdfColorManagementSettings.Mode = _modeCombo.SelectedIndex == 1
                ? PdfColorManagementMode.ExperimentalPhase2Icc
                : PdfColorManagementMode.StablePhase1Fallback;
            RenderCurrentPage();
        };
        HookCanvasMouse(_canvasHost);
        HookCanvasMouse(_canvas);
        _canvasHost.BrowserWheel += CanvasMouseWheel;
        _canvasHost.Scroll += (_, _) => UpdateObjectHoverUnderCursor();

        UpdateNavigationButtons();

        Resize += (_, _) => HandleViewportResize();
    }

    private void ConfigureTooltips()
    {
        _firstPageButton.ToolTipText = "Первая страница";
        _prevButton.ToolTipText = "Предыдущая страница";
        _nextButton.ToolTipText = "Следующая страница";
        _lastPageButton.ToolTipText = "Последняя страница";
        _zoomOutButton.ToolTipText = "Уменьшить";
        _zoomInButton.ToolTipText = "Увеличить";
        _fitHeightButton.ToolTipText = "По высоте";
        _fitWidthButton.ToolTipText = "По ширине";
        _rotateClockwiseButton.ToolTipText = "\u041f\u043e\u0432\u0435\u0440\u043d\u0443\u0442\u044c \u043d\u0430 90\u00b0";
    }

    private async void OpenPdf()
    {
        if (_isLoadingDocument)
            return;

        using var dlg = new OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            Title = "Выберите PDF"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        await LoadDocumentAsync(dlg.FileName);
    }

    private async void LoadDocument(string path)
    {
        await LoadDocumentAsync(path);
    }

    private async Task LoadDocumentAsync(string path)
    {
        if (_isLoadingDocument)
            return;

        try
        {
            SetLoadingState(true);
            UpdateLoadingProgress(PdfParseProgress.Indeterminate("Открытие PDF"));

            var progress = new Progress<PdfParseProgress>(UpdateLoadingProgress);
            SimplePdfDocument loadedDocument = await Task.Run(() => SimplePdfParser.Parse(path, progress));

            UpdateLoadingProgress(PdfParseProgress.Indeterminate("Отрисовка первой страницы"));
            await Task.Yield();
            _currentPath = path;
            _document = loadedDocument;
            _pageIndex = 0;
            _rotationDegrees = 0;
            _fitMode = ViewFitMode.Auto;
            ApplyCurrentFitMode();
            RenderCurrentPage();
            SetInitialViewPosition();
            _canvasHost.Focus();
        }
        catch (Exception ex)
        {
            ShowPdfError("Не удалось открыть PDF.", ex);
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private void RenderCurrentPage()
    {
        if (_document == null || _document.Pages.Count == 0)
            return;

        try
        {
            var page = _document.Pages[_pageIndex];
            PdfRenderResult renderResult = ApplyRotation(SimplePdfRenderer.RenderWithObjects(page, _zoom));
            _canvas.SetRenderedPage(renderResult.Bitmap, renderResult.Objects);
            _canvas.SetObjectOverlayEnabled(_objectOverlayCheckBox.Checked);
            UpdateCanvasLayout();
            _pageLabel.Text = $"{_pageIndex + 1} / {_document.Pages.Count}";
            _zoomLabel.Text = $"{Math.Round(_zoom * 100f)}%";
            Text = $"Simple PDF Renderer — {Path.GetFileName(_currentPath ?? "")}";
            UpdateNavigationButtons();
            UpdateObjectHoverUnderCursor();
        }
        catch (Exception ex)
        {
            ShowPdfError("Не удалось отрисовать страницу PDF.", ex);
        }
    }

    private void ShowPdfError(string message, Exception ex)
    {
        MessageBox.Show(
            this,
            message + Environment.NewLine + Environment.NewLine + ex.Message,
            "Ошибка PDF",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void SetLoadingState(bool isLoading)
    {
        _isLoadingDocument = isLoading;
        UseWaitCursor = isLoading;
        _canvasHost.UseWaitCursor = isLoading;
        _canvas.UseWaitCursor = isLoading;

        _openButton.Enabled = !isLoading;
        _modeCombo.Enabled = !isLoading;
        _loadStatusLabel.Visible = isLoading;
        _loadProgressBar.Visible = isLoading;

        if (isLoading)
        {
            _loadProgressBar.Style = ProgressBarStyle.Marquee;
            _loadProgressBar.Value = 0;
        }

        UpdateNavigationButtons();
    }

    private void UpdateLoadingProgress(PdfParseProgress progress)
    {
        _loadStatusLabel.Text = progress.Message;

        if (!progress.IsDeterminate)
        {
            _loadProgressBar.Style = ProgressBarStyle.Marquee;
            return;
        }

        int total = Math.Max(1, progress.Total!.Value);
        int current = Math.Clamp(progress.Current!.Value, 0, total);
        _loadProgressBar.Style = ProgressBarStyle.Continuous;
        _loadProgressBar.Minimum = 0;
        _loadProgressBar.Maximum = total;
        _loadProgressBar.Value = current;
    }

    private void ChangePage(int delta)
    {
        if (_document == null)
            return;

        int next = Math.Clamp(_pageIndex + delta, 0, _document.Pages.Count - 1);
        if (next == _pageIndex)
            return;

        _pageIndex = next;
        ApplyCurrentFitMode();
        RenderCurrentPage();
        SetInitialViewPosition();
    }

    private void GoToPage(int pageIndex)
    {
        if (_document == null)
            return;

        int next = Math.Clamp(pageIndex, 0, _document.Pages.Count - 1);
        if (next == _pageIndex)
            return;

        _pageIndex = next;
        ApplyCurrentFitMode();
        RenderCurrentPage();
        SetInitialViewPosition();
    }

    private void GoToLastPage()
    {
        if (_document == null)
            return;

        GoToPage(_document.Pages.Count - 1);
    }

    private void ChangeZoom(float factor)
    {
        _fitMode = ViewFitMode.None;
        ZoomAt(factor, GetCanvasHostCenter());
    }

    private void FitWidth()
    {
        if (_document == null || _document.Pages.Count == 0)
            return;

        _fitMode = ViewFitMode.FitWidth;
        ApplyCurrentFitMode();
        RenderCurrentPage();
        SetInitialViewPosition();
    }

    private void FitHeight()
    {
        if (_document == null || _document.Pages.Count == 0)
            return;

        _fitMode = ViewFitMode.FitHeight;
        ApplyCurrentFitMode();
        RenderCurrentPage();
        SetInitialViewPosition();
    }

    private void RotateClockwise()
    {
        if (_document == null || _document.Pages.Count == 0)
            return;

        _rotationDegrees = (_rotationDegrees + 90) % 360;
        ApplyCurrentFitMode();
        RenderCurrentPage();
        SetInitialViewPosition();
    }

    private PdfRenderResult ApplyRotation(PdfRenderResult renderResult)
    {
        if (_rotationDegrees == 0)
            return renderResult;

        int sourceWidth = renderResult.Bitmap.Width;
        int sourceHeight = renderResult.Bitmap.Height;

        switch (_rotationDegrees)
        {
            case 90:
                renderResult.Bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                break;
            case 180:
                renderResult.Bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                break;
            case 270:
                renderResult.Bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                break;
        }

        return new PdfRenderResult(
            renderResult.Bitmap,
            RotateObjects(renderResult.Objects, sourceWidth, sourceHeight));
    }

    private void HandleViewportResize()
    {
        if (_isHandlingResize)
            return;

        _isHandlingResize = true;
        try
        {
            if (_document == null || _document.Pages.Count == 0)
            {
                UpdateCanvasLayout();
                return;
            }

            Point scroll = GetScrollPosition();
            float oldZoom = _zoom;
            ApplyCurrentFitMode();

            if (Math.Abs(_zoom - oldZoom) > 0.0001f)
                RenderCurrentPage();
            else
                UpdateCanvasLayout();

            SetScrollPosition(scroll.X, scroll.Y);
            UpdateObjectHoverUnderCursor();
        }
        finally
        {
            _isHandlingResize = false;
        }
    }

    private void ApplyCurrentFitMode()
    {
        if (_document == null || _document.Pages.Count == 0)
            return;

        var page = _document.Pages[_pageIndex];
        _zoom = _fitMode switch
        {
            ViewFitMode.Auto => CalculateAutoFitZoom(page),
            ViewFitMode.FitHeight => CalculateFitHeightZoom(page),
            ViewFitMode.FitWidth => CalculateFitWidthZoom(page),
            _ => _zoom
        };
    }

    private float CalculateAutoFitZoom(SimplePdfPage page)
    {
        float fitHeight = CalculateFitHeightZoom(page);
        int availableWidth = Math.Max(100, _canvasHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 20);
        return GetRotatedPageWidthPt(page) * fitHeight <= availableWidth
            ? fitHeight
            : CalculateFitWidthZoom(page);
    }

    private void SetInitialViewPosition()
    {
        UpdateCanvasLayout();
        SetScrollPosition(0, 0);
        UpdateObjectHoverUnderCursor();
    }

    private void HookCanvasMouse(Control control)
    {
        control.MouseEnter += (_, _) => _canvasHost.Focus();
        control.MouseDown += CanvasMouseDown;
        control.MouseMove += CanvasMouseMove;
        control.MouseUp += CanvasMouseUp;
        control.MouseLeave += CanvasMouseLeave;
    }

    private void CanvasMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_document == null)
            return;

        _canvasHost.Focus();

        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            Control source = sender as Control ?? _canvasHost;
            Point hostPoint = _canvasHost.PointToClient(source.PointToScreen(e.Location));
            float factor = (float)Math.Pow(1.1, e.Delta / 120.0);
            ZoomAt(factor, hostPoint);
            return;
        }

        ScrollByWheel(e.Delta);
    }

    private void CanvasMouseDown(object? sender, MouseEventArgs e)
    {
        _canvasHost.Focus();

        if (e.Button != MouseButtons.Middle)
            return;

        _isPanning = true;
        _panStartMouseScreen = Control.MousePosition;
        _panStartScroll = GetScrollPosition();
        _canvasHost.Cursor = Cursors.SizeAll;
        _canvas.Cursor = Cursors.SizeAll;

        if (sender is Control control)
            control.Capture = true;
    }

    private void CanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            Point current = Control.MousePosition;
            SetScrollPosition(
                _panStartScroll.X - (current.X - _panStartMouseScreen.X),
                _panStartScroll.Y - (current.Y - _panStartMouseScreen.Y));
            return;
        }

        if (!_objectOverlayCheckBox.Checked || sender is not Control control)
            return;

        UpdateObjectHover(control, e.Location);
    }

    private void CanvasMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
            StopPanning(sender as Control);
    }

    private void CanvasMouseLeave(object? sender, EventArgs e)
    {
        if (_isPanning || !_objectOverlayCheckBox.Checked)
            return;

        if (IsPointerOverCanvasArea(Control.MousePosition))
            return;

        _canvas.ClearHoveredObject();
    }

    private void StopPanning(Control? captureControl)
    {
        _isPanning = false;
        _canvasHost.Cursor = Cursors.Default;
        _canvas.Cursor = Cursors.Default;

        if (captureControl != null)
            captureControl.Capture = false;
    }

    private void ScrollByWheel(int delta)
    {
        if (_document == null || delta == 0)
            return;

        int notches = Math.Max(1, Math.Abs(delta) / 120);
        int step = Math.Max(80, SystemInformation.MouseWheelScrollLines * 48) * notches;
        Point scroll = GetScrollPosition();
        Point maxScroll = GetMaxScrollPosition();
        int nextY = scroll.Y + (delta > 0 ? -step : step);

        if (nextY < 0)
        {
            if (_pageIndex > 0)
                ChangePageForWheel(-1, scrollToBottom: true);
            else
                SetScrollPosition(scroll.X, 0);
            return;
        }

        if (nextY > maxScroll.Y)
        {
            if (_pageIndex < _document.Pages.Count - 1)
                ChangePageForWheel(1, scrollToBottom: false);
            else
                SetScrollPosition(scroll.X, maxScroll.Y);
            return;
        }

        SetScrollPosition(scroll.X, nextY);
    }

    private void ChangePageForWheel(int delta, bool scrollToBottom)
    {
        if (_document == null)
            return;

        int next = Math.Clamp(_pageIndex + delta, 0, _document.Pages.Count - 1);
        if (next == _pageIndex)
            return;

        _pageIndex = next;
        ApplyCurrentFitMode();
        RenderCurrentPage();
        if (scrollToBottom)
        {
            Point maxScroll = GetMaxScrollPosition();
            SetScrollPosition(0, maxScroll.Y);
        }
        else
        {
            SetScrollPosition(0, 0);
        }
    }

    private void ZoomAt(float factor, Point hostPoint)
    {
        if (_document == null || factor <= 0)
            return;

        _fitMode = ViewFitMode.None;
        float oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.1f, 8f);
        if (Math.Abs(_zoom - oldZoom) < 0.0001f)
            return;

        Point scroll = GetScrollPosition();
        Point oldBitmapOffset = _canvas.BitmapOffset;
        float ratio = _zoom / oldZoom;
        float anchorX = scroll.X + hostPoint.X - oldBitmapOffset.X;
        float anchorY = scroll.Y + hostPoint.Y - oldBitmapOffset.Y;

        RenderCurrentPage();
        Point newBitmapOffset = _canvas.BitmapOffset;
        SetScrollPosition(
            (int)Math.Round(anchorX * ratio - hostPoint.X + newBitmapOffset.X),
            (int)Math.Round(anchorY * ratio - hostPoint.Y + newBitmapOffset.Y));
    }

    private Point GetCanvasHostCenter()
    {
        return new Point(_canvasHost.ClientSize.Width / 2, _canvasHost.ClientSize.Height / 2);
    }

    private Point GetScrollPosition()
    {
        return new Point(
            Math.Max(0, -_canvasHost.AutoScrollPosition.X),
            Math.Max(0, -_canvasHost.AutoScrollPosition.Y));
    }

    private void UpdateCanvasLayout()
    {
        Size viewport = GetVisibleCanvasViewportSize();
        _canvas.Location = Point.Empty;
        _canvas.SetViewportSize(viewport);
        _canvasHost.AutoScrollMinSize = _canvas.Size;
    }

    private Size GetVisibleCanvasViewportSize()
    {
        int width = Math.Max(1, _canvasHost.ClientSize.Width);
        int height = Math.Max(1, _canvasHost.ClientSize.Height);
        Size bitmapSize = _canvas.BitmapSize;

        bool needsVerticalScroll = bitmapSize.Height > height;
        if (needsVerticalScroll)
            width = Math.Max(1, width - SystemInformation.VerticalScrollBarWidth);

        bool needsHorizontalScroll = bitmapSize.Width > width;
        if (needsHorizontalScroll)
            height = Math.Max(1, height - SystemInformation.HorizontalScrollBarHeight);

        if (!needsVerticalScroll && bitmapSize.Height > height)
            width = Math.Max(1, width - SystemInformation.VerticalScrollBarWidth);

        return new Size(width, height);
    }

    private Point GetMaxScrollPosition()
    {
        _canvasHost.PerformLayout();
        Size viewport = GetVisibleCanvasViewportSize();
        return new Point(
            Math.Max(0, _canvas.Width - viewport.Width),
            Math.Max(0, _canvas.Height - viewport.Height));
    }

    private void SetScrollPosition(int x, int y)
    {
        Point maxScroll = GetMaxScrollPosition();
        _canvasHost.AutoScrollPosition = new Point(
            Math.Clamp(x, 0, maxScroll.X),
            Math.Clamp(y, 0, maxScroll.Y));
    }

    private float CalculateFitWidthZoom(SimplePdfPage page)
    {
        int available = Math.Max(100, _canvasHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 20);
        return Math.Clamp(available / GetRotatedPageWidthPt(page), 0.1f, 8f);
    }

    private float CalculateFitHeightZoom(SimplePdfPage page)
    {
        int available = Math.Max(100, _canvasHost.ClientSize.Height - SystemInformation.HorizontalScrollBarHeight - 20);
        return Math.Clamp(available / GetRotatedPageHeightPt(page), 0.1f, 8f);
    }

    private float GetRotatedPageWidthPt(SimplePdfPage page)
    {
        return _rotationDegrees is 90 or 270 ? page.HeightPt : page.WidthPt;
    }

    private float GetRotatedPageHeightPt(SimplePdfPage page)
    {
        return _rotationDegrees is 90 or 270 ? page.WidthPt : page.HeightPt;
    }

    private IReadOnlyList<PdfRenderObject> RotateObjects(
        IReadOnlyList<PdfRenderObject> objects,
        int sourceWidth,
        int sourceHeight)
    {
        if (objects.Count == 0)
            return Array.Empty<PdfRenderObject>();

        var rotated = new PdfRenderObject[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            rotated[i] = objects[i] with
            {
                Bounds = RotateBounds(objects[i].Bounds, sourceWidth, sourceHeight)
            };
        }

        return rotated;
    }

    private RectangleF RotateBounds(RectangleF bounds, int sourceWidth, int sourceHeight)
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
            PointF rotated = RotatePoint(points[i], sourceWidth, sourceHeight);
            left = Math.Min(left, rotated.X);
            top = Math.Min(top, rotated.Y);
            right = Math.Max(right, rotated.X);
            bottom = Math.Max(bottom, rotated.Y);
        }

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private PointF RotatePoint(PointF point, int sourceWidth, int sourceHeight)
    {
        return _rotationDegrees switch
        {
            90 => new PointF(sourceHeight - point.Y, point.X),
            180 => new PointF(sourceWidth - point.X, sourceHeight - point.Y),
            270 => new PointF(point.Y, sourceWidth - point.X),
            _ => point
        };
    }

    private void UpdateObjectHoverUnderCursor()
    {
        if (!_objectOverlayCheckBox.Checked || !IsPointerOverCanvasArea(Control.MousePosition))
        {
            _canvas.ClearHoveredObject();
            return;
        }

        _canvas.UpdateHoveredObject(_canvas.PointToClient(Control.MousePosition));
    }

    private void UpdateObjectHover(Control source, Point location)
    {
        _canvas.UpdateHoveredObject(_canvas.PointToClient(source.PointToScreen(location)));
    }

    private bool IsPointerOverCanvasArea(Point screenPoint)
    {
        return _canvasHost.RectangleToScreen(_canvasHost.ClientRectangle).Contains(screenPoint);
    }

    private void UpdateNavigationButtons()
    {
        bool hasDocument = !_isLoadingDocument && _document != null && _document.Pages.Count > 0;
        bool hasPrevious = hasDocument && _pageIndex > 0;
        bool hasNext = hasDocument && _document != null && _pageIndex < _document.Pages.Count - 1;

        _firstPageButton.Enabled = hasPrevious;
        _prevButton.Enabled = hasPrevious;
        _nextButton.Enabled = hasNext;
        _lastPageButton.Enabled = hasNext;
        _zoomOutButton.Enabled = hasDocument;
        _zoomInButton.Enabled = hasDocument;
        _fitHeightButton.Enabled = hasDocument;
        _fitWidthButton.Enabled = hasDocument;
        _rotateClockwiseButton.Enabled = hasDocument;
    }

    private sealed class PdfCanvasHostPanel : Panel
    {
        public event MouseEventHandler? BrowserWheel;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (BrowserWheel == null)
                base.OnMouseWheel(e);
            else
                BrowserWheel(this, e);
        }
    }
}
