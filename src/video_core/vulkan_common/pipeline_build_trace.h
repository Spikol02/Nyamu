// SPDX-License-Identifier: GPL-3.0-or-later

#pragma once

#include <array>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <mutex>
#include <thread>

#include "common/common_types.h"

namespace Vulkan::PipelineBuildTrace {

/// EDEN_NX2_PIPELINE_BUILD_TRACE=<file> (from Lichii #301): a line before and after every driver
/// pipeline build, flushed at once, so the last BEGIN without an END names the shaders a driver
/// crashed or hung on. END lines carry the build's wall time (ms=), which includes waiting on
/// other builds. Builds made outside a labelled scope (host blits, compute passes) show no hashes.
inline std::FILE* File() {
    static std::FILE* const file = [] {
        const char* const path = std::getenv("EDEN_NX2_PIPELINE_BUILD_TRACE");
        return path != nullptr && *path != '\0' ? std::fopen(path, "a") : nullptr;
    }();
    return file;
}

struct Label {
    const char* kind = nullptr;
    u64 key = 0;
    std::array<u64, 6> stages{};
};

inline thread_local Label t_label{};

/// Names the guest pipeline whose driver builds run on this thread until the scope ends.
class Scope {
public:
    explicit Scope(const char* kind, u64 key, const std::array<u64, 6>& stages = {})
        : previous{t_label} {
        t_label = Label{kind, key, stages};
    }
    ~Scope() {
        t_label = previous;
    }
    Scope(const Scope&) = delete;
    Scope& operator=(const Scope&) = delete;

private:
    Label previous;
};

inline void Write(const char* phase, const char* call, u32 flags, double elapsed_ms) {
    std::FILE* const file = File();
    if (file == nullptr) {
        return;
    }
    static std::mutex mutex;
    std::scoped_lock lock{mutex};
    const Label& label = t_label;
    std::fprintf(file, "%s %s %s pipeline=%016llx thread=%zu flags=%#x", phase,
                 label.kind != nullptr ? label.kind : "host", call,
                 static_cast<unsigned long long>(label.key),
                 std::hash<std::thread::id>{}(std::this_thread::get_id()), flags);
    for (size_t stage = 0; stage < label.stages.size(); ++stage) {
        if (label.stages[stage] != 0) {
            std::fprintf(file, " s%zu:%016llx", stage,
                         static_cast<unsigned long long>(label.stages[stage]));
        }
    }
    if (elapsed_ms >= 0.0) {
        std::fprintf(file, " ms=%.1f", elapsed_ms);
    }
    std::fputc('\n', file);
    std::fflush(file);
}

/// Wraps one driver call (vk::Device::CreateGraphicsPipeline / CreateComputePipeline).
template <typename Func>
auto Traced(const char* call, u32 flags, Func&& func) {
    if (File() == nullptr) {
        return func();
    }
    const auto begin = std::chrono::steady_clock::now();
    Write("BEGIN", call, flags, -1.0);
    auto result = func();
    Write("END", call, flags,
          std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - begin)
              .count());
    return result;
}

} // namespace Vulkan::PipelineBuildTrace
