<!--
# SPDX-FileCopyrightText: Copyright 2025 Eden Emulator Project
# SPDX-License-Identifier: GPL-3.0-or-later

# SPDX-FileCopyrightText: 2018 yuzu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
-->
<!-- lang: en-GB -->

<h1 align="center">
  <br>
  <img src="dist/lichii.png" alt="Nyamu" width="200">
  <br>
  <b>Nyamu</b>
  <br>
</h1>

<h4 align="center">A Switch 2 emulator made for one game on one kind of GPU:
<b>The Legend of Zelda: Ocarina of Time</b> on <b>AMD Radeon</b> graphics cards.</h4>

<p align="center">
  <a href="#what-nyamu-is">What Nyamu is</a> |
  <a href="#download">Download</a> |
  <a href="#first-start">First start</a> |
  <a href="#building">Building</a> |
  <a href="#credits">Credits</a> |
  <a href="#license">License</a>
</p>

## What Nyamu is

Nyamu is an unofficial fork of [Eglantine](https://github.com/Djihads80/Eglantine), derived from Eden, yuzu and Sudachi.
Every change is aimed at the Switch 2 remake of **Ocarina of Time** (program id `0400486024DEC000`) running on **AMD Radeon** cards with AMD's own driver.

- **AMD only.** Nyamu is built and tested on RDNA 4 (RX 9070 XT), with fixes for RDNA 2, RDNA 3 and 8 GB cards. NVIDIA, Intel and other drivers aren't supported. They may start, but nobody tests them and bugs on them won't be fixed.
- **Ocarina of Time only.** Other games aren't supported. Fixes and speed-ups are chosen and tuned for Ocarina, even when that makes other games worse.
- **What's in it:**
  - Ocarina's shaders are prepared before you play, so new areas don't stutter.
  - Faster loading: `.nyaa` packages and no file read cap.
  - Fixes for the ocarina crackle, water and waterfall flicker, foliage and hedges, and long-session corruption.
  - Crash fixes for AMD cards running out of video memory.
  - Default settings tuned for Ocarina.
- **What's changed:** see the [Nyamu 2.0 release notes](docs/releases/nyamu-2.0.md) for the full list.

Nyamu does not include any game, keys or firmware. You need your own legally dumped copy of Ocarina of Time and your own console keys.

## Download

Get the latest build from the [Releases page](https://github.com/Spikol02/Nyamu/releases).

- **Windows:** extract the zip and run `nyamu.exe`.
- **Linux:** run the AppImage (`chmod +x` first).

## First start

1. Add the folder with your Ocarina of Time dump (`.nup`, or a loose dump) to the game list.
2. Optional: right-click the `.nup` and choose **Convert to .nyaa (faster loading)...**.
3. On the first boot, Nyamu prepares Ocarina's shaders on the loading screen. This takes about 15 minutes once. You can skip it, and it continues where it stopped on the next boot.

Fine-tuning switches (environment variables) are listed in the [release notes](docs/releases/nyamu-2.0.md#switches-worth-knowing).

## Building

The reference build is Windows with clang-cl (LLVM), the Visual Studio 2022 SDK, Qt 6 and Ninja, run from an *x64 Native Tools Command Prompt for VS 2022*:

```bat
cmake -S C:\Nyamu -B C:\Nyamu\build -G Ninja -DCMAKE_BUILD_TYPE=Release "-DCMAKE_C_COMPILER=C:/Program Files/LLVM/bin/clang-cl.exe" "-DCMAKE_CXX_COMPILER=C:/Program Files/LLVM/bin/clang-cl.exe" -DCMAKE_PREFIX_PATH=C:/lx/qt
cmake --build C:\Nyamu\build --parallel
```

The program is `C:\Nyamu\build\bin\nyamu.exe`. For other platforms, see the [General Build Guide](docs/Build.md).

## Credits

Nyamu stands on the work of [Eglantine](https://github.com/Djihads80/Eglantine), Eden, yuzu and Sudachi. Much of what makes Ocarina run well comes from Lichii (zortos293), Fritchii (Frizti), AMD Superior, CheezerWheezer, sharkiix and nxnative.

## License

Nyamu is licensed under the GPLv3 (or any later version), like Eglantine. See [LICENSE.txt](LICENSE.txt).
