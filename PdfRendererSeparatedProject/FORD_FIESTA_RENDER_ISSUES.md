# Ford Fiesta Render Notes

Updated: 2026-04-14 (Europe/Warsaw)

File under test:

- `C:\Users\Oleg Ogar\Downloads\запчасти форд фиеста сокращенно - Лист1 (4).pdf`

## Observed difference

When comparing the page against browser rendering:

- table lines in our renderer look uniformly dark and heavy,
- browser rendering shows several line tones from light gray to dark gray/black,
- the strongest mismatch is in the upper-left table region, where many thin grid lines should appear optically lighter.

## Working hypothesis

This looks like a thin-line rendering issue rather than a wrong stroke color in the PDF itself.

Current renderer behavior before the fix:

- axis-aligned thin lines were force-upgraded to `1px` black in `SimplePdfRenderer.StrokePath(...)`,
- that made hairline/table strokes lose their original visual weight,
- the result is darker and more uniform than browser/PDFium-style rendering.

## Change applied

File:

- `PdfCore/SimplePdfRenderer.cs`

Adjustment:

- keep the existing thin-line enhancement for axis-aligned strokes,
- but instead of always painting them as fully opaque black `1px`,
- preserve different optical density by modulating alpha from the original effective width.

Implemented helper:

- `ApplyThinLineCoverage(...)`

## Expected effect

- very thin lines remain visible,
- but lighter grid lines should no longer collapse into the same solid black as strong divider lines,
- table rendering should move closer to browser output.

## Next checks

1. Rebuild and inspect the Ford Fiesta PDF again.
2. If lines are still too dark:
   - tune the thin-line coverage curve,
   - or revisit pixel snapping for axis-aligned hairlines.
3. If some lines become too faint:
   - raise the minimum alpha floor,
   - or apply the rule only to widths below a narrower threshold.
