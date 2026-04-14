using PdfCore.Color;
using PdfCore.Graphics;
using PdfCore.Resources;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.RegularExpressions;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;

namespace PdfCore;

using PdfCore.Graphics;
using global::PdfCore.Text;

public static class SimplePdfRenderer
{
    public static Bitmap Render(SimplePdfPage page, float zoom)
        => RenderWithObjects(page, zoom).Bitmap;

    public static PdfRenderResult RenderWithObjects(SimplePdfPage page, float zoom, bool mergeObjects = true)
    {
        var objectCollector = new PdfPageObjectCollector();
        var pageContext = new PdfPageContext
        {
            WidthPt = page.WidthPt,
            HeightPt = page.HeightPt,
            Zoom = zoom,
            ObjectCollector = objectCollector
        };

        var bmp = new Bitmap(pageContext.WidthPx, pageContext.HeightPx);
        using DrawingGraphics g = DrawingGraphics.FromImage(bmp);
        g.Clear(DrawingColor.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        _stateStack.Clear();
        var resourceScope = new PdfResourceScope(page.Resources);
        var gs = new PdfGraphicsState();
        var textEngine = new PdfTextEngine();

        RenderContentStreamInternal(g, page, pageContext, page.ContentStream, resourceScope, gs, textEngine);

        return new PdfRenderResult(bmp, mergeObjects ? objectCollector.Snapshot() : objectCollector.SnapshotRaw());
    }

    private static void RenderContentStreamInternal(
        DrawingGraphics g,
        SimplePdfPage page,
        PdfPageContext pageContext,
        string contentStream,
        PdfResourceScope resourceScope,
        PdfGraphicsState gs,
        PdfTextEngine textEngine)
    {
        var operands = new List<object>();
        var path = new GraphicsPath();
        PointF? currentPoint = null;
        bool pendingClip = false;
        bool pendingClipEvenOdd = false;

        foreach (object tokenObj in Tokenize(contentStream))
        {
            if (tokenObj is InlineImageToken inlineImage)
            {
                RenderInlineImage(g, pageContext, inlineImage, gs);
                continue;
            }

            if (tokenObj is string op && IsOperator(op))
            {
                ExecuteOperator(
                    op,
                    operands,
                    g,
                    page,
                    pageContext,
                    resourceScope,
                    gs,
                    textEngine,
                    path,
                    ref currentPoint,
                    ref pendingClip,
                    ref pendingClipEvenOdd);
            }
            else
            {
                operands.Add(tokenObj);
            }
        }
    }

    private static void ExecuteOperator(
        string op,
        List<object> operands,
        DrawingGraphics g,
        SimplePdfPage page,
        PdfPageContext pageContext,
        PdfResourceScope resourceScope,
        PdfGraphicsState gs,
        PdfTextEngine textEngine,
        GraphicsPath path,
        ref PointF? currentPoint,
        ref bool pendingClip,
        ref bool pendingClipEvenOdd)
    {
        switch (op)
        {
            case "q":
                PushState(gs, textEngine, g);
                break;

            case "Q":
                PopState(gs, textEngine, g);
                break;

            case "cm":
                {
                    float f = ParseFloat(PopOperand(operands));
                    float e = ParseFloat(PopOperand(operands));
                    float d = ParseFloat(PopOperand(operands));
                    float c = ParseFloat(PopOperand(operands));
                    float b = ParseFloat(PopOperand(operands));
                    float a = ParseFloat(PopOperand(operands));
                    using var m = new Matrix(a, b, c, d, e, f);
                    gs.Ctm.Multiply(m, MatrixOrder.Prepend);
                    break;
                }

            case "w":
                gs.LineWidth = ParseFloat(PopOperand(operands));
                break;

            case "J":
                gs.LineCap = (int)ParseFloat(PopOperand(operands));
                break;

            case "j":
                gs.LineJoin = (int)ParseFloat(PopOperand(operands));
                break;

            case "M":
                gs.MiterLimit = ParseFloat(PopOperand(operands));
                break;

            case "i":
            case "ri":
            case "gs":
                if (operands.Count > 0)
                    PopOperand(operands);
                break;

            case "d":
                {
                    float phase = operands.Count > 0 ? ParseFloat(PopOperand(operands)) : 0f;
                    List<object> array = operands.Count > 0 ? PopArray(operands) : new List<object>();
                    gs.DashArray = ToFloatArray(array);
                    gs.DashPhase = phase;
                    break;
                }

            case "CS":
                gs.StrokeColorSpace = ResolveColorSpace(resourceScope, (string)PopOperand(operands));
                break;

            case "cs":
                gs.FillColorSpace = ResolveColorSpace(resourceScope, (string)PopOperand(operands));
                break;

            case "SC":
            case "SCN":
                {
                    (DrawingColor color, string? patternName) = PopColorAndPattern(operands, gs.StrokeColorSpace);
                    gs.StrokeColor = color;
                    gs.StrokePatternName = patternName;
                    break;
                }

            case "sc":
            case "scn":
                {
                    (DrawingColor color, string? patternName) = PopColorAndPattern(operands, gs.FillColorSpace);
                    gs.FillColor = color;
                    gs.FillPatternName = patternName;
                    break;
                }

            case "G":
                gs.StrokeColorSpace = new PdfDeviceGrayColorSpace();
                gs.StrokeColor = PopColor(operands, gs.StrokeColorSpace);
                gs.StrokePatternName = null;
                break;

            case "g":
                gs.FillColorSpace = new PdfDeviceGrayColorSpace();
                gs.FillColor = PopColor(operands, gs.FillColorSpace);
                gs.FillPatternName = null;
                break;

            case "RG":
                {
                    float b = ParseFloat(PopOperand(operands));
                    float gVal = ParseFloat(PopOperand(operands));
                    float r = ParseFloat(PopOperand(operands));
                    gs.StrokeColorSpace = new PdfDeviceRgbColorSpace();
                    gs.StrokeColor = PdfRgbToColor(r, gVal, b);
                    gs.StrokePatternName = null;
                    break;
                }

            case "rg":
                {
                    float b = ParseFloat(PopOperand(operands));
                    float gVal = ParseFloat(PopOperand(operands));
                    float r = ParseFloat(PopOperand(operands));
                    gs.FillColorSpace = new PdfDeviceRgbColorSpace();
                    gs.FillColor = PdfRgbToColor(r, gVal, b);
                    gs.FillPatternName = null;
                    break;
                }

            case "K":
                {
                    float k = ParseFloat(PopOperand(operands));
                    float y = ParseFloat(PopOperand(operands));
                    float m = ParseFloat(PopOperand(operands));
                    float c = ParseFloat(PopOperand(operands));
                    gs.StrokeColorSpace = new PdfDeviceCmykColorSpace();
                    gs.StrokeColor = PdfCmykToColor(c, m, y, k);
                    gs.StrokePatternName = null;
                    break;
                }

            case "k":
                {
                    float k = ParseFloat(PopOperand(operands));
                    float y = ParseFloat(PopOperand(operands));
                    float m = ParseFloat(PopOperand(operands));
                    float c = ParseFloat(PopOperand(operands));
                    gs.FillColorSpace = new PdfDeviceCmykColorSpace();
                    gs.FillColor = PdfCmykToColor(c, m, y, k);
                    gs.FillPatternName = null;
                    break;
                }

            case "m":
                {
                    float y = ParseFloat(PopOperand(operands));
                    float x = ParseFloat(PopOperand(operands));
                    PointF p = TransformPoint(pageContext, gs.Ctm, x, y);
                    path.StartFigure();
                    currentPoint = p;
                    break;
                }

            case "l":
                {
                    float y = ParseFloat(PopOperand(operands));
                    float x = ParseFloat(PopOperand(operands));
                    PointF p = TransformPoint(pageContext, gs.Ctm, x, y);
                    if (currentPoint == null)
                    {
                        path.StartFigure();
                        currentPoint = p;
                    }
                    else
                    {
                        path.AddLine(currentPoint.Value, p);
                        currentPoint = p;
                    }
                    break;
                }

            case "c":
                {
                    float y3 = ParseFloat(PopOperand(operands));
                    float x3 = ParseFloat(PopOperand(operands));
                    float y2 = ParseFloat(PopOperand(operands));
                    float x2 = ParseFloat(PopOperand(operands));
                    float y1 = ParseFloat(PopOperand(operands));
                    float x1 = ParseFloat(PopOperand(operands));
                    if (currentPoint == null)
                        break;

                    PointF p1 = TransformPoint(pageContext, gs.Ctm, x1, y1);
                    PointF p2 = TransformPoint(pageContext, gs.Ctm, x2, y2);
                    PointF p3 = TransformPoint(pageContext, gs.Ctm, x3, y3);
                    path.AddBezier(currentPoint.Value, p1, p2, p3);
                    currentPoint = p3;
                    break;
                }

            case "v":
                {
                    float y3 = ParseFloat(PopOperand(operands));
                    float x3 = ParseFloat(PopOperand(operands));
                    float y2 = ParseFloat(PopOperand(operands));
                    float x2 = ParseFloat(PopOperand(operands));
                    if (currentPoint == null)
                        break;

                    PointF p1 = currentPoint.Value;
                    PointF p2 = TransformPoint(pageContext, gs.Ctm, x2, y2);
                    PointF p3 = TransformPoint(pageContext, gs.Ctm, x3, y3);
                    path.AddBezier(currentPoint.Value, p1, p2, p3);
                    currentPoint = p3;
                    break;
                }

            case "y":
                {
                    float y3 = ParseFloat(PopOperand(operands));
                    float x3 = ParseFloat(PopOperand(operands));
                    float y1 = ParseFloat(PopOperand(operands));
                    float x1 = ParseFloat(PopOperand(operands));
                    if (currentPoint == null)
                        break;

                    PointF p1 = TransformPoint(pageContext, gs.Ctm, x1, y1);
                    PointF p3 = TransformPoint(pageContext, gs.Ctm, x3, y3);
                    path.AddBezier(currentPoint.Value, p1, p3, p3);
                    currentPoint = p3;
                    break;
                }

            case "h":
                path.CloseFigure();
                break;

            case "re":
                {
                    float h = ParseFloat(PopOperand(operands));
                    float w = ParseFloat(PopOperand(operands));
                    float y = ParseFloat(PopOperand(operands));
                    float x = ParseFloat(PopOperand(operands));
                    AddRectanglePath(path, pageContext, gs.Ctm, x, y, w, h);
                    currentPoint = TransformPoint(pageContext, gs.Ctm, x, y);
                    break;
                }

            case "S":
                StrokePath(g, pageContext, gs, path);
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "s":
                CloseCurrentFigureIfNeeded(path);
                StrokePath(g, pageContext, gs, path);
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "f":
                FillPath(g, pageContext, resourceScope, gs, path, evenOdd: false);
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "f*":
                FillPath(g, pageContext, resourceScope, gs, path, evenOdd: true);
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "B":
                FillPath(g, pageContext, resourceScope, gs, path, evenOdd: false);
                StrokePath(g, pageContext, gs, path);
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "B*":
                FillPath(g, pageContext, resourceScope, gs, path, evenOdd: true);
                StrokePath(g, pageContext, gs, path);
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "b":
                CloseCurrentFigureIfNeeded(path);
                FillPath(g, pageContext, resourceScope, gs, path, evenOdd: false);
                StrokePath(g, pageContext, gs, path);
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "b*":
                CloseCurrentFigureIfNeeded(path);
                FillPath(g, pageContext, resourceScope, gs, path, evenOdd: true);
                StrokePath(g, pageContext, gs, path);
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "W":
                pendingClip = true;
                pendingClipEvenOdd = false;
                break;

            case "W*":
                pendingClip = true;
                pendingClipEvenOdd = true;
                break;

            case "n":
                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
                ResetPath(path, ref currentPoint);
                break;

            case "BT":
                textEngine.BeginText();
                break;

            case "ET":
                textEngine.EndText();
                break;

            case "Tf":
                {
                    float size = ParseFloat(PopOperand(operands));
                    string resourceName = (string)PopOperand(operands);
                    textEngine.SetFont(resourceName, size);
                    break;
                }

            case "Tm":
                {
                    float f = ParseFloat(PopOperand(operands));
                    float e = ParseFloat(PopOperand(operands));
                    float d = ParseFloat(PopOperand(operands));
                    float c = ParseFloat(PopOperand(operands));
                    float b = ParseFloat(PopOperand(operands));
                    float a = ParseFloat(PopOperand(operands));
                    textEngine.SetTextMatrix(a, b, c, d, e, f);
                    break;
                }

            case "Td":
                {
                    float ty = ParseFloat(PopOperand(operands));
                    float tx = ParseFloat(PopOperand(operands));
                    textEngine.MoveTextPosition(tx, ty);
                    break;
                }

            case "TD":
                {
                    float ty = ParseFloat(PopOperand(operands));
                    float tx = ParseFloat(PopOperand(operands));
                    textEngine.MoveTextPositionAndSetLeading(tx, ty);
                    break;
                }

            case "T*":
                textEngine.MoveToNextLine();
                break;

            case "Tc":
                textEngine.SetCharSpacing(ParseFloat(PopOperand(operands)));
                break;

            case "Tw":
                textEngine.SetWordSpacing(ParseFloat(PopOperand(operands)));
                break;

            case "Tz":
                textEngine.SetHorizontalScale(ParseFloat(PopOperand(operands)));
                break;

            case "TL":
                textEngine.SetLeading(ParseFloat(PopOperand(operands)));
                break;

            case "Ts":
                textEngine.SetRise(ParseFloat(PopOperand(operands)));
                break;

            case "Tj":
                {
                    ShowTextValue(g, pageContext, gs, resourceScope, textEngine, PopOperand(operands));
                    break;
                }

            case "TJ":
                {
                    List<object> items = PopArray(operands);
                    foreach (object item in items)
                    {
                        if (item is PdfStringToken st)
                        {
                            textEngine.ShowText(g, pageContext, gs, resourceScope, DecodeTextToken(st, resourceScope, textEngine), st.Bytes);
                        }
                        else
                        {
                            float tj = ParseFloat(item);
                            float tx = -(tj / 1000f) * GetCurrentFontSize(textEngine);
                            textEngine.TranslateTextMatrix(tx * GetCurrentHorizontalScale(textEngine), 0f);
                        }
                    }
                    break;
                }

            case "'":
                {
                    textEngine.MoveToNextLine();
                    ShowTextValue(g, pageContext, gs, resourceScope, textEngine, PopOperand(operands));
                    break;
                }

            case "\"":
                {
                    object textValue = PopOperand(operands);
                    float ac = ParseFloat(PopOperand(operands));
                    float aw = ParseFloat(PopOperand(operands));
                    textEngine.SetWordSpacing(aw);
                    textEngine.SetCharSpacing(ac);
                    textEngine.MoveToNextLine();
                    ShowTextValue(g, pageContext, gs, resourceScope, textEngine, textValue);
                    break;
                }

            case "Do":
                {
                    string resourceName = (string)PopOperand(operands);

                    if (resourceScope.TryGetForm(resourceName, out PdfFormXObject? form) && form != null)
                    {
                        RenderFormXObject(g, page, pageContext, form, resourceScope, gs, textEngine);
                        break;
                    }

                    if (resourceScope.TryGetImage(resourceName, out PdfImageXObject? image) && image != null)
                    {
                        RenderImageXObject(g, pageContext, image, gs);
                        break;
                    }

                    throw new NotSupportedException("XObject " + resourceName + " не найден или пока не поддержан.");
                }
        }
    }

    private static float GetCurrentFontSize(PdfTextEngine textEngine)
        => textEngine.CreateSnapshot().FontSize;

    private static float GetCurrentHorizontalScale(PdfTextEngine textEngine)
        => textEngine.CreateSnapshot().HorizontalScale / 100f;

    private static void RenderFormXObject(
        DrawingGraphics g,
        SimplePdfPage page,
        PdfPageContext pageContext,
        PdfFormXObject form,
        PdfResourceScope parentScope,
        PdfGraphicsState currentState,
        PdfTextEngine parentTextEngine)
    {
        GraphicsState savedGraphics = g.Save();
        try
        {
            PdfGraphicsState formState = currentState.Clone();
            using (Matrix formMatrix = form.CreateMatrix())
            {
                formState.Ctm.Multiply(formMatrix, MatrixOrder.Prepend);
            }

            using GraphicsPath bboxClip = CreateBBoxClipPath(pageContext, formState.Ctm, form.BBox);
            g.SetClip(bboxClip, CombineMode.Intersect);

            var formScope = new PdfResourceScope(form.Resources, parentScope);
            var childTextEngine = new PdfTextEngine();
            childTextEngine.RestoreSnapshot(parentTextEngine.CreateSnapshot());

            RenderContentStreamInternal(g, page, pageContext, form.ContentStream, formScope, formState, childTextEngine);
        }
        finally
        {
            g.Restore(savedGraphics);
        }
    }

    private static void RenderImageXObject(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfImageXObject image,
        PdfGraphicsState gs)
    {
        GraphicsState saved = g.Save();
        try
        {
            using Bitmap bmp = image.CreateBitmap(gs.FillColor);

            PointF lowerLeft = TransformPoint(pageContext, gs.Ctm, 0f, 0f);
            PointF upperLeft = TransformPoint(pageContext, gs.Ctm, 0f, 1f);
            PointF upperRight = TransformPoint(pageContext, gs.Ctm, 1f, 1f);
            RectangleF imageBounds = GetImageBounds(lowerLeft, upperLeft, upperRight);

            bool axisAligned =
                Math.Abs(upperLeft.X - lowerLeft.X) < 0.01f &&
                Math.Abs(upperLeft.Y - upperRight.Y) < 0.01f;

            g.InterpolationMode = ShouldUseNearestNeighbor(image)
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (axisAligned)
            {
                float left = upperLeft.X;
                float top = upperLeft.Y;
                float width = upperRight.X - upperLeft.X;
                float height = lowerLeft.Y - upperLeft.Y;

                g.DrawImage(
                    bmp,
                    new RectangleF(left, top, width, height),
                    new RectangleF(0, 0, bmp.Width, bmp.Height),
                    GraphicsUnit.Pixel);
            }
            else
            {
                g.DrawImage(
                    bmp,
                    new[] { upperLeft, upperRight, lowerLeft },
                    new Rectangle(0, 0, bmp.Width, bmp.Height),
                    GraphicsUnit.Pixel);
            }

            pageContext.ObjectCollector?.AddImage(imageBounds, image.ResourceName);
        }
        catch (NotSupportedException)
        {
            // Some PDFs use specialized image encodings, e.g. CCITT masks.
            // Leave the unsupported XObject blank instead of failing the page.
        }
        catch (InvalidOperationException)
        {
            // Malformed or unsupported image data should not prevent rendering the rest of the page.
        }
        finally
        {
            g.Restore(saved);
        }
    }

    private static bool ShouldUseNearestNeighbor(PdfImageXObject image)
        => image.IsImageMask ||
           image.BitsPerComponent <= 1;

    private static void RenderInlineImage(
        DrawingGraphics g,
        PdfPageContext pageContext,
        InlineImageToken inlineImage,
        PdfGraphicsState gs)
    {
        var image = new PdfImageXObject
        {
            ResourceName = "inline",
            Width = inlineImage.Width,
            Height = inlineImage.Height,
            BitsPerComponent = inlineImage.BitsPerComponent,
            ColorSpace = inlineImage.ColorSpace,
            Filter = inlineImage.Filter,
            ImageBytes = inlineImage.ImageBytes,
            IsImageMask = inlineImage.IsImageMask,
            DecodeInverted = inlineImage.DecodeInverted
        };

        RenderImageXObject(g, pageContext, image, gs);
    }

    private static RectangleF GetImageBounds(PointF lowerLeft, PointF upperLeft, PointF upperRight)
    {
        PointF lowerRight = new(
            lowerLeft.X + (upperRight.X - upperLeft.X),
            lowerLeft.Y + (upperRight.Y - upperLeft.Y));

        float left = MathF.Min(MathF.Min(lowerLeft.X, upperLeft.X), MathF.Min(upperRight.X, lowerRight.X));
        float top = MathF.Min(MathF.Min(lowerLeft.Y, upperLeft.Y), MathF.Min(upperRight.Y, lowerRight.Y));
        float right = MathF.Max(MathF.Max(lowerLeft.X, upperLeft.X), MathF.Max(upperRight.X, lowerRight.X));
        float bottom = MathF.Max(MathF.Max(lowerLeft.Y, upperLeft.Y), MathF.Max(upperRight.Y, lowerRight.Y));

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static GraphicsPath CreateBBoxClipPath(PdfPageContext pageContext, Matrix ctm, float[] bbox)
    {
        float x0 = bbox.Length > 0 ? bbox[0] : 0f;
        float y0 = bbox.Length > 1 ? bbox[1] : 0f;
        float x1 = bbox.Length > 2 ? bbox[2] : 0f;
        float y1 = bbox.Length > 3 ? bbox[3] : 0f;

        var gp = new GraphicsPath();
        AddRectanglePath(gp, pageContext, ctm, x0, y0, x1 - x0, y1 - y0);
        return gp;
    }

    private static readonly Stack<(PdfGraphicsState Gs, PdfTextEngine.StateSnapshot Text, GraphicsState Graphics)> _stateStack = new();

    private static void PushState(PdfGraphicsState gs, PdfTextEngine textEngine, DrawingGraphics g)
        => _stateStack.Push((gs.Clone(), textEngine.CreateSnapshot(), g.Save()));

    private static void PopState(PdfGraphicsState gs, PdfTextEngine textEngine, DrawingGraphics g)
    {
        if (_stateStack.Count == 0)
            return;

        var popped = _stateStack.Pop();
        gs.Ctm.Dispose();
        gs.Ctm = popped.Gs.Ctm.Clone();
        gs.FillColorSpace = popped.Gs.FillColorSpace;
        gs.StrokeColorSpace = popped.Gs.StrokeColorSpace;
        gs.FillColor = popped.Gs.FillColor;
        gs.StrokeColor = popped.Gs.StrokeColor;
        gs.FillPatternName = popped.Gs.FillPatternName;
        gs.StrokePatternName = popped.Gs.StrokePatternName;
        gs.LineWidth = popped.Gs.LineWidth;
        gs.LineCap = popped.Gs.LineCap;
        gs.LineJoin = popped.Gs.LineJoin;
        gs.MiterLimit = popped.Gs.MiterLimit;
        gs.DashArray = (float[])popped.Gs.DashArray.Clone();
        gs.DashPhase = popped.Gs.DashPhase;
        textEngine.RestoreSnapshot(popped.Text);
        g.Restore(popped.Graphics);
    }

    private static PointF TransformPoint(PdfPageContext pageContext, Matrix matrix, float x, float y)
    {
        PointF p = ApplyMatrix(matrix, x, y);
        return UserToScreen(pageContext, p);
    }

    private static PointF ApplyMatrix(Matrix matrix, float x, float y)
    {
        PointF[] pts = { new(x, y) };
        using Matrix clone = matrix.Clone();
        clone.TransformPoints(pts);
        return pts[0];
    }

    private static PointF UserToScreen(PdfPageContext pageContext, PointF userPoint)
    {
        return new PointF(
            userPoint.X * pageContext.Zoom,
            (pageContext.HeightPt - userPoint.Y) * pageContext.Zoom);
    }

    private static void AddRectanglePath(GraphicsPath path, PdfPageContext pageContext, Matrix ctm, float x, float y, float w, float h)
    {
        PointF p1 = TransformPoint(pageContext, ctm, x, y);
        PointF p2 = TransformPoint(pageContext, ctm, x + w, y);
        PointF p3 = TransformPoint(pageContext, ctm, x + w, y + h);
        PointF p4 = TransformPoint(pageContext, ctm, x, y + h);

        path.StartFigure();
        path.AddLine(p1, p2);
        path.AddLine(p2, p3);
        path.AddLine(p3, p4);
        path.AddLine(p4, p1);
        path.CloseFigure();
    }

    private static void StrokePath(DrawingGraphics g, PdfPageContext pageContext, PdfGraphicsState gs, GraphicsPath path)
    {
        if (path.PointCount == 0)
            return;

        float effectiveWidth = GetEffectiveLineWidth(pageContext, gs);
        if (ShouldUseEnhancedThinLine(gs, path, effectiveWidth))
            effectiveWidth = 1f;

        using var pen = new Pen(gs.StrokeColor, effectiveWidth);
        pen.StartCap = MapLineCap(gs.LineCap);
        pen.EndCap = MapLineCap(gs.LineCap);
        pen.LineJoin = MapLineJoin(gs.LineJoin);
        pen.MiterLimit = Math.Max(1f, gs.MiterLimit);
        ApplyDashPattern(pen, pageContext, gs, effectiveWidth);
        g.DrawPath(pen, path);
        TrackVectorBounds(pageContext, GetPathBounds(path, pen));
    }

    private static LineCap MapLineCap(int lineCap)
    {
        return lineCap switch
        {
            1 => LineCap.Round,
            2 => LineCap.Square,
            _ => LineCap.Flat
        };
    }

    private static LineJoin MapLineJoin(int lineJoin)
    {
        return lineJoin switch
        {
            1 => LineJoin.Round,
            2 => LineJoin.Bevel,
            _ => LineJoin.Miter
        };
    }

    private static void ApplyDashPattern(Pen pen, PdfPageContext pageContext, PdfGraphicsState gs, float effectiveWidth)
    {
        if (gs.DashArray.Length == 0 || effectiveWidth <= 0.0001f)
            return;

        float strokeScale = GetStrokeScale(pageContext, gs);
        float[] dashPattern = NormalizeDashArray(gs.DashArray)
            .Select(value => Math.Abs(value) * strokeScale / effectiveWidth)
            .Where(value => value > 0.0001f)
            .Select(value => Math.Max(0.1f, value))
            .ToArray();

        if (dashPattern.Length == 0)
            return;

        pen.DashPattern = dashPattern;
        if (Math.Abs(gs.DashPhase) > 0.0001f)
            pen.DashOffset = Math.Abs(gs.DashPhase) * strokeScale / effectiveWidth;
    }

    private static bool ShouldUseEnhancedThinLine(PdfGraphicsState gs, GraphicsPath path, float effectiveWidth)
    {
        if (effectiveWidth <= 0.0001f || effectiveWidth > 3f)
            return false;

        if (gs.DashArray.Length != 0)
            return false;

        return IsAxisAlignedPath(path);
    }

    private static bool IsAxisAlignedPath(GraphicsPath path)
    {
        PointF[] points = path.PathPoints;
        byte[] types = path.PathTypes;
        if (points.Length < 2 || types.Length != points.Length)
            return false;

        const float tolerance = 0.05f;

        for (int i = 1; i < points.Length; i++)
        {
            PathPointType currentType = (PathPointType)(types[i] & (byte)PathPointType.PathTypeMask);
            if (currentType == PathPointType.Start)
                continue;

            if (currentType == PathPointType.Bezier3)
                return false;

            PathPointType previousType = (PathPointType)(types[i - 1] & (byte)PathPointType.PathTypeMask);
            if (previousType == PathPointType.Start && i == 1)
                continue;

            float dx = Math.Abs(points[i].X - points[i - 1].X);
            float dy = Math.Abs(points[i].Y - points[i - 1].Y);
            if (dx <= tolerance || dy <= tolerance)
                continue;

            return false;
        }

        return true;
    }

    private static float[] NormalizeDashArray(float[] source)
    {
        if (source.Length == 0)
            return source;

        var expanded = new List<float>(source.Length % 2 == 0 ? source.Length : source.Length * 2);
        expanded.AddRange(source.Select(Math.Abs));
        if (expanded.Count % 2 == 1)
            expanded.AddRange(source.Select(Math.Abs));

        var normalized = new List<float>(expanded.Count);
        float pendingGap = 0f;
        for (int i = 0; i < expanded.Count; i += 2)
        {
            float on = expanded[i];
            float off = i + 1 < expanded.Count ? expanded[i + 1] : 0f;

            if (on <= 0.0001f)
            {
                pendingGap += off;
                continue;
            }

            if (off <= 0.0001f)
            {
                if (i + 2 < expanded.Count)
                {
                    expanded[i + 2] += on;
                    continue;
                }

                off = 0.0001f;
            }

            if (pendingGap > 0f && normalized.Count >= 2)
            {
                normalized[^1] += pendingGap;
                pendingGap = 0f;
            }

            normalized.Add(on);
            normalized.Add(off);
        }

        if (pendingGap > 0f && normalized.Count >= 2)
            normalized[^1] += pendingGap;

        return normalized.Count == 0 ? Array.Empty<float>() : normalized.ToArray();
    }

    private static float GetEffectiveLineWidth(PdfPageContext pageContext, PdfGraphicsState gs)
    {
        if (gs.LineWidth <= 0.0001f)
            return 1f;

        return Math.Max(0.2f, gs.LineWidth * GetStrokeScale(pageContext, gs));
    }

    private static float GetStrokeScale(PdfPageContext pageContext, PdfGraphicsState gs)
    {
        PointF origin = TransformPoint(pageContext, gs.Ctm, 0f, 0f);
        PointF unitX = TransformPoint(pageContext, gs.Ctm, 1f, 0f);
        PointF unitY = TransformPoint(pageContext, gs.Ctm, 0f, 1f);

        float sx = Distance(origin, unitX);
        float sy = Distance(origin, unitY);
        float scale = (sx + sy) * 0.5f;
        if (scale <= 0.0001f || float.IsNaN(scale) || float.IsInfinity(scale))
            scale = pageContext.Zoom;

        return scale;
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static void FillPath(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfResourceScope resourceScope,
        PdfGraphicsState gs,
        GraphicsPath path,
        bool evenOdd)
    {
        path.FillMode = evenOdd ? FillMode.Alternate : FillMode.Winding;

        if (!string.IsNullOrEmpty(gs.FillPatternName) &&
            resourceScope.TryGetPattern(gs.FillPatternName, out PdfTilingPattern? pattern) &&
            pattern != null)
        {
            (TextureBrush Brush, Bitmap Bitmap)? patternBrush = CreateTilingPatternBrush(pageContext, resourceScope, pattern, gs.FillColor);
            if (patternBrush != null)
            {
                using Bitmap bitmap = patternBrush.Value.Bitmap;
                using TextureBrush textureBrush = patternBrush.Value.Brush;
                g.FillPath(textureBrush, path);
                TrackVectorBounds(pageContext, path.GetBounds());
                return;
            }
        }

        using var brush = new SolidBrush(gs.FillColor);
        g.FillPath(brush, path);
        TrackVectorBounds(pageContext, path.GetBounds());
    }

    private static RectangleF GetPathBounds(GraphicsPath path, Pen? pen = null)
    {
        if (path.PointCount == 0)
            return RectangleF.Empty;

        return pen == null
            ? path.GetBounds()
            : path.GetBounds(matrix: null, pen);
    }

    private static void TrackVectorBounds(PdfPageContext pageContext, RectangleF bounds)
    {
        if (bounds.Width <= 0f && bounds.Height <= 0f)
            return;

        pageContext.ObjectCollector?.AddVectorPath(bounds);
    }

    private static (TextureBrush Brush, Bitmap Bitmap)? CreateTilingPatternBrush(
        PdfPageContext pageContext,
        PdfResourceScope parentScope,
        PdfTilingPattern pattern,
        DrawingColor color)
    {
        float[] matrix = pattern.MatrixValues.Length >= 6
            ? pattern.MatrixValues
            : new[] { 1f, 0f, 0f, 1f, 0f, 0f };

        float matrixScaleX = Distance(new PointF(0f, 0f), new PointF(matrix[0], matrix[1]));
        float matrixScaleY = Distance(new PointF(0f, 0f), new PointF(matrix[2], matrix[3]));
        if (matrixScaleX <= 0.0001f)
            matrixScaleX = 1f;
        if (matrixScaleY <= 0.0001f)
            matrixScaleY = matrixScaleX;

        float tileWidthPt = Math.Abs(pattern.XStep);
        float tileHeightPt = Math.Abs(pattern.YStep);

        if (tileWidthPt <= 0.0001f && pattern.BBox.Length >= 3)
            tileWidthPt = Math.Abs(pattern.BBox[2] - pattern.BBox[0]);
        if (tileHeightPt <= 0.0001f && pattern.BBox.Length >= 4)
            tileHeightPt = Math.Abs(pattern.BBox[3] - pattern.BBox[1]);

        if (tileWidthPt <= 0.0001f || tileHeightPt <= 0.0001f)
            return null;

        float tileZoomX = pageContext.Zoom * matrixScaleX;
        float tileZoomY = pageContext.Zoom * matrixScaleY;
        float tileZoom = (tileZoomX + tileZoomY) * 0.5f;

        int widthPx = Math.Max(1, (int)MathF.Ceiling(tileWidthPt * tileZoomX));
        int heightPx = Math.Max(1, (int)MathF.Ceiling(tileHeightPt * tileZoomY));
        if (widthPx > 2048 || heightPx > 2048)
            return null;

        var bitmap = new Bitmap(widthPx, heightPx);
        using (DrawingGraphics tileGraphics = DrawingGraphics.FromImage(bitmap))
        {
            tileGraphics.Clear(DrawingColor.Transparent);
            tileGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            tileGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            tileGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            tileGraphics.PixelOffsetMode = PixelOffsetMode.Half;

            var tileContext = new PdfPageContext
            {
                WidthPt = tileWidthPt,
                HeightPt = tileHeightPt,
                Zoom = tileZoom
            };

            var tilePage = new SimplePdfPage
            {
                WidthPt = tileWidthPt,
                HeightPt = tileHeightPt,
                ContentStream = pattern.ContentStream,
                Resources = pattern.Resources
            };

            var tileState = new PdfGraphicsState
            {
                StrokeColor = color,
                FillColor = color
            };

            var tileScope = new PdfResourceScope(pattern.Resources, parentScope);
            RenderContentStreamInternal(
                tileGraphics,
                tilePage,
                tileContext,
                pattern.ContentStream,
                tileScope,
                tileState,
                new PdfTextEngine());
        }

        var brush = new TextureBrush(bitmap, WrapMode.Tile);
        return (brush, bitmap);
    }

    private static void CloseCurrentFigureIfNeeded(GraphicsPath path)
    {
        if (path.PointCount > 0)
            path.CloseFigure();
    }

    private static void ApplyPendingClipIfNeeded(DrawingGraphics g, GraphicsPath path, ref bool pendingClip, ref bool pendingClipEvenOdd)
    {
        if (!pendingClip)
            return;

        path.FillMode = pendingClipEvenOdd ? FillMode.Alternate : FillMode.Winding;
        g.SetClip(path, CombineMode.Intersect);
        pendingClip = false;
        pendingClipEvenOdd = false;
    }

    private static void ResetPath(GraphicsPath path, ref PointF? currentPoint)
    {
        path.Reset();
        currentPoint = null;
    }

    private static DrawingColor PdfRgbToColor(float r, float g, float b)
    {
        return DrawingColor.FromArgb(
            ClampToByte(r * 255f),
            ClampToByte(g * 255f),
            ClampToByte(b * 255f));
    }

    private static DrawingColor PopColor(List<object> operands, PdfColorSpace colorSpace)
    {
        if (operands.Count > 0 && operands[^1] is string name && name.StartsWith("/", StringComparison.Ordinal))
            operands.RemoveAt(operands.Count - 1);

        PdfColorSpace effectiveColorSpace = colorSpace.GetFallback();
        int componentCount = Math.Max(1, effectiveColorSpace.Components);
        if (operands.Count < componentCount)
        {
            operands.Clear();
            return DrawingColor.Black;
        }

        var components = new float[componentCount];

        for (int i = componentCount - 1; i >= 0; i--)
        {
            object operand = PopOperand(operands);
            if (!TryParseFloat(operand, out components[i]))
            {
                operands.Clear();
                return DrawingColor.Black;
            }
        }

        return ColorFromComponents(effectiveColorSpace, components);
    }

    private static (DrawingColor Color, string? PatternName) PopColorAndPattern(List<object> operands, PdfColorSpace colorSpace)
    {
        string? patternName = null;
        if (operands.Count > 0 && operands[^1] is string name && name.StartsWith("/", StringComparison.Ordinal))
        {
            patternName = name;
            operands.RemoveAt(operands.Count - 1);
        }

        if (colorSpace is not PdfPatternColorSpace patternColorSpace)
            return (PopColor(operands, colorSpace), patternName);

        PdfColorSpace? baseColorSpace = patternColorSpace.BaseColorSpace;
        if (baseColorSpace == null || operands.Count == 0)
        {
            operands.Clear();
            return (DrawingColor.Black, patternName);
        }

        int componentCount = Math.Max(1, baseColorSpace.Components);
        if (operands.Count < componentCount)
        {
            operands.Clear();
            return (DrawingColor.Black, patternName);
        }

        var components = new float[componentCount];
        for (int i = componentCount - 1; i >= 0; i--)
        {
            object operand = PopOperand(operands);
            if (!TryParseFloat(operand, out components[i]))
            {
                operands.Clear();
                return (DrawingColor.Black, patternName);
            }
        }

        return (ColorFromComponents(baseColorSpace, components), patternName);
    }

    private static DrawingColor ColorFromComponents(PdfColorSpace colorSpace, float[] components)
    {
        PdfColorSpace effectiveColorSpace = colorSpace.GetFallback();

        if (effectiveColorSpace is PdfDeviceGrayColorSpace)
        {
            float gray = components.Length > 0 ? components[0] : 0f;
            return PdfRgbToColor(gray, gray, gray);
        }

        if (effectiveColorSpace is PdfDeviceCmykColorSpace)
        {
            float c = components.Length > 0 ? components[0] : 0f;
            float m = components.Length > 1 ? components[1] : 0f;
            float y = components.Length > 2 ? components[2] : 0f;
            float k = components.Length > 3 ? components[3] : 0f;
            return PdfCmykToColor(c, m, y, k);
        }

        float r = components.Length > 0 ? components[0] : 0f;
        float g = components.Length > 1 ? components[1] : r;
        float b = components.Length > 2 ? components[2] : g;
        return PdfRgbToColor(r, g, b);
    }

    private static PdfColorSpace ResolveColorSpace(PdfResourceScope resourceScope, string colorSpaceName)
    {
        if (colorSpaceName == "/Pattern")
            return new PdfPatternColorSpace();

        if (PdfColorSpaceFactory.IsDeviceColorSpaceName(colorSpaceName))
            return PdfColorSpaceFactory.CreateDeviceSpaceByName(colorSpaceName);

        if (resourceScope.TryGetColorSpace(colorSpaceName, out PdfColorSpace? colorSpace) && colorSpace != null)
            return colorSpace;

        return new PdfDeviceRgbColorSpace();
    }

    private static DrawingColor PdfCmykToColor(float c, float m, float y, float k)
    {
        int r = ClampToByte(255f * (1f - Math.Min(1f, c + k)));
        int g = ClampToByte(255f * (1f - Math.Min(1f, m + k)));
        int b = ClampToByte(255f * (1f - Math.Min(1f, y + k)));
        return DrawingColor.FromArgb(r, g, b);
    }

    private static int ClampToByte(float value)
    {
        if (value < 0f) return 0;
        if (value > 255f) return 255;
        return (int)Math.Round(value);
    }

    private static object PopOperand(List<object> operands)
    {
        if (operands.Count == 0)
            throw new InvalidOperationException("Недостаточно операндов.");
        object value = operands[^1];
        operands.RemoveAt(operands.Count - 1);
        return value;
    }

    private static float ParseFloat(object value)
    {
        if (value is string s)
            return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        throw new InvalidOperationException("Ожидалось число.");
    }

    private static void ShowTextValue(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfResourceScope resourceScope,
        PdfTextEngine textEngine,
        object value)
    {
        switch (value)
        {
            case PdfStringToken st:
                textEngine.ShowText(g, pageContext, gs, resourceScope, DecodeTextToken(st, resourceScope, textEngine), st.Bytes);
                break;
            case string s:
                textEngine.ShowText(g, pageContext, gs, resourceScope, s);
                break;
            default:
                throw new InvalidOperationException("Expected PDF text string.");
        }
    }

    private static string GetTextValue(object value, PdfResourceScope resourceScope, PdfTextEngine textEngine)
    {
        return value switch
        {
            PdfStringToken st => DecodeTextToken(st, resourceScope, textEngine),
            string s => s,
            _ => throw new InvalidOperationException("Ожидалась строка.")
        };
    }

    private static string DecodeTextToken(PdfStringToken token, PdfResourceScope resourceScope, PdfTextEngine textEngine)
    {
        if (token.Bytes == null)
            return token.Value;

        string fontResourceName = textEngine.CreateSnapshot().FontResourceName;
        if (resourceScope.TryGetFont(fontResourceName, out PdfFontResource? fontResource) && fontResource != null)
            return fontResource.DecodeTextBytes(token.Bytes);

        return token.Value;
    }

    private static List<object> PopArray(List<object> operands)
    {
        object value = PopOperand(operands);
        if (value is List<object> list)
            return list;
        throw new InvalidOperationException("Ожидался PDF array.");
    }

    private static bool TryParseFloat(object value, out float result)
    {
        if (value is string s)
            return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);

        result = 0f;
        return false;
    }

    private static float[] ToFloatArray(List<object> values)
    {
        var result = new List<float>(values.Count);
        foreach (object value in values)
        {
            if (TryParseFloat(value, out float parsed))
                result.Add(parsed);
        }

        return result.ToArray();
    }

    private static bool IsOperator(string token)
    {
        return token == "q" || token == "Q" || token == "cm" ||
               token == "BT" || token == "ET" || token == "Tf" || token == "Tm" || token == "Td" || token == "TD" || token == "T*" ||
               token == "Tc" || token == "Tw" || token == "Tz" || token == "TL" || token == "Ts" ||
               token == "Tj" || token == "TJ" || token == "'" || token == "\"" ||
               token == "m" || token == "l" || token == "c" || token == "v" || token == "y" || token == "h" || token == "re" ||
               token == "S" || token == "s" ||
               token == "f" || token == "f*" ||
               token == "B" || token == "B*" ||
               token == "b" || token == "b*" ||
               token == "n" ||
               token == "w" || token == "J" || token == "j" || token == "M" || token == "d" || token == "i" || token == "ri" || token == "gs" ||
               token == "G" || token == "g" ||
               token == "CS" || token == "cs" || token == "SC" || token == "sc" || token == "SCN" || token == "scn" ||
               token == "RG" || token == "rg" || token == "K" || token == "k" ||
               token == "W" || token == "W*" ||
               token == "Do";
    }

    private static IEnumerable<object> Tokenize(string content)
    {
        int i = 0;
        while (i < content.Length)
        {
            char ch = content[i];

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (ch == '%')
            {
                while (i < content.Length && content[i] != '\n' && content[i] != '\r')
                    i++;
                continue;
            }

            if (ch == '[')
            {
                i++;
                yield return ReadArray(content, ref i);
                continue;
            }

            if (ch == '(')
            {
                yield return ReadLiteralStringToken(content, ref i);
                continue;
            }

            if (ch == '<')
            {
                if (i + 1 < content.Length && content[i + 1] == '<')
                {
                    i += 2;
                    yield return "<<";
                }
                else
                {
                    yield return PdfStringToken.FromHex(ReadHexStringBytes(content, ref i));
                }

                continue;
            }

            if (ch == '>')
            {
                if (i + 1 < content.Length && content[i + 1] == '>')
                {
                    i += 2;
                    yield return ">>";
                }
                else
                {
                    i++;
                    yield return ">";
                }

                continue;
            }

            if (ch == '/')
            {
                yield return ReadName(content, ref i);
                continue;
            }

            if (ch == '\'' || ch == '"' || ch == '[' || ch == ']')
            {
                yield return content[i++].ToString();
                continue;
            }

            string token = ReadBareToken(content, ref i);
            if (token == "BI")
                yield return ReadInlineImageToken(content, ref i);
            else
                yield return token;
        }
    }

    private static List<object> ReadArray(string content, ref int i)
    {
        var list = new List<object>();
        while (i < content.Length)
        {
            char ch = content[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }
            if (ch == '%')
            {
                while (i < content.Length && content[i] != '\n' && content[i] != '\r')
                    i++;
                continue;
            }
            if (ch == ']')
            {
                i++;
                break;
            }
            if (ch == '[')
            {
                i++;
                list.Add(ReadArray(content, ref i));
                continue;
            }
            if (ch == '(')
            {
                list.Add(ReadLiteralStringToken(content, ref i));
                continue;
            }
            if (ch == '<')
            {
                if (i + 1 < content.Length && content[i + 1] == '<')
                {
                    i += 2;
                    list.Add("<<");
                }
                else
                {
                    list.Add(PdfStringToken.FromHex(ReadHexStringBytes(content, ref i)));
                }

                continue;
            }
            if (ch == '>')
            {
                if (i + 1 < content.Length && content[i + 1] == '>')
                {
                    i += 2;
                    list.Add(">>");
                }
                else
                {
                    i++;
                    list.Add(">");
                }

                continue;
            }
            if (ch == '/')
            {
                list.Add(ReadName(content, ref i));
                continue;
            }
            list.Add(ReadBareToken(content, ref i));
        }
        return list;
    }

    private static string ReadName(string content, ref int i)
    {
        int start = i++;
        while (i < content.Length)
        {
            char ch = content[i];
            if (char.IsWhiteSpace(ch) || ch == '/' || ch == '[' || ch == ']' || ch == '(' || ch == ')' || ch == '<' || ch == '>')
                break;
            i++;
        }
        return content[start..i];
    }

    private static string ReadBareToken(string content, ref int i)
    {
        int start = i;
        while (i < content.Length)
        {
            char ch = content[i];
            if (char.IsWhiteSpace(ch) || ch == '[' || ch == ']' || ch == '(' || ch == ')' || ch == '<' || ch == '>' || ch == '/')
                break;
            i++;
        }
        return content[start..i];
    }

    private static PdfStringToken ReadLiteralStringToken(string content, ref int i)
    {
        if (content[i] != '(')
            throw new InvalidOperationException("Ожидалась literal string.");
        i++;

        var bytes = new List<byte>();
        int depth = 1;
        while (i < content.Length)
        {
            char ch = content[i++];
            if (ch == '\\')
            {
                if (i >= content.Length)
                    break;
                char esc = content[i++];
                if (esc >= '0' && esc <= '7')
                {
                    int value = esc - '0';
                    int digits = 1;
                    while (digits < 3 && i < content.Length && content[i] >= '0' && content[i] <= '7')
                    {
                        value = value * 8 + (content[i] - '0');
                        i++;
                        digits++;
                    }

                    bytes.Add((byte)(value & 0xFF));
                    continue;
                }

                if (esc == '\r' || esc == '\n')
                {
                    if (esc == '\r' && i < content.Length && content[i] == '\n')
                        i++;
                    continue;
                }

                bytes.Add(esc switch
                {
                    'n' => (byte)'\n',
                    'r' => (byte)'\r',
                    't' => (byte)'\t',
                    'b' => (byte)'\b',
                    'f' => (byte)'\f',
                    '(' => (byte)'(',
                    ')' => (byte)')',
                    '\\' => (byte)'\\',
                    _ => (byte)esc
                });
                continue;
            }
            if (ch == '(')
            {
                depth++;
                bytes.Add((byte)ch);
                continue;
            }
            if (ch == ')')
            {
                depth--;
                if (depth == 0)
                    break;
                bytes.Add((byte)ch);
                continue;
            }
            bytes.Add((byte)ch);
        }

        return PdfStringToken.FromBytes(bytes.ToArray());
    }

    private static byte[] ReadHexStringBytes(string content, ref int i)
    {
        if (content[i] != '<')
            throw new InvalidOperationException("Ожидалась hex string.");
        i++;

        var hex = new System.Text.StringBuilder();
        while (i < content.Length)
        {
            char ch = content[i++];
            if (ch == '>')
                break;

            if (char.IsWhiteSpace(ch))
                continue;

            hex.Append(ch);
        }

        if (hex.Length % 2 == 1)
            hex.Append('0');

        byte[] bytes = new byte[hex.Length / 2];
        for (int b = 0; b < bytes.Length; b++)
        {
            int high = HexValue(hex[b * 2]);
            int low = HexValue(hex[b * 2 + 1]);
            bytes[b] = (byte)((high << 4) | low);
        }

        return bytes;
    }

    private static InlineImageToken ReadInlineImageToken(string content, ref int i)
    {
        int dictionaryStart = i;
        int idStart = -1;

        while (i < content.Length)
        {
            while (i < content.Length && char.IsWhiteSpace(content[i]))
                i++;

            int tokenStart = i;
            string token = ReadBareToken(content, ref i);
            if (token == "ID")
            {
                idStart = tokenStart;
                break;
            }

            if (token.Length == 0)
                i++;
        }

        if (idStart < 0)
            throw new InvalidOperationException("Inline image data marker ID was not found.");

        string dictionaryText = content[dictionaryStart..idStart];

        if (i < content.Length && content[i] == '\r')
        {
            i++;
            if (i < content.Length && content[i] == '\n')
                i++;
        }
        else if (i < content.Length && char.IsWhiteSpace(content[i]))
        {
            i++;
        }

        int dataStart = i;
        int dataEnd = FindInlineImageEnd(content, dataStart, out int nextIndex);
        i = nextIndex;

        byte[] bytes = new byte[Math.Max(0, dataEnd - dataStart)];
        for (int b = 0; b < bytes.Length; b++)
            bytes[b] = (byte)(content[dataStart + b] & 0xFF);

        return CreateInlineImageToken(dictionaryText, bytes);
    }

    private static int FindInlineImageEnd(string content, int dataStart, out int nextIndex)
    {
        for (int i = dataStart; i + 1 < content.Length; i++)
        {
            if (content[i] != 'E' || content[i + 1] != 'I')
                continue;

            bool beforeDelimiter = i == dataStart || char.IsWhiteSpace(content[i - 1]);
            bool afterDelimiter = i + 2 >= content.Length || IsPdfDelimiter(content[i + 2]);
            if (!beforeDelimiter || !afterDelimiter)
                continue;

            int dataEnd = i;
            if (dataEnd > dataStart && char.IsWhiteSpace(content[dataEnd - 1]))
                dataEnd--;

            nextIndex = i + 2;
            return dataEnd;
        }

        nextIndex = content.Length;
        return content.Length;
    }

    private static bool IsPdfDelimiter(char ch)
        => char.IsWhiteSpace(ch) || ch is '/' or '[' or ']' or '(' or ')' or '<' or '>';

    private static InlineImageToken CreateInlineImageToken(string dictionaryText, byte[] bytes)
    {
        int width = ParseInlineImageInt(dictionaryText, "/W", "/Width");
        int height = ParseInlineImageInt(dictionaryText, "/H", "/Height");
        int bitsPerComponent = ParseInlineImageInt(dictionaryText, 1, "/BPC", "/BitsPerComponent");
        bool isImageMask = Regex.IsMatch(dictionaryText, @"/(?:IM|ImageMask)\s+true\b", RegexOptions.IgnoreCase);
        bool decodeInverted = Regex.IsMatch(dictionaryText, @"/(?:D|Decode)\s*\[\s*1\s+0\s*\]", RegexOptions.Singleline);
        string filter = ParseInlineImageName(dictionaryText, "/F", "/Filter") switch
        {
            "/Fl" => "/FlateDecode",
            "/DCT" => "/DCTDecode",
            "/CCF" => "/CCITTFaxDecode",
            string f => f
        };

        return new InlineImageToken(
            width,
            height,
            bitsPerComponent,
            ParseInlineImageColorSpace(dictionaryText),
            filter,
            bytes,
            isImageMask,
            decodeInverted);
    }

    private static int ParseInlineImageInt(string source, params string[] names)
        => ParseInlineImageInt(source, 0, names);

    private static int ParseInlineImageInt(string source, int fallback, params string[] names)
    {
        foreach (string name in names)
        {
            Match match = Regex.Match(source, Regex.Escape(name) + @"\s+([+\-]?\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int value))
                return value;
        }

        return fallback;
    }

    private static string ParseInlineImageName(string source, params string[] names)
    {
        foreach (string name in names)
        {
            Match match = Regex.Match(source, Regex.Escape(name) + @"\s*/([^/\s<>\[\]\(\)]+)");
            if (match.Success)
                return "/" + match.Groups[1].Value;
        }

        return string.Empty;
    }

    private static PdfColorSpace ParseInlineImageColorSpace(string source)
    {
        string name = ParseInlineImageName(source, "/CS", "/ColorSpace");
        return name switch
        {
            "/G" or "/DeviceGray" => new PdfDeviceGrayColorSpace(),
            "/RGB" or "/DeviceRGB" => new PdfDeviceRgbColorSpace(),
            "/CMYK" or "/DeviceCMYK" => new PdfDeviceCmykColorSpace(),
            _ => new PdfDeviceGrayColorSpace()
        };
    }

    private static int HexValue(char ch)
    {
        if (ch >= '0' && ch <= '9') return ch - '0';
        if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
        if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
        throw new InvalidOperationException("Некорректный символ в hex string.");
    }

    private sealed class PdfStringToken
    {
        public PdfStringToken(string value) => Value = value;
        private PdfStringToken(string value, byte[] bytes)
        {
            Value = value;
            Bytes = bytes;
        }

        public string Value { get; }
        public byte[]? Bytes { get; }

        public static PdfStringToken FromBytes(byte[] bytes)
        {
            return new PdfStringToken(System.Text.Encoding.Latin1.GetString(bytes), bytes);
        }

        public static PdfStringToken FromHex(byte[] bytes) => FromBytes(bytes);
    }

    private sealed record InlineImageToken(
        int Width,
        int Height,
        int BitsPerComponent,
        PdfColorSpace ColorSpace,
        string Filter,
        byte[] ImageBytes,
        bool IsImageMask,
        bool DecodeInverted);
}
//public static class SimplePdfRenderer
//{
//    public static Bitmap Render(SimplePdfPage page, float zoom)
//    {
//        var pageContext = new PdfPageContext
//        {
//            WidthPt = page.WidthPt,
//            HeightPt = page.HeightPt,
//            Zoom = zoom
//        };

//        var bmp = new Bitmap(pageContext.WidthPx, pageContext.HeightPx);
//        using DrawingGraphics g = DrawingGraphics.FromImage(bmp);
//        g.Clear(DrawingColor.White);
//        g.SmoothingMode = SmoothingMode.AntiAlias;
//        //g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
//        g.TextRenderingHint = TextRenderingHint.AntiAlias;
//        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
//        g.PixelOffsetMode = PixelOffsetMode.Half;

//        _stateStack.Clear();
//        var resourceScope = new PdfResourceScope(page.Resources);
//        var gs = new PdfGraphicsState();
//        RenderContentStreamInternal(g, page, pageContext, page.ContentStream, resourceScope, gs);

//        return bmp;
//    }

//    private static void RenderContentStreamInternal(
//        DrawingGraphics g,
//        SimplePdfPage page,
//        PdfPageContext pageContext,
//        string contentStream,
//        PdfResourceScope resourceScope,
//        PdfGraphicsState gs)
//    {
//        var operands = new List<object>();
//        var path = new GraphicsPath();
//        PointF? currentPoint = null;
//        bool pendingClip = false;
//        bool pendingClipEvenOdd = false;
//        var textState = new TextState();

//        foreach (object tokenObj in Tokenize(contentStream))
//        {
//            if (tokenObj is string op && IsOperator(op))
//            {
//                ExecuteOperator(op, operands, g, page, pageContext, resourceScope, gs, path, ref currentPoint, ref pendingClip, ref pendingClipEvenOdd, textState);
//            }
//            else
//            {
//                operands.Add(tokenObj);
//            }
//        }
//    }

//    private static void ExecuteOperator(
//        string op,
//        List<object> operands,
//        DrawingGraphics g,
//        SimplePdfPage page,
//        PdfPageContext pageContext,
//        PdfResourceScope resourceScope,
//        PdfGraphicsState gs,
//        GraphicsPath path,
//        ref PointF? currentPoint,
//        ref bool pendingClip,
//        ref bool pendingClipEvenOdd,
//        TextState textState)
//    {
//        switch (op)
//        {
//            case "q":
//                PushState(gs, textState, g);
//                break;
//            case "Q":
//                PopState(gs, textState, g);
//                break;
//            case "cm":
//            {
//                float f = ParseFloat(PopOperand(operands));
//                float e = ParseFloat(PopOperand(operands));
//                float d = ParseFloat(PopOperand(operands));
//                float c = ParseFloat(PopOperand(operands));
//                float b = ParseFloat(PopOperand(operands));
//                float a = ParseFloat(PopOperand(operands));
//                using var m = new Matrix(a, b, c, d, e, f);
//                gs.Ctm.Multiply(m, MatrixOrder.Prepend);
//                break;
//            }
//            case "w":
//                gs.LineWidth = ParseFloat(PopOperand(operands));
//                break;
//            case "RG":
//            {
//                float b = ParseFloat(PopOperand(operands));
//                float gVal = ParseFloat(PopOperand(operands));
//                float r = ParseFloat(PopOperand(operands));
//                gs.StrokeColor = PdfRgbToColor(r, gVal, b);
//                break;
//            }
//            case "rg":
//            {
//                float b = ParseFloat(PopOperand(operands));
//                float gVal = ParseFloat(PopOperand(operands));
//                float r = ParseFloat(PopOperand(operands));
//                gs.FillColor = PdfRgbToColor(r, gVal, b);
//                break;
//            }
//            case "K":
//            {
//                float k = ParseFloat(PopOperand(operands));
//                float y = ParseFloat(PopOperand(operands));
//                float m = ParseFloat(PopOperand(operands));
//                float c = ParseFloat(PopOperand(operands));
//                gs.StrokeColor = PdfCmykToColor(c, m, y, k);
//                break;
//            }
//            case "k":
//            {
//                float k = ParseFloat(PopOperand(operands));
//                float y = ParseFloat(PopOperand(operands));
//                float m = ParseFloat(PopOperand(operands));
//                float c = ParseFloat(PopOperand(operands));
//                gs.FillColor = PdfCmykToColor(c, m, y, k);
//                break;
//            }
//            case "m":
//            {
//                float y = ParseFloat(PopOperand(operands));
//                float x = ParseFloat(PopOperand(operands));
//                PointF p = TransformPoint(pageContext, gs.Ctm, x, y);
//                path.StartFigure();
//                currentPoint = p;
//                break;
//            }
//            case "l":
//            {
//                float y = ParseFloat(PopOperand(operands));
//                float x = ParseFloat(PopOperand(operands));
//                PointF p = TransformPoint(pageContext, gs.Ctm, x, y);
//                if (currentPoint == null)
//                {
//                    path.StartFigure();
//                    currentPoint = p;
//                }
//                else
//                {
//                    path.AddLine(currentPoint.Value, p);
//                    currentPoint = p;
//                }
//                break;
//            }
//            case "c":
//            {
//                float y3 = ParseFloat(PopOperand(operands));
//                float x3 = ParseFloat(PopOperand(operands));
//                float y2 = ParseFloat(PopOperand(operands));
//                float x2 = ParseFloat(PopOperand(operands));
//                float y1 = ParseFloat(PopOperand(operands));
//                float x1 = ParseFloat(PopOperand(operands));
//                if (currentPoint == null)
//                    break;

//                PointF p1 = TransformPoint(pageContext, gs.Ctm, x1, y1);
//                PointF p2 = TransformPoint(pageContext, gs.Ctm, x2, y2);
//                PointF p3 = TransformPoint(pageContext, gs.Ctm, x3, y3);
//                path.AddBezier(currentPoint.Value, p1, p2, p3);
//                currentPoint = p3;
//                break;
//            }
//            case "v":
//            {
//                float y3 = ParseFloat(PopOperand(operands));
//                float x3 = ParseFloat(PopOperand(operands));
//                float y2 = ParseFloat(PopOperand(operands));
//                float x2 = ParseFloat(PopOperand(operands));
//                if (currentPoint == null)
//                    break;

//                PointF p1 = currentPoint.Value;
//                PointF p2 = TransformPoint(pageContext, gs.Ctm, x2, y2);
//                PointF p3 = TransformPoint(pageContext, gs.Ctm, x3, y3);
//                path.AddBezier(currentPoint.Value, p1, p2, p3);
//                currentPoint = p3;
//                break;
//            }
//            case "y":
//            {
//                float y3 = ParseFloat(PopOperand(operands));
//                float x3 = ParseFloat(PopOperand(operands));
//                float y1 = ParseFloat(PopOperand(operands));
//                float x1 = ParseFloat(PopOperand(operands));
//                if (currentPoint == null)
//                    break;

//                PointF p1 = TransformPoint(pageContext, gs.Ctm, x1, y1);
//                PointF p3 = TransformPoint(pageContext, gs.Ctm, x3, y3);
//                path.AddBezier(currentPoint.Value, p1, p3, p3);
//                currentPoint = p3;
//                break;
//            }
//            case "h":
//                path.CloseFigure();
//                break;
//            case "re":
//            {
//                float h = ParseFloat(PopOperand(operands));
//                float w = ParseFloat(PopOperand(operands));
//                float y = ParseFloat(PopOperand(operands));
//                float x = ParseFloat(PopOperand(operands));
//                AddRectanglePath(path, pageContext, gs.Ctm, x, y, w, h);
//                currentPoint = TransformPoint(pageContext, gs.Ctm, x, y);
//                break;
//            }
//            case "S":
//                StrokePath(g, pageContext, gs, path);
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "s":
//                CloseCurrentFigureIfNeeded(path);
//                StrokePath(g, pageContext, gs, path);
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "f":
//                FillPath(g, gs, path, evenOdd: false);
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "f*":
//                FillPath(g, gs, path, evenOdd: true);
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "B":
//                FillPath(g, gs, path, evenOdd: false);
//                StrokePath(g, pageContext, gs, path);
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "B*":
//                FillPath(g, gs, path, evenOdd: true);
//                StrokePath(g, pageContext, gs, path);
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "b":
//                CloseCurrentFigureIfNeeded(path);
//                FillPath(g, gs, path, evenOdd: false);
//                StrokePath(g, pageContext, gs, path);
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "b*":
//                CloseCurrentFigureIfNeeded(path);
//                FillPath(g, gs, path, evenOdd: true);
//                StrokePath(g, pageContext, gs, path);
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "W":
//                pendingClip = true;
//                pendingClipEvenOdd = false;
//                break;
//            case "W*":
//                pendingClip = true;
//                pendingClipEvenOdd = true;
//                break;
//            case "n":
//                ApplyPendingClipIfNeeded(g, path, ref pendingClip, ref pendingClipEvenOdd);
//                ResetPath(path, ref currentPoint);
//                break;
//            case "BT":
//                textState.BeginText();
//                break;
//            case "ET":
//                textState.EndText();
//                break;
//            case "Tf":
//            {
//                textState.FontSize = ParseFloat(PopOperand(operands));
//                textState.FontResourceName = (string)PopOperand(operands);
//                break;
//            }
//            case "Tm":
//            {
//                float f = ParseFloat(PopOperand(operands));
//                float e = ParseFloat(PopOperand(operands));
//                float d = ParseFloat(PopOperand(operands));
//                float c = ParseFloat(PopOperand(operands));
//                float b = ParseFloat(PopOperand(operands));
//                float a = ParseFloat(PopOperand(operands));
//                textState.SetTextMatrix(a, b, c, d, e, f);
//                break;
//            }
//            case "Td":
//            {
//                float ty = ParseFloat(PopOperand(operands));
//                float tx = ParseFloat(PopOperand(operands));
//                textState.MoveTextPosition(tx, ty);
//                break;
//            }
//            case "TD":
//            {
//                float ty = ParseFloat(PopOperand(operands));
//                float tx = ParseFloat(PopOperand(operands));
//                textState.Leading = -ty;
//                textState.MoveTextPosition(tx, ty);
//                break;
//            }
//            case "T*":
//                textState.MoveToNextLine();
//                break;
//            case "Tc":
//                textState.CharSpacing = ParseFloat(PopOperand(operands));
//                break;
//            case "Tw":
//                textState.WordSpacing = ParseFloat(PopOperand(operands));
//                break;
//            case "Tz":
//                textState.HorizontalScale = ParseFloat(PopOperand(operands));
//                break;
//            case "TL":
//                textState.Leading = ParseFloat(PopOperand(operands));
//                break;
//            case "Ts":
//                textState.Rise = ParseFloat(PopOperand(operands));
//                break;
//            case "Tj":
//            {
//                string text = GetStringValue(PopOperand(operands));
//                ShowText(g, pageContext, gs, resourceScope, textState, text);
//                break;
//            }
//            case "TJ":
//            {
//                List<object> items = PopArray(operands);
//                foreach (object item in items)
//                {
//                    if (item is PdfStringToken st)
//                    {
//                        ShowText(g, pageContext, gs, resourceScope, textState, st.Value);
//                    }
//                    else
//                    {
//                        float tj = ParseFloat(item);
//                        float tx = -(tj / 1000f) * textState.FontSize * (textState.HorizontalScale / 100f);
//                        textState.TranslateTextMatrix(tx, 0f);
//                    }
//                }
//                break;
//            }
//            case "'":
//            {
//                textState.MoveToNextLine();
//                string text = GetStringValue(PopOperand(operands));
//                ShowText(g, pageContext, gs, resourceScope, textState, text);
//                break;
//            }
//            case "\"":
//            {
//                string text = GetStringValue(PopOperand(operands));
//                float ac = ParseFloat(PopOperand(operands));
//                float aw = ParseFloat(PopOperand(operands));
//                textState.WordSpacing = aw;
//                textState.CharSpacing = ac;
//                textState.MoveToNextLine();
//                ShowText(g, pageContext, gs, resourceScope, textState, text);
//                break;
//            }
//            case "Do":
//            {
//                string resourceName = (string)PopOperand(operands);

//                if (resourceScope.TryGetForm(resourceName, out PdfFormXObject? form) && form != null)
//                {
//                    RenderFormXObject(g, page, pageContext, form, resourceScope, gs);
//                    break;
//                }

//                if (resourceScope.TryGetImage(resourceName, out PdfImageXObject? image) && image != null)
//                {
//                    RenderImageXObject(g, pageContext, image, gs);
//                    break;
//                }

//                throw new NotSupportedException("XObject " + resourceName + " не найден или пока не поддержан.");
//            }
//        }
//    }

//    private static void RenderFormXObject(
//        DrawingGraphics g,
//        SimplePdfPage page,
//        PdfPageContext pageContext,
//        PdfFormXObject form,
//        PdfResourceScope parentScope,
//        PdfGraphicsState currentState)
//    {
//        GraphicsState savedGraphics = g.Save();
//        try
//        {
//            PdfGraphicsState formState = currentState.Clone();
//            using (Matrix formMatrix = form.CreateMatrix())
//            {
//                formState.Ctm.Multiply(formMatrix, MatrixOrder.Prepend);
//            }

//            using GraphicsPath bboxClip = CreateBBoxClipPath(pageContext, formState.Ctm, form.BBox);
//            g.SetClip(bboxClip, CombineMode.Intersect);

//            var formScope = new PdfResourceScope(form.Resources, parentScope);
//            RenderContentStreamInternal(g, page, pageContext, form.ContentStream, formScope, formState);
//        }
//        finally
//        {
//            g.Restore(savedGraphics);
//        }
//    }

//    private static void RenderImageXObject(
//        DrawingGraphics g,
//        PdfPageContext pageContext,
//        PdfImageXObject image,
//        PdfGraphicsState gs)
//    {
//        GraphicsState saved = g.Save();
//        try
//        {
//            using Bitmap bmp = image.CreateBitmap();

//            PointF lowerLeft = TransformPoint(pageContext, gs.Ctm, 0f, 0f);
//            PointF upperLeft = TransformPoint(pageContext, gs.Ctm, 0f, 1f);
//            PointF upperRight = TransformPoint(pageContext, gs.Ctm, 1f, 1f);

//            bool axisAligned =
//                Math.Abs(upperLeft.X - lowerLeft.X) < 0.01f &&
//                Math.Abs(upperLeft.Y - upperRight.Y) < 0.01f;

//            float imageYOffset = axisAligned ? -3f : -4f;
//            upperLeft.Y += imageYOffset;
//            upperRight.Y += imageYOffset;
//            lowerLeft.Y += imageYOffset;

//            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
//            g.PixelOffsetMode = PixelOffsetMode.Half;

//            if (axisAligned)
//            {
//                float left = upperLeft.X;
//                float top = upperLeft.Y;
//                float width = upperRight.X - upperLeft.X;
//                float height = lowerLeft.Y - upperLeft.Y;

//                g.DrawImage(
//                    bmp,
//                    new RectangleF(left, top, width, height),
//                    new RectangleF(0, 0, bmp.Width, bmp.Height),
//                    GraphicsUnit.Pixel);
//            }
//            else
//            {
//                g.DrawImage(
//                    bmp,
//                    new[] { upperLeft, upperRight, lowerLeft },
//                    new Rectangle(0, 0, bmp.Width, bmp.Height),
//                    GraphicsUnit.Pixel);
//            }
//        }
//        finally
//        {
//            g.Restore(saved);
//        }
//    }

//    private static GraphicsPath CreateBBoxClipPath(PdfPageContext pageContext, Matrix ctm, float[] bbox)
//    {
//        float x0 = bbox.Length > 0 ? bbox[0] : 0f;
//        float y0 = bbox.Length > 1 ? bbox[1] : 0f;
//        float x1 = bbox.Length > 2 ? bbox[2] : 0f;
//        float y1 = bbox.Length > 3 ? bbox[3] : 0f;

//        var gp = new GraphicsPath();
//        AddRectanglePath(gp, pageContext, ctm, x0, y0, x1 - x0, y1 - y0);
//        return gp;
//    }


//    private static readonly StringFormat _typographicFormat = CreateTypographicFormat();

//    private static StringFormat CreateTypographicFormat()
//    {
//        var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
//        sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces
//                       | StringFormatFlags.NoClip
//                       | StringFormatFlags.NoWrap;
//                       //| StringFormatFlags.NoFitBlackBox;
//        sf.Trimming = StringTrimming.None;
//        return sf;
//    }
//    //private static StringFormat CreateTypographicFormat()
//    //{
//    //    var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
//    //    sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces
//    //                   | StringFormatFlags.NoClip
//    //                   | StringFormatFlags.NoWrap;
//    //    sf.Trimming = StringTrimming.None;
//    //    return sf;
//    //}
//    private static readonly Stack<(PdfGraphicsState Gs, TextState Text, GraphicsState Graphics)> _stateStack = new();

//    private static void PushState(PdfGraphicsState gs, TextState textState, DrawingGraphics g)
//        => _stateStack.Push((gs.Clone(), textState.Clone(), g.Save()));

//    private static void PopState(PdfGraphicsState gs, TextState textState, DrawingGraphics g)
//    {
//        if (_stateStack.Count == 0)
//            return;

//        var popped = _stateStack.Pop();
//        gs.Ctm.Dispose();
//        gs.Ctm = popped.Gs.Ctm.Clone();
//        gs.FillColor = popped.Gs.FillColor;
//        gs.StrokeColor = popped.Gs.StrokeColor;
//        gs.LineWidth = popped.Gs.LineWidth;
//        textState.CopyFrom(popped.Text);
//        g.Restore(popped.Graphics);
//    }

//    private static void ShowText(
//    DrawingGraphics g,
//    PdfPageContext pageContext,
//    PdfGraphicsState gs,
//    PdfResourceScope resourceScope,
//    TextState textState,
//    string text)
//    {
//        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(textState.FontResourceName))
//            return;

//        if (!resourceScope.TryGetFont(textState.FontResourceName, out PdfFontResource? fontResource) || fontResource == null)
//            return;

//        using Font font = ResolveFont(fontResource, textState.FontSize * pageContext.Zoom);
//        using SolidBrush brush = new(gs.FillColor);

//        string familyName = font.FontFamily.Name;
//        FontStyle style = font.Style;
//        float emSizePx = font.Size;

//        foreach (char ch in text)
//        {
//            PointF originUser = ApplyMatrix(textState.TextMatrix, 0f, textState.Rise);
//            PointF dirUser = ApplyMatrix(textState.TextMatrix, 1f, textState.Rise);

//            PointF originPage = ApplyMatrix(gs.Ctm, originUser.X, originUser.Y);
//            PointF dirPage = ApplyMatrix(gs.Ctm, dirUser.X, dirUser.Y);

//            PointF originScreen = UserToScreen(pageContext, originPage);
//            PointF dirScreen = UserToScreen(pageContext, dirPage);

//            float angle = (float)(Math.Atan2(
//                dirScreen.Y - originScreen.Y,
//                dirScreen.X - originScreen.X) * 180.0 / Math.PI);

//            GraphicsState saved = g.Save();
//            try
//            {
//                g.TranslateTransform(originScreen.X, originScreen.Y);
//                g.RotateTransform(angle);

//                using var glyphPath = new GraphicsPath();
//                glyphPath.AddString(
//                    ch.ToString(),
//                    font.FontFamily,
//                    (int)style,
//                    emSizePx,
//                    new PointF(0f, -GetFontAscentPx(font)),
//                    _typographicFormat);

//                g.FillPath(brush, glyphPath);
//            }
//            finally
//            {
//                g.Restore(saved);
//            }

//            float glyphWidth = fontResource.GetGlyphWidth(ch);
//            float wordSpacing = ch == ' ' ? textState.WordSpacing : 0f;

//            float advance =
//                ((glyphWidth / 1000f) * textState.FontSize
//                 + textState.CharSpacing
//                 + wordSpacing)
//                * (textState.HorizontalScale / 100f);

//            textState.TranslateTextMatrix(advance, 0f);
//        }
//    }

//    //private static void ShowText(
//    //DrawingGraphics g,
//    //PdfPageContext pageContext,
//    //PdfGraphicsState gs,
//    //PdfResourceScope resourceScope,
//    //TextState textState,
//    //string text)
//    //{
//    //    if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(textState.FontResourceName))
//    //        return;

//    //    if (!resourceScope.TryGetFont(textState.FontResourceName, out PdfFontResource? fontResource) || fontResource == null)
//    //        return;

//    //    using Font font = ResolveFont(fontResource, textState.FontSize * pageContext.Zoom);
//    //    using SolidBrush brush = new(gs.FillColor);

//    //    float ascentPx = GetFontAscentPx(font);

//    //    foreach (char ch in text)
//    //    {
//    //        PointF originUser = ApplyMatrix(textState.TextMatrix, 0f, textState.Rise);
//    //        PointF dirUser = ApplyMatrix(textState.TextMatrix, 1f, textState.Rise);

//    //        PointF originPage = ApplyMatrix(gs.Ctm, originUser.X, originUser.Y);
//    //        PointF dirPage = ApplyMatrix(gs.Ctm, dirUser.X, dirUser.Y);

//    //        PointF originScreen = UserToScreen(pageContext, originPage);
//    //        PointF dirScreen = UserToScreen(pageContext, dirPage);

//    //        float angle = (float)(Math.Atan2(
//    //            dirScreen.Y - originScreen.Y,
//    //            dirScreen.X - originScreen.X) * 180.0 / Math.PI);

//    //        GraphicsState saved = g.Save();
//    //        try
//    //        {
//    //            g.TranslateTransform(originScreen.X, originScreen.Y);
//    //            g.RotateTransform(angle);

//    //            g.DrawString(
//    //                ch.ToString(),
//    //                font,
//    //                brush,
//    //                new PointF(0f, -ascentPx),
//    //                _typographicFormat);
//    //        }
//    //        finally
//    //        {
//    //            g.Restore(saved);
//    //        }

//    //        float glyphWidth = fontResource.GetGlyphWidth(ch);
//    //        float wordSpacing = ch == ' ' ? textState.WordSpacing : 0f;

//    //        float advance =
//    //            ((glyphWidth / 1000f) * textState.FontSize
//    //             + textState.CharSpacing
//    //             + wordSpacing)
//    //            * (textState.HorizontalScale / 100f);

//    //        textState.TranslateTextMatrix(advance, 0f);
//    //    }
//    //}
//    //private static void ShowText(
//    //    DrawingGraphics g,
//    //    PdfPageContext pageContext,
//    //    PdfGraphicsState gs,
//    //    PdfResourceScope resourceScope,
//    //    TextState textState,
//    //    string text)
//    //{
//    //    if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(textState.FontResourceName))
//    //        return;

//    //    if (!resourceScope.TryGetFont(textState.FontResourceName, out PdfFontResource? fontResource) || fontResource == null)
//    //        return;

//    //    using Font font = ResolveFont(fontResource, textState.FontSize * pageContext.Zoom);
//    //    using SolidBrush brush = new(gs.FillColor);

//    //    foreach (char ch in text)
//    //    {
//    //        PointF originUser = ApplyMatrix(textState.TextMatrix, 0f, textState.Rise);
//    //        PointF dirUser = ApplyMatrix(textState.TextMatrix, 1f, textState.Rise);

//    //        PointF originPage = ApplyMatrix(gs.Ctm, originUser.X, originUser.Y);
//    //        PointF dirPage = ApplyMatrix(gs.Ctm, dirUser.X, dirUser.Y);

//    //        PointF originScreen = UserToScreen(pageContext, originPage);
//    //        PointF dirScreen = UserToScreen(pageContext, dirPage);

//    //        float angle = (float)(Math.Atan2(dirScreen.Y - originScreen.Y, dirScreen.X - originScreen.X) * 180.0 / Math.PI);

//    //        GraphicsState saved = g.Save();
//    //        try
//    //        {
//    //            g.TranslateTransform(originScreen.X, originScreen.Y);
//    //            g.RotateTransform(angle);
//    //            g.DrawString(ch.ToString(), font, brush, 0f, -font.SizeInPoints * pageContext.Zoom);
//    //        }
//    //        finally
//    //        {
//    //            g.Restore(saved);
//    //        }

//    //        float glyphWidth = fontResource.GetGlyphWidth(ch);
//    //        float wordSpacing = ch == ' ' ? textState.WordSpacing : 0f;
//    //        float advance = ((glyphWidth / 1000f) * textState.FontSize + textState.CharSpacing + wordSpacing) * (textState.HorizontalScale / 100f);
//    //        textState.TranslateTextMatrix(advance, 0f);
//    //    }
//    //}

//    private static Font ResolveFont(PdfFontResource fontResource, float sizePx)
//    {
//        string family = fontResource.BaseFontName switch
//        {
//            "Helvetica" => "Arial",
//            "Helvetica-Bold" => "Arial",
//            "Times-Roman" => "Times New Roman",
//            "Times-Bold" => "Times New Roman",
//            //"Courier" => "Consolas",
//            //"Courier-Bold" => "Consolas",
//            "Courier" => "Courier New",
//            "Courier-Bold" => "Courier New",
//            _ => "Arial"
//        };
//        return new Font(family, Math.Max(1f, sizePx), GraphicsUnit.Pixel);
//    }
//    private static float GetFontAscentPx(Font font)
//    {
//        FontStyle style = font.Style;
//        FontFamily family = font.FontFamily;

//        int ascent = family.GetCellAscent(style);
//        int em = family.GetEmHeight(style);

//        if (em <= 0)
//            return font.Size;

//        return font.Size * ascent / em;
//    }
//    private static PointF TransformPoint(PdfPageContext pageContext, Matrix matrix, float x, float y)
//    {
//        PointF p = ApplyMatrix(matrix, x, y);
//        return UserToScreen(pageContext, p);
//    }

//    private static PointF ApplyMatrix(Matrix matrix, float x, float y)
//    {
//        PointF[] pts = { new(x, y) };
//        using Matrix clone = matrix.Clone();
//        clone.TransformPoints(pts);
//        return pts[0];
//    }

//    private static PointF UserToScreen(PdfPageContext pageContext, PointF userPoint)
//    {
//        return new PointF(
//            userPoint.X * pageContext.Zoom,
//            (pageContext.HeightPt - userPoint.Y) * pageContext.Zoom);
//    }

//    private static void AddRectanglePath(GraphicsPath path, PdfPageContext pageContext, Matrix ctm, float x, float y, float w, float h)
//    {
//        PointF p1 = TransformPoint(pageContext, ctm, x, y);
//        PointF p2 = TransformPoint(pageContext, ctm, x + w, y);
//        PointF p3 = TransformPoint(pageContext, ctm, x + w, y + h);
//        PointF p4 = TransformPoint(pageContext, ctm, x, y + h);

//        path.StartFigure();
//        path.AddLine(p1, p2);
//        path.AddLine(p2, p3);
//        path.AddLine(p3, p4);
//        path.AddLine(p4, p1);
//        path.CloseFigure();
//    }

//    private static void StrokePath(DrawingGraphics g, PdfPageContext pageContext, PdfGraphicsState gs, GraphicsPath path)
//    {
//        float effectiveWidth = Math.Max(1f, gs.LineWidth * pageContext.Zoom);
//        using var pen = new Pen(gs.StrokeColor, effectiveWidth);
//        g.DrawPath(pen, path);
//    }

//    private static void FillPath(DrawingGraphics g, PdfGraphicsState gs, GraphicsPath path, bool evenOdd)
//    {
//        path.FillMode = evenOdd ? FillMode.Alternate : FillMode.Winding;
//        using var brush = new SolidBrush(gs.FillColor);
//        g.FillPath(brush, path);
//    }

//    private static void CloseCurrentFigureIfNeeded(GraphicsPath path)
//    {
//        if (path.PointCount > 0)
//            path.CloseFigure();
//    }

//    private static void ApplyPendingClipIfNeeded(DrawingGraphics g, GraphicsPath path, ref bool pendingClip, ref bool pendingClipEvenOdd)
//    {
//        if (!pendingClip)
//            return;

//        path.FillMode = pendingClipEvenOdd ? FillMode.Alternate : FillMode.Winding;
//        g.SetClip(path, CombineMode.Intersect);
//        pendingClip = false;
//        pendingClipEvenOdd = false;
//    }

//    private static void ResetPath(GraphicsPath path, ref PointF? currentPoint)
//    {
//        path.Reset();
//        currentPoint = null;
//    }

//    private static DrawingColor PdfRgbToColor(float r, float g, float b)
//    {
//        return DrawingColor.FromArgb(
//            ClampToByte(r * 255f),
//            ClampToByte(g * 255f),
//            ClampToByte(b * 255f));
//    }

//    private static DrawingColor PdfCmykToColor(float c, float m, float y, float k)
//    {
//        int r = ClampToByte(255f * (1f - Math.Min(1f, c + k)));
//        int g = ClampToByte(255f * (1f - Math.Min(1f, m + k)));
//        int b = ClampToByte(255f * (1f - Math.Min(1f, y + k)));
//        return DrawingColor.FromArgb(r, g, b);
//    }

//    private static int ClampToByte(float value)
//    {
//        if (value < 0f) return 0;
//        if (value > 255f) return 255;
//        return (int)Math.Round(value);
//    }

//    private static object PopOperand(List<object> operands)
//    {
//        if (operands.Count == 0)
//            throw new InvalidOperationException("Недостаточно операндов.");
//        object value = operands[^1];
//        operands.RemoveAt(operands.Count - 1);
//        return value;
//    }

//    private static float ParseFloat(object value)
//    {
//        if (value is string s)
//            return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
//        throw new InvalidOperationException("Ожидалось число.");
//    }

//    private static string GetStringValue(object value)
//    {
//        return value switch
//        {
//            PdfStringToken st => st.Value,
//            string s => s,
//            _ => throw new InvalidOperationException("Ожидалась строка.")
//        };
//    }

//    private static List<object> PopArray(List<object> operands)
//    {
//        object value = PopOperand(operands);
//        if (value is List<object> list)
//            return list;
//        throw new InvalidOperationException("Ожидался PDF array.");
//    }

//    private static bool IsOperator(string token)
//    {
//        return token == "q" || token == "Q" || token == "cm" ||
//               token == "BT" || token == "ET" || token == "Tf" || token == "Tm" || token == "Td" || token == "TD" || token == "T*" ||
//               token == "Tc" || token == "Tw" || token == "Tz" || token == "TL" || token == "Ts" ||
//               token == "Tj" || token == "TJ" || token == "'" || token == "\"" ||
//               token == "m" || token == "l" || token == "c" || token == "v" || token == "y" || token == "h" || token == "re" ||
//               token == "S" || token == "s" ||
//               token == "f" || token == "f*" ||
//               token == "B" || token == "B*" ||
//               token == "b" || token == "b*" ||
//               token == "n" ||
//               token == "w" || token == "RG" || token == "rg" || token == "K" || token == "k" ||
//               token == "W" || token == "W*" ||
//               token == "Do";
//    }

//    private static IEnumerable<object> Tokenize(string content)
//    {
//        int i = 0;
//        while (i < content.Length)
//        {
//            char ch = content[i];

//            if (char.IsWhiteSpace(ch))
//            {
//                i++;
//                continue;
//            }

//            if (ch == '%')
//            {
//                while (i < content.Length && content[i] != '\n' && content[i] != '\r')
//                    i++;
//                continue;
//            }

//            if (ch == '[')
//            {
//                i++;
//                yield return ReadArray(content, ref i);
//                continue;
//            }

//            if (ch == '(')
//            {
//                yield return new PdfStringToken(ReadLiteralString(content, ref i));
//                continue;
//            }

//            if (ch == '/')
//            {
//                yield return ReadName(content, ref i);
//                continue;
//            }

//            if (ch == '\'' || ch == '"' || ch == '[' || ch == ']')
//            {
//                yield return content[i++].ToString();
//                continue;
//            }

//            yield return ReadBareToken(content, ref i);
//        }
//    }

//    private static List<object> ReadArray(string content, ref int i)
//    {
//        var list = new List<object>();
//        while (i < content.Length)
//        {
//            char ch = content[i];
//            if (char.IsWhiteSpace(ch))
//            {
//                i++;
//                continue;
//            }
//            if (ch == '%')
//            {
//                while (i < content.Length && content[i] != '\n' && content[i] != '\r')
//                    i++;
//                continue;
//            }
//            if (ch == ']')
//            {
//                i++;
//                break;
//            }
//            if (ch == '[')
//            {
//                i++;
//                list.Add(ReadArray(content, ref i));
//                continue;
//            }
//            if (ch == '(')
//            {
//                list.Add(new PdfStringToken(ReadLiteralString(content, ref i)));
//                continue;
//            }
//            if (ch == '/')
//            {
//                list.Add(ReadName(content, ref i));
//                continue;
//            }
//            list.Add(ReadBareToken(content, ref i));
//        }
//        return list;
//    }

//    private static string ReadName(string content, ref int i)
//    {
//        int start = i++;
//        while (i < content.Length)
//        {
//            char ch = content[i];
//            if (char.IsWhiteSpace(ch) || ch == '/' || ch == '[' || ch == ']' || ch == '(' || ch == ')' || ch == '<' || ch == '>')
//                break;
//            i++;
//        }
//        return content[start..i];
//    }

//    private static string ReadBareToken(string content, ref int i)
//    {
//        int start = i;
//        while (i < content.Length)
//        {
//            char ch = content[i];
//            if (char.IsWhiteSpace(ch) || ch == '[' || ch == ']' || ch == '(' || ch == ')' || ch == '<' || ch == '>' || ch == '/')
//                break;
//            i++;
//        }
//        return content[start..i];
//    }

//    private static string ReadLiteralString(string content, ref int i)
//    {
//        if (content[i] != '(')
//            throw new InvalidOperationException("Ожидалась literal string.");
//        i++; // skip '('

//        var sb = new System.Text.StringBuilder();
//        int depth = 1;
//        while (i < content.Length)
//        {
//            char ch = content[i++];
//            if (ch == '\\')
//            {
//                if (i >= content.Length)
//                    break;
//                char esc = content[i++];
//                sb.Append(esc switch
//                {
//                    'n' => '\n',
//                    'r' => '\r',
//                    't' => '\t',
//                    'b' => '\b',
//                    'f' => '\f',
//                    '(' => '(',
//                    ')' => ')',
//                    '\\' => '\\',
//                    _ => esc
//                });
//                continue;
//            }
//            if (ch == '(')
//            {
//                depth++;
//                sb.Append(ch);
//                continue;
//            }
//            if (ch == ')')
//            {
//                depth--;
//                if (depth == 0)
//                    break;
//                sb.Append(ch);
//                continue;
//            }
//            sb.Append(ch);
//        }

//        return sb.ToString();
//    }

//    private sealed class PdfStringToken
//    {
//        public PdfStringToken(string value) => Value = value;
//        public string Value { get; }
//    }


//    private sealed class TextState
//    {
//        public string FontResourceName { get; set; } = "/F1";
//        public float FontSize { get; set; } = 12f;
//        public float CharSpacing { get; set; }
//        public float WordSpacing { get; set; }
//        public float HorizontalScale { get; set; } = 100f;
//        public float Leading { get; set; }
//        public float Rise { get; set; }

//        public Matrix TextMatrix { get; private set; } = new();
//        public Matrix LineMatrix { get; private set; } = new();

//        public void BeginText()
//        {
//            TextMatrix.Dispose();
//            LineMatrix.Dispose();
//            TextMatrix = new Matrix();
//            LineMatrix = new Matrix();
//        }

//        public void EndText()
//        {
//        }

//        public void SetTextMatrix(float a, float b, float c, float d, float e, float f)
//        {
//            TextMatrix.Dispose();
//            LineMatrix.Dispose();
//            TextMatrix = new Matrix(a, b, c, d, e, f);
//            LineMatrix = new Matrix(a, b, c, d, e, f);
//        }

//        public void MoveTextPosition(float tx, float ty)
//        {
//            using var t = new Matrix(1, 0, 0, 1, tx, ty);
//            LineMatrix.Multiply(t, MatrixOrder.Prepend);
//            TextMatrix.Dispose();
//            TextMatrix = LineMatrix.Clone();
//        }

//        public void MoveToNextLine() => MoveTextPosition(0f, -Leading);

//        public void TranslateTextMatrix(float tx, float ty)
//        {
//            using var t = new Matrix(1, 0, 0, 1, tx, ty);
//            TextMatrix.Multiply(t, MatrixOrder.Prepend);
//        }

//        public TextState Clone()
//        {
//            var clone = new TextState
//            {
//                FontResourceName = FontResourceName,
//                FontSize = FontSize,
//                CharSpacing = CharSpacing,
//                WordSpacing = WordSpacing,
//                HorizontalScale = HorizontalScale,
//                Leading = Leading,
//                Rise = Rise
//            };
//            clone.TextMatrix.Dispose();
//            clone.LineMatrix.Dispose();
//            clone.TextMatrix = TextMatrix.Clone();
//            clone.LineMatrix = LineMatrix.Clone();
//            return clone;
//        }

//        public void CopyFrom(TextState other)
//        {
//            FontResourceName = other.FontResourceName;
//            FontSize = other.FontSize;
//            CharSpacing = other.CharSpacing;
//            WordSpacing = other.WordSpacing;
//            HorizontalScale = other.HorizontalScale;
//            Leading = other.Leading;
//            Rise = other.Rise;
//            TextMatrix.Dispose();
//            LineMatrix.Dispose();
//            TextMatrix = other.TextMatrix.Clone();
//            LineMatrix = other.LineMatrix.Clone();
//        }
//    }
//}
