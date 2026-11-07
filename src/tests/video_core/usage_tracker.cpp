// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#include <algorithm>
#include <random>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "common/alignment.h"
#include "video_core/buffer_cache/usage_tracker.h"

namespace {

// UsageTracker as it was before full pages were skipped.
class ReferenceTracker {
    static constexpr size_t BUFFER_PAGE_SHIFT = 12;
    static constexpr size_t BUFFER_PAGE_BYTES = size_t{1} << BUFFER_PAGE_SHIFT;

public:
    explicit ReferenceTracker(size_t size) : pages((size >> BUFFER_PAGE_SHIFT) + 1, 0) {}

    void Track(u64 offset, u64 size) {
        const size_t page = offset >> BUFFER_PAGE_SHIFT;
        const size_t page_end = (offset + size) >> BUFFER_PAGE_SHIFT;
        if (page_end < page || page_end >= pages.size()) {
            return;
        }
        TrackPage(page, offset, size);
        if (page == page_end) {
            return;
        }
        for (size_t i = page + 1; i < page_end; i++) {
            pages[i] = ~u64{0};
        }
        const size_t offset_end = offset + size;
        const size_t offset_end_page_aligned = Common::AlignDown(offset_end, BUFFER_PAGE_BYTES);
        TrackPage(page_end, offset_end_page_aligned, offset_end - offset_end_page_aligned);
    }

    bool IsUsed(u64 offset, u64 size) const {
        const size_t page = offset >> BUFFER_PAGE_SHIFT;
        const size_t page_end = (offset + size) >> BUFFER_PAGE_SHIFT;
        if (page_end < page || page_end >= pages.size()) {
            return false;
        }
        if (IsPageUsed(page, offset, size)) {
            return true;
        }
        for (size_t i = page + 1; i < page_end; i++) {
            if (pages[i] != 0) {
                return true;
            }
        }
        const size_t offset_end = offset + size;
        const size_t offset_end_page_aligned = Common::AlignDown(offset_end, BUFFER_PAGE_BYTES);
        return IsPageUsed(page_end, offset_end_page_aligned, offset_end - offset_end_page_aligned);
    }

private:
    static u64 Mask(u64 offset, u64 size) {
        const size_t first_bit = (offset % BUFFER_PAGE_BYTES) >> 6;
        const size_t num_bits = (std::min<size_t>)(size, BUFFER_PAGE_BYTES) >> 6;
        return (~u64{0} >> (64 - num_bits)) << first_bit;
    }

    void TrackPage(u64 page, u64 offset, u64 size) {
        pages[page] |= Mask(offset, size);
    }

    bool IsPageUsed(u64 page, u64 offset, u64 size) const {
        return (pages[page] & Mask(offset, size)) != 0;
    }

    std::vector<u64> pages;
};

} // Anonymous namespace

TEST_CASE("UsageTracker skips full pages without changing what it tracks",
          "[video_core][usage_tracker]") {
    constexpr size_t buffer_size = 4 << 20;
    std::mt19937_64 rng{1234};
    for (int round = 0; round < 20; ++round) {
        VideoCommon::UsageTracker tracker{buffer_size};
        ReferenceTracker reference{buffer_size};
        if (round % 2 == 1) {
            tracker.Reset();
        }
        std::uniform_int_distribution<u64> offset_dist{0, buffer_size - 1};
        for (int i = 0; i < 300; ++i) {
            // Sizes that end less than 64 bytes into a page shift by 64 in TrackPage: skip them.
            const u64 offset = offset_dist(rng) & ~u64{63};
            const u64 size = std::uniform_int_distribution<u64>{64, 1 << 20}(rng) & ~u64{63};
            if (offset + size >= buffer_size || (offset + size) % 4096 < 64) {
                continue;
            }
            tracker.Track(offset, size);
            reference.Track(offset, size);
        }
        for (u64 page = 0; page < (buffer_size >> 12); ++page) {
            for (u64 chunk = 0; chunk < 63; chunk += 7) {
                const u64 offset = (page << 12) + (chunk << 6);
                REQUIRE(tracker.IsUsed(offset, 64) == reference.IsUsed(offset, 64));
            }
        }
    }
}

TEST_CASE("UsageTracker forgets full pages on reset", "[video_core][usage_tracker]") {
    VideoCommon::UsageTracker tracker{1 << 20};
    tracker.Track(0, 1 << 19);
    REQUIRE(tracker.IsUsed(1 << 18, 64));
    tracker.Reset();
    REQUIRE_FALSE(tracker.IsUsed(1 << 18, 64));
    tracker.Track(0, 1 << 19);
    REQUIRE(tracker.IsUsed(1 << 18, 64));
}
