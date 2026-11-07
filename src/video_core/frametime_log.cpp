// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <numeric>
#include <string>
#include <system_error>
#include <vector>

#ifdef _WIN32
#include <process.h>
#define NX2_GETPID _getpid
#else
#include <unistd.h>
#define NX2_GETPID getpid
#endif

#include "common/fs/path_util.h"
#include "common/logging.h"
#include "video_core/frametime_log.h"

namespace VideoCore::FrametimeLog {

namespace {

using Clock = std::chrono::steady_clock;

struct State {
    bool enabled = false;
    double seconds = 60.0;
    std::filesystem::path trigger;
    std::filesystem::path csv_path;
    Clock::time_point next_check{};
    Clock::time_point last_present{};
    Clock::time_point segment_start{};
    bool recording = false;
    int segment = 0;
    std::vector<double> frames;
};

State& Get() {
    static State state = [] {
        State s;
        const char* value = std::getenv("EDEN_NX2_FRAMETIME_LOG");
        s.enabled = value && value[0] == '1';
        if (!s.enabled) {
            return s;
        }
        if (const char* seconds = std::getenv("EDEN_NX2_FRAMETIME_SECONDS")) {
            const double parsed = std::atof(seconds);
            if (parsed > 0.0) {
                s.seconds = parsed;
            }
        }
        const auto log_dir = Common::FS::GetEdenPath(Common::FS::EdenPath::LogDir);
        s.trigger = log_dir / "frametime.now";
        s.csv_path = log_dir / ("frametimes_" + std::to_string(NX2_GETPID()) + ".csv");
        LOG_INFO(Render, "Frame time log: create {} to record {:.0f} s", s.trigger.string(),
                 s.seconds);
        return s;
    }();
    return state;
}

void Finish(State& s) {
    s.recording = false;
    if (s.frames.empty()) {
        return;
    }
    if (std::FILE* file = std::fopen(s.csv_path.string().c_str(), "a")) {
        for (const double ms : s.frames) {
            std::fprintf(file, "%d,%.3f\n", s.segment, ms);
        }
        std::fclose(file);
    }
    const size_t count = s.frames.size();
    const Summary summary = Summarize(s.frames);
    LOG_INFO(Render,
             "Frame time segment {}: {} frames, average {:.1f} fps, 1% low {:.1f} fps, frame time "
             "average {:.2f} ms, 99th percentile {:.2f} ms, max {:.2f} ms",
             s.segment, count, summary.average_fps, summary.one_percent_low_fps,
             summary.average_ms, summary.p99_ms, summary.max_ms);
    s.frames.clear();
}

} // Anonymous namespace

Summary Summarize(std::span<double> frame_ms) {
    std::ranges::sort(frame_ms);
    const size_t count = frame_ms.size();
    const double total = std::accumulate(frame_ms.begin(), frame_ms.end(), 0.0);
    const size_t slow = (std::max<size_t>)(1, count / 100);
    const double slow_total = std::accumulate(frame_ms.end() - slow, frame_ms.end(), 0.0);
    const size_t p99_index = (std::min)(count - 1, count * 99 / 100);
    return Summary{
        .average_fps = 1000.0 * static_cast<double>(count) / total,
        .one_percent_low_fps = 1000.0 * static_cast<double>(slow) / slow_total,
        .average_ms = total / static_cast<double>(count),
        .p99_ms = frame_ms[p99_index],
        .max_ms = frame_ms.back(),
    };
}

void OnPresent() {
    State& s = Get();
    if (!s.enabled) {
        return;
    }
    const auto now = Clock::now();
    const auto last = s.last_present;
    s.last_present = now;
    if (s.recording) {
        s.frames.push_back(std::chrono::duration<double, std::milli>(now - last).count());
        if (now - s.segment_start >= std::chrono::duration<double>(s.seconds)) {
            Finish(s);
        }
        return;
    }
    if (now < s.next_check) {
        return;
    }
    s.next_check = now + std::chrono::milliseconds{250};
    std::error_code ec;
    if (!std::filesystem::exists(s.trigger, ec)) {
        return;
    }
    std::filesystem::remove(s.trigger, ec);
    s.recording = true;
    s.segment_start = now;
    ++s.segment;
    LOG_INFO(Render, "Frame time segment {}: recording {:.0f} s", s.segment, s.seconds);
}

} // namespace VideoCore::FrametimeLog
