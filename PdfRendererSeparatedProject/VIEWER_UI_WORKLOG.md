# Viewer UI Worklog

Updated: 2026-04-17 (Europe/Warsaw)

## Goal

This file tracks recent WinForms viewer-side changes that are not pure core-render changes:

- page presentation parity versus browser/PDF viewers,
- thumbnail strip behavior,
- text/layout fixes that only become visible in the live app,
- UI cleanup and removal of demo-only features.

Use this file as the main continuation checkpoint for viewer/UI work.
It should be updated after each meaningful UI-facing change, rollback, or newly
confirmed blocker so the next chat can continue without rediscovering context.

## Files most involved recently

- `PdfViewer.WinForms/PdfThumbnailStripControl.cs`
- `PdfViewer.WinForms/PdfPageViewControl.cs`
- `PdfViewer.WinForms/PdfDocumentView.cs`
- `PdfViewer.WinForms/ImageCanvasControl.cs`
- `PdfViewer.WinForms/DisplayColorProfileTransform.cs`
- `PdfViewer.WinForms/DocumentMainForm.cs`
- `PdfViewer.WinForms/MainForm.cs`
- `PdfCore/PdfTextEngine.cs`

## Recent change history

### 1. Viewer display-color parity was improved

The live WinForms viewer now applies a display-stage transform from `sRGB` into
the current monitor profile before drawing already-rendered page bitmaps.

This affects:

- live page view,
- thumbnails,
- image canvas paths that show rendered page content.

Practical result:

- the small residual hue mismatch against the browser became much harder to
  distinguish by eye on the current machine,
- `poradnik2013.pdf` page `1` and page `188` remain the main viewer-parity
  regression pages for this path.

## 2. Text layout in `Ulotka-45202-2022-10-31-1923_D-2022-11-15.pdf` was improved

Viewer-side verification exposed a bad text fallback path for `Identity-H`
fonts without usable width data.

What was changed:

- better measured whole-string fallback was added for the problematic
  `Identity-H` case,
- family mapping for `TimesNewRomanPS*` was corrected so it no longer falls
  back to `Arial`.

Practical result:

- Polish leaflet pages became much closer to the browser in spacing and glyph
  placement,
- this was a text-metrics/viewer validation issue, not a JPX/image issue.

## 3. Demo/test PDF generation was removed

The viewer no longer exposes demo-only PDF creation from the UI.

Removed from the codebase:

- demo menu/buttons from `DocumentMainForm`,
- corresponding legacy/demo UI from `MainForm`,
- demo generation files that created sample PDFs.

Practical result:

- the app is now documented and maintained as a PDF viewer/renderer for real
  files,
- UI surface is smaller and less confusing,
- future work should not assume demo-PDF generation still exists.

## 4. Thumbnail strip was redesigned to react to panel width

The thumbnail strip was changed so that thumbnail size is no longer locked to a
fixed default size.

Implemented:

- a dedicated caption area under each thumbnail,
- larger caption height so page numbers are less likely to clip,
- thumbnail size derived from the current width of the strip,
- relayout on resize,
- eager render requests for visible thumbnail slots.

Expected behavior after this redesign:

- wider strip -> larger thumbnails -> fewer fit on screen,
- narrower strip -> smaller thumbnails -> more fit on screen.

This direction is correct and already visible in the current build.

## 5. Current thumbnail-strip state is still unstable

The thumbnail-strip redesign uncovered a second class of bugs that are still
open as of `2026-04-17`.

Observed regressions:

- while scrolling, page numbers under thumbnails can disappear,
- later thumbnails can stay as empty white placeholders until a forced repaint,
- resizing the strip can fix the view temporarily because it forces a full
  relayout/repaint,
- after changing strip width, the vertical scrollbar range can remain stale and
  stop early, for example around page `4/6` instead of allowing access to the
  end of the document.

Important nuance:

- this is not a core PDF parsing/rendering blocker,
- this is a viewer-side invalidation / scroll-range / paint synchronization
  problem inside `PdfThumbnailStripControl`.

## 6. What has already been attempted in the thumbnail strip

The latest code already contains several stabilizing changes:

- visible thumbnails are requested again during `OnPaint`,
- visible thumbnails are also requested after `OnScroll`,
- `WndProc` reacts to `WM_HSCROLL`, `WM_VSCROLL` and `WM_MOUSEWHEEL` to force
  another render/invalidate pass,
- relayout now calls `AdjustFormScrollbars(true)` and restores scroll position,
- old rendered bitmap is no longer eagerly discarded on every layout change
  before a replacement bitmap is ready.

These changes improved behavior, but did not fully close the issue.

## 7. Current checkpoint

Viewer/UI status on `2026-04-17`:

- page view itself is usable and much closer to browser parity than before,
- display-profile parity work is in a good state,
- demo UI removal is complete,
- thumbnail strip sizing UX is improved,
- thumbnail strip scroll stability is still an active open bug.

## 8. Loading progress UI was moved out of the crowded toolbar

The old loading progress controls lived inside the same `ToolStrip` that already
contained navigation buttons, zoom controls, mode selectors and overlay toggles.
That made the progress state easy to miss or partially hidden.

What was changed:

- the loading status text and progress bar were moved into a dedicated top
  loading strip,
- this strip is shown above the main toolbar only while a document is loading,
- the toolbar itself no longer needs to reserve space for loading widgets.

Practical result:

- loading progress is now much more visible during `Open PDF`,
- the progress UI no longer competes with normal toolbar controls.

## 9. Smooth-scroll page-limit investigation: root cause found, last fix rolled back

Long documents in `Smooth` mode still expose an upper page limit that depends on
zoom.

Current understanding:

- the limit is not primarily caused by rendering memory anymore,
- the deeper issue is the current `Smooth` implementation relying on WinForms
  `AutoScroll` over a very tall virtual document,
- once total document height grows too large, the effective vertical scroll
  range becomes unreliable.

An experimental fix was attempted:

- logical scaling of smooth-scroll Y coordinates,
- keeping actual page content positions separate from the logical scrollbar
  range.

That experiment was **rolled back** in the same session because it introduced:

- duplicated/repeated page fragments on screen,
- noticeably slower scrolling,
- extra CPU load.

So the current checkpoint is:

- root cause is better understood than before,
- the attempted scaling fix should be considered rejected,
- the long-document smooth-scroll limit is still an active open issue.

## 10. Preferred next direction for smooth mode

Do **not** continue layering fixes on top of the current "many child page
controls + giant AutoScroll surface" approach.

Preferred next step:

1. move smooth mode toward the same idea already used by the thumbnail strip,
2. keep page slots and page positions separately from the on-screen controls,
3. render/cache only visible pages and a small neighbor window,
4. draw those pages on a small virtualized drawing surface instead of relying on
   one live WinForms child control for every page in the document.

This should remove both:

- the practical page-count limit in long smooth-scroll sessions,
- the need for risky scrollbar-coordinate hacks.

## 11. Last known good build checkpoints

Successful builds from this session:

- loading-strip UI change:
  - `_bin_build_loading_strip\Debug\net8.0-windows\PdfViewer.WinForms.dll`
- rollback of the broken smooth-scroll scaling experiment:
  - `_bin_build_revert_smooth_scaling\Debug\net8.0-windows\PdfViewer.WinForms.dll`
- rollback of the later broken smooth-scroll resident-page virtualization:
  - `_bin_build_restore_stable_smooth\Debug\net8.0-windows\PdfViewer.WinForms.dll`

Warnings still present and already known:

- `PdfCore/Parsing/SimplePdfParser.cs:1760`
- `PdfCore/Parsing/SimplePdfParser.cs:1777`

## 12. Next viewer-side task

Important update from the same session:

- a follow-up attempt to make smooth mode more aggressive by hiding/releasing
  non-resident page controls also turned out unstable in practice,
- the user observed duplicated page fragments, very slow scrolling and high CPU,
- that follow-up attempt was also rolled back,
- the current code is again on the older visually stable smooth-scroll path.

So the real status is:

- smooth mode is back to the last stable behavior,
- the long-document page-limit problem is still open,
- the next valid attempt should start from a fresh thumbnail-strip-style smooth
  surface, not from incremental hacks over the current page-control layout.

The next focused UI task should be:

1. fully stabilize thumbnail strip repaint during scroll,
2. make page-number captions survive scroll/transform reliably,
3. make scrollbar range always refresh after strip-width changes,
4. replace the current smooth-mode long-document scrolling path with a
   thumbnail-strip-style virtualized page surface.

Until this is done, treat the thumbnail strip as "implemented, but still under
active stabilization".

## 13. Manual-scroll migration checkpoint

User confirmation after the rollback:

- `Smooth` mode was again visually stable,
- but the document still stopped around page `18/19` on `poradnik2013.pdf`,
- so the practical long-document limit clearly remained.

New implementation step completed in the same session:

- `PdfDocumentView` was moved off WinForms `AutoScroll`,
- the viewer now keeps its own `_scrollPosition`,
- internal `VScrollBar` / `HScrollBar` controls are used instead of relying on
  a giant `AutoScrollMinSize`,
- page controls are repositioned from document coordinates to viewport
  coordinates manually,
- viewport calculations for smooth mode and page-by-page mode now use the
  custom viewport size rather than `ClientSize` directly.

Intent of this change:

- remove the old practical scrollbar-range ceiling,
- keep the previously stable page-control rendering path,
- avoid reintroducing the rejected logical-scaling experiment.

Verification status:

- code compiles successfully with:
  - `dotnet build PdfViewer.WinForms.csproj /p:UseAppHost=false`
- output verified:
  - `PdfViewer.WinForms\bin\Debug\net8.0-windows\PdfViewer.WinForms.dll`
- normal `apphost` build/publish was blocked in-session by locked viewer
  executable / intermediate files, so live GUI behavior still needs direct
  runtime confirmation after restarting the viewer from the updated build.

So the current checkpoint is now:

- `AutoScroll`-based smooth scrolling is no longer the active implementation in
  `PdfDocumentView`,
- the intended fix for the page-18/19 ceiling is implemented,
- final status still depends on live manual verification in the running viewer.

## 14. Page-by-page wheel double-step fix

User reported a separate navigation bug:

- in page-by-page mode, one mouse-wheel notch switched not one page but two.

Root cause:

- `PdfPageViewControl` forwarded `MouseWheel` to `PdfDocumentView`,
- but `PdfDocumentView` was already receiving the wheel event itself because it
  takes focus on mouse enter,
- so the same physical wheel action was effectively processed twice.

Fix applied:

- `HandleWheelFrom(...)` in `PdfDocumentView` now skips manual forwarding when
  the document view already contains focus.

Verification:

- `dotnet build PdfViewer.WinForms.csproj /p:UseAppHost=false` succeeded,
- only the already-known `SimplePdfParser.cs` nullable warnings remain,
- the locked running `.exe` warning also remained because the viewer process was
  still open during the build.
