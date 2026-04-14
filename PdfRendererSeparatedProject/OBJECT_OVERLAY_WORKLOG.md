# Object Overlay Worklog

Updated: 2026-04-13 00:10 (Europe/Warsaw)

## Goal

Improve the `Объекты` hover mode so that:

- formulas highlight as whole objects instead of tiny text fragments,
- vector graphics (graphs, frames, lines, charts, schemes) also highlight,
- nearby text fragments are glued into larger line/block objects where it makes sense.

## Files touched

- `PdfCore/PdfRenderObject.cs`
- `PdfViewer.WinForms/ImageCanvasControl.cs`

## What is already done

### 1. Split vector merging into two stages

`PdfRenderObject.cs`

- Added a primitive vector merge stage:
  - `ShouldMergeVectorPrimitive(...)`
- Added a second-stage split:
  - text-like vector blocks: `LooksLikeTextLikeVectorBlock(...)`
  - graphic/vector blocks: everything else
- Added separate merge rules:
  - `ShouldMergeTextLikeVectorBlocks(...)`
  - `ShouldMergeGraphicVectorBlocks(...)`
- Added helper:
  - `CreateMergedVectorObject(...)`

This was done to stop treating all vector paths the same. Formula glyph outlines and chart lines should not be merged with the same heuristics.

### 2. Fixed stale vector/vector hover merge path

`PdfRenderObject.cs`

- In `ShouldMergeHoverObjects(...)`, the `VectorPath + VectorPath` branch no longer tries to call the old removed `ShouldMergeVector(...)`.
- For the hover-merge pass, vector/vector currently returns `false`.

Reason:
- vector objects are already merged earlier,
- a second aggressive vector/vector merge at hover time tends to create oversized regions.

### 3. Improved hover dominance of large vector blocks over tiny inner text fragments

`ImageCanvasControl.cs`

- Added `IsTextWrappedByVectorBlock(...)`
- Extended `IsDominatedFragment(...)` so that small/line-like `Text` objects can be suppressed when they are fully contained inside a much larger `VectorPath` object.

Reason:
- on some PDF pages, visible formulas or outlined text are mostly vector paths,
- but one tiny real text fragment still exists inside,
- hover was selecting that tiny text fragment instead of the whole formula/object.

## Diagnostics already confirmed

### `poradnik2013.pdf` page 10

Commands:

```powershell
dotnet "D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\_diag_bookcheck\bin\Debug\net8.0-windows\_diag_bookcheck.dll" inspectobjects "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 10 zoom=3.29
dotnet "D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\_diag_bookcheck\bin\Debug\net8.0-windows\_diag_bookcheck.dll" inspectrawobjects "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 10 zoom=3.29
```

Findings:

- visible formula/text regions on page 10 are largely stored as `VectorPath`, not normal `Text`,
- there are only a few real text fragments there (for example `sumaryczne zyski ciepła jawnego [kW]`),
- that is why hover previously snapped to only a small gray text box.

Current merged objects on page 10:

- top text/vector block: `0001 VectorPath x=122.22 y=162.85 w=1249.54 h=729.44`
- formula block: `0003 Text x=468.03 y=855.35 w=465.53 h=141.85 text=jawnego\nj`
- definition block: `0004 VectorPath x=122.65 y=1020.52 w=1244.06 h=606.58`
- lower formula/definition block: `0005 VectorPath x=122.35 y=1654.84 w=980.14 h=348.14`
- tiny real line text still present: `0006 Text ... text=j sumaryczne zyski ciepła jawnego [kW]`

Meaning:
- hover now has the data needed to prefer a bigger formula/definition block instead of only the tiny line fragment.

### `poradnik2013.pdf` page 159

Command:

```powershell
dotnet "D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\_diag_bookcheck\bin\Debug\net8.0-windows\_diag_bookcheck.dll" inspectobjects "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 159 zoom=3.29
```

Findings:

- chart blocks are already present as `VectorPath`,
- product photo is present as `Image`.

Important objects:

- chart 1: `0002 VectorPath x=104.96 y=1563.18 w=646.86 h=448.79`
- chart 2: `0014 VectorPath x=104.96 y=173.37 w=782.5 h=622.94`
- image: `0018 Image x=153.44 y=849.77 w=480.99 h=346.37`

Meaning:
- page 159 does already expose hoverable chart/image objects,
- if something still does not highlight in the UI, the problem is likely final hit-testing/selection behavior, not object collection absence.

## Build status

Successful build without apphost:

```powershell
dotnet build D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\PdfRendererSeparatedProject.sln -v:minimal -m:1 /nr:false /p:UseSharedCompilation=false /p:UseAppHost=false
```

Fresh output:

- `PdfViewer.WinForms\bin\Debug\net8.0-windows\PdfViewer.WinForms.dll`
- timestamp seen during last check: `2026-04-13 00:07:09`

`exe` may stay stale if the running viewer process keeps locking `apphost.exe`.

## Remaining work

### Hover/object mode

- visually verify page 10 with the new DLL build,
- if needed, further reduce over-merged text-like vector blocks,
- consider more explicit paragraph/formula grouping rules.

### Path/Stroke/Fill support

- basic vector bounds are already collected through `FillPath(...)` / `StrokePath(...)`,
- next refinement is semantic grouping quality, not raw path collection existence.

### Text gluing

- current text gluing still works mostly by line/block heuristics,
- more work may still be needed for:
  - multi-line formulas,
  - mixed `Text + VectorPath` formulas,
  - pages where visible text is actually glyph outlines.

