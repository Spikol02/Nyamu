// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#pragma once

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <optional>
#include <span>
#include <string>
#include <vector>

#include "common/common_types.h"

/// Requests from the debug server (EDEN_NX2_DEBUG_SERVER, docs/nx2/debug-mcp.md) to the thread
/// that owns the rasterizer. The server posts them from its own thread; RasterizerVulkan takes
/// them at the end of each presented frame, so every cache they touch is used from its owner.
/// Without the server nothing is posted and the per-frame cost is one relaxed atomic load.
namespace VideoCore::Nx2Debug {

/// Numbered experiment switches EDEN_NX2_EXP0 .. EDEN_NX2_EXP63 (from Fritchii): slots for fixes
/// that can be A/B-tested by their number. 0 = off. Kept experiments are on by default in
/// LoadExperiments; their variable set to 0 turns them off.
inline constexpr int ExperimentCount = 64;
inline std::atomic<int> g_experiments[ExperimentCount]{};

inline void LoadExperiments() {
    static const bool loaded = [] {
        for (int n = 0; n < ExperimentCount; ++n) {
            const std::string name = "EDEN_NX2_EXP" + std::to_string(n);
            if (const char* value = std::getenv(name.c_str())) {
                g_experiments[n].store(std::atoi(value), std::memory_order_relaxed);
            }
        }
        return true;
    }();
    static_cast<void>(loaded);
}

/// The value of experiment switch `n` (EDEN_NX2_EXP<n>), 0 unless set.
[[nodiscard]] inline int Experiment(int n) {
    LoadExperiments();
    return g_experiments[n].load(std::memory_order_relaxed);
}

struct CaptureRequest {
    u64 id;
    std::filesystem::path directory;
    bool sampled;
};

struct CaptureResult {
    std::string error;
};

struct RejectChange {
    u64 generation;
    /// Whether a hash left the list, so programs rejected before must be lowered again.
    bool unrejected;
};

/// Opens a debug server session (clears requests and rejections) or ends it (wakes waiters).
void BeginSession();
void EndSession();

/// Rasterizer thread: whether a capture or a reject change is waiting.
[[nodiscard]] bool HasRequests() noexcept;

/// Server thread: queues a frame capture; the rasterizer takes it at the next frame end.
void RequestCapture(CaptureRequest request);
/// Server thread: withdraws a capture that was not taken yet (it timed out).
void CancelCapture(u64 id);
/// Server thread: waits for the capture's manifest to be complete. Empty on timeout or when the
/// session ends.
[[nodiscard]] std::optional<CaptureResult> WaitCapture(u64 id, std::chrono::milliseconds timeout);
/// Rasterizer thread.
[[nodiscard]] std::optional<CaptureRequest> TakeCapture();
/// Frame dump writer thread, once the manifest summary is written (or the capture failed).
void FinishCapture(u64 id, CaptureResult result);

/// Server thread: adds or removes native program keys or shader hashes (as EDEN_NX2_REJECT_HASH
/// takes them). Returns the generation to wait for.
u64 SetRejected(std::span<const u64> hashes, bool rejected);
[[nodiscard]] std::vector<u64> RejectedHashes();
/// Lowering: whether the debug server rejects this program. Cheap while the list is empty.
[[nodiscard]] bool IsRejected(u64 hash);
[[nodiscard]] bool AnyRejected() noexcept;
/// Rasterizer thread: the pending reject change, once.
[[nodiscard]] std::optional<RejectChange> TakeRejectChange();
/// Rasterizer thread: the cached programs of this generation were dropped.
void NoteRejectApplied(u64 generation);
/// Server thread: waits until the rasterizer applied the generation.
[[nodiscard]] bool WaitRejectApplied(u64 generation, std::chrono::milliseconds timeout);

} // namespace VideoCore::Nx2Debug
