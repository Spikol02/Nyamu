// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#pragma once

#include <span>

namespace VideoCore::FrametimeLog {

/// EDEN_NX2_FRAMETIME_LOG=1: once frametime.now exists in the log folder it is removed and the
/// next EDEN_NX2_FRAMETIME_SECONDS (default 60) of presented frames are recorded. The log gets
/// average fps, 1% lows and frame times; every frame time goes to frametimes_<pid>.csv.
/// Creating frametime.now again records another segment.
void OnPresent();

struct Summary {
    double average_fps;
    double one_percent_low_fps; ///< Frames per second over the slowest 1% of frames
    double average_ms;
    double p99_ms;
    double max_ms;
};

/// frame_ms must not be empty. Sorts it.
Summary Summarize(std::span<double> frame_ms);

} // namespace VideoCore::FrametimeLog
