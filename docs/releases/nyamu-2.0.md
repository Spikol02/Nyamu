# Nyamu 2.0 — THE ULTIMATE NYAAA

Nyamu is tuned for **The Legend of Zelda: Ocarina of Time** (Switch 2). 2.0 gathers 72 pull requests since Nyamu V1: our own work plus fixes ported from Lichii, Fritchii, AMD Superior, CheezerWheezer and nxnative.

## Highlights

- **Shaders prepared before you play.** Ocarina's shader recipe (from Fritchii v5) is included. On the first boot Nyamu builds its pipelines on the loading screen, which takes about 15 minutes once and can be skipped and resumed. Fritchii measured walking freezes in new areas dropping from about 700–850 ms to 40–70 ms. There's also a shader precache before the game starts, and compiled shaders are saved with `VK_KHR_pipeline_binary`.
- **Faster loading.**
  - **`.nyaa` packages:** right-click a `.nup` and choose *Convert to .nyaa*. Files are stored as chunked zstd, so a read only unpacks what it needs.
  - **No more 40 MB/s read cap:** game file reads are no longer paced, which removes 36–151 ms sleeps at area loads.
- **AMD fixes**, including RDNA 2 and 8 GB cards:
  - boot fixes for RDNA 2 and Polaris;
  - surviving running out of video memory;
  - texture decode out-of-bounds writes fixed;
  - texture cache limits that respect Windows' commit limit (fixes an Ocarina out-of-memory crash);
  - the neural-network anti-aliasing pass is skipped on cards without hardware matrix units.
- **Smoother CPU side.**
  - The HostTiming thread sleeps instead of keeping a whole core busy.
  - All-zero memory pages are trimmed, about 7 GB on a 16 GB PC.
  - A guest memory reserve, plus many small speedups.

## Ocarina of Time fixes

- **Audio:** the ocarina and Song of Time crackle is fixed (ADPCM sign).
- **Water:** waterfall and water flicker fixed.
- **Foliage and scenery:**
  - foliage culling;
  - leafless near hedges and see-through Gerudo Valley rocks;
  - fountain flower flicker;
  - flickering flowerbeds.
- **Long sessions:** corruption that built up and only cleared on a restart is fixed (evicted pipelines keep their storage buffer pointers).
- **Leftover memory mappings:** GPU ranges into freed memory are unmapped, so draws no longer read leftover heap data.
- **Texture uploads:** they can no longer land in the wrong order, and they're batched.
- **Neural networks:** the game's movie and anti-aliasing networks are skipped where they're emulated. The *Auto* mode keeps them on cards with hardware matrix units.
- **Built-in patches:** Dynamic FPS and Occlusion Off are back. Default settings are tuned for Ocarina.

## Stability

- Fixed:
  - a shutdown hang;
  - a page table overrun;
  - stale memory translations when a game is relaunched;
  - a channel deadlock;
  - fastmem on low-memory PCs;
  - a settings freeze while the loading screen is up;
  - a right-click crash on unusual game paths.
- Pipelines stay below 8,000 instead of being evicted after 2.5 minutes, and cached ones build newest first.
- Better crash diagnostics: `EDEN_NX2_PIPELINE_BUILD_TRACE` names the shaders a driver crashed on, and out-of-host-memory failures now explain the Windows page file.

## Switches worth knowing

Set these before starting `nyamu.exe`, for example: `set NAME=VALUE&& nyamu.exe`.

| Switch | What it does |
| --- | --- |
| `EDEN_NX2_PRECOMPILE=0` | Don't prepare the precompiled shaders. |
| `EDEN_NX2_READ_THROTTLE=1` | Bring back the 40 MB/s read pacing. |
| `EDEN_NX2_HOSTTIMING_SLEEP=0` | HostTiming waits in hardware again. |
| `EDEN_NX2_TRIM_ZERO=0` | No zero-page trimming. |
| `EDEN_NX2_SEC_RESERVE=0` | Commit all guest memory up front. |
| `EDEN_NX2_EVICT_KEEP_POINTERS=0` | The old behaviour for re-translated pipelines. |
| `EDEN_NX2_NUP_TRACE=1` | Log `.nup` / `.nyaa` decompression times. |
| `EDEN_NX2_PIPELINE_BUILD_TRACE=<file>` | Log every driver pipeline build, to find driver crashes. |

## Thanks

Lichii (zortos293), Fritchii (Frizti), AMD Superior, CheezerWheezer, sharkiix and nxnative, whose fixes make up much of this release.
