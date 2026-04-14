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

- `visual`: full-page cover image is missing or renders as blank/garbled cover, depending on build.
- `resource`:
  - `/Im0` -> `/JPXDecode`, `642x906`, `ICCBased(n=3, alt=DeviceRGB)`
- `status`:
  - image resource is definitely present
  - current JPX path decodes it incorrectly; saved diagnostic PNG contains corrupted checkerboard-like output
  - this is not a missing-resource problem
- `next`:
  - stop preferring bad JPX output when an internal decode is possible
  - compare WIC vs internal JPX result for this codestream

## Page 145

- `visual`: page now renders correctly, including product image.
- `resource`: page uses ordinary image content that our renderer can decode.
- `status`: working baseline page for comparison.
- `next`: use as a control/reference page.

## Page 159

- `visual`: large central product image is missing; graphs render.
- `resource`:
  - `/Im0` -> `/JPXDecode`, `196x141`, `Indexed(ICCBased(n=3, alt=DeviceRGB), high=249)`
- `status`:
  - missing image is not caused by absent XObject
  - current code already routes `/JPXDecode` through `Jpeg2000Decoder.Decode(...)` before indexed-palette application
  - so the old diagnosis about raw `CreateIndexedBitmap()` is obsolete
  - page still behaves like a JPX decode / JPX quality problem, not a resource lookup problem
- `next`:
  - prefer the internal JPX decoder whenever its codestream support check passes
  - compare the internal result against the current WIC-based path on this codestream

## Page 177

- `visual`:
  - in the live app screenshot the large left product image was reported missing
  - in a fresh diagnostic render the large left product image is present, but visibly degraded / noisy
  - lower-right product/control image renders correctly
  - earlier builds had spacing/encoding drift / broken Polish glyphs
- `resource`:
  - `/Im0` -> `414 0 obj`, `/JPXDecode`, `175x201`, indexed via `680 0 R`
  - `/Im1` -> `415 0 obj`, `/FlateDecode`, `97x97`, indexed via `682 0 R`
- `status`:
  - saved diagnostic PNG for `/Im0` exists, so the resource is present and decodes into an image object
  - page-level clean render also shows the large product image, so this is no longer treated as a hard missing-resource case
  - `/Im0` is visibly degraded / noisy, so this is tracked as a JPX quality problem rather than a missing-resource problem
  - `/Im1` saves and renders correctly as a raw indexed Flate image
  - after adding CID-keyed CFF charset mapping, the Polish text on this page renders correctly in the fresh diagnostic output
- `next`:
  - improve JPX decode quality for `/Im0`
  - verify why the live app view can still diverge from the clean renderer output

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
  - user screenshot reported top icon cluster missing
  - user screenshot reported lower icon cluster missing
  - mid-left product image has poor quality / wrong appearance
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
  - `/Im2` (`JPXDecode`, ICCBased) decodes to visibly degraded output
  - page-level object inspection shows `/Im0` ... `/Im5` all have bounds on the page
  - fresh page render also shows the icon clusters, so the renderer core currently treats them as present
  - after adding CID-keyed CFF charset mapping, the page text renders correctly in the fresh diagnostic output
  - therefore the remaining confirmed issue here is primarily:
    - JPX quality problem on `/Im2`
- `next`:
  - improve JPX decode quality for `/Im2`
  - verify why the live app view can still diverge from the clean renderer output

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

---

## Current technical priority

1. Fix `JPXDecode + Indexed` / `JPXDecode + ICCBased` quality so pages `1`, `159`, `177`, `182` stop rendering degraded images.
2. Re-check live app output versus clean renderer output after each rebuild, so we separate core-render issues from stale-app / UI mismatches.
3. Keep page `178` as a regression-check page, because its missing graph now reproduces as present in fresh renders.
4. Keep the CID-keyed CFF text fix under regression watch, because it resolved the Polish glyph/text corruption on `177`, `182`, `183`.
