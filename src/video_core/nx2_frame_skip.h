// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#pragma once

#include <atomic>
#include <chrono>
#include <cstdlib>

#include "common/common_types.h"
#include "common/logging.h"

// Frame skipping for Switch 2 titles (EDEN_NX2_FRAMESKIP=<n>, n = most frames skipped in a row,
// 0 = off). When the game presents slower than its swap interval asks, the GPU thread drops the
// draws of the next frame (not compute, clears, copies or draws that write memory, which later
// work reads) and the frame is not shown: the previous one stays on screen. The GPU thread's
// work for that frame shrinks, the game waits less on its fences, and game time keeps closer to
// real time at the cost of shown frames.
namespace VideoCommon::Nx2FrameSkip {

inline int MaxSkipped() {
    static const int value = [] {
        const char* env = std::getenv("EDEN_NX2_FRAMESKIP");
        if (env == nullptr) {
            return 0;
        }
        const int n = std::atoi(env);
        return n < 0 ? 0 : (n > 4 ? 4 : n);
    }();
    return value;
}

inline bool Enabled() {
    return MaxSkipped() > 0;
}

struct State {
    /// Set by the present thread: the last frame came later than the swap interval asked.
    std::atomic<bool> behind{};
    /// Read on the GPU thread for every draw: drop draws of the current frame.
    std::atomic<bool> skipping{};
    // GPU thread only (frame boundaries).
    int skipped_in_a_row{};
    u64 frames{};
    u64 skipped_frames{};
    u64 skipped_draws{};
    u64 kept_draws{};
    std::chrono::steady_clock::time_point window_start{};
};

inline State& Get() {
    static State state;
    return state;
}

/// Present thread: how long the last frame took against what its swap interval asks.
inline void NotePresent(double frame_ms, u32 swap_interval) {
    if (!Enabled()) {
        return;
    }
    const double target_ms = 1000.0 / 60.0 * (swap_interval == 0 ? 1 : swap_interval);
    // 10% slack: a frame a little late now and then is vblank jitter, not slowdown.
    Get().behind.store(frame_ms > target_ms * 1.10, std::memory_order_relaxed);
}

/// GPU thread, at a frame's end (its present fence): whether that frame was skipped, and
/// whether to skip the next.
inline bool EndFrame() {
    auto& state = Get();
    const bool was_skipped = state.skipping.load(std::memory_order_relaxed);
    if (!Enabled()) {
        return false;
    }
    ++state.frames;
    if (was_skipped) {
        ++state.skipped_frames;
        ++state.skipped_in_a_row;
    } else {
        state.skipped_in_a_row = 0;
    }
    const bool skip_next = state.behind.load(std::memory_order_relaxed) &&
                           state.skipped_in_a_row < MaxSkipped();
    state.skipping.store(skip_next, std::memory_order_relaxed);

    const auto now = std::chrono::steady_clock::now();
    if (state.window_start == std::chrono::steady_clock::time_point{}) {
        state.window_start = now;
    } else if (now - state.window_start >= std::chrono::seconds{10}) {
        LOG_INFO(HW_GPU,
                 "Frame skip: {} of {} frames skipped in the last 10 s ({} draws dropped, {} kept "
                 "in skipped frames)",
                 state.skipped_frames, state.frames, state.skipped_draws, state.kept_draws);
        state.frames = state.skipped_frames = state.skipped_draws = state.kept_draws = 0;
        state.window_start = now;
    }
    return was_skipped;
}

/// GPU thread, per draw: whether to drop it.
inline bool SkipDraw(bool writes_memory) {
    auto& state = Get();
    if (!state.skipping.load(std::memory_order_relaxed)) {
        return false;
    }
    if (writes_memory) {
        ++state.kept_draws;
        return false;
    }
    ++state.skipped_draws;
    return true;
}

} // namespace VideoCommon::Nx2FrameSkip
