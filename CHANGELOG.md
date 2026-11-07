# Changelog

All notable changes to Eglantine are documented here. Each release's notes are taken from its
`## [X.Y.Z]` section (release candidates use the section of the version they are a candidate for).
New entries go under `## [Unreleased]`; `tools/ci/bump-version.sh` turns them into a version section.

## [Unreleased]

Nyamu is an unofficial, experimental build tuned for The Legend of Zelda: Ocarina of Time. It is
based on the Selevet fork of Eglantine (Eglantine builds on Eden and yuzu). All credit for the
emulator itself goes to those projects. Please don't report problems with Nyamu to them.

### New

- Merged sharkiix v3.1 ([Shakoquo/Lichii-sharkiix-version](https://github.com/Shakoquo/Lichii-sharkiix-version)):
  - Ocarina of Time fixes from Lichii build 11.
  - Graphics pipeline libraries ("shader parts") on AMD.
  - A clang-cl build fix.
- Navi no longer flickers. Her glow used to jump away from her body from frame to frame. It stays
  on her now.

### Restored after the merge

- Nyamu branding: the program is called Nyamu again (title bar and About box), the catgirl icon is
  back, and the program file is `nyamu.exe` again.
- Built-in Ocarina of Time patches: *Dynamic FPS* and *Occlusion Off* are back next to *Max
  Resolution*. Turn them on in the game's **Properties → Add-ons**.

### Build

- The unit tests compile with clang-cl again (`core_timing` no longer stops the build).
- clang-cl release builds now keep function names and line numbers in `nyamu.pdb`, so crash dumps
  and the profiler can show readable function names. The generated code is unchanged.

### Performance work (in progress)

- The built-in CPU sampling profiler is back (`EDEN_NX2_SAMPLE_PROFILE`). Reports are made with
  `tools/nx2/sample_report.py`.
- Experimental: the emulator's GPU thread no longer stops to wait at a semaphore when a GPU command
  already queued will produce the value it's waiting for. Values are tracked per 4-byte semaphore,
  including 3D report writes. This is on by default; `EDEN_NX2_PREDICT_ACQUIRE=0` turns it off.
- `EDEN_NX2_GPU_PROFILE=1` now also logs how often that skip applies, and why it doesn't when it
  doesn't.
- No FPS gain measured yet. With the 30 fps cap removed (`EDEN_NX2_SWAP_INTERVAL=1` and Dynamic
  FPS on), Kokiri Forest ran at about 25–26 fps on a Ryzen 9 5900X with an RX 9070 XT before the
  per-semaphore tracking; that version hasn't been measured yet. The default 30 fps cap is
  unchanged.

### You need

- Your own keys, firmware and games. None are included.

## [0.1.0]

- Initial release candidate
