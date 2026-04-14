using System.Drawing;

namespace PdfCore;

public enum PdfRenderObjectKind
{
    Text,
    Image,
    VectorPath
}

public sealed record PdfRenderObject(
    PdfRenderObjectKind Kind,
    RectangleF Bounds,
    string? Content = null);

public sealed record PdfRenderResult(
    Bitmap Bitmap,
    IReadOnlyList<PdfRenderObject> Objects);

internal sealed class PdfPageObjectCollector
{
    private readonly List<PdfRenderObject> _objects = new();

    public void AddText(RectangleF bounds, string? content)
        => Add(PdfRenderObjectKind.Text, bounds, content);

    public void AddImage(RectangleF bounds, string? content)
        => Add(PdfRenderObjectKind.Image, bounds, content);

    public void AddVectorPath(RectangleF bounds, string? content = null)
        => Add(PdfRenderObjectKind.VectorPath, bounds, content);

    public IReadOnlyList<PdfRenderObject> Snapshot()
    {
        if (_objects.Count == 0)
            return Array.Empty<PdfRenderObject>();

        var indexed = _objects
            .Select((obj, index) => new IndexedRenderObject(index, obj))
            .ToArray();

        IndexedRenderObject[] mergedText = MergeTextObjects(indexed.Where(item => item.Object.Kind == PdfRenderObjectKind.Text).ToArray()).ToArray();
        IndexedRenderObject[] mergedVector = MergeVectorObjects(indexed.Where(item => item.Object.Kind == PdfRenderObjectKind.VectorPath).ToArray()).ToArray();
        IndexedRenderObject[] mergedHover = MergeHoverObjects(mergedText.Concat(mergedVector).OrderBy(item => item.Index).ToArray()).ToArray();

        return mergedHover
            .Concat(indexed.Where(item => item.Object.Kind == PdfRenderObjectKind.Image))
            .OrderBy(item => item.Index)
            .Select(item => item.Object)
            .ToArray();
    }

    internal IReadOnlyList<PdfRenderObject> SnapshotRaw()
        => _objects.ToArray();

    private void Add(PdfRenderObjectKind kind, RectangleF bounds, string? content)
    {
        RectangleF normalized = Normalize(bounds);
        normalized = EnsureMinimumExtent(
            normalized,
            kind == PdfRenderObjectKind.VectorPath ? 3f : 1f,
            kind == PdfRenderObjectKind.VectorPath ? 3f : 1f);

        if (normalized.Width <= 0f || normalized.Height <= 0f)
            return;

        _objects.Add(new PdfRenderObject(kind, normalized, content));
    }

    private static RectangleF Normalize(RectangleF bounds)
    {
        float x1 = Math.Min(bounds.Left, bounds.Right);
        float y1 = Math.Min(bounds.Top, bounds.Bottom);
        float x2 = Math.Max(bounds.Left, bounds.Right);
        float y2 = Math.Max(bounds.Top, bounds.Bottom);
        return RectangleF.FromLTRB(x1, y1, x2, y2);
    }

    private static IReadOnlyList<IndexedRenderObject> MergeTextObjects(IReadOnlyList<IndexedRenderObject> objects)
    {
        if (objects.Count <= 1)
            return objects.ToArray();

        return BuildTextLineGroups(objects)
            .Select(CreateMergedTextObject)
            .ToArray();
    }

    private static IReadOnlyList<IndexedRenderObject> MergeVectorObjects(IReadOnlyList<IndexedRenderObject> objects)
    {
        if (objects.Count <= 1)
            return objects.ToArray();

        IndexedRenderObject[] primitiveGroups = MergeConnectedComponents(
            objects,
            ShouldMergeVectorPrimitive,
            CreateMergedVectorObject).ToArray();

        IndexedRenderObject[] textLikeGroups = primitiveGroups
            .Where(item => LooksLikeTextLikeVectorBlock(item.Object.Bounds))
            .ToArray();
        IndexedRenderObject[] graphicGroups = primitiveGroups
            .Where(item => !LooksLikeTextLikeVectorBlock(item.Object.Bounds))
            .ToArray();

        IndexedRenderObject[] mergedTextLike = MergeConnectedComponents(
            textLikeGroups,
            ShouldMergeTextLikeVectorBlocks,
            CreateMergedVectorObject).ToArray();

        IndexedRenderObject[] mergedGraphics = MergeConnectedComponents(
            graphicGroups,
            ShouldMergeGraphicVectorBlocks,
            CreateMergedVectorObject).ToArray();

        return mergedTextLike
            .Concat(mergedGraphics)
            .OrderBy(item => item.Index)
            .ToArray();
    }

    private static IReadOnlyList<IndexedRenderObject> MergeHoverObjects(IReadOnlyList<IndexedRenderObject> objects)
    {
        return MergeConnectedComponents(
            objects,
            ShouldMergeHoverObjects,
            CreateMergedHoverObject);
    }

    private static IReadOnlyList<IndexedRenderObject> MergeConnectedComponents(
        IReadOnlyList<IndexedRenderObject> objects,
        Func<PdfRenderObject, PdfRenderObject, bool> shouldMerge,
        Func<IReadOnlyList<IndexedRenderObject>, IndexedRenderObject> createMerged)
    {
        if (objects.Count <= 1)
            return objects.ToArray();

        var dsu = new DisjointSet(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            for (int j = i + 1; j < objects.Count; j++)
            {
                if (shouldMerge(objects[i].Object, objects[j].Object))
                    dsu.Union(i, j);
            }
        }

        var groups = new Dictionary<int, List<IndexedRenderObject>>();
        for (int i = 0; i < objects.Count; i++)
        {
            int root = dsu.Find(i);
            if (!groups.TryGetValue(root, out List<IndexedRenderObject>? group))
            {
                group = new List<IndexedRenderObject>();
                groups[root] = group;
            }

            group.Add(objects[i]);
        }

        return groups.Values
            .Select(createMerged)
            .OrderBy(item => item.Index)
            .ToArray();
    }

    private static IReadOnlyList<List<IndexedRenderObject>> BuildTextLineGroups(IReadOnlyList<IndexedRenderObject> objects)
    {
        var groups = new List<TextLineGroup>();
        foreach (IndexedRenderObject item in objects.OrderBy(item => item.Object.Bounds.Top).ThenBy(item => item.Object.Bounds.Left))
        {
            TextLineGroup? bestGroup = null;
            float bestScore = float.MaxValue;

            foreach (TextLineGroup group in groups)
            {
                if (!BelongsToSameTextLine(group.Bounds, item.Object.Bounds))
                    continue;

                float score = Math.Abs(GetCenter(group.Bounds, vertical: true) - GetCenter(item.Object.Bounds, vertical: true));
                if (score < bestScore)
                {
                    bestScore = score;
                    bestGroup = group;
                }
            }

            if (bestGroup == null)
            {
                bestGroup = new TextLineGroup(item);
                groups.Add(bestGroup);
                continue;
            }

            bestGroup.Add(item);
        }

        return groups
            .OrderBy(group => group.Bounds.Top)
            .ThenBy(group => group.Bounds.Left)
            .Select(group => group.Items)
            .ToArray();
    }

    private static IndexedRenderObject CreateMergedTextObject(IReadOnlyList<IndexedRenderObject> group)
    {
        string content = BuildMergedTextContent(group, preserveLineBreaks: false);

        return new IndexedRenderObject(
            group.Min(item => item.Index),
            new PdfRenderObject(
                PdfRenderObjectKind.Text,
                UnionBounds(group.Select(item => item.Object.Bounds)),
                string.IsNullOrWhiteSpace(content) ? null : content));
    }

    private static IndexedRenderObject CreateMergedHoverObject(IReadOnlyList<IndexedRenderObject> group)
    {
        PdfRenderObjectKind kind = group.Any(item => item.Object.Kind == PdfRenderObjectKind.Text)
            ? PdfRenderObjectKind.Text
            : PdfRenderObjectKind.VectorPath;

        string content = BuildMergedTextContent(
            group.Where(item => item.Object.Kind == PdfRenderObjectKind.Text).ToArray(),
            preserveLineBreaks: true);

        return new IndexedRenderObject(
            group.Min(item => item.Index),
            new PdfRenderObject(
                kind,
                UnionBounds(group.Select(item => item.Object.Bounds)),
                string.IsNullOrWhiteSpace(content) ? null : content));
    }

    private static IndexedRenderObject CreateMergedVectorObject(IReadOnlyList<IndexedRenderObject> group)
    {
        return new IndexedRenderObject(
            group.Min(item => item.Index),
            new PdfRenderObject(
                PdfRenderObjectKind.VectorPath,
                UnionBounds(group.Select(item => item.Object.Bounds)),
                null));
    }

    private static string BuildMergedTextContent(IReadOnlyList<IndexedRenderObject> group, bool preserveLineBreaks)
    {
        if (group.Count == 0)
            return string.Empty;

        var ordered = group
            .Where(item => !string.IsNullOrWhiteSpace(item.Object.Content))
            .OrderBy(item => item.Object.Bounds.Top)
            .ThenBy(item => item.Object.Bounds.Left)
            .ToArray();
        if (ordered.Length == 0)
            return string.Empty;

        var lines = new List<List<IndexedRenderObject>>();
        foreach (IndexedRenderObject item in ordered)
        {
            List<IndexedRenderObject>? bestLine = null;
            float bestDistance = float.MaxValue;

            foreach (List<IndexedRenderObject> line in lines)
            {
                RectangleF lineBounds = UnionBounds(line.Select(x => x.Object.Bounds));
                if (!BelongsToSameTextLine(lineBounds, item.Object.Bounds))
                    continue;

                float distance = Math.Abs(GetCenter(lineBounds, vertical: true) - GetCenter(item.Object.Bounds, vertical: true));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestLine = line;
                }
            }

            if (bestLine == null)
            {
                bestLine = new List<IndexedRenderObject>();
                lines.Add(bestLine);
            }

            bestLine.Add(item);
        }

        var parts = new List<string>(lines.Count);
        foreach (List<IndexedRenderObject> line in lines.OrderBy(line => UnionBounds(line.Select(x => x.Object.Bounds)).Top))
        {
            var sb = new System.Text.StringBuilder();
            IndexedRenderObject? previous = null;
            foreach (IndexedRenderObject current in line.OrderBy(x => x.Object.Bounds.Left))
            {
                string text = current.Object.Content!.Trim();
                if (text.Length == 0)
                    continue;

                if (previous != null)
                {
                    float gap = current.Object.Bounds.Left - previous.Object.Bounds.Right;
                    float minHeight = Math.Max(1f, Math.Min(previous.Object.Bounds.Height, current.Object.Bounds.Height));
                    float spaceThreshold = Math.Max(2.5f, minHeight * 0.22f);
                    if (gap > spaceThreshold &&
                        !NeedsTightJoin(previous.Object.Content) &&
                        !NeedsTightJoin(text))
                    {
                        sb.Append(' ');
                    }
                }

                sb.Append(text);
                previous = current;
            }

            if (sb.Length > 0)
                parts.Add(sb.ToString());
        }

        return preserveLineBreaks
            ? string.Join("\n", parts)
            : string.Join(" ", parts);
    }

    private static bool NeedsTightJoin(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        char ch = text.Trim()[0];
        return char.IsPunctuation(ch) ||
               ch is '+' or '-' or '=' or '/' or '*' or '^' or '_' or ')' or '(' or '[' or ']' or '{' or '}';
    }

    private static bool BelongsToSameTextLine(RectangleF existingBounds, RectangleF candidateBounds)
    {
        float minHeight = Math.Max(1f, Math.Min(existingBounds.Height, candidateBounds.Height));
        float maxHeight = Math.Max(existingBounds.Height, candidateBounds.Height);
        float verticalOverlap = GetAxisOverlap(existingBounds.Top, existingBounds.Bottom, candidateBounds.Top, candidateBounds.Bottom);
        float centerYDelta = Math.Abs(GetCenter(existingBounds, vertical: true) - GetCenter(candidateBounds, vertical: true));

        if (verticalOverlap >= minHeight * 0.12f)
            return true;

        return centerYDelta <= Math.Max(10f, maxHeight * 0.85f);
    }

    private static bool ShouldMergeTextBlocks(PdfRenderObject left, PdfRenderObject right)
    {
        RectangleF a = left.Bounds;
        RectangleF b = right.Bounds;

        float minHeight = Math.Max(1f, Math.Min(a.Height, b.Height));
        float minWidth = Math.Max(1f, Math.Min(a.Width, b.Width));
        float maxHeight = Math.Max(a.Height, b.Height);
        float verticalGap = GetAxisGap(a.Top, a.Bottom, b.Top, b.Bottom);
        float horizontalGap = GetAxisGap(a.Left, a.Right, b.Left, b.Right);
        float verticalOverlap = GetAxisOverlap(a.Top, a.Bottom, b.Top, b.Bottom);
        float horizontalOverlap = GetAxisOverlap(a.Left, a.Right, b.Left, b.Right);
        RectangleF union = UnionBounds(new[] { a, b });

        bool sameLine =
            verticalOverlap >= minHeight * 0.35f &&
            horizontalGap <= Math.Max(26f, minHeight * 3.1f);
        if (sameLine)
            return true;

        bool bothCompact =
            Math.Max(a.Width, b.Width) <= 900f &&
            Math.Max(a.Height, b.Height) <= 220f;

        bool sameParagraphBlock =
            verticalGap <= Math.Max(18f, minHeight * 1.45f) &&
            union.Width <= 1450f &&
            union.Height <= 980f &&
            (
                horizontalOverlap >= minWidth * 0.18f ||
                Math.Abs(a.Left - b.Left) <= Math.Max(18f, minHeight * 1.15f) ||
                Math.Abs(a.Right - b.Right) <= Math.Max(26f, minHeight * 1.55f) ||
                IsHorizontallyContained(a, b)
            );
        if (sameParagraphBlock)
            return true;

        bool centeredFormulaLines =
            bothCompact &&
            verticalGap <= Math.Max(20f, minHeight * 1.35f) &&
            Math.Abs(GetCenter(a, vertical: false) - GetCenter(b, vertical: false)) <= Math.Max(90f, minWidth * 1.4f) &&
            union.Height <= Math.Max(300f, maxHeight * 6f) &&
            union.Width <= 1100f;
        if (centeredFormulaLines)
            return true;

        bool looksLikeFormulaCluster =
            bothCompact &&
            horizontalOverlap >= minWidth * 0.08f &&
            verticalGap <= Math.Max(14f, minHeight * 1.05f) &&
            Math.Max(a.Width, b.Width) <= Math.Max(a.Height, b.Height) * 22f &&
            union.Height <= Math.Max(260f, maxHeight * 5f);

        return looksLikeFormulaCluster;
    }

    private static bool ShouldMergeHoverObjects(PdfRenderObject left, PdfRenderObject right)
    {
        if (left.Kind == PdfRenderObjectKind.Image || right.Kind == PdfRenderObjectKind.Image)
            return false;

        if (left.Kind == PdfRenderObjectKind.Text && right.Kind == PdfRenderObjectKind.Text)
            return ShouldMergeTextBlocks(left, right);

        if (left.Kind == PdfRenderObjectKind.VectorPath && right.Kind == PdfRenderObjectKind.VectorPath)
        {
            if (LooksLikeTextLikeVectorBlock(left.Bounds) && LooksLikeTextLikeVectorBlock(right.Bounds))
                return ShouldMergeTextLikeVectorBlocks(left, right);

            return ShouldMergeGraphicVectorBlocks(left, right);
        }

        PdfRenderObject text = left.Kind == PdfRenderObjectKind.Text ? left : right;
        PdfRenderObject vector = left.Kind == PdfRenderObjectKind.VectorPath ? left : right;
        return ShouldMergeTextAndVector(text, vector);
    }

    private static bool ShouldMergeVectorPrimitive(PdfRenderObject left, PdfRenderObject right)
    {
        RectangleF a = left.Bounds;
        RectangleF b = right.Bounds;

        float gapX = GetAxisGap(a.Left, a.Right, b.Left, b.Right);
        float gapY = GetAxisGap(a.Top, a.Bottom, b.Top, b.Bottom);
        float minHeight = Math.Max(1f, Math.Min(a.Height, b.Height));
        float minWidth = Math.Max(1f, Math.Min(a.Width, b.Width));
        float horizontalOverlap = GetAxisOverlap(a.Left, a.Right, b.Left, b.Right);
        float verticalOverlap = GetAxisOverlap(a.Top, a.Bottom, b.Top, b.Bottom);
        RectangleF union = UnionBounds(new[] { a, b });
        float tolerance = Math.Clamp(minHeight * 0.55f, 2f, 8f);

        bool overlappingShapes =
            gapX == 0f &&
            gapY == 0f &&
            union.Width <= Math.Max(a.Width, b.Width) + 24f &&
            union.Height <= Math.Max(a.Height, b.Height) + 24f;
        if (overlappingShapes)
            return true;

        bool sameLineCluster =
            verticalOverlap >= minHeight * 0.30f &&
            gapX <= Math.Max(10f, minHeight * 1.6f) &&
            union.Width <= 320f &&
            union.Height <= Math.Max(90f, Math.Max(a.Height, b.Height) * 2.2f);
        if (sameLineCluster)
            return true;

        bool compactStackedCluster =
            horizontalOverlap >= minWidth * 0.25f &&
            gapY <= Math.Max(8f, minHeight * 1.1f) &&
            union.Width <= 240f &&
            union.Height <= 140f;
        if (compactStackedCluster)
            return true;

        RectangleF expandedA = a;
        expandedA.Inflate(tolerance, tolerance);
        if (!expandedA.IntersectsWith(b))
            return false;

        return union.Width <= Math.Max(a.Width, b.Width) + 36f &&
               union.Height <= Math.Max(a.Height, b.Height) + 36f;
    }

    private static bool ShouldMergeTextLikeVectorBlocks(PdfRenderObject left, PdfRenderObject right)
    {
        RectangleF a = left.Bounds;
        RectangleF b = right.Bounds;

        float minHeight = Math.Max(1f, Math.Min(a.Height, b.Height));
        float minWidth = Math.Max(1f, Math.Min(a.Width, b.Width));
        float maxHeight = Math.Max(a.Height, b.Height);
        float verticalGap = GetAxisGap(a.Top, a.Bottom, b.Top, b.Bottom);
        float horizontalGap = GetAxisGap(a.Left, a.Right, b.Left, b.Right);
        float verticalOverlap = GetAxisOverlap(a.Top, a.Bottom, b.Top, b.Bottom);
        float horizontalOverlap = GetAxisOverlap(a.Left, a.Right, b.Left, b.Right);
        RectangleF union = UnionBounds(new[] { a, b });

        bool sameLine =
            verticalOverlap >= minHeight * 0.32f &&
            horizontalGap <= Math.Max(12f, minHeight * 1.3f) &&
            union.Width <= Math.Max(1400f, Math.Max(a.Width, b.Width) * 1.55f) &&
            union.Height <= Math.Max(120f, maxHeight * 2.4f);
        if (sameLine)
            return true;

        bool centeredFormulaLines =
            union.Width <= 1200f &&
            union.Height <= Math.Max(360f, maxHeight * 7f) &&
            verticalGap <= Math.Max(24f, minHeight * 1.55f) &&
            Math.Abs(GetCenter(a, vertical: false) - GetCenter(b, vertical: false)) <= Math.Max(120f, minWidth * 1.9f);
        if (centeredFormulaLines)
            return true;

        bool stackedTextBlock =
            horizontalOverlap >= minWidth * 0.06f &&
            verticalGap <= Math.Max(18f, minHeight * 1.35f) &&
            union.Width <= 1350f &&
            union.Height <= 900f;

        return stackedTextBlock;
    }

    private static bool ShouldMergeGraphicVectorBlocks(PdfRenderObject left, PdfRenderObject right)
    {
        RectangleF a = left.Bounds;
        RectangleF b = right.Bounds;

        float gapX = GetAxisGap(a.Left, a.Right, b.Left, b.Right);
        float gapY = GetAxisGap(a.Top, a.Bottom, b.Top, b.Bottom);
        float minHeight = Math.Max(1f, Math.Min(a.Height, b.Height));
        float minWidth = Math.Max(1f, Math.Min(a.Width, b.Width));
        float horizontalOverlap = GetAxisOverlap(a.Left, a.Right, b.Left, b.Right);
        float verticalOverlap = GetAxisOverlap(a.Top, a.Bottom, b.Top, b.Bottom);
        RectangleF union = UnionBounds(new[] { a, b });

        bool chartOrFrameCluster =
            union.Width <= 980f &&
            union.Height <= 980f &&
            (
                (horizontalOverlap >= minWidth * 0.12f && gapY <= Math.Max(18f, minHeight * 1.6f)) ||
                (verticalOverlap >= minHeight * 0.12f && gapX <= Math.Max(18f, minHeight * 1.6f)) ||
                (gapX <= 10f && gapY <= 10f)
            );
        if (chartOrFrameCluster)
            return true;

        RectangleF expandedA = a;
        expandedA.Inflate(6f, 6f);
        return expandedA.IntersectsWith(b) &&
               union.Width <= Math.Max(a.Width, b.Width) + 60f &&
               union.Height <= Math.Max(a.Height, b.Height) + 60f;
    }

    private static bool ShouldMergeTextAndVector(PdfRenderObject text, PdfRenderObject vector)
    {
        RectangleF a = text.Bounds;
        RectangleF b = vector.Bounds;

        float minHeight = Math.Max(1f, Math.Min(a.Height, b.Height));
        float minWidth = Math.Max(1f, Math.Min(a.Width, b.Width));
        float maxHeight = Math.Max(a.Height, b.Height);
        float horizontalGap = GetAxisGap(a.Left, a.Right, b.Left, b.Right);
        float verticalGap = GetAxisGap(a.Top, a.Bottom, b.Top, b.Bottom);
        float horizontalOverlap = GetAxisOverlap(a.Left, a.Right, b.Left, b.Right);
        float verticalOverlap = GetAxisOverlap(a.Top, a.Bottom, b.Top, b.Bottom);
        RectangleF union = UnionBounds(new[] { a, b });
        float vectorMaxExtent = Math.Max(b.Width, b.Height);
        bool compactText =
            a.Width <= 900f &&
            a.Height <= 220f;
        bool compactUnion =
            union.Width <= 1100f &&
            union.Height <= 320f;
        bool formulaishText = LooksFormulaLike(text.Content);
        bool shortText =
            !string.IsNullOrWhiteSpace(text.Content) &&
            text.Content!.Trim().Length <= 96;

        if ((!compactText && !formulaishText) || b.Width > 950f || b.Height > 420f)
            return false;

        bool inlineFormulaCluster =
            verticalOverlap >= minHeight * 0.28f &&
            horizontalGap <= Math.Max(18f, minHeight * 2.0f) &&
            compactUnion &&
            vectorMaxExtent <= 420f &&
            (formulaishText || shortText);
        if (inlineFormulaCluster)
            return true;

        bool looksLikeStackedFormula =
            horizontalOverlap >= minWidth * 0.08f &&
            verticalGap <= Math.Max(18f, minHeight * 1.35f) &&
            union.Height <= Math.Max(280f, maxHeight * 6f) &&
            compactUnion &&
            vectorMaxExtent <= 420f &&
            (formulaishText || shortText);
        if (looksLikeStackedFormula)
            return true;

        bool thinVectorNearText =
            IsThinVector(b) &&
            vectorMaxExtent <= 420f &&
            compactUnion &&
            ((horizontalOverlap > 0f && verticalGap <= Math.Max(12f, minHeight * 1.8f)) ||
             (verticalOverlap > 0f && horizontalGap <= Math.Max(12f, minHeight * 1.8f)));
        if (thinVectorNearText)
            return true;

        bool textWrappedByCompactVector =
            compactUnion &&
            shortText &&
            vectorMaxExtent <= 420f &&
            union.Height <= Math.Max(280f, maxHeight * 6f) &&
            IsTextInsideVectorBlock(a, b);
        if (textWrappedByCompactVector)
            return true;

        RectangleF expandedText = a;
        expandedText.Inflate(Math.Max(4f, minHeight * 0.30f), Math.Max(4f, minHeight * 0.30f));
        if (expandedText.IntersectsWith(b) &&
            compactUnion &&
            vectorMaxExtent <= 320f &&
            union.Height <= Math.Max(220f, maxHeight * 5f) &&
            (formulaishText || shortText))
        {
            return true;
        }

        PointF textCenter = new(GetCenter(a, vertical: false), GetCenter(a, vertical: true));
        PointF vectorCenter = new(GetCenter(b, vertical: false), GetCenter(b, vertical: true));
        return compactUnion &&
               vectorMaxExtent <= 320f &&
               (formulaishText || shortText) &&
               (a.Contains(vectorCenter) || b.Contains(textCenter));
    }

    private static bool IsTextInsideVectorBlock(RectangleF textBounds, RectangleF vectorBounds)
    {
        RectangleF expandedVector = vectorBounds;
        expandedVector.Inflate(36f, 20f);
        if (!expandedVector.Contains(GetCenter(textBounds, vertical: false), GetCenter(textBounds, vertical: true)))
            return false;

        return textBounds.Left >= expandedVector.Left - 4f &&
               textBounds.Right <= expandedVector.Right + 4f &&
               textBounds.Top >= expandedVector.Top - 12f &&
               textBounds.Bottom <= expandedVector.Bottom + 12f;
    }

    private static bool LooksLikeTextLikeVectorBlock(RectangleF bounds)
    {
        float width = bounds.Width;
        float height = bounds.Height;
        float maxExtent = Math.Max(width, height);
        float minExtent = Math.Max(1f, Math.Min(width, height));
        float aspect = maxExtent / minExtent;

        if (width <= 0f || height <= 0f)
            return false;

        if (height <= 6f)
            return width <= 260f;

        if (width <= 26f && height <= 26f)
            return false;

        if (height <= 120f && aspect >= 2.2f && width <= 1500f)
            return true;

        if (height <= 180f && width <= 700f && aspect >= 1.35f)
            return true;

        return false;
    }

    private static RectangleF UnionBounds(IEnumerable<RectangleF> bounds)
    {
        using IEnumerator<RectangleF> enumerator = bounds.GetEnumerator();
        if (!enumerator.MoveNext())
            return RectangleF.Empty;

        RectangleF result = enumerator.Current;
        while (enumerator.MoveNext())
            result = RectangleF.Union(result, enumerator.Current);

        return Normalize(result);
    }

    private static RectangleF EnsureMinimumExtent(RectangleF bounds, float minWidth, float minHeight)
    {
        float width = bounds.Width;
        float height = bounds.Height;
        float left = bounds.Left;
        float top = bounds.Top;
        float right = bounds.Right;
        float bottom = bounds.Bottom;

        if (width < minWidth)
        {
            float pad = (minWidth - width) / 2f;
            left -= pad;
            right += pad;
        }

        if (height < minHeight)
        {
            float pad = (minHeight - height) / 2f;
            top -= pad;
            bottom += pad;
        }

        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static float GetAxisOverlap(float a1, float a2, float b1, float b2)
        => Math.Max(0f, Math.Min(a2, b2) - Math.Max(a1, b1));

    private static float GetAxisGap(float a1, float a2, float b1, float b2)
    {
        if (GetAxisOverlap(a1, a2, b1, b2) > 0f)
            return 0f;

        if (a2 < b1)
            return b1 - a2;

        return a1 - b2;
    }

    private static float GetCenter(RectangleF bounds, bool vertical)
        => vertical
            ? bounds.Top + (bounds.Height / 2f)
            : bounds.Left + (bounds.Width / 2f);

    private static bool IsThinVector(RectangleF bounds)
    {
        float minExtent = Math.Min(bounds.Width, bounds.Height);
        float maxExtent = Math.Max(bounds.Width, bounds.Height);
        return minExtent <= 8f && maxExtent >= 18f;
    }

    private static bool IsHorizontallyContained(RectangleF a, RectangleF b)
    {
        RectangleF expandedA = a;
        expandedA.Inflate(16f, 0f);
        RectangleF expandedB = b;
        expandedB.Inflate(16f, 0f);

        bool aContainsB =
            expandedA.Left <= b.Left &&
            expandedA.Right >= b.Right;
        bool bContainsA =
            expandedB.Left <= a.Left &&
            expandedB.Right >= a.Right;

        return aContainsB || bContainsA;
    }

    private static bool LooksFormulaLike(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length == 0 || span.Length > 80)
            return false;

        const string formulaMarkers = "=+-/*()[]{}<>^_|Δ∑Σ√∫≈≤≥·×÷ρβθαπµΩ∂ƒ∇∝∞≠±∈∉∪∩";
        int markerCount = 0;
        int digitCount = 0;
        int letterCount = 0;

        foreach (char ch in span)
        {
            if (formulaMarkers.IndexOf(ch) >= 0)
            {
                markerCount++;
                continue;
            }

            if (char.IsDigit(ch))
            {
                digitCount++;
                continue;
            }

            if (char.IsLetter(ch))
                letterCount++;
        }

        if (markerCount >= 2)
            return true;

        if (markerCount >= 1 && digitCount >= 1)
            return true;

        return letterCount <= 8 && digitCount <= 8 && markerCount >= 1;
    }

    private sealed record IndexedRenderObject(int Index, PdfRenderObject Object);

    private sealed class TextLineGroup
    {
        public TextLineGroup(IndexedRenderObject first)
        {
            Items = new List<IndexedRenderObject> { first };
            Bounds = first.Object.Bounds;
        }

        public List<IndexedRenderObject> Items { get; }

        public RectangleF Bounds { get; private set; }

        public void Add(IndexedRenderObject item)
        {
            Items.Add(item);
            Bounds = RectangleF.Union(Bounds, item.Object.Bounds);
        }
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public DisjointSet(int size)
        {
            _parent = Enumerable.Range(0, size).ToArray();
            _rank = new int[size];
        }

        public int Find(int value)
        {
            if (_parent[value] != value)
                _parent[value] = Find(_parent[value]);

            return _parent[value];
        }

        public void Union(int left, int right)
        {
            int rootLeft = Find(left);
            int rootRight = Find(right);
            if (rootLeft == rootRight)
                return;

            if (_rank[rootLeft] < _rank[rootRight])
            {
                _parent[rootLeft] = rootRight;
                return;
            }

            if (_rank[rootLeft] > _rank[rootRight])
            {
                _parent[rootRight] = rootLeft;
                return;
            }

            _parent[rootRight] = rootLeft;
            _rank[rootLeft]++;
        }
    }
}
