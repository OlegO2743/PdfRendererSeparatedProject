# poradnik2013.pdf - current issue log

This file tracks page-specific rendering defects for:

- `C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf`

The goal is to keep a stable checklist so we do not re-diagnose the same pages after context compaction or a paused session.

## Legend

- `visual` - what the user sees in our renderer
- `resource` - what the PDF page contains
- `status` - current diagnosis state
- `next` - next technical step

---

## Page 1

- `visual`: page renders with the expected blue cover artwork; the old blank / gray embossed JPX failure mode is gone.
- `resource`:
  - `/Im0` -> `/JPXDecode`, `642x906`, `ICCBased(n=3, alt=DeviceRGB)`
- `status`:
  - image resource is definitely present
  - superseded checkpoint note: the old blank-page blocker and the old gray / embossed JPX failure mode are both solved
  - the decisive fix was the inverse 9/7 low/high normalization correction in `Synthesize97(...)`
  - fresh isolated-image render of `/Im0` is visually close to the Pillow reference decode from the raw JPX payload
  - later diagnostics showed that the isolated internal decode and the Pillow decode are effectively identical, so the remaining tiny hue mismatch was not a JPX-core issue
  - live viewer parity improved further after adding a display-profile transform in the WinForms presentation layer
  - user validation after that viewer change: the page is now visually "1:1" and hard to distinguish from the browser by eye
  - the old “blank page” blocker is solved
- `next`:
  - keep this page as the primary high-signal regression page for both JPX and viewer display-profile parity
  - only revisit if live app output diverges again after future viewer/render changes

## Page 145

- `visual`: page now renders correctly, including product image.
- `resource`: page uses ordinary image content that our renderer can decode.
- `status`: working baseline page for comparison.
- `next`: use as a control/reference page.

## Page 141

- `visual`:
  - the user-reported large product image used to look too contrasty and lose
    inner fan detail
  - in fresh diagnostic renders the page is now visually close to the browser
    reference
- `resource`:
  - `/Im0` -> `/JPXDecode`, `168x144`, grayscale (`comps=1`), reversible `5/3`
  - `/Im1` -> `/DCTDecode`, `498x327`, ICCBased
- `status`:
  - isolated-image diagnostics showed that the harsh look came from `/Im0`
    itself, not from page composition
  - the remaining issue was fixed by normalizing Tier-1 reconstructed
    coefficients by `0.5` before using them as real wavelet coefficients
  - the refreshed isolated `/Im0` render regains the darker inner fan detail
    instead of clipping it into near-black
  - fresh page render is now treated as visually OK
- `next`:
  - keep as a regression page for reversible single-component JPX

## Page 159

- `visual`: large central product image renders correctly in fresh diagnostic output; graphs also render correctly.
- `resource`:
  - `/Im0` -> `/JPXDecode`, `196x141`, `Indexed(ICCBased(n=3, alt=DeviceRGB), high=249)`
- `status`:
  - page is currently visually OK in fresh diagnostic renders
  - keep it as a regression page because it previously failed and because it exercises `JPXDecode + Indexed`
- `next`:
  - keep under regression watch after later JPX changes

## Page 177

- `visual`:
  - in the live app screenshot the large left product image was reported missing
  - in fresh diagnostic renders the large left product image is present and visually acceptable
  - lower-right product/control image renders correctly
  - earlier builds had spacing/encoding drift / broken Polish glyphs
- `resource`:
  - `/Im0` -> `414 0 obj`, `/JPXDecode`, `175x201`, indexed via `680 0 R`
  - `/Im1` -> `415 0 obj`, `/FlateDecode`, `97x97`, indexed via `682 0 R`
- `status`:
  - saved diagnostic PNG for `/Im0` exists, so the resource is present and decodes into an image object
  - page-level clean render also shows the large product image, so this is no longer treated as a hard missing-resource case
  - after the `HH` context fix, `/Im0` looks materially cleaner than the earlier noisy / degraded state
  - `/Im1` saves and renders correctly as a raw indexed Flate image
  - after adding CID-keyed CFF charset mapping, the Polish text on this page renders correctly in the fresh diagnostic output
- `next`:
  - keep under regression watch; revisit only if later JPX work regresses `/Im0`
  - verify live app parity only if a rebuilt viewer still diverges from the clean renderer output

## Page 178

- `visual`:
  - user screenshot reported the top-left graph as missing
  - in the fresh diagnostic render both top graphs, the icon strip, and the lower product image are present
- `resource`:
  - `/Im0` -> `420 0 obj`, `/FlateDecode`, `165x162`, indexed via `687 0 R`
  - `/Im1` -> `421 0 obj`, `/DCTDecode`
  - `/Im2` -> `422 0 obj`, `/DCTDecode`
- `status`:
  - saved diagnostic PNG for `/Im0` is correct
  - page-level object inspection shows `/Im0`, `/Im1`, `/Im2` all have bounds in the final render object list
  - fresh page render also shows all three assets, so this page is currently treated as visually OK in the renderer core
- `next`:
  - keep as a regression-check page after later image / text fixes

## Page 182

- `visual`:
  - icon clusters are present in fresh page renders
  - the mid-left product image now renders correctly enough for the current checkpoint
  - earlier builds had slight text drift and broken Polish glyphs
- `resource`:
  - `/Im0` -> `441 0 obj`, `/FlateDecode`, `64x54`, indexed via `703 0 R`
  - `/Im1` -> `442 0 obj`, `/FlateDecode`, `136x54`, indexed via `706 0 R`
  - `/Im2` -> `443 0 obj`, `/JPXDecode`, `189x66`, ICCBased
  - `/Im3` -> `444 0 obj`, `/FlateDecode`, `209x54`, indexed via `707 0 R`
  - `/Im4` -> `445 0 obj`, `/FlateDecode`, `209x54`, indexed via `704 0 R`
  - `/Im5` -> `446 0 obj`, `/DCTDecode`, `197x151`, ICCBased
- `status`:
  - saved diagnostic PNGs for `/Im0`, `/Im1`, `/Im3`, `/Im4`, `/Im5` are correct
  - `/Im2` (`JPXDecode`, ICCBased) was the smallest focused repro for the remaining 3-component 9/7 issue
  - after fixing reversed low/high normalization in `Synthesize97(...)`, the isolated-image render of `/Im2` is visually close to the Pillow reference decode from the raw JPX payload
  - after additionally normalizing Tier-1 reconstructed coefficients by `0.5`, `/Im2` is no longer visibly over-contrasty
  - page-level object inspection shows `/Im0` ... `/Im5` all have bounds on the page
  - fresh page render also shows the icon clusters, so the renderer core currently treats them as present
  - after adding CID-keyed CFF charset mapping, the page text renders correctly in the fresh diagnostic output
  - this page is no longer an active JPX blocker for the current checkpoint
- `next`:
  - keep `/Im2` as a compact regression repro for future 3-component 9/7 work
  - verify live app parity only if a rebuilt viewer still diverges from the clean renderer output

## Page 183

- `visual`:
  - user screenshot reported three small icon tiles between product sections as missing
  - earlier builds had lower-block text drift / broken glyphs
- `resource`:
  - `/Im0` -> `449 0 obj`, `/DCTDecode`, `198x501`, ICCBased
  - `/Im1` -> `450 0 obj`, `/FlateDecode`, `209x54`, indexed via `710 0 R`
  - `/Im2` -> `451 0 obj`, `/FlateDecode`, `209x54`, indexed via `565 0 R`
- `status`:
  - saved diagnostic PNGs for `/Im1` and `/Im2` are correct
  - saved diagnostic PNG for `/Im0` is also correct and contains both large product photos
  - `inspectobjects` confirms all three image bounds are present on the page
  - fresh page render also shows the icon tiles and both product photos
  - after adding CID-keyed CFF charset mapping, the text on this page renders correctly in the fresh diagnostic output
  - this page is no longer tracked as a core renderer defect unless the live app still diverges after rebuild
- `next`:
  - revisit only if live-app divergence still reproduces after rebuild

## Page 188

- `visual`:
  - the user-reported back cover used to render noticeably too dark / too
    contrasty compared with the browser/PDF viewer
  - after the core JPX fixes the page became much closer in tone to the
    browser reference
  - after the later viewer display-profile change, any remaining mismatch is
    treated as subtle parity tuning rather than a broken decode
- `resource`:
  - `/Im0` -> `/JPXDecode`, `641x906`, ICCBased
- `status`:
  - isolated-image diagnostics confirmed the tonal mismatch already existed in
    `/Im0` before page composition
  - the embedded ICC profile is ordinary `sRGB IEC61966-2.1`, so the remaining
    mismatch was not caused by missing ICC conversion
  - the decisive fix was the Tier-1 coefficient normalization by `0.5`
  - later viewer work added a display-profile transform for page and thumbnail
    presentation, which should reduce any residual live-app hue difference
  - this page is now treated as visually OK in renderer core and as a
    high-value regression page for subtle viewer parity
- `next`:
  - keep as the main regression page for large 3-component irreversible JPX and
    subtle live-app/browser parity checks

---

## Current technical priority

1. Treat page `1` and page `188` as the primary parity pages after the viewer display-profile fix.
2. Treat pages `141`, `159`, `177`, `178`, `182`, `183` as renderer-core regression pages after the JPX coefficient-normalization fix.
3. Re-check live app output versus clean renderer output after each rebuild, so we separate core-render issues from stale-app / UI mismatches.
4. Keep the CID-keyed CFF text fix under regression watch, because it resolved the Polish glyph/text corruption on `177`, `182`, `183`.
5. If future JPX work is needed, start from raw export + Pillow reference decode before changing tier-1 or DWT code again.

## 2026-04-16 restart checkpoint

User explicitly requested a persistent checkpoint so work can resume cleanly after restart.

### Main unresolved issue

- The old page `1` blank / embossed JPX blocker is solved.
- The old page `182 /Im2` bad-JPX blocker is also solved for the current checkpoint.
- The later tonal-contrast mismatch on pages `141`, `182`, `188` is also solved in renderer core.
- A later tiny live-app hue mismatch was largely addressed by a viewer-side display-profile transform.
- If work resumes later, the likely remaining work is subtle live-app parity or new PDFs, not the old poradnik2013 broken JPX imagery.

### Resume order

1. Re-check the rebuilt live app and thumbnail strip against pages `1` and `188` first, because they are the most sensitive parity pages after the display-profile change.
2. Then spot-check pages `141`, `159`, `177`, `178`, `182`, `183` as renderer-core regression pages.
3. If a JPX mismatch reappears, export the raw payload first and compare against Pillow before changing the decoder.
4. If a mismatch exists only in the live app but not in isolated-image/page diagnostics, inspect the viewer display-profile path before touching JPX code.
5. Keep page `178` as a regression page for previously missing graphics.
6. Only treat future issues as core-render regressions after comparing live app output with fresh diagnostic renderer output.
