// SPDX-License-Identifier: GPL-3.0-or-later

#include <atomic>

#ifdef _WIN32
#include <windows.h>
#include <psapi.h>
#endif

#include "common/common_types.h"
#include "common/logging.h"
#include "video_core/renderer_vulkan/pipeline_build_failure.h"

namespace Vulkan {

void ReportPipelineBuildFailure(VkResult result) {
    static std::atomic_flag reported;
    if (result != VK_ERROR_OUT_OF_HOST_MEMORY || reported.test_and_set()) {
        return;
    }
#ifdef _WIN32
    // The system-wide commit and limit (GetPerformanceInfo). GlobalMemoryStatusEx would give the
    // process's own limit instead when that is lower, which the page file doesn't change.
    PERFORMANCE_INFORMATION performance{};
    performance.cb = sizeof(performance);
    if (GetPerformanceInfo(&performance, sizeof(performance))) {
        const u64 commit = u64{performance.CommitTotal} * performance.PageSize;
        const u64 limit = u64{performance.CommitLimit} * performance.PageSize;
        constexpr double GiB = 1024.0 * 1024.0 * 1024.0;
        LOG_CRITICAL(Render_Vulkan,
                     "The GPU driver ran out of host memory building a pipeline: Windows commit is "
                     "{:.1f} of {:.1f} GiB. The commit limit is RAM plus the page file, and a "
                     "Switch 2 game can commit up to 14 GiB of guest memory. If commit is "
                     "near the limit, enlarge the page file. Pipelines that fail draw nothing.",
                     static_cast<double>(commit) / GiB,
                     static_cast<double>(limit) / GiB);
        return;
    }
#endif
    LOG_CRITICAL(Render_Vulkan,
                 "The GPU driver ran out of host memory building a pipeline. Close other programs "
                 "or add swap; until then, pipelines that fail draw nothing.");
}

} // namespace Vulkan
