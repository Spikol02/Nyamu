// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

// SPDX-FileCopyrightText: Copyright 2023 yuzu Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#pragma once

#include <algorithm>
#include <array>
#include <cstdlib>
#include <utility>
#include <vector>

#include "common/alignment.h"
#include "common/common_types.h"

namespace VideoCommon {

class UsageTracker {
    // PAGE_SHIFT is a macro on FreeBSD
    static constexpr size_t BUFFER_BYTES_PER_BITSHIFT = 6;
    static constexpr size_t BUFFER_PAGE_SHIFT = 6 + BUFFER_BYTES_PER_BITSHIFT;
    static constexpr size_t BUFFER_PAGE_BYTES = 1 << BUFFER_PAGE_SHIFT;
public:
    explicit UsageTracker(size_t size) {
        const size_t num_pages = (size >> BUFFER_PAGE_SHIFT) + 1;
        pages.resize(num_pages, 0ULL);
        full_pages.resize((num_pages + 63) / 64, 0ULL);
    }

    void Reset() noexcept {
        std::ranges::fill(pages, 0ULL);
        std::ranges::fill(full_pages, 0ULL);
        recent.fill({~u64{0}, 0});
    }

    void Track(u64 offset, u64 size) noexcept {
        // Tracking is idempotent until Reset: skip ranges tracked just before. Draws bind the
        // same (often multi-megabyte) storage buffers again and again, and each Track wrote a
        // word per 4 KiB of the range.
        for (const auto& [recent_offset, recent_size] : recent) {
            if (recent_offset == offset && recent_size == size) {
                return;
            }
        }
        recent[recent_cursor++ % recent.size()] = {offset, size};
        const size_t page = offset >> BUFFER_PAGE_SHIFT;
        const size_t page_end = (offset + size) >> BUFFER_PAGE_SHIFT;
        if (page_end < page || page_end >= pages.size()) {
            return;
        }
        TrackPage(page, offset, size);
        if (page == page_end) {
            return;
        }
        if (SkipFullPages()) {
            FillPages(page + 1, page_end);
        } else {
            for (size_t i = page + 1; i < page_end; i++) {
                pages[i] = ~u64{0};
            }
        }
        const size_t offset_end = offset + size;
        const size_t offset_end_page_aligned = Common::AlignDown(offset_end, BUFFER_PAGE_BYTES);
        TrackPage(page_end, offset_end_page_aligned, offset_end - offset_end_page_aligned);
    }

    [[nodiscard]] bool IsUsed(u64 offset, u64 size) const noexcept {
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
    /// EDEN_NX2_USAGE_TRACKER_SKIP_FULL=0 writes every page of a range again, as before.
    static bool SkipFullPages() noexcept {
        static const bool skip = [] {
            const char* value = std::getenv("EDEN_NX2_USAGE_TRACKER_SKIP_FULL");
            return !(value && value[0] == '0');
        }();
        return skip;
    }

    /// Marks pages [begin, end) fully used. Draws bind the same multi-megabyte storage buffers
    /// many times between resets, so 64 pages at a time already full are skipped.
    void FillPages(size_t begin, size_t end) noexcept {
        for (size_t i = begin; i < end;) {
            const size_t word = i >> 6;
            const size_t bit = i & 63;
            const size_t count = std::min<size_t>(64 - bit, end - i);
            const u64 mask = (count == 64 ? ~u64{0} : (u64{1} << count) - 1) << bit;
            if ((full_pages[word] & mask) != mask) {
                std::fill_n(pages.begin() + i, count, ~u64{0});
                full_pages[word] |= mask;
            }
            i += count;
        }
    }

    void TrackPage(u64 page, u64 offset, u64 size) noexcept {
        const size_t offset_in_page = offset % BUFFER_PAGE_BYTES;
        const size_t first_bit = offset_in_page >> BUFFER_BYTES_PER_BITSHIFT;
        const size_t num_bits = std::min<size_t>(size, BUFFER_PAGE_BYTES) >> BUFFER_BYTES_PER_BITSHIFT;
        const size_t mask = ~u64{0} >> (64 - num_bits);
        pages[page] |= (~u64{0} & mask) << first_bit;
    }

    bool IsPageUsed(u64 page, u64 offset, u64 size) const noexcept {
        const size_t offset_in_page = offset % BUFFER_PAGE_BYTES;
        const size_t first_bit = offset_in_page >> BUFFER_BYTES_PER_BITSHIFT;
        const size_t num_bits = std::min<size_t>(size, BUFFER_PAGE_BYTES) >> BUFFER_BYTES_PER_BITSHIFT;
        const size_t mask = ~u64{0} >> (64 - num_bits);
        const size_t mask2 = (~u64{0} & mask) << first_bit;
        return (pages[page] & mask2) != 0;
    }

private:
    std::vector<u64> pages;
    /// One bit per page: set only when that page is all ones in pages.
    std::vector<u64> full_pages;
    /// Ranges tracked last, (offset, size).
    std::array<std::pair<u64, u64>, 4> recent{{{~u64{0}, 0}, {~u64{0}, 0}, {~u64{0}, 0}, {~u64{0}, 0}}};
    size_t recent_cursor{};
};

} // namespace VideoCommon
