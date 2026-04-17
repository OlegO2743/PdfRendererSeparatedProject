using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PdfCore.Graphics;
using PdfCore.Resources;
using DrawingGraphics = System.Drawing.Graphics;

namespace PdfCore.Text;

public sealed class PdfTextEngine
{
    private readonly StateSnapshot _state = new();

    private static readonly StringFormat _typographicFormat = CreateTypographicFormat();

    public StateSnapshot CreateSnapshot() => _state.Clone();

    public void RestoreSnapshot(StateSnapshot snapshot) => _state.CopyFrom(snapshot);

    public void BeginText() => _state.BeginText();

    public void EndText() => _state.EndText();

    public void SetFont(string fontResourceName, float fontSize)
    {
        _state.FontResourceName = fontResourceName;
        _state.FontSize = fontSize;
    }

    public void SetTextMatrix(float a, float b, float c, float d, float e, float f)
        => _state.SetTextMatrix(a, b, c, d, e, f);

    public void MoveTextPosition(float tx, float ty)
        => _state.MoveTextPosition(tx, ty);

    public void MoveTextPositionAndSetLeading(float tx, float ty)
    {
        _state.Leading = -ty;
        _state.MoveTextPosition(tx, ty);
    }

    public void MoveToNextLine() => _state.MoveToNextLine();

    public void SetCharSpacing(float value) => _state.CharSpacing = value;

    public void SetWordSpacing(float value) => _state.WordSpacing = value;

    public void SetHorizontalScale(float value) => _state.HorizontalScale = value;

    public void SetLeading(float value) => _state.Leading = value;

    public void SetRise(float value) => _state.Rise = value;

    public void TranslateTextMatrix(float tx, float ty)
        => _state.TranslateTextMatrix(tx, ty);

    public void ShowText(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfResourceScope resourceScope,
        string text,
        byte[]? rawBytes = null)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_state.FontResourceName))
            return;

        if (!resourceScope.TryGetFont(_state.FontResourceName, out PdfFontResource? fontResource) || fontResource == null)
            return;

        using SolidBrush brush = new(ApplyOpacity(gs.FillColor, gs.FillAlpha));

        float textUnitScalePx = GetTextUnitScalePx(pageContext, gs);

        if (rawBytes != null &&
            rawBytes.Length > 0 &&
            ShouldUseType1GlyphRendering(fontResource) &&
            TryResolveType1GlyphFont(fontResource) is PdfType1Font type1Font &&
            TryCreateType1GlyphPlan(fontResource, type1Font, rawBytes, out List<SimpleGlyph> type1Glyphs))
        {
            RenderType1Glyphs(g, pageContext, gs, fontResource, type1Font, brush, type1Glyphs, text);
            _state.TranslateTextMatrix(ComputeAdvance(type1Glyphs), 0f);
            return;
        }

        if (rawBytes != null &&
            rawBytes.Length > 0 &&
            fontResource.IsIdentityH &&
            TryResolveCffGlyphFont(fontResource) is PdfCffFont identityCffFont &&
            TryCreateIdentityCffGlyphPlan(fontResource, identityCffFont, rawBytes, out List<SimpleGlyph> identityCffGlyphs))
        {
            RenderCffGlyphs(g, pageContext, gs, identityCffFont, brush, identityCffGlyphs, text);
            _state.TranslateTextMatrix(ComputeAdvance(identityCffGlyphs), 0f);
            return;
        }

        if (rawBytes != null &&
            rawBytes.Length > 0 &&
            TryResolveCffGlyphFont(fontResource) is PdfCffFont cffFont &&
            TryCreateCffGlyphPlan(fontResource, cffFont, rawBytes, out List<SimpleGlyph> cffGlyphs))
        {
            RenderCffGlyphs(g, pageContext, gs, cffFont, brush, cffGlyphs, text);
            _state.TranslateTextMatrix(ComputeAdvance(cffGlyphs), 0f);
            return;
        }

        if (rawBytes != null &&
            rawBytes.Length > 0 &&
            !fontResource.IsIdentityH &&
            !fontResource.PreferCidGlyphCodesForRendering &&
            TryResolveTrueTypeGlyphFont(fontResource) is PdfTrueTypeFont simpleGlyphFont &&
            TryCreateSimpleGlyphPlan(fontResource, simpleGlyphFont, text, rawBytes, out List<SimpleGlyph> simpleGlyphs))
        {
            RenderSimpleGlyphs(g, pageContext, gs, simpleGlyphFont, brush, simpleGlyphs, text);
            _state.TranslateTextMatrix(ComputeAdvance(simpleGlyphs), 0f);
            return;
        }

        if (fontResource.PreferCidGlyphCodesForRendering &&
            rawBytes != null &&
            rawBytes.Length > 0 &&
            TryResolveTrueTypeGlyphFont(fontResource) is PdfTrueTypeFont glyphFont &&
            TryCreateIdentityGlyphPlan(fontResource, rawBytes, out List<SimpleGlyph> identityGlyphs))
        {
            RenderSimpleGlyphs(g, pageContext, gs, glyphFont, brush, identityGlyphs, text);
            _state.TranslateTextMatrix(ComputeAdvance(identityGlyphs), 0f);
            return;
        }

        using Font font = ResolveFont(fontResource, _state.FontSize * textUnitScalePx);

        bool hasPdfMetrics =
            fontResource.Widths != null ||
            fontResource.CidWidths != null ||
            fontResource.EncodingMap != null ||
            fontResource.ToUnicodeMap != null;

        bool renderWholeString =
            !hasPdfMetrics &&
            Math.Abs(_state.CharSpacing) < 0.001f &&
            Math.Abs(_state.WordSpacing) < 0.001f;

        if (ShouldUseMeasuredWholeStringFallback(fontResource) &&
            Math.Abs(_state.CharSpacing) < 0.001f &&
            Math.Abs(_state.WordSpacing) < 0.001f)
        {
            float measuredAdvance = RenderMeasuredWholeString(g, pageContext, gs, font, brush, text);
            _state.TranslateTextMatrix(measuredAdvance, 0f);
            return;
        }

        float totalAdvance;
        if (renderWholeString)
        {
            RenderWholeString(g, pageContext, gs, font, brush, text);
            totalAdvance = ComputeAdvance(fontResource, text);
        }
        else
        {
            totalAdvance = RenderPerCharacter(g, pageContext, gs, fontResource, font, brush, text);
        }

        _state.TranslateTextMatrix(totalAdvance, 0f);
    }

    private void RenderSimpleGlyphs(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfTrueTypeFont glyphFont,
        SolidBrush brush,
        IReadOnlyList<SimpleGlyph> glyphs,
        string text)
    {
        using var path = new GraphicsPath(FillMode.Winding);
        float x = 0f;
        foreach (SimpleGlyph glyph in glyphs)
        {
            using Matrix transform = CreateGlyphScreenTransform(pageContext, gs, glyphFont, x);
            glyphFont.AddGlyphPath(path, glyph.GlyphId, transform);
            x += glyph.DrawAdvance;
        }

        g.FillPath(brush, path);
        TrackTextBounds(pageContext, text, path);
    }

    private void RenderCffGlyphs(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfCffFont cffFont,
        SolidBrush brush,
        IReadOnlyList<SimpleGlyph> glyphs,
        string text)
    {
        using var path = new GraphicsPath(FillMode.Winding);
        float x = 0f;
        foreach (SimpleGlyph glyph in glyphs)
        {
            using Matrix transform = CreateCffGlyphScreenTransform(pageContext, gs, cffFont, x);
            cffFont.AddGlyphPath(path, glyph.GlyphId, transform);
            x += glyph.DrawAdvance;
        }

        if (path.PointCount > 0)
        {
            g.FillPath(brush, path);
            TrackTextBounds(pageContext, text, path);
        }
    }

    private void RenderType1Glyphs(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfFontResource fontResource,
        PdfType1Font type1Font,
        SolidBrush brush,
        IReadOnlyList<SimpleGlyph> glyphs,
        string text)
    {
        using var path = new GraphicsPath(FillMode.Winding);
        float x = 0f;
        foreach (SimpleGlyph glyph in glyphs)
        {
            using Matrix transform = CreateType1GlyphScreenTransform(pageContext, gs, type1Font, x);
            type1Font.AddGlyphPath(path, glyph.GlyphId, transform);
            x += glyph.DrawAdvance;
        }

        if (path.PointCount > 0)
        {
            FillType1GlyphPath(g, pageContext, gs, fontResource, brush, path);
            TrackTextBounds(pageContext, text, path);
        }
    }

        private static System.Drawing.Color ApplyOpacity(System.Drawing.Color color, float opacity)
        {
            int alpha = (int)MathF.Round(Math.Clamp(color.A * Math.Clamp(opacity, 0f, 1f), 0f, 255f));
            return System.Drawing.Color.FromArgb(alpha, color.R, color.G, color.B);
        }

    private void FillType1GlyphPath(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfFontResource fontResource,
        SolidBrush brush,
        GraphicsPath path)
    {
        GraphicsState saved = g.Save();
        try
        {
            // Browser PDF engines render Type1 fonts through a font rasterizer with
            // stem hinting. GDI+ path filling has no Type1 hinting, so tiny math
            // glyphs can look too light or slightly unstable on the pixel grid.
            // A tiny centered stroke darkens stems without changing PDF advances.
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.CompositingQuality = CompositingQuality.GammaCorrected;
            g.FillPath(brush, path);

            float strokeWidth = GetType1HintStrokeWidthPx(pageContext, gs, fontResource);
            if (strokeWidth > 0f)
            {
                using var pen = new Pen(brush.Color, strokeWidth)
                {
                    LineJoin = LineJoin.Miter,
                    StartCap = LineCap.Flat,
                    EndCap = LineCap.Flat,
                    MiterLimit = 6f
                };
                g.DrawPath(pen, path);
            }
        }
        finally
        {
            g.Restore(saved);
        }
    }

    private float GetType1HintStrokeWidthPx(
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfFontResource fontResource)
    {
        float fontSizePx = MathF.Abs(_state.FontSize * GetTextUnitScalePx(pageContext, gs));
        if (fontSizePx <= 0.01f)
            return 0f;

        // Keep this deliberately tiny. It is only a rasterization aid for Type1
        // formula fonts, not a synthetic bold effect.
        string normalizedName = StripSubsetPrefix(fontResource.BaseFontName);
        bool mathFont = normalizedName.StartsWith("CM", StringComparison.OrdinalIgnoreCase) ||
                        normalizedName.StartsWith("MSBM", StringComparison.OrdinalIgnoreCase);

        return mathFont
            ? Math.Clamp(fontSizePx * 0.0055f, 0.04f, 0.16f)
            : Math.Clamp(fontSizePx * 0.0025f, 0.02f, 0.07f);
    }

    private bool TryCreateCffGlyphPlan(
        PdfFontResource fontResource,
        PdfCffFont cffFont,
        byte[] rawBytes,
        out List<SimpleGlyph> glyphs)
    {
        glyphs = new List<SimpleGlyph>(rawBytes.Length);

        foreach (byte code in rawBytes)
        {
            int glyphId;
            if (fontResource.GlyphNameMap != null &&
                fontResource.GlyphNameMap.TryGetValue(code, out string? glyphName) &&
                cffFont.TryMapGlyphName(glyphName, out int mappedGlyphId))
            {
                glyphId = mappedGlyphId;
            }
            else if (!cffFont.TryMapCharacterCode(code, out glyphId))
            {
                return false;
            }

            glyphs.Add(new SimpleGlyph(
                glyphId,
                ComputeAdvanceForCode(fontResource, code),
                ComputeDrawAdvanceForCode(fontResource, code),
                DecodeSingleByteGlyphText(fontResource, code)));
        }

        return glyphs.Count > 0;
    }

    private bool TryCreateIdentityCffGlyphPlan(
        PdfFontResource fontResource,
        PdfCffFont cffFont,
        byte[] rawBytes,
        out List<SimpleGlyph> glyphs)
    {
        glyphs = new List<SimpleGlyph>(Math.Max(1, rawBytes.Length / 2));

        for (int i = 0; i < rawBytes.Length; i += 2)
        {
            int code = rawBytes[i] << 8;
            if (i + 1 < rawBytes.Length)
                code |= rawBytes[i + 1];

            if (!cffFont.TryMapCharacterCode(code, out int glyphId))
                return false;

            glyphs.Add(new SimpleGlyph(
                glyphId,
                ComputeAdvanceForCode(fontResource, code),
                ComputeDrawAdvanceForCode(fontResource, code),
                DecodeIdentityGlyphText(fontResource, code)));
        }

        return glyphs.Count > 0;
    }

    private bool TryCreateType1GlyphPlan(
        PdfFontResource fontResource,
        PdfType1Font type1Font,
        byte[] rawBytes,
        out List<SimpleGlyph> glyphs)
    {
        glyphs = new List<SimpleGlyph>(rawBytes.Length);

        if (fontResource.GlyphNameMap == null || fontResource.GlyphNameMap.Count == 0)
            return false;

        foreach (byte code in rawBytes)
        {
            if (!fontResource.GlyphNameMap.TryGetValue(code, out string? glyphName) ||
                string.IsNullOrWhiteSpace(glyphName) ||
                !type1Font.TryMapGlyphName(glyphName, out int glyphId))
            {
                return false;
            }

            glyphs.Add(new SimpleGlyph(
                glyphId,
                ComputeAdvanceForCode(fontResource, code),
                ComputeDrawAdvanceForCode(fontResource, code),
                DecodeSingleByteGlyphText(fontResource, code)));
        }

        return glyphs.Count > 0;
    }

    private bool TryCreateSimpleGlyphPlan(
        PdfFontResource fontResource,
        PdfTrueTypeFont glyphFont,
        string text,
        byte[] rawBytes,
        out List<SimpleGlyph> glyphs)
    {
        glyphs = new List<SimpleGlyph>(rawBytes.Length);

        if (fontResource.EncodingMap != null)
        {
            foreach (byte code in rawBytes)
            {
                if (!glyphFont.TryMapCharacterCode(code, out int glyphId))
                    return false;

                glyphs.Add(new SimpleGlyph(
                    glyphId,
                    ComputeAdvanceForCode(fontResource, code),
                    ComputeDrawAdvanceForCode(fontResource, code),
                    DecodeSingleByteGlyphText(fontResource, code)));
            }

            return glyphs.Count > 0;
        }

        if (fontResource.ToUnicodeMap != null)
            return false;

        int mappedUnicode = 0;
        int runeCount = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            runeCount++;
            if (!glyphFont.TryMapUnicode(rune.Value, out int glyphId))
                continue;

            mappedUnicode++;
            glyphs.Add(new SimpleGlyph(
                glyphId,
                ComputeAdvanceForCode(fontResource, rune.Value),
                ComputeDrawAdvanceForCode(fontResource, rune.Value),
                rune.ToString()));
        }

        return runeCount > 0 && mappedUnicode == runeCount;
    }

    private bool TryCreateIdentityGlyphPlan(
        PdfFontResource fontResource,
        byte[] rawBytes,
        out List<SimpleGlyph> glyphs)
    {
        glyphs = new List<SimpleGlyph>(Math.Max(1, rawBytes.Length / 2));

        for (int i = 0; i < rawBytes.Length; i += 2)
        {
            int code = rawBytes[i] << 8;
            if (i + 1 < rawBytes.Length)
                code |= rawBytes[i + 1];

            glyphs.Add(new SimpleGlyph(
                code,
                ComputeAdvanceForCode(fontResource, code),
                ComputeDrawAdvanceForCode(fontResource, code),
                DecodeIdentityGlyphText(fontResource, code)));
        }

        return glyphs.Count > 0;
    }

    private void RenderCidGlyphString(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfFontResource fontResource,
        PdfTrueTypeFont glyphFont,
        SolidBrush brush,
        string text)
    {
        using var path = new GraphicsPath(FillMode.Winding);
        float x = 0f;
        foreach (char ch in text)
        {
            int glyphId = ch;
            using Matrix transform = CreateGlyphScreenTransform(pageContext, gs, glyphFont, x);
            glyphFont.AddGlyphPath(path, glyphId, transform);
            x += (fontResource.GetGlyphWidth(glyphId) / 1000f) * _state.FontSize;
            x += _state.CharSpacing;
            if (ch == ' ')
                x += _state.WordSpacing;
        }

        g.FillPath(brush, path);
        TrackTextBounds(pageContext, text, path);
    }

    private void RenderWholeString(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        Font font,
        SolidBrush brush,
        string text)
    {
        PointF originUser = ApplyMatrix(_state.TextMatrix, 0f, _state.Rise);
        PointF dirUser = ApplyMatrix(_state.TextMatrix, 1f, _state.Rise);

        PointF originPage = ApplyMatrix(gs.Ctm, originUser.X, originUser.Y);
        PointF dirPage = ApplyMatrix(gs.Ctm, dirUser.X, dirUser.Y);

        PointF originScreen = UserToScreen(pageContext, originPage);
        PointF dirScreen = UserToScreen(pageContext, dirPage);

        float angle = (float)(Math.Atan2(
            dirScreen.Y - originScreen.Y,
            dirScreen.X - originScreen.X) * 180.0 / Math.PI);

        float scaleX = _state.HorizontalScale / 100f;
        float ascentPx = GetFontAscentPx(font);

        GraphicsState saved = g.Save();
        try
        {
            g.TranslateTransform(originScreen.X, originScreen.Y);
            g.RotateTransform(angle);

            if (Math.Abs(scaleX - 1f) > 0.0001f)
                g.ScaleTransform(scaleX, 1f);

            g.DrawString(
                text,
                font,
                brush,
                new PointF(0f, -ascentPx),
                _typographicFormat);
        }
        finally
        {
            g.Restore(saved);
        }

        TrackTextBounds(
            pageContext,
            text,
            CreateRotatedTextBounds(
                originScreen,
                angle,
                scaleX,
                1f,
                0f,
                -ascentPx,
                g.MeasureString(text, font, PointF.Empty, _typographicFormat).Width,
                font.GetHeight(g)));
    }

    private float RenderMeasuredWholeString(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        Font font,
        SolidBrush brush,
        string text)
    {
        RenderWholeString(g, pageContext, gs, font, brush, text);

        float measuredWidthPx = g.MeasureString(text, font, PointF.Empty, _typographicFormat).Width;
        float textUnitScalePx = GetTextUnitScalePx(pageContext, gs);
        float scaleX = _state.HorizontalScale / 100f;
        if (textUnitScalePx <= 0.001f)
            return 0f;

        float glyphAdvance = (measuredWidthPx * scaleX) / textUnitScalePx;
        int characterCount = text.Length;
        int wordCount = text.Count(ch => ch == ' ');
        return glyphAdvance + (_state.CharSpacing * characterCount + _state.WordSpacing * wordCount) * scaleX;
    }

    private float RenderPerCharacter(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfFontResource fontResource,
        Font font,
        SolidBrush brush,
        string text)
    {
        float scaleX = _state.HorizontalScale / 100f;
        float ascentPx = GetFontAscentPx(font);
        float textUnitScalePx = GetTextUnitScalePx(pageContext, gs);
        bool adjustGlyphWidth = fontResource.Widths != null || fontResource.CidWidths != null;
        bool useMeasuredAdvanceFallback = ShouldUseMeasuredAdvanceFallback(fontResource);
        RectangleF? trackedBounds = null;
        float totalAdvance = 0f;

        foreach (char ch in text)
        {
            PointF originUser = ApplyMatrix(_state.TextMatrix, 0f, _state.Rise);
            PointF dirUser = ApplyMatrix(_state.TextMatrix, 1f, _state.Rise);

            PointF originPage = ApplyMatrix(gs.Ctm, originUser.X, originUser.Y);
            PointF dirPage = ApplyMatrix(gs.Ctm, dirUser.X, dirUser.Y);

            PointF originScreen = UserToScreen(pageContext, originPage);
            PointF dirScreen = UserToScreen(pageContext, dirPage);

            float angle = (float)(Math.Atan2(
                dirScreen.Y - originScreen.Y,
                dirScreen.X - originScreen.X) * 180.0 / Math.PI);
            float glyphScaleAdjust = adjustGlyphWidth
                ? ComputeFallbackGlyphScaleAdjust(g, pageContext, gs, fontResource, font, ch)
                : 1f;
            float glyphWidthPx = g.MeasureString(ch.ToString(), font, PointF.Empty, _typographicFormat).Width;

            GraphicsState saved = g.Save();
            try
            {
                g.TranslateTransform(originScreen.X, originScreen.Y);
                g.RotateTransform(angle);

                if (Math.Abs(scaleX - 1f) > 0.0001f)
                    g.ScaleTransform(scaleX, 1f);

                if (Math.Abs(glyphScaleAdjust - 1f) > 0.0001f)
                    g.ScaleTransform(glyphScaleAdjust, 1f);

                g.DrawString(
                    ch.ToString(),
                    font,
                    brush,
                    new PointF(0f, -ascentPx),
                    _typographicFormat);
            }
            finally
            {
                g.Restore(saved);
            }

            RectangleF charBounds = CreateRotatedTextBounds(
                originScreen,
                angle,
                scaleX,
                glyphScaleAdjust,
                0f,
                -ascentPx,
                glyphWidthPx,
                font.GetHeight(g));
            trackedBounds = trackedBounds.HasValue
                ? RectangleF.Union(trackedBounds.Value, charBounds)
                : charBounds;

            float advance = useMeasuredAdvanceFallback && !char.IsWhiteSpace(ch)
                ? ComputeMeasuredAdvance(glyphWidthPx, glyphScaleAdjust, textUnitScalePx, scaleX, ch)
                : ComputeAdvance(fontResource, ch.ToString());
            totalAdvance += advance;
            _state.TranslateTextMatrix(advance, 0f);
        }

        // Внешний ShowText уже передвинет матрицу на всю строку.
        // Чтобы не было двойного сдвига, возвращаем матрицу назад.
        _state.TranslateTextMatrix(-totalAdvance, 0f);

        if (trackedBounds.HasValue)
            TrackTextBounds(pageContext, text, trackedBounds.Value);

        return totalAdvance;
    }

    private static void TrackTextBounds(PdfPageContext pageContext, string text, GraphicsPath path)
    {
        if (string.IsNullOrWhiteSpace(text) || path.PointCount == 0)
            return;

        pageContext.ObjectCollector?.AddText(path.GetBounds(), text);
    }

    private static void TrackTextBounds(PdfPageContext pageContext, string text, RectangleF bounds)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        pageContext.ObjectCollector?.AddText(bounds, text);
    }

    private static RectangleF CreateRotatedTextBounds(
        PointF origin,
        float angleDegrees,
        float scaleX,
        float extraScaleX,
        float localX,
        float localY,
        float width,
        float height)
    {
        float effectiveScaleX = scaleX * extraScaleX;
        PointF p1 = TransformLocalTextPoint(origin, angleDegrees, effectiveScaleX, localX, localY);
        PointF p2 = TransformLocalTextPoint(origin, angleDegrees, effectiveScaleX, localX + width, localY);
        PointF p3 = TransformLocalTextPoint(origin, angleDegrees, effectiveScaleX, localX + width, localY + height);
        PointF p4 = TransformLocalTextPoint(origin, angleDegrees, effectiveScaleX, localX, localY + height);

        float left = MathF.Min(MathF.Min(p1.X, p2.X), MathF.Min(p3.X, p4.X));
        float top = MathF.Min(MathF.Min(p1.Y, p2.Y), MathF.Min(p3.Y, p4.Y));
        float right = MathF.Max(MathF.Max(p1.X, p2.X), MathF.Max(p3.X, p4.X));
        float bottom = MathF.Max(MathF.Max(p1.Y, p2.Y), MathF.Max(p3.Y, p4.Y));
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static PointF TransformLocalTextPoint(
        PointF origin,
        float angleDegrees,
        float scaleX,
        float x,
        float y)
    {
        float radians = angleDegrees * MathF.PI / 180f;
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        float scaledX = x * scaleX;
        return new PointF(
            origin.X + scaledX * cos - y * sin,
            origin.Y + scaledX * sin + y * cos);
    }

    private float ComputeFallbackGlyphScaleAdjust(
        DrawingGraphics g,
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfFontResource fontResource,
        Font font,
        char ch)
    {
        if (char.IsWhiteSpace(ch))
            return 1f;

        float pdfWidth = fontResource.GetGlyphWidth(ch);
        if (pdfWidth <= 0.01f)
            return 1f;

        float targetWidthPx = (pdfWidth / 1000f) * _state.FontSize * GetTextUnitScalePx(pageContext, gs);
        if (targetWidthPx <= 0.01f)
            return 1f;

        float measuredWidthPx = g.MeasureString(ch.ToString(), font, PointF.Empty, _typographicFormat).Width;
        if (measuredWidthPx <= 0.01f)
            return 1f;

        return Math.Clamp(targetWidthPx / measuredWidthPx, 0.55f, 1.75f);
    }

    private bool ShouldUseMeasuredAdvanceFallback(PdfFontResource fontResource)
    {
        if (!fontResource.IsIdentityH)
            return false;

        return fontResource.CidWidths == null || fontResource.CidWidths.Count == 0;
    }

    private bool ShouldUseMeasuredWholeStringFallback(PdfFontResource fontResource)
    {
        if (!fontResource.IsIdentityH)
            return false;

        bool hasCidWidths = fontResource.CidWidths != null && fontResource.CidWidths.Count > 0;
        bool hasSimpleWidths = fontResource.Widths != null && fontResource.Widths.Length > 0;
        return !hasCidWidths && !hasSimpleWidths;
    }

    private float ComputeMeasuredAdvance(
        float measuredWidthPx,
        float glyphScaleAdjust,
        float textUnitScalePx,
        float scaleX,
        char ch)
    {
        float glyphAdvance = textUnitScalePx > 0.001f
            ? (measuredWidthPx * glyphScaleAdjust * scaleX) / textUnitScalePx
            : 0f;
        float wordSpacing = ch == ' ' ? _state.WordSpacing : 0f;
        return glyphAdvance + (_state.CharSpacing + wordSpacing) * scaleX;
    }

    private float ComputeAdvance(PdfFontResource fontResource, string text)
    {
        float total = 0f;

        foreach (char ch in text)
        {
            float glyphWidth = fontResource.GetGlyphWidth(ch);
            float wordSpacing = ch == ' ' ? _state.WordSpacing : 0f;

            total +=
                ((glyphWidth / 1000f) * _state.FontSize
                 + _state.CharSpacing
                 + wordSpacing)
                * (_state.HorizontalScale / 100f);
        }

        return total;
    }

    private float ComputeAdvance(IReadOnlyList<SimpleGlyph> glyphs)
    {
        float total = 0f;
        foreach (SimpleGlyph glyph in glyphs)
            total += glyph.TextAdvance;
        return total;
    }

    private float ComputeAdvanceForCode(PdfFontResource fontResource, int code)
    {
        float glyphWidth = fontResource.GetGlyphWidth(code);
        float wordSpacing = IsSpaceCode(fontResource, code) ? _state.WordSpacing : 0f;

        return
            ((glyphWidth / 1000f) * _state.FontSize
             + _state.CharSpacing
             + wordSpacing)
            * (_state.HorizontalScale / 100f);
    }

    private float ComputeDrawAdvanceForCode(PdfFontResource fontResource, int code)
    {
        float glyphWidth = fontResource.GetGlyphWidth(code);
        float wordSpacing = IsSpaceCode(fontResource, code) ? _state.WordSpacing : 0f;

        return (glyphWidth / 1000f) * _state.FontSize
             + _state.CharSpacing
             + wordSpacing;
    }

    private static string DecodeSingleByteGlyphText(PdfFontResource fontResource, int code)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = (byte)(code & 0xFF);
        return fontResource.DecodeTextBytes(bytes.ToArray());
    }

    private static string DecodeIdentityGlyphText(PdfFontResource fontResource, int code)
    {
        if (fontResource.ToUnicodeMap != null &&
            fontResource.ToUnicodeMap.TryGetValue(code, out string? text) &&
            !string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (code >= 0 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF))
            return char.ConvertFromUtf32(code);

        return string.Empty;
    }

    private static bool IsSpaceCode(PdfFontResource fontResource, int code)
    {
        if (code == ' ')
            return true;

        return fontResource.EncodingMap != null &&
               fontResource.EncodingMap.TryGetValue(code, out string? mappedText) &&
               mappedText == " ";
    }

    private float ComputeTrueTypeAdvance(PdfTrueTypeFont glyphFont, string text)
    {
        float total = 0f;

        foreach (char ch in text)
        {
            float glyphWidth = glyphFont.GetAdvanceWidth(ch);
            float wordSpacing = ch == ' ' ? _state.WordSpacing : 0f;

            total +=
                ((glyphWidth / glyphFont.UnitsPerEm) * _state.FontSize
                 + _state.CharSpacing
                 + wordSpacing)
                * (_state.HorizontalScale / 100f);
        }

        return total;
    }

    private static Font ResolveFont(PdfFontResource fontResource, float sizePx)
    {
        string normalizedName = StripSubsetPrefix(fontResource.BaseFontName);
        FontStyle style = normalizedName switch
        {
            "Helvetica-Bold" => FontStyle.Bold,
            "Times-Bold" => FontStyle.Bold,
            "Courier-Bold" => FontStyle.Bold,
            _ when normalizedName.StartsWith("CMMI", StringComparison.OrdinalIgnoreCase) => FontStyle.Italic,
            _ when normalizedName.Contains("Bold", StringComparison.OrdinalIgnoreCase) => FontStyle.Bold,
            _ when normalizedName.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
                   normalizedName.Contains("Oblique", StringComparison.OrdinalIgnoreCase) => FontStyle.Italic,
            _ => FontStyle.Regular
        };

        Font? embeddedFont = TryResolveEmbeddedFont(fontResource, sizePx, style);
        if (embeddedFont != null)
            return embeddedFont;

        string family = normalizedName switch
        {
            _ when IsComputerModernMathFont(normalizedName) => "Cambria Math",
            _ when normalizedName.StartsWith("TimesNewRoman", StringComparison.OrdinalIgnoreCase) => "Times New Roman",
            _ when normalizedName.StartsWith("Arial", StringComparison.OrdinalIgnoreCase) => "Arial",
            _ when normalizedName.StartsWith("CourierNew", StringComparison.OrdinalIgnoreCase) => "Courier New",
            _ when normalizedName.StartsWith("CMR", StringComparison.OrdinalIgnoreCase) => "Times New Roman",
            _ when normalizedName.StartsWith("CMSS", StringComparison.OrdinalIgnoreCase) => "Arial",
            _ when normalizedName.StartsWith("CMTT", StringComparison.OrdinalIgnoreCase) => "Courier New",
            "Helvetica" => "Arial",
            "Helvetica-Bold" => "Arial",
            "Times-Roman" => "Times New Roman",
            "Times-Bold" => "Times New Roman",
            "Courier" => "Courier New",
            "Courier-Bold" => "Courier New",
            _ when normalizedName.Contains("Classic", StringComparison.OrdinalIgnoreCase) => "Palatino Linotype",
            _ when normalizedName.Contains("Switzer", StringComparison.OrdinalIgnoreCase) => "Arial",
            _ when normalizedName.Contains("Serif", StringComparison.OrdinalIgnoreCase) => "Times New Roman",
            _ when normalizedName.Contains("Mono", StringComparison.OrdinalIgnoreCase) => "Courier New",
            _ => "Arial"
        };

        return CreateFont(family, Math.Max(1f, sizePx), style);
    }

    private static Font? TryResolveEmbeddedFont(PdfFontResource fontResource, float sizePx, FontStyle style)
    {
        if (fontResource.FontFileBytes == null || fontResource.FontFileBytes.Length == 0)
            return null;

        if (string.Equals(fontResource.FontFileSubtype, "/Type1C", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fontResource.FontFileSubtype, "/CIDFontType0C", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fontResource.FontFileSubtype, "/Type1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            EmbeddedFontEntry entry = GetEmbeddedFontEntry(fontResource);
            if (entry.Collection.Families.Length == 0)
                return null;

            FontFamily family = entry.Collection.Families[0];
            FontStyle resolvedStyle = ResolveAvailableStyle(family, style);
            return new Font(family, Math.Max(1f, sizePx), resolvedStyle, GraphicsUnit.Pixel);
        }
        catch
        {
            return null;
        }
    }

    private static Font CreateFont(string familyName, float sizePx, FontStyle style)
    {
        using FontFamily family = new(familyName);
        FontStyle resolvedStyle = ResolveAvailableStyle(family, style);
        return new Font(familyName, sizePx, resolvedStyle, GraphicsUnit.Pixel);
    }

    private static FontStyle ResolveAvailableStyle(FontFamily family, FontStyle preferred)
    {
        if (family.IsStyleAvailable(preferred))
            return preferred;

        if (preferred.HasFlag(FontStyle.Bold) && family.IsStyleAvailable(FontStyle.Bold))
            return FontStyle.Bold;

        if (preferred.HasFlag(FontStyle.Italic) && family.IsStyleAvailable(FontStyle.Italic))
            return FontStyle.Italic;

        return family.IsStyleAvailable(FontStyle.Regular)
            ? FontStyle.Regular
            : preferred;
    }

    private static string StripSubsetPrefix(string fontName)
    {
        int plus = fontName.IndexOf('+');
        return plus >= 0 && plus + 1 < fontName.Length ? fontName[(plus + 1)..] : fontName;
    }

    private static readonly object EmbeddedFontsLock = new();
    private static readonly Dictionary<string, EmbeddedFontEntry> EmbeddedFonts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PdfTrueTypeFont> TrueTypeGlyphFonts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PdfCffFont> CffGlyphFonts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PdfType1Font> Type1GlyphFonts = new(StringComparer.Ordinal);

    private static EmbeddedFontEntry GetEmbeddedFontEntry(PdfFontResource fontResource)
    {
        byte[] bytes = fontResource.FontFileBytes ?? Array.Empty<byte>();
        string key = GetFontBytesKey(fontResource);

        lock (EmbeddedFontsLock)
        {
            if (!EmbeddedFonts.TryGetValue(key, out EmbeddedFontEntry? entry))
            {
                entry = new EmbeddedFontEntry(bytes);
                EmbeddedFonts[key] = entry;
            }

            return entry;
        }
    }

    private static PdfTrueTypeFont? TryResolveTrueTypeGlyphFont(PdfFontResource fontResource)
    {
        if (fontResource.FontFileBytes == null || fontResource.FontFileBytes.Length == 0)
            return null;

        if (string.Equals(fontResource.FontFileSubtype, "/Type1C", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fontResource.FontFileSubtype, "/CIDFontType0C", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fontResource.FontFileSubtype, "/Type1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string key = GetFontBytesKey(fontResource);

        lock (EmbeddedFontsLock)
        {
            if (TrueTypeGlyphFonts.TryGetValue(key, out PdfTrueTypeFont? cached))
                return cached;

            if (!PdfTrueTypeFont.TryCreate(fontResource.FontFileBytes, out PdfTrueTypeFont? created) || created == null)
                return null;

            TrueTypeGlyphFonts[key] = created;
            return created;
        }
    }

    private static PdfCffFont? TryResolveCffGlyphFont(PdfFontResource fontResource)
    {
        if (fontResource.FontFileBytes == null || fontResource.FontFileBytes.Length == 0)
            return null;

        if (!string.Equals(fontResource.FontFileSubtype, "/Type1C", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fontResource.FontFileSubtype, "/CIDFontType0C", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string key = GetFontBytesKey(fontResource);

        lock (EmbeddedFontsLock)
        {
            if (CffGlyphFonts.TryGetValue(key, out PdfCffFont? cached))
                return cached;

            if (!PdfCffFont.TryCreate(fontResource.FontFileBytes, out PdfCffFont? created) || created == null)
                return null;

            CffGlyphFonts[key] = created;
            return created;
        }
    }

    private static PdfType1Font? TryResolveType1GlyphFont(PdfFontResource fontResource)
    {
        if (fontResource.FontFileBytes == null || fontResource.FontFileBytes.Length == 0)
            return null;

        if (!string.Equals(fontResource.FontFileSubtype, "/Type1", StringComparison.OrdinalIgnoreCase))
            return null;

        string key = GetFontBytesKey(fontResource);

        lock (EmbeddedFontsLock)
        {
            if (Type1GlyphFonts.TryGetValue(key, out PdfType1Font? cached))
                return cached;

            if (!PdfType1Font.TryCreate(fontResource.FontFileBytes, out PdfType1Font? created) || created == null)
                return null;

            Type1GlyphFonts[key] = created;
            return created;
        }
    }

    private static string GetFontBytesKey(PdfFontResource fontResource)
    {
        byte[] bytes = fontResource.FontFileBytes ?? Array.Empty<byte>();
        return fontResource.BaseFontName + ":" + Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool IsComputerModernMathFont(string normalizedName)
    {
        return normalizedName.StartsWith("CMMI", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.StartsWith("CMSY", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.StartsWith("CMEX", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.StartsWith("MSBM", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseType1GlyphRendering(PdfFontResource fontResource)
    {
        if (!string.Equals(fontResource.FontFileSubtype, "/Type1", StringComparison.OrdinalIgnoreCase))
            return false;

        return fontResource.GlyphNameMap != null && fontResource.GlyphNameMap.Count > 0;
    }

    private sealed class EmbeddedFontEntry
    {
        private readonly GCHandle _fontHandle;

        public EmbeddedFontEntry(byte[] fontBytes)
        {
            _fontHandle = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
            Collection = new PrivateFontCollection();
            try
            {
                Collection.AddMemoryFont(_fontHandle.AddrOfPinnedObject(), fontBytes.Length);
            }
            catch
            {
                Collection.Dispose();
                _fontHandle.Free();
                throw;
            }
        }

        public PrivateFontCollection Collection { get; }
    }

    private static float GetFontAscentPx(Font font)
    {
        FontStyle style = font.Style;
        FontFamily family = font.FontFamily;

        int ascent = family.GetCellAscent(style);
        int em = family.GetEmHeight(style);

        if (em <= 0)
            return font.Size;

        return font.Size * ascent / em;
    }

    private static StringFormat CreateTypographicFormat()
    {
        var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces
                       | StringFormatFlags.NoClip
                       | StringFormatFlags.NoWrap;
        sf.Trimming = StringTrimming.None;
        return sf;
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

    private Matrix CreateGlyphScreenTransform(
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfTrueTypeFont glyphFont,
        float textX)
    {
        PointF originScreen = TransformTextPointToScreen(pageContext, gs, 0f, _state.Rise);
        PointF xUnitScreen = TransformTextPointToScreen(pageContext, gs, 1f, _state.Rise);
        PointF yUnitScreen = TransformTextPointToScreen(pageContext, gs, 0f, _state.Rise + 1f);

        float hScale = _state.HorizontalScale / 100f;
        float fontScale = _state.FontSize / glyphFont.UnitsPerEm;

        PointF xAxis = new(xUnitScreen.X - originScreen.X, xUnitScreen.Y - originScreen.Y);
        PointF yAxis = new(yUnitScreen.X - originScreen.X, yUnitScreen.Y - originScreen.Y);

        float offsetX = originScreen.X + xAxis.X * textX * hScale;
        float offsetY = originScreen.Y + xAxis.Y * textX * hScale;

        return new Matrix(
            xAxis.X * fontScale * hScale,
            xAxis.Y * fontScale * hScale,
            yAxis.X * fontScale,
            yAxis.Y * fontScale,
            offsetX,
            offsetY);
    }

    private Matrix CreateCffGlyphScreenTransform(
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfCffFont glyphFont,
        float textX)
    {
        PointF originScreen = TransformTextPointToScreen(pageContext, gs, 0f, _state.Rise);
        PointF xUnitScreen = TransformTextPointToScreen(pageContext, gs, 1f, _state.Rise);
        PointF yUnitScreen = TransformTextPointToScreen(pageContext, gs, 0f, _state.Rise + 1f);

        float hScale = _state.HorizontalScale / 100f;
        PointF xAxis = new(xUnitScreen.X - originScreen.X, xUnitScreen.Y - originScreen.Y);
        PointF yAxis = new(yUnitScreen.X - originScreen.X, yUnitScreen.Y - originScreen.Y);

        float a = _state.FontSize * glyphFont.MatrixA;
        float b = _state.FontSize * glyphFont.MatrixB;
        float c = _state.FontSize * glyphFont.MatrixC;
        float d = _state.FontSize * glyphFont.MatrixD;
        float e = _state.FontSize * glyphFont.MatrixE;
        float f = _state.FontSize * glyphFont.MatrixF;

        float offsetX = originScreen.X + xAxis.X * (textX * hScale + e * hScale) + yAxis.X * f;
        float offsetY = originScreen.Y + xAxis.Y * (textX * hScale + e * hScale) + yAxis.Y * f;

        return new Matrix(
            xAxis.X * a * hScale + yAxis.X * b,
            xAxis.Y * a * hScale + yAxis.Y * b,
            xAxis.X * c * hScale + yAxis.X * d,
            xAxis.Y * c * hScale + yAxis.Y * d,
            offsetX,
            offsetY);
    }

    private Matrix CreateType1GlyphScreenTransform(
        PdfPageContext pageContext,
        PdfGraphicsState gs,
        PdfType1Font glyphFont,
        float textX)
    {
        PointF originScreen = TransformTextPointToScreen(pageContext, gs, 0f, _state.Rise);
        PointF xUnitScreen = TransformTextPointToScreen(pageContext, gs, 1f, _state.Rise);
        PointF yUnitScreen = TransformTextPointToScreen(pageContext, gs, 0f, _state.Rise + 1f);

        float hScale = _state.HorizontalScale / 100f;
        PointF xAxis = new(xUnitScreen.X - originScreen.X, xUnitScreen.Y - originScreen.Y);
        PointF yAxis = new(yUnitScreen.X - originScreen.X, yUnitScreen.Y - originScreen.Y);

        float a = _state.FontSize * glyphFont.MatrixA;
        float b = _state.FontSize * glyphFont.MatrixB;
        float c = _state.FontSize * glyphFont.MatrixC;
        float d = _state.FontSize * glyphFont.MatrixD;
        float e = _state.FontSize * glyphFont.MatrixE;
        float f = _state.FontSize * glyphFont.MatrixF;

        float offsetX = originScreen.X + xAxis.X * (textX * hScale + e * hScale) + yAxis.X * f;
        float offsetY = originScreen.Y + xAxis.Y * (textX * hScale + e * hScale) + yAxis.Y * f;

        return new Matrix(
            xAxis.X * a * hScale + yAxis.X * b,
            xAxis.Y * a * hScale + yAxis.Y * b,
            xAxis.X * c * hScale + yAxis.X * d,
            xAxis.Y * c * hScale + yAxis.Y * d,
            offsetX,
            offsetY);
    }

    private PointF TransformTextPointToScreen(PdfPageContext pageContext, PdfGraphicsState gs, float x, float y)
    {
        PointF user = ApplyMatrix(_state.TextMatrix, x, y);
        PointF page = ApplyMatrix(gs.Ctm, user.X, user.Y);
        return UserToScreen(pageContext, page);
    }

    private float GetTextUnitScalePx(PdfPageContext pageContext, PdfGraphicsState gs)
    {
        PointF originUser = ApplyMatrix(_state.TextMatrix, 0f, _state.Rise);
        PointF dirUser = ApplyMatrix(_state.TextMatrix, 1f, _state.Rise);

        PointF originPage = ApplyMatrix(gs.Ctm, originUser.X, originUser.Y);
        PointF dirPage = ApplyMatrix(gs.Ctm, dirUser.X, dirUser.Y);

        PointF originScreen = UserToScreen(pageContext, originPage);
        PointF dirScreen = UserToScreen(pageContext, dirPage);

        float dx = dirScreen.X - originScreen.X;
        float dy = dirScreen.Y - originScreen.Y;
        float scale = MathF.Sqrt(dx * dx + dy * dy);
        return scale > 0.001f ? scale : pageContext.Zoom;
    }

    public sealed class StateSnapshot
    {
        public string FontResourceName { get; set; } = "/F1";
        public float FontSize { get; set; } = 12f;
        public float CharSpacing { get; set; }
        public float WordSpacing { get; set; }
        public float HorizontalScale { get; set; } = 100f;
        public float Leading { get; set; }
        public float Rise { get; set; }

        public Matrix TextMatrix { get; private set; } = new();
        public Matrix LineMatrix { get; private set; } = new();

        public void BeginText()
        {
            TextMatrix.Dispose();
            LineMatrix.Dispose();
            TextMatrix = new Matrix();
            LineMatrix = new Matrix();
        }

        public void EndText()
        {
        }

        public void SetTextMatrix(float a, float b, float c, float d, float e, float f)
        {
            TextMatrix.Dispose();
            LineMatrix.Dispose();
            TextMatrix = new Matrix(a, b, c, d, e, f);
            LineMatrix = new Matrix(a, b, c, d, e, f);
        }

        public void MoveTextPosition(float tx, float ty)
        {
            using var t = new Matrix(1, 0, 0, 1, tx, ty);
            LineMatrix.Multiply(t, MatrixOrder.Prepend);
            TextMatrix.Dispose();
            TextMatrix = LineMatrix.Clone();
        }

        public void MoveToNextLine() => MoveTextPosition(0f, -Leading);

        public void TranslateTextMatrix(float tx, float ty)
        {
            using var t = new Matrix(1, 0, 0, 1, tx, ty);
            TextMatrix.Multiply(t, MatrixOrder.Prepend);
        }

        public StateSnapshot Clone()
        {
            var clone = new StateSnapshot
            {
                FontResourceName = FontResourceName,
                FontSize = FontSize,
                CharSpacing = CharSpacing,
                WordSpacing = WordSpacing,
                HorizontalScale = HorizontalScale,
                Leading = Leading,
                Rise = Rise
            };
            clone.TextMatrix.Dispose();
            clone.LineMatrix.Dispose();
            clone.TextMatrix = TextMatrix.Clone();
            clone.LineMatrix = LineMatrix.Clone();
            return clone;
        }

        public void CopyFrom(StateSnapshot other)
        {
            FontResourceName = other.FontResourceName;
            FontSize = other.FontSize;
            CharSpacing = other.CharSpacing;
            WordSpacing = other.WordSpacing;
            HorizontalScale = other.HorizontalScale;
            Leading = other.Leading;
            Rise = other.Rise;

            TextMatrix.Dispose();
            LineMatrix.Dispose();
            TextMatrix = other.TextMatrix.Clone();
            LineMatrix = other.LineMatrix.Clone();
        }
    }

    private readonly record struct SimpleGlyph(int GlyphId, float TextAdvance, float DrawAdvance, string? Content = null);
}
