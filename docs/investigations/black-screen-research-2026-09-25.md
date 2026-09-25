# Mortal Shell black screen — external research and fix plan (2026-09-25)

Companion to `mortal-shell-black-screen.md` (our own measurements) and
`docs/GAME_TRACKING.md` M6/M15–M20. Everything below is either **cited** from
another emulator's documentation/code or marked **[ours]** when it is verified
in this tree.

## 1. Is there a published fix for this title?

No. The available sources are status reports, not fixes:

| Source | Status |
|---|---|
| **KytyPS5 (sibling PS5 emulator) #58** — "[GAME BUG]: Mortal Shell: Enhanced Edition (BOOTS)", PPSA02868, 0.0.5.5 | "Game boots, then crashes", expected "Splash screens would load", default settings, **no maintainer diagnosis and no workaround**. Kyty is *behind* SharpEmu here: we run 300 s / 250k draws / 6015 flips with no crash. |
| **sharpemu/sharpemu issues** | **no issue mentions Mortal Shell at all**. Rendering issues there are generic: #904 (gen5 runtime + shader buffer paths), #882 (ItSetPredication), #881 (AGC rendering + module loading), #853 (DCC fast-clear publish + texture skip — already consumed here as `2a75ad4`), #852/#850 (RGB10→RGBA16F present encode). |
| **KytyPS5** generally | per-title "[GAME STATUS]" reports only; nothing on placeholder textures. |
| **RPCS3 #9968** "[Meta] Async texture streaming bugs" | a bare tracker (known-broken: Persona 5, GOW3, Killzone 3; child "Meta: Fix vkCmdWaitEvents spec violation"). |

So the fix has to come from platform-level prior art. The best source is
**shadPS4** — a PS4 emulator sharing our exact hardware model (AMD GCN
descriptors, unified guest memory, one `VkImage` per (address, format) instead
of hardware's single physical surface).
## 2. shadPS4 prior art — this is our bug class, documented

### 2.1 Buffer Cache architecture (DeepWiki)

- **Page-granular write tracking.** `PageManager` keeps *write watchers* per
  page; when `num_write_watchers > 0` the page is write-protected
  (`MemoryPermission::None`), using `userfaultfd`
  (`UFFDIO_REGISTER_MODE_WP`) on Linux to catch the fault.
- Memory is tracked in 16 MB regions as `RegionBits` over 4 KB pages;
  `CACHING_PAGEBITS 14` ⇒ a 16 KB indexing page.
- **CPU reads of GPU-written bytes**: `ReadMemory` → `InvalidateMemory` →
  `DownloadBufferMemory` issues `vkCmdCopyBuffer` into a download buffer and
  writes guest memory back via `TryWriteBacking`, deferred or synchronous.

**[ours]** This is the *designed* version of our `GuestImageWriteTracker` — the
page guards plus `[SYNC] cpu-write-drain`. We had to switch that tracker off
because arming a page turns a *managed* write into a CLR-fatal fault that then
reverse-P/Invokes the managed VEH handler (M12). The documented equivalent never
enters a managed transition from inside the fault: the fault is handled
natively and the drain happens on the emulator's own thread. **That is the
template for re-implementing our tracker safely.**

### 2.2 PR #4591 "Synchronization Between Storage Images and Guest Memory"

The closest published match to our symptom, in the author's own words:

> PS4 unified memory lets textures at the same address read CS output directly;
> in shadPS4 each format gets its own `VkImage`, so data must be explicitly
> copied through guest memory.

What it implements (`storage_image_sync`):

1. after each CS dispatch, **download the storage image to guest memory**;
2. **re-tile** it — `CopyImageToBuffer` always produces linear data, "guest
   memory must stay tiled to match the T# descriptor" (`TileLinearBuffer`);
3. **notify the buffer cache** "so downstream texture refreshes pick up the new
   data".

Sibling toggles from the same PR: `rt_alias_copy` (pull model — when a texture
at an address aliases a render target, copy RT→texture), `_1x1_readback`, and
`depth_clear_skip`. The author then removed the game-specific gating because
"the mechanisms themselves are generic".

**[ours]** Our log has exactly this shape, and a crucial new decode of it:

```
SharpEmu compute cs=0x0000002004FF0000 storage=0x000000200CC20000 1x1 fmt10 1x1x1
SharpEmu compute cs=0x0000002004FF0000 storage=0x000000200CC30000 1x1 fmt4  1x1x1
```

`BuildComputeDebugName` prints `"{storage.Width}x{storage.Height} fmt{Format}
{GroupCountX}x{GroupCountY}x{GroupCountZ}"`, so **`1x1` is the
descriptor-decoded image size and `1x1x1` is the guest's dispatch group
count** — the storage writes are bound to 1×1 images at 64 KB steps, in the
same address family as our placeholders (`0x200CC10000`, `0x200CC20000`).
A texture bound later at such an address is a *different* `VkImage` in this
emulator, and our `IsGuestImageUploadKnown` answer is not a substitute for the
copy step shadPS4 added.

### 2.3 PR #4542 "Fix stale guest memory when reusing GPU-written linear images through overlapping aliases"

Ordering bug class, documented as:

> Some games (namely GOW3) reuse the same guest memory range through different
> image representations. A linear image may be written by the GPU and later
> reinterpreted as a tiled or compressed texture. With Enable Readback Linear
> Images enabled, shadPS4 already scheduled GPU-to-CPU readbacks … **those
> readbacks were asynchronous**, leaving a timing window where an overlapping
> image could be recreated from guest memory **before the pending writeback had
> completed**. In that case, the new texture was up[loaded with stale data].

Fix: make the readback ordered against alias recreation. The review thread also
split off a second fix ("fix auto-exposure chain by copying RT data to aliased
texture views").

**[ours]** Same territory as our `_pendingGuestImageUploads`,
`_cpuBackedUploadGenerations` and `_availableGuestImages` bookkeeping.

### 2.4 Investigation methodology (`akitaonrails/shadPS4`, `docs/driveclub-investigation/phase-15-runtime-texture-dump.md`)

A documented recipe for chasing a blackout, better than what we wrote:

- `SHADPS4_DC_TEX_DUMP=1` dumps the **guest memory** for every unique texture
  address at bind time, plus a `[dc-texdump] addr=0x… w×h fmt tile` log line
  that acts as a **searchable index**.
- Dumps are **raw guest bytes, still tiled**; de-tiling happens offline
  (`tools/texdump_to_png.py`, porting the GCN tile math from
  `src/video_core/amdgpu/tiling.cpp`).
- Hit criteria are stated up front: "find a dump whose first-seen submit lands
  after a gate re-arm, whose dimensions are roughly 1920×1080 …, whose pixel
  format matches a photo asset".
- They added a **dispatch log** (`SHADPS4_DC_DISPATCHLOG=1`, hooked into
  `Rasterizer::Dispatch`) because "auto-exposure / bloom / luma-integration
  passes are compute dispatches **which the drawlog doesn't see**".

**[ours]** Our `bgra_to_png.ps1` + `ms_frames` is the analogue for *GPU* images;
what is missing is a **guest-memory** dump at the descriptor base address and a
compute-side binding log. Both are cheap to add.

## 3. Unity: why a 1×1 descriptor is not automatically garbage

- Unity Texture Streaming "gives you control over **which mipmap levels** Unity
  loads into memory"; an unstreamed texture exists at *reduced mips* and looks
  blurry, and streaming relies on asynchronous loading plus compressed
  container formats.
- A descriptor whose base mip is 1×1 is therefore the natural "only the
  smallest mip is resident yet" state — i.e. **nothing has been streamed**,
  which matches M20.
- Unity also deliberately uses 1×1 default textures (white/black/gray/bump) for
  unbound slots, so "1×1 with a valid base address" can be legitimately small
  *and* legitimately a placeholder.

## 4. Other emulators on degenerate sizes

xemu #2516, "0-sized surfaces are not handled correctly": creating a zero-sized
surface trips a tracking assertion and then a "GL does not support a zero-sized
texture" assertion — while "**Hardware gracefully handles zero-sized
surfaces**". Our decode of `fields[2] == 0` yields width/height 1 rather than 0,
so we already degrade gracefully; the lesson is that *silently substituting
content* (our zero-filled image) is what makes this class so hard to see.

## 5. Fix plan for SharpEmu (ordered by expected value)

### F1 — stop the upload-known short-cut from answering with no evidence

**[ours]** `VulkanVideoPresenter.IsGuestImageUploadKnown` takes its tracker-off
path through `IsUntrackedGuestImageContentUnchanged(address, probeByteCount)`,
and that function returns **`true` — skip the upload — whenever
`byteCount == 0`**, which is exactly the case when the address has no registered
extent:

```csharp
if (memory is null || byteCount == 0)
{
    // No probe possible — preserve the historical skip so UI stays cheap
    return true;
}
```

The caller then builds the texture with `initialPixels: []` (the draw-texture
path in `AgcExports.cs`), so the composite samples a GPU image that nobody ever
filled ⇒ **all-zero output, black flip**.

Instrumented this session (no behaviour change unless asked):

- `SHARPEMU_TRACE_UPLOAD_KNOWN=1` → `vk.upload_known_skip addr=… fmt=…
  probe_bytes=` once per address, saying outright when the skip was
  unconditional because the probe had no basis.
- `SHARPEMU_FORCE_GUEST_TEXEL_UPLOAD=1` → refuses the short-cut so the caller
  copies guest texels (the experiment, and a usable fallback).
- `run_ms_forceupload.ps1 [-ForceUpload]` → A/B harness; both arms read the
  swapchain back and dump frames, so the verdict is `nonblack_pixels=`, not an
  inference.

**Result (measured — 150 s, `ms_forceupload_forced_20260925_184216.txt`):**

- `vk.upload_known_refused` fired **85 times across 83 distinct addresses**, so
  the evidence-free short-cut really is taken for many textures, not just the
  placeholder.
- **All five** placeholder addresses are in the refused set, i.e. the forced arm
  did copy guest texels for exactly the descriptors the composite samples.
- The presented image was nevertheless **still
  `nonzero_bytes=0/8294400 nonblack_pixels=0/2073600`, identical hash, on all
  14 readbacks**, with `failfast=0` and the same 5 placeholder descriptors.

So the evidence-free skip is a **real defect that now has a safety valve**, but
it is **not the black-screen cause**: restoring the guest-texel copy for those
addresses does not change a single presented pixel. Either the guest bytes at
those addresses are zero/transparent (the dummy is empty by the guest's own
doing) or a second reuse path in the presenter still keeps the existing image.
Either way F1 alone cannot fix this, and the weight moves to F2 (CS output never
reaching the address) and to M20 (the real texture is never streamed).

Keep the two env vars: `SHARPEMU_TRACE_UPLOAD_KNOWN=1` is now the cheapest way to
audit "are we ever skipping an upload we should not?", which is a question any
title with streamed CPU-updated textures will hit again.

### F2 — implement `storage_image_sync` (shadPS4 #4591) for compute writes

After a CS dispatch that writes a storage image: download → re-tile →
**notify the image/buffer caches**, instead of only marking the address
available. This is what makes "a texture at the same address sees the CS output"
true under unified memory. Verify with the existing
`SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS=<addr>` trace (already reports
`read=` / `nonzero=` / `initial_bytes=` for one address), extended with an
all-addresses mode so the placeholder address can be checked in the same run
that discovers it.

### F3 — order GPU→CPU readbacks against alias recreation (shadPS4 #4542)

Audit `_pendingGuestImageUploads` / `_availableGuestImages` /
`_cpuBackedUploadGenerations` for the documented window: an overlapping image
recreated from guest memory *before* the pending writeback lands. Any place we
mark an image available while a copy is still queued is the same bug.

### F4 — re-implement write detection natively (Buffer Cache doctrine)

The tracker we disabled for M12 is the right idea implemented on the wrong side:
a Windows `PAGE_GUARD`/`VirtualProtect` fault must be recorded **natively**
(lock-free dirty-page bitmap) and drained on the emulator's own thread. Only
then can page-guard write detection be trusted to catch CPU writes to texels a
GPU image has already uploaded.

### F5 — adopt the shadPS4 diagnostic shape

Dump the **guest bytes** at each placeholder descriptor base (tiled, as stored)
with an index line per address, and add a compute-binding log (storage image,
format, dims, dispatch count). Our current warnings show the *descriptor* but
never the *content the guest stored at it*.

## 6. Sources

- shadPS4 Buffer Cache: https://deepwiki.com/shadps4-emu/shadPS4/4.7-buffer-cache
- shadPS4 PR #4591 (storage image ↔ guest memory): https://github.com/shadps4-emu/shadPS4/pull/4591
- shadPS4 PR #4542 (stale guest memory via overlapping aliases): https://github.com/shadps4-emu/shadPS4/pull/4542
- shadPS4 runtime texture dump methodology: https://github.com/akitaonrails/shadPS4/blob/gamma-debug/docs/driveclub-investigation/phase-15-runtime-texture-dump.md
- KytyPS5 Mortal Shell report: https://github.com/KytyPS5/KytyPS5/issues/58
- RPCS3 async texture streaming tracker: https://github.com/RPCS3/rpcs3/issues/9968
- Unity Texture Streaming: https://docs.unity3d.com/2019.1/Documentation/Manual/TextureStreaming.html
- xemu zero-sized surfaces: https://github.com/xemu-project/xemu/issues/2516

## 7. Measured bisect (2026-09-25) — what is actually wrong

Three env-only experiments, all reading the presented image back, moved this
from "some texture is a placeholder" to a single statement of the bug. All are
reproducible with `run_ms_forcebisect.ps1`.

| # | Experiment | Result | Meaning |
|---|---|---|---|
| 1 | `-Targets 0x8FC0000000,0x8FC2000000` (magenta fullscreen fragment + fullscreen vertex + default raster state, scoped to the flip buffers) | still `nonblack=0/2073600`, same hash | **inconclusive** — see M24: `AnyTargetAddressMatches` is only consulted in `CreateTranslatedDrawResources`, and the scoped path did not visibly fire. Verify with `SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT=<path>`, which the presenter writes **iff** `forceSolidFragment` was true (source: `VulkanVideoPresenter:7438`) |
| 2 | `-WhiteTextureTargets "*"` (every uploaded texture filled with `0xFF`; 765 uploads, incl. `3200x1800`, `1024x1024`, `64x64`) | one frame read back **`nonblack=2073600/2073600` — a fully white screen** | **the render/target/blit path works.** The composite executes and the present blit shows its output. The black frame is a *content* problem |
| 3 | sibling dump on the pass that samples the placeholder | `ps=0x… pc=0x50 op=ImageSample storage=False addr=0x2004550000 1x1 fmt=10 num=0 tile=1 type=9 mip=0-0/0` — **the only image binding of that pass** | **the whole frame equals that single 1×1 texel.** Zero ⇒ flat black; `0xFF` ⇒ flat white. Nothing else enters the image |

Consequences:

- The five 1×1 placeholders reported by `agc.texture_1x1_linear_binding` are not
  a harmless warning and not one input among many: for the pass that fills the
  flip buffer, the 1×1 dummy **is the entire input**, so the frame is a flat
  fill of its texel.
- "Flat black" and "flat white" are the only two outputs observed, which rules
  out any scene-content explanation for the current image: whatever the game
  renders elsewhere never reaches the flip buffer through this pass.
- The remaining question is therefore narrow and answerable: **what should that
  1×1 image contain, and who should have written it?** Either
  (i) the descriptor is a placeholder for a texture the guest never streamed
  (M19/M20 — then the guest would also show a flat fill on hardware), or
  (ii) we sample the wrong image for that descriptor (e.g. the address-0
  fallback / a shared degenerate 1×1 image, which is why whitening *all*
  textures changed the output even though this descriptor's own address was
  never whitened).

Discriminating experiment for (i) vs (ii): `run_ms_forcebisect.ps1
-WhiteTextureTargets 0` whitens **only** address-0 fallbacks.

| # | Follow-up | Result | Meaning |
|---|---|---|---|
| 4 | `-WhiteTextureTargets 0` (address-0 fallbacks only) | `texture_force_white` = **0 lines** (no address-0 fallback upload happened at all) and the frame stayed all-zero | the pass does **not** sample the address-0 fallback image; it samples the descriptor's own 1×1 image. So (ii) as written ("we substitute the fallback") is ruled out for the dumped pass |
| 5 | `-TraceGuestTextureAddresses "*" -ForceTexelUpload` | 90 s produced only **1** `vk.texture_upload_contents` line (a 1024×1024 `R8Unorm` with `nonzero_bytes=1565/1048576`) | with the upload-known short-cut active, essentially **no texture upload path runs at all** — which is why the guest's texel content for the 1×1 has still not been observed. Re-run needs `SHARPEMU_TRACE_UPLOAD_KNOWN=1` (now forced on by the harness) to prove the force actually applied |

So the open question is now exactly one sentence: **is the guest's 1×1 texel
zero, or is it non-zero and we fail to sample it?** That is the difference
between "the guest has not produced the texture yet" (upstream/streaming work)
and "our texture binding drops the content" (a rendering fix here).

Note the current best explanation for the flat fill being *black* rather than a
1-pixel scene: the pass writes the single texel across the whole target (that is
what a fullscreen draw sampling a 1×1 texture does), so the frame is a flat fill
by construction. The wildcard run whitened 1×1 textures elsewhere in the chain
and the flat fill turned white, confirming that the frame content is decided by
1×1 placeholder content rather than by anything scene-shaped.

## 8. Answer to "is the guest's texel zero?" — yes, and it is the guest's own state

`SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS=*` (wildcard mode added this session,
because the addresses change every boot) reports, for every compute **storage**
target:

```
agc.storage_initial_data addr=0x000000200D090000 op_storage=True upload_known=False
  read=True nonzero=False initial_bytes=0 logical_bytes=4 physical_bytes=65536
  size=1x1 pitch=1 fmt=10 num=0 tile=27 mip=0
```

and the sampled binds that skip their copy report the same emptiness directly:

```
vk.upload_known_skip addr=0x0000002BC6450000 fmt=2147486208 probe_bytes=0
  guest_bytes=00000000000000000000000000000000
```

So, with the emulator reading guest memory and finding zeros:

- the guest's **own** descriptors are 1×1 (`size=1x1`, `logical_bytes=4`) —
  including for the compute storage images it writes;
- guest memory behind those addresses is **all zero**;
- the 64 KiB `physical_bytes`/address stride is the tiled allocation footprint,
  not a 128×128 tile copy (my earlier "64 KiB = one tile" reading was wrong);
- the sampled placeholder whose content fills the whole frame is not even one of
  the storage targets in the same run.

**Conclusion: the rendering side is exonerated.** The emulator draws exactly what
the guest asks for — one 1×1 zero texel per frame, i.e. a flat black fill — and
every mechanism on our side is demonstrably working (raster, offscreen
submission, present blit, forced texel uploads, DCC publish; M23's flat-white
frame is the proof). The open work is upstream of rendering: why the game only
ever produces 1×1 with no content. Unity's streaming model makes that concrete —
1×1 is the smallest mip, i.e. "nothing streamed yet" — so the next step is the
streaming/IO path (M20 in `docs/GAME_TRACKING.md`), not the descriptor path.

This is also why the shadPS4 prior art (§2) stays relevant despite the finding:
their `storage_image_sync` and readback-ordering fixes address the same
*coherence* class, which will matter as soon as content actually exists to be
kept coherent.

Related tooling added while doing this: the sibling dump prints a pass' whole
binding set once, in the same run that triggers the placeholder warning, because
the composite's shader address changes on every boot (`0x2005B40000`,
`0x2035DEC0000`, `0x394A140000` observed), which makes a pre-set
`SHARPEMU_TRACE_PIXEL_SHADER_ADDRESS` useless for this pass.






