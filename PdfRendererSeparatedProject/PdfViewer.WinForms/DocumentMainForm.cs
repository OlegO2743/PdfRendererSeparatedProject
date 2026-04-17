using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PdfCore;
using PdfCore.Color;
using PdfCore.Parsing;

namespace PdfViewer.WinForms;

public sealed partial class DocumentMainForm : Form
{
    private const int DefaultThumbnailWidth = 220;
    private const int FitPadding = 72;

    private enum ViewFitMode
    {
        None,
        Auto,
        FitHeight,
        FitWidth
    }

    private readonly SplitContainer _viewerSplit = new()
    {
        Dock = DockStyle.Fill,
        FixedPanel = FixedPanel.Panel1,
        Panel1MinSize = 170,
        SplitterDistance = DefaultThumbnailWidth,
        SplitterWidth = 6
    };

    private readonly PdfThumbnailStripControl _thumbnailStrip = new()
    {
        Dock = DockStyle.Fill
    };

    private readonly PdfDocumentView _documentView = new()
    {
        Dock = DockStyle.Fill
    };

    private readonly ToolStrip _toolStrip = new()
    {
        Dock = DockStyle.Top,
        GripStyle = ToolStripGripStyle.Hidden
    };

    private readonly ToolStripButton _openButton = new("Открыть PDF");
    private readonly ToolStripButton _printButton = new("Печать");
    private readonly ToolStripButton _toggleThumbnailsButton = new("Страницы")
    {
        CheckOnClick = true,
        Checked = true
    };
    private readonly ToolStripButton _firstPageButton = new("|◀");
    private readonly ToolStripButton _prevButton = new("◀");
    private readonly ToolStripButton _nextButton = new("▶");
    private readonly ToolStripButton _lastPageButton = new("▶|");
    private readonly ToolStripButton _zoomOutButton = new("−");
    private readonly ToolStripButton _zoomInButton = new("+");
    private readonly ToolStripButton _fitHeightButton = new("↕");
    private readonly ToolStripButton _fitWidthButton = new("↔");
    private readonly ToolStripButton _rotateClockwiseButton = new("↻");
    private readonly ToolStripLabel _pageLabel = new("0 / 0");
    private readonly ToolStripLabel _zoomLabel = new("100%");
    private readonly Panel _loadingPanel = new()
    {
        Dock = DockStyle.Top,
        Height = 30,
        Padding = new Padding(10, 5, 10, 5),
        BackColor = Color.FromArgb(245, 247, 250),
        Visible = false
    };
    private readonly Label _loadStatusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly ProgressBar _loadProgressBar = new()
    {
        Dock = DockStyle.Right,
        Width = 220,
        Style = ProgressBarStyle.Blocks
    };
    private readonly ToolStripComboBox _modeCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 120
    };
    private readonly ToolStripComboBox _scrollModeCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 120
    };
    private readonly CheckBox _objectOverlayCheckBox = new()
    {
        AutoSize = true,
        Text = "Объекты"
    };

    private readonly ToolStripControlHost _objectOverlayHost;

    private SimplePdfDocument? _document;
    private string? _currentPath;
    private int _pageIndex;
    private float _zoom = 1f;
    private ViewFitMode _fitMode = ViewFitMode.None;
    private int _rotationDegrees;
    private bool _isHandlingResize;
    private bool _isLoadingDocument;
    private int _thumbnailPanelWidth = DefaultThumbnailWidth;

    public DocumentMainForm()
    {
        Text = "Simple PDF Renderer";
        Width = 1400;
        Height = 920;

        _objectOverlayHost = new ToolStripControlHost(_objectOverlayCheckBox)
        {
            Margin = new Padding(4, 0, 4, 0),
            Padding = Padding.Empty
        };

        _modeCombo.Items.AddRange(
        [
            "Stable",
            "Experimental ICC"
        ]);
        _modeCombo.SelectedIndex = 0;

        _scrollModeCombo.Items.AddRange(
        [
            "Плавно",
            "По странице"
        ]);
        _scrollModeCombo.SelectedIndex = 0;

        ConfigureTooltips();
        ConfigureLayout();
        ConfigureToolbar();
        HookEvents();
        UpdateNavigationButtons();
    }

    private void ConfigureLayout()
    {
        _loadingPanel.Controls.Add(_loadProgressBar);
        _loadingPanel.Controls.Add(_loadStatusLabel);
        _viewerSplit.Panel1.Controls.Add(_thumbnailStrip);
        _viewerSplit.Panel2.Controls.Add(_documentView);
        Controls.Add(_viewerSplit);
        Controls.Add(_toolStrip);
        Controls.Add(_loadingPanel);
    }

    private void ConfigureToolbar()
    {
        _toolStrip.Items.Add(_openButton);
        _toolStrip.Items.Add(_printButton);
        _toolStrip.Items.Add(_toggleThumbnailsButton);
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
        _toolStrip.Items.Add(new ToolStripLabel("Прокрутка:"));
        _toolStrip.Items.Add(_scrollModeCombo);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(new ToolStripLabel("Цвет:"));
        _toolStrip.Items.Add(_modeCombo);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_objectOverlayHost);
    }

    private void HookEvents()
    {
        _openButton.Click += (_, _) => OpenPdf();
        _printButton.Click += (_, _) => PrintCurrentDocument();
        _toggleThumbnailsButton.CheckedChanged += (_, _) => UpdateThumbnailsVisibility(_toggleThumbnailsButton.Checked);
        _firstPageButton.Click += (_, _) => GoToPage(0);
        _prevButton.Click += (_, _) => ChangePage(-1);
        _nextButton.Click += (_, _) => ChangePage(1);
        _lastPageButton.Click += (_, _) => GoToLastPage();
        _zoomOutButton.Click += (_, _) => ChangeZoom(1f / 1.25f);
        _zoomInButton.Click += (_, _) => ChangeZoom(1.25f);
        _fitHeightButton.Click += (_, _) => FitHeight();
        _fitWidthButton.Click += (_, _) => FitWidth();
        _rotateClockwiseButton.Click += (_, _) => RotateClockwise();
        _modeCombo.SelectedIndexChanged += (_, _) => HandleColorModeChanged();
        _scrollModeCombo.SelectedIndexChanged += (_, _) => ApplyScrollMode(GetSelectedScrollMode());
        _objectOverlayCheckBox.CheckedChanged += (_, _) => _documentView.SetObjectOverlayEnabled(_objectOverlayCheckBox.Checked);
        _viewerSplit.SplitterMoved += (_, _) =>
        {
            if (!_viewerSplit.Panel1Collapsed)
                _thumbnailPanelWidth = _viewerSplit.SplitterDistance;
        };
        _thumbnailStrip.PageSelected += (_, pageIndex) => GoToPage(pageIndex);
        _documentView.CurrentPageChanged += HandleCurrentPageChanged;
        _documentView.ZoomWheel += HandleDocumentZoomWheel;
        Resize += (_, _) => HandleViewportResize();
    }

    private void ConfigureTooltips()
    {
        _openButton.ToolTipText = "Открыть PDF";
        _printButton.ToolTipText = "Печать страниц";
        _toggleThumbnailsButton.ToolTipText = "Показать или скрыть ленту миниатюр";
        _firstPageButton.ToolTipText = "Первая страница";
        _prevButton.ToolTipText = "Предыдущая страница";
        _nextButton.ToolTipText = "Следующая страница";
        _lastPageButton.ToolTipText = "Последняя страница";
        _zoomOutButton.ToolTipText = "Уменьшить";
        _zoomInButton.ToolTipText = "Увеличить";
        _fitHeightButton.ToolTipText = "По высоте";
        _fitWidthButton.ToolTipText = "По ширине";
        _rotateClockwiseButton.ToolTipText = "Повернуть на 90°";
    }

    private async void OpenPdf()
    {
        if (_isLoadingDocument)
            return;

        using var dialog = new OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            Title = "Выберите PDF"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        await LoadDocumentAsync(dialog.FileName);
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

            UpdateLoadingProgress(PdfParseProgress.Indeterminate("Подготовка страниц"));
            await Task.Yield();

            _currentPath = path;
            _document = loadedDocument;
            _pageIndex = 0;
            _rotationDegrees = 0;
            _fitMode = ViewFitMode.Auto;

            _documentView.SetDocument(_document);
            ApplyScrollMode(GetSelectedScrollMode());
            _documentView.SetObjectOverlayEnabled(_objectOverlayCheckBox.Checked);
            _thumbnailStrip.SetDocument(_document, _rotationDegrees);

            ApplyCurrentFitMode();
            _documentView.ScrollToPage(0, alignTop: ShouldAlignTop());
            _thumbnailStrip.SetCurrentPage(0);

            Text = $"Simple PDF Renderer — {Path.GetFileName(path)}";
            UpdateLabels();
            UpdateNavigationButtons();
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

    private void ShowPdfError(string message, Exception ex)
    {
        MessageBox.Show(
            this,
            $"{message}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
            "Ошибка PDF",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void SetLoadingState(bool isLoading)
    {
        _isLoadingDocument = isLoading;
        UseWaitCursor = isLoading;
        _loadingPanel.Visible = isLoading;

        if (!isLoading)
        {
            _loadStatusLabel.Text = string.Empty;
            _loadProgressBar.Style = ProgressBarStyle.Blocks;
            _loadProgressBar.Value = 0;
        }

        UpdateNavigationButtons();
    }

    private void UpdateLoadingProgress(PdfParseProgress progress)
    {
        _loadingPanel.Visible = true;
        _loadStatusLabel.Text = progress.Message;

        if (!progress.IsDeterminate)
        {
            _loadProgressBar.Style = ProgressBarStyle.Marquee;
            return;
        }

        _loadProgressBar.Style = ProgressBarStyle.Blocks;
        _loadProgressBar.Maximum = Math.Max(1, progress.Total ?? 1);
        _loadProgressBar.Value = Math.Clamp(progress.Current ?? 0, 0, _loadProgressBar.Maximum);
    }

    private void HandleCurrentPageChanged(object? sender, EventArgs e)
    {
        _pageIndex = _documentView.CurrentPageIndex;
        _thumbnailStrip.SetCurrentPage(_pageIndex);
        UpdateLabels();
        UpdateNavigationButtons();
    }

    private void ChangePage(int delta)
    {
        if (_document == null)
            return;

        GoToPage(Math.Clamp(_pageIndex + delta, 0, _document.Pages.Count - 1));
    }

    private void GoToPage(int pageIndex)
    {
        if (_document == null || pageIndex < 0 || pageIndex >= _document.Pages.Count)
            return;

        _pageIndex = pageIndex;
        if (_fitMode == ViewFitMode.Auto)
            ApplyCurrentFitMode();

        _documentView.ScrollToPage(pageIndex, alignTop: ShouldAlignTop());
        _thumbnailStrip.SetCurrentPage(pageIndex);
        UpdateLabels();
        UpdateNavigationButtons();
    }

    private void GoToLastPage()
    {
        if (_document == null || _document.Pages.Count == 0)
            return;

        GoToPage(_document.Pages.Count - 1);
    }

    private void ChangeZoom(float factor)
    {
        if (_document == null)
            return;

        ZoomAt(_zoom * factor, _documentView.GetViewportCenter());
    }

    private void HandleDocumentZoomWheel(object? sender, MouseEventArgs e)
    {
        if (_document == null)
            return;

        float factor = e.Delta > 0 ? 1.25f : 1f / 1.25f;
        ZoomAt(_zoom * factor, e.Location);
    }

    private void ZoomAt(float zoom, Point viewportAnchor)
    {
        if (_document == null)
            return;

        _fitMode = ViewFitMode.None;
        _zoom = Math.Clamp(zoom, 0.1f, 8f);
        _documentView.UpdateView(_zoom, _rotationDegrees, viewportAnchor);
        UpdateLabels();
        UpdateNavigationButtons();
    }

    private void FitWidth()
    {
        if (_document == null)
            return;

        _fitMode = ViewFitMode.FitWidth;
        ApplyCurrentFitMode(_documentView.GetViewportCenter());
    }

    private void FitHeight()
    {
        if (_document == null)
            return;

        _fitMode = ViewFitMode.FitHeight;
        ApplyCurrentFitMode(_documentView.GetViewportCenter());
    }

    private void RotateClockwise()
    {
        if (_document == null)
            return;

        _rotationDegrees = (_rotationDegrees + 90) % 360;
        _thumbnailStrip.SetRotation(_rotationDegrees);

        if (_fitMode != ViewFitMode.None)
            ApplyCurrentFitMode();
        else
            _documentView.UpdateView(_zoom, _rotationDegrees, _documentView.GetViewportCenter());

        _documentView.ScrollToPage(_pageIndex, alignTop: ShouldAlignTop());
        UpdateLabels();
    }

    private void HandleViewportResize()
    {
        if (_isHandlingResize || _document == null)
            return;

        if (_fitMode == ViewFitMode.None)
            return;

        _isHandlingResize = true;
        try
        {
            ApplyCurrentFitMode(_documentView.GetViewportCenter());
        }
        finally
        {
            _isHandlingResize = false;
        }
    }

    private void ApplyCurrentFitMode(Point? anchor = null)
    {
        if (_document == null || _document.Pages.Count == 0)
            return;

        SimplePdfPage page = _document.Pages[Math.Clamp(_pageIndex, 0, _document.Pages.Count - 1)];
        _zoom = _fitMode switch
        {
            ViewFitMode.FitWidth => CalculateFitWidthZoom(page),
            ViewFitMode.FitHeight => CalculateFitHeightZoom(page),
            ViewFitMode.Auto => CalculateAutoFitZoom(page),
            _ => _zoom
        };

        _zoom = Math.Clamp(_zoom, 0.1f, 8f);
        _documentView.UpdateView(_zoom, _rotationDegrees, anchor);
        UpdateLabels();
    }

    private float CalculateAutoFitZoom(SimplePdfPage page)
    {
        return IsLandscape(page) ? CalculateFitWidthZoom(page) : CalculateFitHeightZoom(page);
    }

    private float CalculateFitWidthZoom(SimplePdfPage page)
    {
        int available = Math.Max(100, _documentView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - FitPadding);
        return available / Math.Max(1f, GetRotatedPageWidthPt(page));
    }

    private float CalculateFitHeightZoom(SimplePdfPage page)
    {
        int available = Math.Max(100, _documentView.ClientSize.Height - SystemInformation.HorizontalScrollBarHeight - FitPadding);
        return available / Math.Max(1f, GetRotatedPageHeightPt(page));
    }

    private static bool IsLandscape(SimplePdfPage page)
        => page.WidthPt > page.HeightPt;

    private float GetRotatedPageWidthPt(SimplePdfPage page)
        => _rotationDegrees is 90 or 270 ? page.HeightPt : page.WidthPt;

    private float GetRotatedPageHeightPt(SimplePdfPage page)
        => _rotationDegrees is 90 or 270 ? page.WidthPt : page.HeightPt;

    private bool ShouldAlignTop()
    {
        if (_document == null || _document.Pages.Count == 0)
            return true;

        SimplePdfPage page = _document.Pages[Math.Clamp(_pageIndex, 0, _document.Pages.Count - 1)];
        return _fitMode switch
        {
            ViewFitMode.FitWidth => false,
            ViewFitMode.FitHeight => true,
            ViewFitMode.Auto => !IsLandscape(page),
            _ => true
        };
    }

    private PdfScrollMode GetSelectedScrollMode()
        => _scrollModeCombo.SelectedIndex == 1 ? PdfScrollMode.PageByPage : PdfScrollMode.Smooth;

    private void ApplyScrollMode(PdfScrollMode mode)
    {
        _documentView.ScrollMode = mode;

        if (_document != null && _pageIndex >= 0 && _pageIndex < _document.Pages.Count)
        {
            _documentView.ScrollToPage(_pageIndex, alignTop: ShouldAlignTop());
            _thumbnailStrip.SetCurrentPage(_pageIndex);
            UpdateLabels();
            UpdateNavigationButtons();
        }
    }

    private void UpdateThumbnailsVisibility(bool visible)
    {
        if (visible)
        {
            _viewerSplit.Panel1Collapsed = false;
            _viewerSplit.SplitterDistance = Math.Clamp(_thumbnailPanelWidth, _viewerSplit.Panel1MinSize, Math.Max(_viewerSplit.Panel1MinSize, Width / 3));
            return;
        }

        if (!_viewerSplit.Panel1Collapsed)
            _thumbnailPanelWidth = Math.Max(_viewerSplit.Panel1MinSize, _viewerSplit.SplitterDistance);

        _viewerSplit.Panel1Collapsed = true;
    }

    private void PrintCurrentDocument()
    {
        if (_document == null)
            return;

        PdfPrintService.Print(this, _document, _rotationDegrees, _pageIndex, Path.GetFileName(_currentPath));
    }

    private void HandleColorModeChanged()
    {
        PdfColorManagementSettings.Mode = _modeCombo.SelectedIndex == 1
            ? PdfColorManagementMode.ExperimentalPhase2Icc
            : PdfColorManagementMode.StablePhase1Fallback;

        if (_document == null)
            return;

        Point anchor = _documentView.GetViewportCenter();
        _documentView.UpdateView(_zoom, _rotationDegrees, anchor);
        _thumbnailStrip.RefreshRenderedThumbnails();
    }

    private void UpdateLabels()
    {
        if (_document == null || _document.Pages.Count == 0)
        {
            _pageLabel.Text = "0 / 0";
            _zoomLabel.Text = "100%";
            return;
        }

        _pageLabel.Text = $"{Math.Clamp(_pageIndex + 1, 1, _document.Pages.Count)} / {_document.Pages.Count}";
        _zoomLabel.Text = $"{Math.Round(_zoom * 100f)}%";
    }

    private void UpdateNavigationButtons()
    {
        bool hasDocument = !_isLoadingDocument && _document != null && _document.Pages.Count > 0;
        bool hasPrevious = hasDocument && _pageIndex > 0;
        bool hasNext = hasDocument && _document != null && _pageIndex < _document.Pages.Count - 1;

        _openButton.Enabled = !_isLoadingDocument;
        _printButton.Enabled = hasDocument;
        _toggleThumbnailsButton.Enabled = true;
        _firstPageButton.Enabled = hasPrevious;
        _prevButton.Enabled = hasPrevious;
        _nextButton.Enabled = hasNext;
        _lastPageButton.Enabled = hasNext;
        _zoomOutButton.Enabled = hasDocument;
        _zoomInButton.Enabled = hasDocument;
        _fitHeightButton.Enabled = hasDocument;
        _fitWidthButton.Enabled = hasDocument;
        _rotateClockwiseButton.Enabled = hasDocument;
        _scrollModeCombo.Enabled = hasDocument;
        _modeCombo.Enabled = true;
        _objectOverlayCheckBox.Enabled = hasDocument;
    }
}
