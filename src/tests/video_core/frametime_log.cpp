// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#include <vector>

#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include "video_core/frametime_log.h"

TEST_CASE("Frame time summary reports averages and the slowest 1%", "[video_core][frametime]") {
    // 198 frames at 20 ms and 2 at 50 ms: the slowest 1% is the two 50 ms frames.
    std::vector<double> frames(198, 20.0);
    frames.push_back(50.0);
    frames.push_back(50.0);
    const auto summary = VideoCore::FrametimeLog::Summarize(frames);
    REQUIRE(summary.average_ms == Catch::Approx(20.3));
    REQUIRE(summary.average_fps == Catch::Approx(1000.0 / 20.3));
    REQUIRE(summary.one_percent_low_fps == Catch::Approx(20.0));
    REQUIRE(summary.p99_ms == Catch::Approx(50.0));
    REQUIRE(summary.max_ms == Catch::Approx(50.0));
}

TEST_CASE("Frame time summary of fewer than 100 frames uses the slowest frame",
          "[video_core][frametime]") {
    std::vector<double> frames{10.0, 40.0, 10.0};
    const auto summary = VideoCore::FrametimeLog::Summarize(frames);
    REQUIRE(summary.one_percent_low_fps == Catch::Approx(25.0));
    REQUIRE(summary.max_ms == Catch::Approx(40.0));
}
