// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <memory>
#include <optional>
#include <thread>
#include <type_traits>
#include <unordered_map>
#include <unordered_set>

#include "common/common_types.h"

namespace Core {
class System;
}

namespace Tegra::Engines {
class EngineInterface;
}

namespace VideoCommon {

/// Why the split's front waited for its back (EDEN_NX2_GPU_PROFILE counts each).
enum class DrainSite : u32 {
    Other,
    FetchDrain,     ///< EDEN_NX2_FETCH_DRAIN: before every entry
    Acquire,        ///< A semaphore acquire reads the value the back may release
    SemaphoreFlush, ///< The acquired semaphore is in the buffer cache
    FenceRelease,   ///< The puller's acquires release the pending fences
    ListWriters,    ///< GPU-written command lists: the dispatches and copies sent before them
    ListSync,       ///< Command lists on draw-written pages: the work before the last host sync
    ListFlush,      ///< Command lists on GPU-written pages: drain and flush the pages
    MacroDmaRead,   ///< The front's macros read memory (MME DMA)
    MacroDmaFlush,  ///< Those words are in the buffer cache
    GpSyncFence,    ///< A GP entry's sync bit (sync_memory_operations)
    Map,            ///< GPU memory maps before later command reads
    FrontStart,     ///< The front takes an engine's macros over
    FrontVerify,    ///< EDEN_NX2_FRONT_VERIFY
    Count,
};

inline constexpr std::array<const char*, static_cast<size_t>(DrainSite::Count)> drain_site_names{
    "other",       "fetch drain",     "acquire",    "semaphore flush", "fence release",
    "list writers", "list sync",      "list flush", "macro dma read",  "macro dma flush",
    "gp sync fence", "map",           "front start", "front verify",
};

// Splits the GPU thread in two (NX2_GPU_THREAD_SPLIT.md). The GPU thread (the front) takes the
// CPU's commands, parses command lists and runs the puller; engine method calls and everything
// behind them (register state, macros, draws, the rasterizer and its caches) run in order on a
// second thread (the back), fed through a single-producer single-consumer ring. Work the front
// would do on back-owned state is deferred into the same ring; the front waits for the back to
// catch up (Drain) only where it needs a result from that state, e.g. a semaphore it acquires.
// On for Switch 2 titles with the 3D and compute engines' macros on the front, against copies of
// the registers they read (Maxwell3D and KeplerCompute Front*), which sends the writes they make.
// EDEN_NX2_SPLIT=1 keeps the macros on the back; EDEN_NX2_SPLIT=0 runs everything on one thread.
class GpuSplit {
public:
    static GpuSplit& Get() {
        return instance;
    }

    /// Whether the front sends work to the back (front thread only).
    [[nodiscard]] bool Active() const {
        return active;
    }
    /// Whether the front runs the engines' macros (EDEN_NX2_SPLIT=2).
    [[nodiscard]] bool FrontMacros() const {
        return front_macros;
    }

    /// EDEN_NX2_MACRO_SINK=1 (default off): the state writes the front's macros make (3D
    /// registers outside execution_mask; native compute registers that only store their value)
    /// go to the back's method sink like the command list's own state writes, instead of one
    /// CallMethod each; the back applies its sink, in order, before the engine's next executable
    /// method. Compute also drops writes that keep a register's value.
    [[nodiscard]] static bool MacroSink() {
        return macro_sink;
    }
    /// EDEN_NX2_RING_PAIRS=1 (default off): the state writes that would go to the back's method
    /// sink (the command list's 3D state writes; with EDEN_NX2_MACRO_SINK the macros' 3D and
    /// compute ones too) go as (method, value) pairs in Pairs blocks (PairWrite), which the back
    /// applies in one loop per block (EngineInterface::ApplyPairs), after its method sink.
    [[nodiscard]] static bool RingPairs() {
        return ring_pairs;
    }

    /// The front's writes that took a batched path (EDEN_NX2_MACRO_SINK, EDEN_NX2_RING_PAIRS),
    /// logged every 10 s.
    enum class Batched : u32 {
        MacroSunk,   ///< 3D macro state writes sent to the method sink (or as pairs)
        ComputeSunk, ///< Compute state writes sent to the method sink (or as pairs)
        ComputeSame, ///< Compute state writes dropped: the register held the value
        Pairs,       ///< Writes sent as pairs (any engine, any source)
        PairBlocks,  ///< Pairs blocks opened
        Count,
    };
    void CountBatched(Batched counter, u64 amount = 1) {
        batched[static_cast<size_t>(counter)] += amount;
    }

    /// Whether the calling thread is the back.
    [[nodiscard]] static bool OnBackThread() {
        return on_back_thread;
    }
    /// Called on the back before it sleeps waiting for work (the rasterizer submits deferred work).
    void SetIdleHook(void (*hook)(void*), void* argument) {
        idle_hook = hook;
        idle_argument = argument;
    }
    /// Any thread: whether the back thread runs (false before it starts and once it stopped).
    [[nodiscard]] bool BackRunning() const {
        return back_running.load(std::memory_order_acquire);
    }
    /// Any thread: has the back call the idle hook soon, between entries or at once if it sleeps.
    void RequestIdleHook() {
        hook_requested.store(true, std::memory_order_seq_cst);
        wake.fetch_add(1, std::memory_order_seq_cst);
        wake.notify_one();
    }

    /// Starts the back thread once the split is wanted (EDEN_NX2_SPLIT=1, a Switch 2 title,
    /// commands read without cache flushes). Called by the front between commands.
    void MaybeStart(Core::System& system);
    void Stop();

    /// An engine method write, executed as DmaPusher would (method sink, ConsumeSink, call).
    /// `segment`: the engine's current_dma_segment for the call, when the engine reads it.
    void Method(Tegra::Engines::EngineInterface* engine, u32 method, u32 argument, bool is_last,
                const GPUVAddr* segment) {
        SelectEngine(engine);
        const size_t words = segment != nullptr ? 2 : 1;
        u64* const entry = Reserve(words);
        entry[0] = static_cast<u64>(Kind::Method) | (is_last ? IsLast : 0) |
                   (segment != nullptr ? HasSegment : 0) | (u64{method & 0xffff} << 8) |
                   (u64{argument} << 32);
        if (segment != nullptr) {
            entry[1] = *segment;
        }
        Commit(words);
    }
    void MultiMethod(Tegra::Engines::EngineInterface* engine, u32 method, const u32* words,
                     u32 amount, u32 methods_pending, const GPUVAddr* segment);
    /// A write made by a macro: CallMethod without the method sink, as the engines' macros do.
    void MacroWrite(Tegra::Engines::EngineInterface* engine, u32 method, u32 argument) {
        SelectEngine(engine);
        u64* const entry = Reserve(1);
        entry[0] = static_cast<u64>(Kind::Method) | IsLast | Direct | (u64{method & 0xffff} << 8) |
                   (u64{argument} << 32);
        Commit(1);
    }
    /// EDEN_NX2_MACRO_SINK: a write that only stores a register's value, to an engine whose
    /// execution_mask has the register (native compute): the back puts it in the engine's method
    /// sink tagged EngineInterface::SplitPlainTag, applied before the engine's next other write.
    void SinkMethod(Tegra::Engines::EngineInterface* engine, u32 method, u32 argument) {
        SelectEngine(engine);
        u64* const entry = Reserve(1);
        entry[0] = static_cast<u64>(Kind::Method) | IsLast | Sink | (u64{method & 0xffff} << 8) |
                   (u64{argument} << 32);
        Commit(1);
    }
    /// EDEN_NX2_RING_PAIRS: a write that only stores a register's value (no execution_mask side
    /// effect; native compute: native_plain_methods), appended to the open Pairs block. A block
    /// is open only until anything else is reserved or the ring is published (Reserve, Publish),
    /// so published entries never change and a block never crosses the ring's end.
    void PairWrite(Tegra::Engines::EngineInterface* engine, u32 method, u32 argument) {
        SelectEngine(engine);
        const u64 pair = u64{method & 0xffff} | (u64{argument} << 32);
        const size_t index = write_position & RingMask;
        if (pairs_header != nullptr && index != 0 && pairs_count < MaxPairs &&
            write_position + 1 - consumed_cache <= RingWords) [[likely]] {
            ring[index] = pair;
            ++pairs_count;
            *pairs_header = static_cast<u64>(Kind::Pairs) | (u64{pairs_count} << 32);
            CountBatched(Batched::Pairs);
            Commit(1);
            return;
        }
        u64* const entry = Reserve(2);
        entry[0] = static_cast<u64>(Kind::Pairs) | (u64{1} << 32);
        entry[1] = pair;
        pairs_header = entry;
        pairs_count = 1;
        CountBatched(Batched::Pairs);
        CountBatched(Batched::PairBlocks);
        Commit(2);
    }

    /// Runs `func` on the back after everything sent before it. Must be trivially copyable.
    template <typename Func>
    void Defer(const Func& func) {
        static_assert(std::is_trivially_copyable_v<Func> && alignof(Func) <= alignof(u64));
        constexpr size_t payload = (sizeof(Func) + sizeof(u64) - 1) / sizeof(u64);
        u64* const entry = Reserve(2 + payload);
        entry[0] = static_cast<u64>(Kind::Call) | (u64{payload} << 8);
        entry[1] = reinterpret_cast<u64>(&CallThunk<Func>);
        std::memcpy(entry + 2, &func, sizeof(Func));
        Commit(2 + payload);
    }
    void DeferFunction(std::function<void()>&& func);
    /// Runs `func` on the back and waits for it: for work on back-owned state, such as a buffer
    /// cache download that records into the Vulkan scheduler. A drained back still submits from
    /// its idle hook, so the front never touches that state itself.
    template <typename Func>
    void Run(const Func& func, DrainSite site = DrainSite::Other) {
        if (on_back_thread || !active) {
            func();
            return;
        }
        Defer(func);
        Drain(site);
    }

    /// EDEN_NX2_SPLIT_CHECK=1 (debug): reports, with its stack, a thread other than the back
    /// using back-owned state (the Vulkan scheduler) while the back runs entries.
    static inline bool check_owner{};
    static void CheckOwner(const char* where);

    /// Makes what was sent visible to the back.
    void Publish();
    /// Waits until the back executed everything sent so far.
    void Drain(DrainSite site = DrainSite::Other);

    /// Compute or copy work was just sent: what can write GPU-written command lists.
    void NoteWriter() {
        last_writer_position = write_position;
    }
    /// Waits until the back executed the compute and copy work sent so far (not what came after).
    /// Returns whether any of it was still queued.
    bool WaitForWriters(DrainSite site = DrainSite::ListWriters);
    bool WaitForSyncedWriters(DrainSite site = DrainSite::ListWriters);
    /// Work that writes the device range [begin, begin + size) was just sent (an inline upload, a
    /// report, a semaphore release). size ~0 means an unknown range.
    void NoteMemoryWrite(u64 begin, u64 size, const char* what = "unknown") {
        PendingWrite& write = pending_writes[pending_write_count++ % pending_writes.size()];
        write.what = what;
        write.begin = begin;
        write.end = size == ~u64{0} ? ~u64{0} : begin + size;
        write.position = write_position;
        write.words = 0;
    }
    /// As NoteMemoryWrite, for a write whose words the front knows (a fence release payload,
    /// linear inline upload data): reads overlay them instead of waiting.
    void NoteMemoryWriteData(u64 begin, const u32* data, u32 words) {
        while (words != 0) {
            const u32 count = (std::min<u32>)(words, PendingWriteWords);
            PendingWrite& write = pending_writes[pending_write_count++ % pending_writes.size()];
            write.what = "known";
            write.begin = begin;
            write.end = begin + u64{count} * sizeof(u32);
            write.position = write_position;
            write.words = count;
            std::memcpy(write.data.data(), data, count * sizeof(u32));
            begin += u64{count} * sizeof(u32);
            data += count;
            words -= count;
        }
    }
    /// The back's position, for OverlayQueuedWrites: take it before reading guest memory.
    [[nodiscard]] u64 ConsumedNow() {
        return consumed.load(std::memory_order_acquire);
    }
    /// Applies to `out` (the words at device address `begin`, read from guest memory after
    /// `since` = ConsumedNow()) the queued writes the front knows the words of. False when an
    /// overlapping queued write's words are unknown (the caller waits instead).
    bool OverlayQueuedWrites(u64 since, u64 begin, u32* out, u32 words);
    /// Waits until the back executed the noted writes that overlap [begin, begin + size).
    /// Returns false when more writes are queued than are remembered (the caller drains).
    bool WaitForWritesTo(u64 begin, u64 size);
    /// Whether a noted write that overlaps [begin, begin + size) is still queued (or too many
    /// are queued to tell).
    bool HasQueuedWritesTo(u64 begin, u64 size);
    /// Front: a write that lands when a host query completes or a fence signals (counter
    /// reports, timed releases), not when the back runs it (Tegra::Nx2AsyncReportsSettled).
    void NoteAsyncWrite(u64 begin, u64 size) {
        NoteReportWrite(begin, size);
        for (u64 slot = begin >> 4; slot <= (begin + size - 1) >> 4; ++slot) {
            async_writes[slot] = write_position;
        }
    }
    /// Front: whether these bytes lie on 4 KiB pages reports or semaphore releases write (query
    /// and semaphore pools), which draws and dispatches don't write.
    [[nodiscard]] bool IsReportMemory(u64 begin, u64 size) const {
        for (u64 page = begin >> 12; page <= (begin + size - 1) >> 12; ++page) {
            if (!report_pages.contains(page)) {
                return false;
            }
        }
        return true;
    }
    /// Front: a report or semaphore release was sent to these bytes.
    void NoteReportWrite(u64 begin, u64 size) {
        for (u64 page = begin >> 12; page <= (begin + size - 1) >> 12; ++page) {
            report_pages.insert(page);
        }
    }
    /// Front: the ring position of the newest async write noted to these bytes, or 0.
    [[nodiscard]] u64 HasAsyncWrite(u64 begin, u64 size) const {
        u64 newest = 0;
        if (async_writes.empty()) {
            return newest;
        }
        for (u64 slot = begin >> 4; slot <= (begin + size - 1) >> 4; ++slot) {
            if (const auto it = async_writes.find(slot); it != async_writes.end()) {
                newest = (std::max)(newest, it->second);
            }
        }
        return newest;
    }

    /// A host-level wait was just sent (a syncpoint acquire, a host WFI, a GP entry with its sync
    /// bit; semaphore acquires drain): the guest's GPU work before it is complete before the
    /// commands after it are fetched.
    void NoteSync() {
        last_sync_position = write_position;
    }
    /// Waits until the back executed the work sent before the last NoteSync. Returns whether any
    /// of it was still queued.
    bool WaitForSync();

    /// Semaphore and report writes are tracked per 16-byte granule, tagged: an untagged 64-slot
    /// hash made unrelated macro reads wait for the back (~250 ms a second in Ocarina).
    static constexpr size_t SemSlot(u64 granule) noexcept {
        return static_cast<size_t>((granule ^ (granule >> 10) ^ (granule >> 20)) & (SemSlots - 1));
    }
    /// `payload`: the value the write stores at `addr`, when the front knows it (a plain
    /// release); `size`: the bytes it writes (16 with a timestamp, 4 for a one-word release).
    void NoteSemaphoreWrite(u64 addr, std::optional<u32> payload = std::nullopt,
                            u64 size = 16) noexcept {
        const u64 granule = addr >> 4;
        SemWrite& slot = sem_writes[SemSlot(granule)];
        // Evicting a write the back has not run yet: later lookups can't see it, so wait for it.
        if (slot.granule != granule && slot.position > consumed_cache) {
            sem_evicted_position = std::max(sem_evicted_position, slot.position);
        }
        slot = {granule, write_position};
        // Per word: Switch 2 packs semaphores tightly, so one granule holds several.
        for (u64 word = addr >> 2; word <= (addr + size - 1) >> 2; ++word) {
            const bool known = payload && word == addr >> 2;
            word_writes[WordSlot(word)] = {word, write_position, known ? *payload : 0, known};
        }
        last_release_position = write_position;
    }
    /// The value a release still queued for the back will store at `addr`, when it is the last
    /// write sent to that word, its value is known and no compute or copy work (which could
    /// also write there) was sent after it. The back runs it before anything sent later, so an
    /// acquire it meets needs no wait.
    enum class ReleaseMiss : u8 { None, NoWrite, OtherAddress, UnknownValue, WriterAfter, Ran };
    struct ReleaseLookup {
        std::optional<u32> value;
        ReleaseMiss miss{};
        u64 position{}; ///< Where the release is queued (when value is set)
    };
    [[nodiscard]] ReleaseLookup QueuedRelease(u64 addr) const noexcept {
        if ((addr & 3) != 0) {
            return {std::nullopt, ReleaseMiss::OtherAddress};
        }
        const WordWrite& slot = word_writes[WordSlot(addr >> 2)];
        if (slot.word != addr >> 2) {
            return {std::nullopt, ReleaseMiss::NoWrite};
        }
        if (!slot.known) {
            return {std::nullopt, ReleaseMiss::UnknownValue};
        }
        if (slot.position < last_writer_position) {
            return {std::nullopt, ReleaseMiss::WriterAfter};
        }
        if (slot.position <= consumed.load(std::memory_order_acquire)) {
            return {std::nullopt, ReleaseMiss::Ran};
        }
        return {slot.payload, ReleaseMiss::None, slot.position};
    }
    /// An acquire went on without waiting because the release queued at `position` meets it.
    /// The guest takes the acquire to mean the GPU work before that release is done, so the
    /// front's own reads of GPU-written memory (macro DMA reads, GPU-written command lists)
    /// wait for the back to reach it: a 3D report release is sent after the work it guards, and
    /// reading ahead of that work made Ocarina of Time's fountain flowers flicker.
    /// EDEN_NX2_PREDICT_SYNC=0 goes on without that wait, as before.
    void NotePredictedRelease(u64 position) noexcept {
        if (PredictSyncEnabled()) {
            predicted_release_position = std::max(predicted_release_position, position);
            last_sync_position = std::max(last_sync_position, position);
        }
    }
    /// Waits until the back ran the releases acquires were predicted to be met by.
    bool WaitForPredictedReleases(DrainSite site) {
        return WaitFor(std::min(predicted_release_position, write_position), site);
    }
    static bool PredictSyncEnabled() noexcept {
        static const bool enabled = [] {
            const char* value = std::getenv("EDEN_NX2_PREDICT_SYNC");
            return !(value && value[0] == '0');
        }();
        return enabled;
    }
    void NoteAnyRelease() noexcept {
        last_release_position = write_position;
    }
    [[nodiscard]] u64 LastReleasePosition() const noexcept {
        return last_release_position;
    }
    /// Waits for the queued semaphore and report writes to [addr, addr + size).
    bool WaitForSemaphore(u64 addr, DrainSite site = DrainSite::Acquire, u64 size = 4) {
        u64 target = sem_evicted_position;
        for (u64 granule = addr >> 4; granule <= (addr + size - 1) >> 4; ++granule) {
            const SemWrite& slot = sem_writes[SemSlot(granule)];
            if (slot.granule == granule) {
                target = std::max(target, slot.position);
            }
        }
        return WaitFor(std::min(target, write_position), site);
    }

private:
    enum class Kind : u64 {
        Wrap,   ///< The rest of the ring is unused: continue at its start
        Engine, ///< The engine the next methods go to: word 1
        Method, ///< Bits 8-23 method, 32-63 argument; flags IsLast, HasSegment (word 1)
        Multi,  ///< Bits 8-23 method, 32-63 amount; word 1 methods pending, [segment], words
        Call,   ///< Bits 8-31 payload size in words; word 1 thunk, then the payload
        Pairs,  ///< Bits 32-63 count; then one word per write: bits 0-15 method, 32-63 value
    };
    static constexpr u64 IsLast = 1 << 4;
    static constexpr u64 HasSegment = 1 << 5;
    static constexpr u64 Direct = 1 << 6; ///< Method: call it even if not executable (macros)
    static constexpr u64 Sink = 1 << 7;   ///< Method: into the method sink, tagged (SinkMethod)

    static inline const bool macro_sink = [] {
        const char* value = std::getenv("EDEN_NX2_MACRO_SINK");
        return value != nullptr && value[0] != '0';
    }();
    static inline const bool ring_pairs = [] {
        const char* value = std::getenv("EDEN_NX2_RING_PAIRS");
        return value != nullptr && value[0] != '0';
    }();
    static constexpr u32 MaxPairs = 4096;

    std::unordered_map<u64, u64> async_writes; ///< Front: 16-byte slot -> ring position
    std::unordered_set<u64> report_pages;      ///< Front: 4 KiB pages reports and releases wrote

    static constexpr size_t RingWords = size_t{1} << 20; // 8 MiB
    static constexpr size_t RingMask = RingWords - 1;
    static constexpr u32 MaxMultiWords = 8192;
    static constexpr size_t PublishEvery = 256;

    template <typename Func>
    static void CallThunk(const u64* payload) {
        (*reinterpret_cast<const Func*>(payload))();
    }

    /// Room for an entry of `words` at the write position (never across the ring's end).
    u64* Reserve(size_t words) {
        pairs_header = nullptr; // What follows ends the open Pairs block
        const size_t index = write_position & RingMask;
        if (index + words <= RingWords && write_position + words - consumed_cache <= RingWords)
            [[likely]] {
            return &ring[index];
        }
        return ReserveSlow(words);
    }
    u64* ReserveSlow(size_t words);
    void Commit(size_t words) {
        write_position += words;
        if (write_position - published_local >= publish_every) {
            Publish();
        }
    }
    void WaitForSpace(size_t words);
    void SelectEngine(Tegra::Engines::EngineInterface* engine) {
        if (engine != front_engine) [[unlikely]] {
            SwitchEngine(engine);
        }
    }
    void SwitchEngine(Tegra::Engines::EngineInterface* engine);
    bool WaitFor(u64 target, DrainSite site);

    void BackLoop(std::stop_token stop_token);
    size_t Execute(const u64* entry);

    Core::System* system{};
    bool active{};
    bool front_macros{};
    std::unique_ptr<u64[]> ring;

    // Front side.
    u64 write_position{};
    u64 last_writer_position{};
    static constexpr u32 PendingWriteWords = 8;
    struct PendingWrite {
        u64 begin;
        u64 end;
        u64 position;
        u32 words; ///< Known words from begin, or 0 (unknown contents)
        const char* what;
        std::array<u32, PendingWriteWords> data;
    };
    std::array<PendingWrite, 8192> pending_writes{};
    u64 pending_write_count{};
    u64 last_sync_position{};
    u64 last_release_position{};
    u64 predicted_release_position{};
    static constexpr size_t SemSlots = 1024;
    struct SemWrite {
        u64 granule{~u64{0}};
        u64 position{};
    };
    std::array<SemWrite, SemSlots> sem_writes{};
    static constexpr size_t WordSlots = 4096;
    static constexpr size_t WordSlot(u64 word) noexcept {
        return static_cast<size_t>((word ^ (word >> 12) ^ (word >> 24)) & (WordSlots - 1));
    }
    struct WordWrite {
        u64 word{~u64{0}};
        u64 position{};
        u32 payload{};
        bool known{};
    };
    std::array<WordWrite, WordSlots> word_writes{};
    u64 sem_evicted_position{};
    u64 published_local{};
    u64 consumed_cache{};
    /// Words between publishes: small right after a drain, while the back has nothing to do.
    size_t publish_every{PublishEvery};
    Tegra::Engines::EngineInterface* front_engine{};
    /// The open Pairs block's head (PairWrite), and its count.
    u64* pairs_header{};
    u32 pairs_count{};
    std::array<u64, static_cast<size_t>(Batched::Count)> batched{};
    std::chrono::steady_clock::time_point batched_since{};
    u32 publishes{};
    void ReportBatched();

    // Back side.
    Tegra::Engines::EngineInterface* back_engine{};

    static GpuSplit instance;
    static inline thread_local bool on_back_thread{};
    void (*idle_hook)(void*){};
    void* idle_argument{};
    alignas(64) std::atomic<bool> hook_requested{};
    std::atomic<bool> back_running{};

    // 128 bytes: x86 spatial prefetchers pull 64-byte lines in pairs. back_waiting and wake are
    // always touched together by both threads, so they share a block.
    alignas(128) std::atomic<u64> published{};
    alignas(128) std::atomic<u64> consumed{};
    /// A position the front waits for (WaitFor): the back stores `consumed` once past it.
    alignas(128) std::atomic<u64> wait_target{~u64{0}};
    alignas(128) std::atomic<bool> back_waiting{};
    std::atomic<u32> wake{};
    std::jthread back_thread;
    alignas(128) std::atomic<bool> front_waiting{};
    std::atomic<u32> front_wake{};
};

} // namespace VideoCommon
