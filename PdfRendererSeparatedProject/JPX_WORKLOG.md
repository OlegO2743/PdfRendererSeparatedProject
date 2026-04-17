# JPX / JPEG2000 Worklog

This file tracks confirmed findings and next steps for the internal JPX/JPEG2000 path.
It exists so work can resume without redoing already verified diagnostics.

## Update 2026-04-17 (latest)

### New confirmed findings

#### AD. The tiny remaining blue/hue mismatch in the live WinForms app was a display-stage issue, not a JPX decode issue

After the core JPX fixes were in place, the user still reported a very small
"slightly too blue" live-app mismatch on the cover page.

The decisive diagnostic result was:

- isolated `poradnik2013.pdf` page `1`, `/Im0`
- internal decode output
- Pillow decode of the raw exported `.jpx`

Those two isolated-image outputs were effectively pixel-identical, which means
the remaining hue mismatch was no longer in:

- Tier-1
- dequantization
- inverse `9/7`
- ICC handling of the image XObject itself

Interpretation:

- the core JPX renderer is now the source-truth path
- the remaining user-visible mismatch was happening later, at screen display
  time inside the WinForms viewer

#### AE. Viewer parity was improved by applying the system display profile to page and thumbnail bitmaps before drawing them

The WinForms viewer now applies a display-stage transform from `sRGB` into the
current monitor profile obtained from `GetICMProfile(...)`.

This was wired into:

- page view bitmaps
- centered image canvas bitmaps
- thumbnails

Practical result:

- the user reported that page `1` is now visually "1:1" / extremely difficult
  to distinguish from the browser by eye
- this closes the remaining obvious live-app hue mismatch for the main cover
  regression

Diagnostic note:

- the display transform can be disabled with `PDF_VIEWER_DISABLE_DISPLAY_ICC=1`
  if future debugging needs the raw unadjusted viewer output

#### AB. The remaining "too contrasty" JPX output was not an ICC issue; it came from a missing 0.5 normalization of Tier-1 reconstructed coefficients

Fresh diagnostics for the user-reported pages:

- `poradnik2013.pdf` page `141`, `/Im0`
- `poradnik2013.pdf` page `182`, `/Im2`
- `poradnik2013.pdf` page `188`, `/Im0`

showed that the isolated JPX images themselves were already over-contrasty
before page composition. A temporary `ICC` path was checked, but the embedded
profile on the problematic RGB assets is ordinary `sRGB IEC61966-2.1`, so ICC
conversion was not the source of the large visual mismatch.

The actual remaining defect was that `Jpeg2000Tier1Decoder.DecodeCodeBlock(...)`
reconstructs magnitudes in a midpoint/fixed-point form with one extra `1/2`
bit, while our downstream code was feeding those values directly into:

- reversible `5/3` inverse DWT (`Transform53`)
- irreversible `9/7` dequantization / inverse DWT (`Transform97`)

The decisive fix was to normalize those coefficients by `0.5` before using
them as real coefficient values:

- reversible path: divide stored codeblock coefficients by `2`
- irreversible path: multiply stored codeblock coefficients by `0.5f` before
  applying the quantization step size

Interpretation:

- the old output was effectively using coefficients at about `2x` amplitude
- that explains the remaining blown highlights / crushed shadows after the
  earlier `Synthesize97(...)` fix
- this coefficient normalization fix is real and must stay

#### AC. After the Tier-1 coefficient normalization fix, the active poradnik2013 JPX pages line up much better with the browser reference

Fresh renders after the coefficient normalization fix show:

- page `1`: still good
- page `141`: the large product image regains internal detail and no longer
  looks unnaturally harsh
- page `159`: still correct
- page `177`: still correct / acceptable
- page `182`: the mid-left metallic product image is no longer over-contrasty
- page `183`: still correct
- page `188`: the back cover is much closer in tone to the browser/PDF viewer

This means the current operational checkpoint is now:

- page `1` visible in live page view and thumbnails: solved
- remaining user-reported JPX contrast issue on pages `141/182/188`: solved in
  renderer core
- keep `159/177/183` as regression pages

#### Y. The remaining “emboss / gray” JPX defect was primarily caused by reversed low/high normalization in the inverse 9/7 synthesis

After exporting the raw JPX payloads via `_diag_renderpage image-raw ...` and
decoding them locally with Pillow on this machine, we finally had a trustworthy
reference for:

- `poradnik2013.pdf` page `1`, `/Im0`
- `poradnik2013.pdf` page `182`, `/Im2`

The reference images showed that our decoder was no longer dropping the assets,
but was reconstructing them as a high-frequency-heavy “emboss” version of the
correct picture.

The decisive fix was in
`PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs`,
`Synthesize97(...)`:

- before: low-frequency samples were multiplied by `1/K`, high-frequency
  samples by `K`
- now: low-frequency samples are multiplied by `K`, high-frequency samples by
  `1/K`

Interpretation:

- the previous code was suppressing LL energy while over-emphasizing the
  high-pass bands
- that exactly matches the old gray / edge-only / embossed appearance
- this fix is real and must stay

#### Z. With the low/high normalization fix in place, the operational `poradnik2013` checkpoint is effectively closed

Fresh diagnostic renders after the `Synthesize97(...)` fix show:

- page `1 /Im0`: cover image is restored and visually close to the Pillow
  reference
- page `182 /Im2`: the metallic product image is restored and visually close to
  the Pillow reference
- page `159`: still correct
- page `177`: still correct / acceptable
- page `183`: still correct

Also confirmed in code:

- page view rendering uses `SimplePdfRenderer.RenderWithObjects(...)`
- thumbnail rendering uses `SimplePdfRenderer.Render(...)`

So this JPX fix is shared by:

- the live page view
- the thumbnail strip

and does not live only in the diagnostic path.

#### AA. A raw-image diagnostic path now exists and should be reused if JPX work resumes later

`_diag_renderpage` now supports:

```powershell
dotnet ...\_diag_renderpage.dll image-raw <pdf> <page> <resource> [output]
```

This writes the raw image payload (`.jpx` for `JPXDecode`) without passing
through our decoder, which makes it easy to compare:

- internal decode
- external reference decode (Pillow on this machine)

#### U. The `HH` significance-context mapping was wrong and fixing it materially improved the 3-component 9/7 path

`PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs`,
`GetSignificanceLabelOrient2(...)`, now uses the stricter `HH` context split:

```csharp
if (d == 0)
    return h == 0 && v == 0 ? 0 : h + v == 1 ? 1 : 2;
if (d == 1)
    return h == 0 && v == 0 ? 3 : h + v == 1 ? 4 : 5;
if (d == 2)
    return h == 0 && v == 0 ? 6 : 7;
return 8;
```

This is a real fix, not just a diagnostic toggle:

- `poradnik2013.pdf` page `1` is still not final, but it is much cleaner than the old heavy mosaic / checkerboard state
- page `159` remains correct
- page `177` remains visually acceptable and no worse than before
- page `183` remains correct

Interpretation:

- the previous `HH` context grouping was materially corrupting the irreversible JPX decode
- do **not** revert this `HH` context fix

#### V. The remaining JPX repro is no longer “multi-tile only”; page `182` isolates it to a smaller single-tile codestream

Fresh isolated-image diagnostics for `poradnik2013.pdf`, page `182`, `/Im2` now provide a cleaner repro:

- `/Im2` -> `/JPXDecode`, `189x66`
- codestream summary:
  - `3` components
  - `1` tile
  - `6` tile-parts
  - progression `RLCP`
  - `5` decomposition levels
  - irreversible transform (`transform=0`, 9/7)
  - quantization style `2`
  - `scod=0x00` (`precincts=no`)

Interpretation:

- the remaining quality defect is **not** explained by page-level composition or multi-tile merge alone
- page `182 /Im2` is now the smallest focused repro for the still-bad 3-component 9/7 path

#### W. `HH` was the dominant corruption source, but not the last one

Diagnostic-only helpers added during this session:

- `JPX_MAX_RESOLUTION`
- `JPX_SKIP_SUBBANDS`

Confirmed on page `1`:

- `LL` / low-resolution-only output is coherent but very soft
- skipping `HH` dramatically cleans the image
- skipping `HL` or `LH` is not comparably beneficial

Interpretation:

- `HH` was the worst structural corruption source
- after the `HH` context fix, the remaining problem is subtler and looks more like residual irreversible reconstruction quality than a total packet scramble

#### X. On page `182 /Im2`, packet presence looks sane for component `0`

Focused traces with `JPX_TRACE_PACKET='0,0'` on `page 182 /Im2` showed:

- `res=0` `LL` included with plausible `zbp/passes/len`
- higher resolutions contain plausible `HL/LH/HH` included blocks for component `0`

At the same time, `JPX_DUMP_STATS='1'` on the same image showed:

- `comp=0` non-zero range (`min=-205.8223`, `max=158.3393`, `mean=2.6702`)
- `comp=1` and `comp=2` stayed exactly `0`

Interpretation:

- for this small metallic image, the remaining defect is not “component 0 fully missing”
- the current bad appearance is now later / subtler than the old packet-length failures

### Current best-known resume point

If work resumes later, continue from this exact state:

1. keep the `HH` context fix in `GetSignificanceLabelOrient2(...)`
2. keep the inverse 9/7 low/high normalization fix in `Synthesize97(...)`
3. treat `page 1`, `159`, `177`, `182`, `183` as regression pages, not as open blockers
4. keep `page 159` and `page 183` as “must not regress”
5. if further debugging is needed, start from:
   - `JPX_DUMP_STATS`
   - `JPX_TRACE_PACKET`
   - isolated-image render via `_diag_renderpage image ...`

### Current best-known resume point (updated after the 9/7 normalization fix)

Use this instead of the older five-step list above.

1. keep the `HH` context fix in `GetSignificanceLabelOrient2(...)`
2. keep the inverse 9/7 low/high normalization fix in `Synthesize97(...)`
3. treat `page 1`, `159`, `177`, `182`, `183` as regression pages, not as open blockers
4. if future JPX work is needed, start from:
   - `JPX_DUMP_STATS`
   - `JPX_TRACE_PACKET`
   - isolated-image render via `_diag_renderpage image ...`
   - raw export via `_diag_renderpage image-raw ...`
   - Pillow reference decode of the raw `.jpx`
5. the remaining gap versus Pillow is now tonal / parity-level, not the old
   “missing or embossed image” failure mode

## Update 2026-04-12 (latest)

### New confirmed findings

#### Q. Rewriting the 9/7 edge/parity handling did not materially improve page 1

`PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs` now contains a rewritten
`Synthesize97(...)` with explicit symmetric extension at the edges.

That patch was worth trying and should stay in the code for now because it is
cleaner and less parity-fragile than the old branchy version. However, after a
fresh diagnostic rebuild and full-page renders:

- `poradnik2013.pdf` page `1` remained recognizable-but-corrupted
- `poradnik2013.pdf` page `159` remained correct

Interpretation:

- the remaining page `1` artifact is **not** explained by the final 9/7 edge
  parity handling alone
- do **not** burn more time repeating small blind coefficient/edge tweaks as
  the main line of attack

#### R. A diagnostic stats path now exists for post-transform tile/component ranges

Diagnostic-only support was added in
`PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs`:

```csharp
private static readonly bool DumpStatsForDiagnostics = string.Equals(
    Environment.GetEnvironmentVariable("JPX_DUMP_STATS"),
    "1",
    StringComparison.Ordinal);
```

and `DecodeGeneral(...)` now logs per-tile / per-component stats after inverse
transform through `LogComponentStats(...)`.

This instrumentation should be reused instead of recreated if work resumes.

#### S. Page 1 component ranges are numerically plausible, so this no longer looks like a simple amplitude-scale bug

Diagnostic command:

```powershell
$env:JPX_DUMP_STATS='1'
dotnet ...\_diag_type1check.dll "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" out 1
```

Observed on page `1`:

- all 12 tiles produced plausible post-transform ranges
- component `0` ranges were on the order of several hundred
- component `1` and `2` ranges were on the order of several tens
- tile means were reasonably consistent across tiles

Interpretation:

- this does **not** look like a simple global dequantization magnitude problem
- this does **not** look like a crude per-tile DC offset problem
- the remaining artifact is more likely structural: packet/tile-part
  aggregation, codeblock inclusion/order, or another reconstruction-stage data
  assembly problem that still preserves overall coefficient amplitude

#### T. Current prime suspect has shifted back toward packet / tile-part aggregation

At the current state:

- page `159` remains a good regression page
- page `1` is no longer blank and no longer blocked by the old geometry bug
- page `1` still shows seam / mosaic / relief-like corruption
- rewriting `Synthesize97(...)` did not visibly fix it
- post-transform component stats look plausible

Current best suspect order:

1. packet / tile-part aggregation for RLCP multi-tile codestreams
2. codeblock segment accumulation / ordering across tile-parts
3. only after that, any deeper irreversible reconstruction corner case

### Current best-known resume point

If work resumes later, continue from this exact sequence:

1. keep the current `Synthesize97(...)` rewrite
2. keep the `JPX_DUMP_STATS` instrumentation
3. stop revisiting generic dequantization/scale guesses unless new evidence
   appears
4. inspect and instrument:
   - `PdfCore\Images\Jpeg2000\Jpeg2000PacketParser.cs`
   - `PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs`
     - `BuildAggregateTile(...)`
     - `MergeTiles(...)`
5. next useful diagnostic should measure codeblock / segment / pass aggregation
   per tile-part for `poradnik2013.pdf` page `1`

## Update 2026-04-12 (later)

### New confirmed findings

#### M. The 9/7 lifting sign fix is real and must stay

In `PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs`, the inverse 9/7
lifting path uses:

```csharp
const float Alpha = -1.586134342059924f;
const float Beta = -0.052980118572961f;
```

This materially improved `poradnik2013.pdf` page `1` from a completely broken
decode into a recognizable-but-still-corrupted image. Do **not** revert this
change while debugging the remaining JPX issues.

#### N. Forcing generic full RLCP parsing is not the right fix for page 1

Diagnostic run with:

```powershell
$env:JPX_FORCE_FULL_RLCP='1'
dotnet ...\_diag_type1check.dll "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" out 1 159
```

Confirmed:

- page `1` becomes blank
- page `159` still renders correctly

Interpretation:

- the `ParseRlcpResolutionTilePart(...)` shortcut is still required for page
  `1`'s codestream shape
- the remaining corruption on page `1` is **not** solved by simply forcing
  `ParseRlcp(...)` over all tile-parts

#### O. Disabling MCT does not remove the page 1 artifact pattern

A diagnostic-only toggle was added in
`PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs`:

```csharp
private static readonly bool DisableMctForDiagnostics = string.Equals(
    Environment.GetEnvironmentVariable("JPX_DISABLE_MCT"),
    "1",
    StringComparison.Ordinal);
```

and used to bypass the MCT branch in `CopyTileToPixels(...)`.

Diagnostic run with:

```powershell
$env:JPX_DISABLE_MCT='1'
dotnet ...\_diag_type1check.dll "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" out 1 159
```

Confirmed:

- page `1` changes color dramatically
- but the same seam / mosaic artifact pattern remains
- page `159` is unaffected (single component)

Interpretation:

- MCT/color conversion is **not** the primary remaining cause of the page `1`
  corruption
- the defect is earlier in the pipeline

#### P. The remaining prime suspect is irreversible dequantization, not packet geometry

At this point the evidence is:

- page `159` (`5/3`, `qstyle=0`, single component) is correct
- page `1` (`9/7`, `qstyle=2`, multi-tile, 3 components, `mct=1`) decodes but
  still has strong reconstruction artifacts
- disabling MCT does not remove the artifact structure
- forcing generic full RLCP makes page `1` blank, so that is not the next fix

Current most likely next focus:

- `GetIrreversibleStepSize(...)` in
  `PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs`
- and, if needed after that, the remaining details of `Transform97(...)`

This is now the best-known resume point. Do **not** restart from generic packet
parser work unless a later change reintroduces the old out-of-range packet
length failures.

## Update 2026-04-12

### New confirmed findings

#### H. Fresh diagnostics build path that avoids the WinForms/apphost lock

The reliable way to build the JPX diagnostics without fighting a locked viewer
process was:

```powershell
$stamp=Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$obj='D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\_obj_jpxdiag_'+$stamp+'\'
$bin='D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\_bin_jpxdiag_'+$stamp+'\'
New-Item -ItemType Directory -Path $obj | Out-Null
New-Item -ItemType Directory -Path $bin | Out-Null
dotnet build 'D:\Projects\C#\PdfRendererSeparatedProject\PdfRendererSeparatedProject\_diag_type1check\_diag_type1check.csproj' -c Release -v:minimal -m:1 /nr:false /p:UseSharedCompilation=false /p:UseAppHost=false /p:BaseIntermediateOutputPath=$obj /p:OutDir=$bin
```

Confirmed successful build output from this session:

- `_bin_jpxdiag_20260412_015340_090\_diag_type1check.dll`

Do **not** spend time again rediscovering a build workaround unless this path
stops working.

#### I. The code-block grid fix is active and the old packet-length failure moved

Fresh geometry inspection for `poradnik2013.pdf`, page `1`, `tile=2` confirmed
that the local subband grid fix in
`PdfCore\Images\Jpeg2000\Jpeg2000TileGeometry.cs` is live.

Notably:

- the earlier phantom edge code-block pattern is gone
- page `1` no longer dies at the previous geometry/placement stage

This means the earlier local-grid correction was real and should be preserved.

#### J. Internal decode now succeeds for page 1 and page 159 at the image-object level

Diagnostic command:

```powershell
dotnet '...\_diag_type1check.dll' decodeimage "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1 145 159
```

Confirmed result:

- page `1`
  - `/Im0 /JPXDecode: System.Drawing ok 642x906`
- page `145`
  - `/Im0 /DCTDecode: System.Drawing ok 206x139`
- page `159`
  - `/Im0 /JPXDecode: System.Drawing ok 196x141`

So page `1` is no longer blocked by JPX packet parsing or by outright decode
failure. The remaining problem is later in image reconstruction quality.

#### K. Page 159 full-page render is correct; page 1 full-page render is mosaic, not blank

Full render diagnostic output from this session:

- `_diag_type1check\out_jpx_pages_20260412_015340\page_1.png`
- `_diag_type1check\out_jpx_pages_20260412_015340\page_145.png`
- `_diag_type1check\out_jpx_pages_20260412_015340\page_159.png`

Confirmed:

- `page_159.png` renders correctly, including the product image
- `page_145.png` remains correct
- `page_1.png` is **not blank anymore**, but becomes a colorful / blocky mosaic

Interpretation:

- the parser/local-grid stage is now good enough to produce image data
- the remaining page `1` bug is in JPX reconstruction, not page content
  placement

#### L. Page 1 and 159 take different reconstruction paths

Codestream inspection confirmed:

Page `1`:

- 3 components
- 12 tiles
- progression `RLCP`
- irreversible transform (`transform=0`, 9/7)
- quantization style `2`
- multiple component transform present (`mct=1`)

Page `159`:

- 1 component
- 1 tile
- progression `RLCP`
- reversible transform (`transform=1`, 5/3)
- quantization style `0`

Interpretation:

- the still-bad case is **multi-tile + 3-component + irreversible + MCT**
- the already-good case is **single-tile + single-component + reversible**

This sharply narrows the remaining suspects to:

- irreversible dequantization (`GetIrreversibleStepSize(...)`)
- inverse 9/7 transform (`Transform97(...)`)
- multi-component transform (MCT) handling
- multi-tile assembly in the general decode path

### Current best-known resume point

If work resumes later, continue from this exact state:

1. keep the local code-block grid fix in `Jpeg2000TileGeometry.cs`
2. keep using page `159` as the known-good JPX regression page
3. treat page `1` as the active reconstruction-quality bug:
   - decodes
   - renders
   - but as a mosaic instead of the expected cover
4. debug `PdfCore\Images\Jpeg2000\Jpeg2000InternalDecoder.cs`,
   specifically `DecodeGeneral(...)` and the helpers:
   - `GetIrreversibleStepSize(...)`
   - `Transform97(...)`
   - MCT branch in `CopyTileToPixels(...)`
5. do **not** go back to the old packet-length / negative-offset investigations
   unless a new change reintroduces them

## Update 2026-04-11 (later)

### New confirmed findings

#### E. Tile/component geometry mismatch in internal decode was real and is now fixed

The previous internal decode crash for `poradnik2013.pdf` page `1` was not a
generic decoder failure. It was a concrete geometry bug.

Diagnostic after rebuilding `_diag_type1check`:

```powershell
dotnet .\_diag_type1check\bin\Release\net8.0-windows\_diag_type1check.dll decodeimage "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1
```

The failing message before the fix was:

```text
JPX WriteCodeBlock failed. comp=[256,0..512,256] cb=[8,0..16,8] offset=(-248,0) block=8x8 target=256x256 blockLen=64 scale=1
```

Interpretation:

- codeblock bounds were being treated in tile/global-style coordinates
- coefficient buffers were tile-local
- so the internal decoder wrote with negative offsets

Confirmed fix:

- `PdfCore\Images\Jpeg2000\Jpeg2000TileGeometry.cs`

The geometry builder now uses tile-local component / resolution / subband
bounds instead of mixing global component coordinates into the decoder path.

This fix must be preserved; do **not** re-investigate the old negative-offset
write failure unless a later change reintroduces it.

#### F. Page 159 still decodes internally after the geometry fix

Diagnostic command:

```powershell
dotnet .\_diag_type1check\bin\Release\net8.0-windows\_diag_type1check.dll decodeimage "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 159
```

Confirmed:

- page `159`
  - `/Im0 /JPXDecode: System.Drawing ok 196x141`

So the geometry fix did not break the already-working internal JPX path for
page `159`.

#### G. Page 1 is now blocked by packet parsing, not coefficient placement

After the geometry fix, page `1` no longer fails in `WriteCodeBlock(...)`.
It now fails later in packet parsing with a narrower reproducible condition.

Diagnostic command:

```powershell
dotnet .\_diag_type1check\bin\Release\net8.0-windows\_diag_type1check.dll decodeimage "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1 159
```

Current failure:

```text
JPEG2000 packet data length points outside tile-part data.
tile=2, component=0, resolution=4, layer=0, codeblock=3, grid=(1,1),
requested=181, position=24377, end=24440, remaining=63
```

Focused packet trace:

```powershell
$env:JPX_TRACE_PACKET='2,0,4,0'
dotnet .\_diag_type1check\bin\Release\net8.0-windows\_diag_type1check.dll inspectjpx "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1
```

Trace excerpt:

```text
JPX TRACE packet tile=2 comp=0 res=4 layer=0 startPos=24302 present=1
JPX TRACE   sb=HL cb=0 grid=(0,0) zbp=4 passes=5 lblock=3 lenBits=5 len=8 pos=24304
JPX TRACE   sb=HL cb=1 grid=(1,0) zbp=3 passes=10 lblock=3 lenBits=6 len=44 pos=24306
JPX TRACE   sb=HL cb=2 grid=(0,1) zbp=3 passes=1 lblock=3 lenBits=3 len=3 pos=24307
JPX TRACE   sb=HL cb=3 grid=(1,1) zbp=3 passes=1 lblock=3 lenBits=3 len=1 pos=24308
JPX TRACE   sb=LH cb=0 grid=(0,0) not included
JPX TRACE   sb=LH cb=1 grid=(1,0) not included
JPX TRACE   sb=LH cb=2 grid=(0,1) not included
JPX TRACE   sb=LH cb=3 grid=(1,1) not included
JPX TRACE   sb=HH cb=0 grid=(0,0) zbp=0 passes=5 lblock=3 lenBits=5 len=8 pos=24310
JPX TRACE   sb=HH cb=1 grid=(1,0) not included
JPX TRACE   sb=HH cb=2 grid=(0,1) not included
JPX TRACE   sb=HH cb=3 grid=(1,1) zbp=2 passes=24 lblock=4 lenBits=8 len=181 pos=24313
```

Interpretation:

- the first resolved geometry bug is out of the way
- the next blocker is specifically packet-header interpretation for:
  - `tile=2`
  - `component=0`
  - `resolution=4`
  - `layer=0`
  - `HH codeblock 3`
- the suspicious values are now:
  - `passes=24`
  - `len=181`

Most likely suspects from this point onward:

- packet header bit alignment / byte-stuffing after `0xFF`
- coding pass count decode
- inclusion / zero-bitplane tag-tree state
- codeblock ordering within this packet

### Current best-known resume point

If work resumes later, continue from this exact state:

1. keep the tile-local geometry fix in `Jpeg2000TileGeometry.cs`
2. treat page `159` as the “still good” regression page
3. investigate packet parsing for page `1` at:
   - `tile=2`
   - `component=0`
   - `resolution=4`
   - `layer=0`
   - `HH cb=3`
4. do **not** spend time again on the old negative-offset `WriteCodeBlock(...)`
   failure unless it reappears after a new patch

## Update 2026-04-11

### New confirmed findings

#### A. Packet reader state was being copied by value

The biggest confirmed parser bug so far was not in arithmetic itself, but in how
`Jpeg2000PacketBitReader` was passed around.

`Jpeg2000PacketBitReader` is a `ref struct`, but several parser helpers still
accepted it **by value**, which silently copied the reader state and caused
repeated or inconsistent bit consumption.

Confirmed fixed signatures in:

- `PdfCore\Images\Jpeg2000\Jpeg2000PacketParser.cs`

Changed from by-value to `ref`:

- `ParseLrcp(...)`
- `ParseRlcp(...)`
- `ParseRlcpResolutionTilePart(...)`
- `ReadPacket(...)`
- `ReadZeroBitPlanes(...)`
- `ReadCodingPassCount(...)`
- `Jpeg2000TagTree.Decode(...)`
- `Jpeg2000InclusionTree.Decode(...)`

This was the first fix that materially changed parser behavior.

#### B. Page 159 packet parsing now succeeds

Diagnostic command:

```powershell
dotnet run --project .\_diag_type1check\_diag_type1check.csproj --no-self-contained /p:UseAppHost=false -- inspectjpx "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 159
```

Confirmed after the `ref` fix:

- packet parsing completes successfully
- reported summary:
  - `codeblocks=348`
  - `included=55`
  - `passes=1096`
  - `codedBytes=15952`

So for page `159` the packet parser is no longer the immediate blocker.

#### C. Page 1 still fails, but now at a much narrower point

Diagnostic command:

```powershell
$env:JPX_TRACE_PACKET='2,2,4,0'
dotnet run --project .\_diag_type1check\_diag_type1check.csproj --no-self-contained /p:UseAppHost=false -- inspectjpx "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1
```

Current failure:

```text
JPEG2000 packet data length points outside tile-part data.
tile=2, component=2, resolution=4, layer=0, codeblock=0, grid=(0,0),
requested=196, position=24381, end=24440, remaining=59
```

Focused trace around the failure:

```text
JPX TRACE packet tile=2 comp=2 res=4 layer=0 startPos=24376 present=1
JPX TRACE   sb=LH cb=0 grid=(0,0) zbp=3 passes=5 lblock=6 lenBits=8 len=196 pos=24378
JPX TRACE   sb=LH cb=1 grid=(1,0) not included
JPX TRACE   sb=LH cb=2 grid=(0,1) not included
JPX TRACE   sb=LH cb=3 grid=(1,1) zbp=4 passes=2 lblock=4 lenBits=5 len=15 pos=24381
JPX TRACE   sb=HL cb=0 grid=(0,0) not included
JPX TRACE   sb=HL cb=1 grid=(0,1) not included
JPX TRACE   sb=HH cb=0 grid=(0,0) not included
JPX TRACE   sb=HH cb=1 grid=(0,1) not included
```

Interpretation:

- page `1` is now failing later and more specifically
- the remaining issue is likely in higher-resolution RLCP packet state,
  band ordering assumptions, or tile-part persistence logic

#### D. Decoder itself is still not implemented internally

Diagnostic command:

```powershell
dotnet run --project .\_diag_type1check\_diag_type1check.csproj --no-self-contained /p:UseAppHost=false -- decodeimage "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1 159
```

Confirmed:

- packet parser improvement did **not** make images visible yet
- both pages still fail in `Jpeg2000Decoder.Decode(...)`
- current internal path is still effectively:
  - try Windows WIC
  - if WIC cannot decode, throw `NotSupportedException`

So the work is now split into two clean stages:

1. finish stabilizing packet parsing, especially for page `1`
2. implement the internal JPEG2000 decode path after parser output is trustworthy

### Additional code note

There was also an experimental `HL/LH` subband order swap in
`PdfCore\Images\Jpeg2000\Jpeg2000TileGeometry.cs`.

That experiment should not be treated as a trusted final fix; it must always be
revalidated against the page `1` trace after rebuild.

### Current best-known resume point

If work resumes later, do **not** restart from generic JPX investigation.
Resume from this exact sequence:

1. rebuild after the latest `Jpeg2000TileGeometry.cs` subband-order state
2. rerun:
   - `inspectjpx ... 159`
   - `inspectjpx ... 1` with `JPX_TRACE_PACKET='2,2,4,0'`
3. if page `159` remains good and page `1` still fails, continue narrowing
   RLCP packet state for `tile=2`, `component=2`, `resolution=4`, `layer=0`

## Goal

Make the internal PDF renderer decode `/JPXDecode` image XObjects without relying on external libraries or Windows WIC codecs.

Current user-visible blockers:

- `C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf`
  - page `1`: cover image missing
  - page `159`: product image missing
- page `145` in the same file already works because it uses ordinary JPEG (`/DCTDecode`)

## Confirmed facts

### 1. Problem pages use JPX, not JPEG

Diagnostic command:

```powershell
dotnet run --project PdfRendererSeparatedProject\_diag_type1check\_diag_type1check.csproj --no-self-contained /p:UseAppHost=false -- images "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1 145 159
```

Confirmed:

- page `1`: `/Im0`, `642x906`, `filter='/JPXDecode'`, `cs=PdfIccBasedColorSpace`
- page `145`: `/Im0`, `206x139`, `filter='/DCTDecode'`
- page `159`: `/Im0`, `196x141`, `filter='/JPXDecode'`, `cs=PdfDeviceRgbColorSpace`

### 2. Internal JPEG2000 decoder is still incomplete

Diagnostic command:

```powershell
dotnet run --project PdfRendererSeparatedProject\_diag_type1check\_diag_type1check.csproj --no-self-contained /p:UseAppHost=false -- decodeimage "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1 145 159
```

Confirmed:

- page `145` decodes as normal JPEG
- pages `1` and `159` fail because `PdfCore.Images.Jpeg2000.Jpeg2000Decoder.Decode(...)` still falls back to:
  - try WIC
  - if WIC fails, throw `NotSupportedException`

### 3. Page 1 and 159 codestream metadata

Diagnostic command:

```powershell
dotnet run --project PdfRendererSeparatedProject\_diag_type1check\_diag_type1check.csproj --no-self-contained /p:UseAppHost=false -- inspectjpx "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 1 159
```

Confirmed:

- page `1`
  - `642x906`
  - `3` components
  - `12` tiles
  - `72` tile-parts
  - progression `RLCP`
  - `5` decomposition levels
  - `1` layer
  - transform `9/7 irreversible`
  - codeblock style `0x00`
- page `159`
  - `196x141`
  - `1` component
  - `1` tile
  - `6` tile-parts
  - progression `RLCP`
  - `5` decomposition levels
  - `1` layer
  - transform `5/3 reversible`
  - codeblock style `0x00`

### 4. Failure currently happens already in packet parsing

Current exception during `inspectjpx`:

- page `1`
  - `requested=161, remaining=127`
- page `159`
  - `requested=2010, remaining=74`

This means the parser is already misreading packet headers or codeblock state before tier-1 entropy decode.

### 5. Focused trace for page 159, resolution 1

Diagnostic command:

```powershell
$env:JPX_TRACE_PACKET='0,0,1,0'
dotnet run --project PdfRendererSeparatedProject\_diag_type1check\_diag_type1check.csproj --no-self-contained /p:UseAppHost=false -- inspectjpx "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 159
```

Trace:

```text
JPX TRACE packet tile=0 comp=0 res=1 layer=0 startPos=1058 present=1
JPX TRACE   sb=HL cb=0 grid=(0,0) zbp=0 passes=2 lblock=4 lenBits=5 len=15 pos=1058
JPX TRACE   sb=LH cb=0 grid=(0,0) zbp=0 passes=3 lblock=5 lenBits=6 len=0 pos=1060
JPX TRACE   sb=HH cb=0 grid=(0,0) zbp=0 passes=30 lblock=9 lenBits=13 len=2010 pos=1062
```

Interpretation:

- `HL len=15` is plausible
- `LH len=0` is odd but still possible
- `HH len=2010` is impossible because only `74` bytes remain in that tile-part

This strongly suggests a bug in one of:

- inclusion / zero-bitplane tag-tree decode
- packet header bitstream handling
- codeblock ordering / geometry assumptions for higher resolutions

### 6. Focused trace for page 159, resolution 0

Diagnostic command:

```powershell
$env:JPX_TRACE_PACKET='0,0,0,0'
dotnet run --project PdfRendererSeparatedProject\_diag_type1check\_diag_type1check.csproj --no-self-contained /p:UseAppHost=false -- inspectjpx "C:\Users\Oleg Ogar\Downloads\poradnik2013.pdf" 159
```

Trace:

```text
JPX TRACE packet tile=0 comp=0 res=0 layer=0 startPos=1002 present=1
JPX TRACE   sb=LL cb=0 grid=(0,0) zbp=0 passes=2 lblock=4 lenBits=5 len=31 pos=1002
```

Interpretation:

- resolution `0` looks sane
- parser goes wrong later, not immediately from the first packet

## Relevant code files

- `PdfCore\Images\Jpeg2000\Jpeg2000Decoder.cs`
- `PdfCore\Images\Jpeg2000\Jpeg2000PacketParser.cs`
- `PdfCore\Images\Jpeg2000\Jpeg2000TileGeometry.cs`
- `PdfCore\Images\Jpeg2000\Jpeg2000Codestream.cs`

## Already changed before this worklog

- `ReadCodingPassCount(...)` in `Jpeg2000PacketParser.cs` was corrected to:

```csharp
private static int ReadCodingPassCount(Jpeg2000PacketBitReader reader)
{
    if (reader.ReadBit() == 0)
        return 1;

    if (reader.ReadBit() == 0)
        return 2;

    int twoBits = reader.ReadBits(2);
    if (twoBits < 3)
        return 3 + twoBits;

    int fiveBits = reader.ReadBits(5);
    if (fiveBits < 31)
        return 6 + fiveBits;

    return 37 + reader.ReadBits(7);
}
```

This improved some traces but did not fix the missing images.

## Current hypotheses to test next

1. Verify whether `ParseRlcpResolutionTilePart(...)` is safe for these files or whether state must be preserved across tile-parts.
2. Check if higher-resolution subband/codeblock geometry is wrong for `HL/LH/HH`.
3. Check `Jpeg2000TagTree.Decode(...)` against JPEG2000 packet-header rules for included codeblocks and zero-bitplanes.
4. Only after packet parsing is sane, continue with tier-1 + inverse transform path in `Jpeg2000Decoder`.

## Resume point

Resume from packet parser diagnostics, specifically page `159`, `tile=0`, `component=0`, `resolution=1`, `layer=0`.
That is the smallest reproducible failure currently known.

## 2026-04-16 restart checkpoint

User explicitly requested a persistent checkpoint so work can resume cleanly after restart.

### Main live failure

- `poradnik2013.pdf`, page `1`, is still blank in the live app and in the thumbnail strip.
- Expected result: the blue cover image must be visible.
- The user copied `poradnik2013.pdf` into the project folder specifically as the failing reproduction file.

### What is already known

- Packet-parser work improved traces (`ReadCodingPassCount(...)` fix), but did not solve the missing JPX images.
- Page `159`, `tile=0`, `component=0`, `resolution=1`, `layer=0` remains the smallest low-level packet-parser repro and should stay the regression point.
- Page `1` likely needs inspection beyond packet lengths alone:
  - JP2/JPX container interpretation
  - decoded component reconstruction
  - alpha / soft-mask handling
  - colorspace application

### Resume order

1. Make `poradnik2013.pdf` page `1` visible in the live app.
2. If page `1` is still blank, log for the page-1 JPX image:
   - dimensions
   - component count / bit depth
   - colorspace
   - presence of alpha / `SMask`
   - whether decoded pixels are non-zero before page composition
3. Keep page `159` as the low-level parser regression target.
4. After page `1` becomes visible, continue quality fixes for pages `159`, `177`, `182`, `183`.
