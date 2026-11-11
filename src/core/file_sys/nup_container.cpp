// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#include "core/file_sys/nup_container.h"

#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <cstddef>
#include <cstring>
#include <list>
#include <map>
#include <mutex>
#include <random>
#include <span>
#include <unordered_map>
#include <unordered_set>

#include <fmt/format.h>
#include <openssl/evp.h>
#include <zstd.h>

#include "common/fs/path_util.h"
#include "common/logging.h"
#include "common/swap.h"
#include "core/file_sys/vfs/vfs_offset.h"

namespace FileSys {
namespace {

constexpr u32 NoDir = 0xFFFFFFFF;
constexpr std::size_t SuperblockSize = 0x80;
constexpr std::size_t PackageRecordSize = 0x40;
constexpr std::size_t DirRecordSize = 0x18;
constexpr std::size_t FileRecordSize = 0x50;
constexpr std::size_t TocHeaderSize = 0x30;
constexpr std::size_t TlvHeaderSize = 0x8;
constexpr u32 TocMagic = 0x01434F54; // "TOC\x01" little-endian
constexpr u8 CodecRaw = 0;
constexpr u8 CodecZstd = 1;
/// .nyaa: the extent is a frame table and independent zstd frames, so a read decompresses only the
/// frames it touches (NupFrames in nup_container.h).
constexpr u8 CodecZstdFrames = 2;
constexpr u8 PackageFlagStandaloneUpdate = 0x01;
constexpr u32 MaxDirDepth = 128;

#pragma pack(push, 1)
struct Superblock {
    std::array<u8, 4> magic;
    u16_le header_size;
    u16_le header_version;
    u64_le toc_offset;
    u64_le toc_size;
    std::array<u8, 0x20> toc_sha256;
    u64_le blob_offset;
    u64_le file_size;
    u32_le package_count;
    u32_le flags;
    std::array<u8, 0x10> uuid;
    std::array<u8, 0x20> reserved;
};
static_assert(sizeof(Superblock) == SuperblockSize);

struct TocHeader {
    u32_le magic;
    u32_le package_count;
    u64_le dir_count;
    u64_le file_count;
    u64_le pool_offset;
    u64_le pool_size;
    u64_le reserved;
};
static_assert(sizeof(TocHeader) == TocHeaderSize);

struct PackageRecord {
    u64_le title_id; // 0x00
    u32_le root_dir; // 0x08
    u32_le exefs_dir; // 0x0C
    u32_le romfs_dir; // 0x10
    u32_le version; // 0x14
    u8 kind; // 0x18
    u8 flags; // 0x19
    u16_le reserved; // 0x1A
    u32_le reserved2; // 0x1C
    u64_le meta_offset; // 0x20
    u32_le meta_size; // 0x28
    std::array<u8, 20> reserved3; // 0x2C
};
static_assert(sizeof(PackageRecord) == PackageRecordSize);
static_assert(offsetof(PackageRecord, title_id) == 0x00);
static_assert(offsetof(PackageRecord, root_dir) == 0x08);
static_assert(offsetof(PackageRecord, exefs_dir) == 0x0C);
static_assert(offsetof(PackageRecord, romfs_dir) == 0x10);
static_assert(offsetof(PackageRecord, version) == 0x14);
static_assert(offsetof(PackageRecord, kind) == 0x18);
static_assert(offsetof(PackageRecord, flags) == 0x19);
static_assert(offsetof(PackageRecord, reserved) == 0x1A);
static_assert(offsetof(PackageRecord, reserved2) == 0x1C);
static_assert(offsetof(PackageRecord, meta_offset) == 0x20);
static_assert(offsetof(PackageRecord, meta_size) == 0x28);

struct DirRecord {
    u32_le name_offset;
    u32_le name_length;
    u32_le first_child;
    u32_le child_count;
    u64_le reserved;
};
static_assert(sizeof(DirRecord) == DirRecordSize);

struct FileRecord {
    u32_le name_offset; // 0x00
    u32_le name_length; // 0x04
    u64_le size; // 0x08
    u64_le extent_offset; // 0x10
    u64_le extent_size; // 0x18
    std::array<u8, 0x20> sha256; // 0x20
    u8 codec; // 0x40
    u8 codec_flags; // 0x41
    u16_le reserved2; // 0x42
    std::array<u8, 12> reserved3; // 0x44
};
static_assert(sizeof(FileRecord) == FileRecordSize);
static_assert(offsetof(FileRecord, name_offset) == 0x00);
static_assert(offsetof(FileRecord, name_length) == 0x04);
static_assert(offsetof(FileRecord, size) == 0x08);
static_assert(offsetof(FileRecord, extent_offset) == 0x10);
static_assert(offsetof(FileRecord, extent_size) == 0x18);
static_assert(offsetof(FileRecord, sha256) == 0x20);
static_assert(offsetof(FileRecord, codec) == 0x40);
static_assert(offsetof(FileRecord, codec_flags) == 0x41);
static_assert(offsetof(FileRecord, reserved2) == 0x42);

struct TlvHeader {
    u16_le type;
    u16_le reserved;
    u32_le size;
};
static_assert(sizeof(TlvHeader) == TlvHeaderSize);
#pragma pack(pop)

enum MetaType : u16 {
    MetaNpdm = 0x0001,
    MetaIcon = 0x0003,
    MetaDisplayVersion = 0x0004,
    MetaTitleName = 0x0005,
    MetaPublisher = 0x0006,
    MetaSourceDump = 0x0008,
    MetaLocalizedTitles = 0x0009,
    MetaLocalizedIcons = 0x000A,
};

bool Sha256(const u8* data, std::size_t size, std::array<u8, 0x20>& out) {
    unsigned int length = 0;
    EVP_Digest(data, size, out.data(), &length, EVP_sha256(), nullptr);
    return length == out.size();
}

/// EDEN_NX2_NUP_TRACE=1: times the decompression of NUP members, to see how much of a load is
/// spent there. Logs each slow or large decompression, every member decompressed more than once
/// (evicted from the cache and read again), and a summary every 5 seconds while reads happen.
namespace NupTrace {
bool Enabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("EDEN_NX2_NUP_TRACE");
        return value != nullptr && value[0] == '1';
    }();
    return enabled;
}

struct Stats {
    std::mutex mutex;
    std::unordered_set<u64> seen; ///< (cache, member) pairs decompressed at least once
    u64 decompressions{};
    u64 repeats{};
    u64 spills{};
    u64 bytes_in{};
    u64 bytes_out{};
    u64 hits{};
    u64 hit_bytes{};
    std::chrono::nanoseconds time{};
    std::chrono::steady_clock::time_point last_summary{};
    u32 lines{};
};

Stats& Get() {
    static Stats stats;
    return stats;
}

void MaybeSummary(Stats& stats) {
    const auto now = std::chrono::steady_clock::now();
    if (stats.last_summary.time_since_epoch().count() != 0 &&
        now - stats.last_summary < std::chrono::seconds{5}) {
        return;
    }
    stats.last_summary = now;
    LOG_INFO(Loader,
             "NUP trace: {} decompressions ({} repeated, {} to temporary files), {} MiB -> {} MiB "
             "in {:.0f} ms total; {} cache hits ({} MiB copied)",
             stats.decompressions, stats.repeats, stats.spills, stats.bytes_in >> 20,
             stats.bytes_out >> 20,
             std::chrono::duration<double, std::milli>(stats.time).count(), stats.hits,
             stats.hit_bytes >> 20);
}

void NoteDecompress(const void* cache, std::size_t index, const std::string& name, u64 in,
                    u64 out, std::chrono::nanoseconds time, bool spilled) {
    Stats& stats = Get();
    std::scoped_lock lock{stats.mutex};
    const u64 key = (reinterpret_cast<uintptr_t>(cache) * 0x9E3779B97F4A7C15ULL) ^ index;
    const bool repeat = !stats.seen.insert(key).second;
    ++stats.decompressions;
    stats.repeats += repeat;
    stats.spills += spilled;
    stats.bytes_in += in;
    stats.bytes_out += out;
    stats.time += time;
    const double ms = std::chrono::duration<double, std::milli>(time).count();
    if ((ms >= 20.0 || out >= (u64{4} << 20) || repeat || spilled) && stats.lines < 400) {
        ++stats.lines;
        LOG_INFO(Loader, "NUP trace: {} {} KiB -> {} KiB in {:.1f} ms{}{}", name, in >> 10,
                 out >> 10, ms, spilled ? " (to a temporary file)" : "",
                 repeat ? " (again: evicted from the cache)" : "");
    }
    MaybeSummary(stats);
}

void NoteHit(std::size_t length) {
    Stats& stats = Get();
    std::scoped_lock lock{stats.mutex};
    ++stats.hits;
    stats.hit_bytes += length;
    MaybeSummary(stats);
}
} // namespace NupTrace

/// A member file whose extent is zstd-compressed. Decompresses on first read and caches the
/// result (LRU-evicted); a file over half the cache cap is decompressed once to a temporary file.
class NupVfsFile final : public VfsFile {
public:
    NupVfsFile(VirtualFile backing_, u64 extent_offset_, u64 extent_size_, u64 uncompressed_size_,
               std::string name_, std::shared_ptr<NupDecompressCache> cache_,
               std::size_t cache_index_)
        : backing(std::move(backing_)), extent_offset(extent_offset_), extent_size(extent_size_),
          uncompressed_size(uncompressed_size_), name(std::move(name_)), cache(std::move(cache_)),
          cache_index(cache_index_), cache_enabled(uncompressed_size <= cache->cap / 2) {}

    std::string GetName() const override {
        return name;
    }

    std::size_t GetSize() const override {
        return static_cast<std::size_t>(uncompressed_size);
    }

    bool Resize(std::size_t) override {
        return false;
    }

    VirtualDir GetContainingDirectory() const override {
        return nullptr;
    }

    bool IsWritable() const override {
        return false;
    }

    bool IsReadable() const override {
        return true;
    }

    std::size_t Read(u8* data, std::size_t length, std::size_t offset) const override {
        if (offset >= uncompressed_size) {
            return 0;
        }
        length = std::min(length, static_cast<std::size_t>(uncompressed_size) - offset);

        if (!cache_enabled) {
            return ReadSpilled(data, length, offset);
        }

        {
            std::scoped_lock lock{cache->mutex};
            if (const auto* cached = cache->Find(cache_index)) {
                std::memcpy(data, cached->data() + offset, length);
                if (NupTrace::Enabled()) {
                    NupTrace::NoteHit(length);
                }
                return length;
            }
        }

        // Miss: decompress without holding the lock, then publish and copy.
        const auto begin = std::chrono::steady_clock::now();
        std::vector<u8> scratch = Decompress();
        if (NupTrace::Enabled() && !scratch.empty()) {
            NupTrace::NoteDecompress(cache.get(), cache_index, name, extent_size, scratch.size(),
                                     std::chrono::steady_clock::now() - begin, false);
        }
        if (scratch.empty()) {
            return 0;
        }
        {
            std::scoped_lock lock{cache->mutex};
            cache->Insert(cache_index, scratch);
            if (const auto* cached = cache->Find(cache_index)) {
                std::memcpy(data, cached->data() + offset, length);
                return length;
            }
        }
        std::memcpy(data, scratch.data() + offset, length);
        return length;
    }

    std::size_t Write(const u8*, std::size_t, std::size_t) override {
        return 0;
    }

    bool Rename(std::string_view new_name) override {
        name = std::string(new_name);
        return true;
    }

private:
    std::size_t ReadSpilled(u8* data, std::size_t length, std::size_t offset) const {
        std::shared_ptr<NupSpillFile> spill;
        {
            std::scoped_lock lock{cache->mutex};
            spill = cache->GetSpill(cache_index);
        }
        std::scoped_lock lock{spill->mutex};
        if (!spill->attempted) {
            spill->attempted = true;
            const auto begin = std::chrono::steady_clock::now();
            DecompressToFile(*spill);
            if (NupTrace::Enabled()) {
                NupTrace::NoteDecompress(cache.get(), cache_index, name, extent_size,
                                         uncompressed_size,
                                         std::chrono::steady_clock::now() - begin, true);
            }
        }
        if (!spill->file.IsOpen() || !spill->file.Seek(static_cast<s64>(offset))) {
            return 0;
        }
        return spill->file.ReadSpan(std::span{data, length});
    }

    void DecompressToFile(NupSpillFile& spill) const {
        const auto fail = [&](const char* reason) {
            LOG_ERROR(Loader, "NUP zstd decompression of {} to a temporary file failed: {}", name,
                      reason);
            spill.file.Close();
            if (!spill.path.empty()) {
                std::error_code ec;
                std::filesystem::remove(spill.path, ec);
            }
        };
        if (extent_size > backing->GetSize() || extent_offset > backing->GetSize() - extent_size) {
            return fail("extent out of range");
        }

        std::random_device random;
        std::error_code ec;
        std::filesystem::create_directories(cache->spill_dir, ec);
        spill.path =
            cache->spill_dir / fmt::format("{:08x}{:08x}-{}.tmp", random(), random(), cache_index);
        Common::FS::IOFile out{spill.path, Common::FS::FileAccessMode::Write};
        if (!out.IsOpen()) {
            return fail("cannot create the temporary file");
        }

        std::unique_ptr<ZSTD_DCtx, decltype(&ZSTD_freeDCtx)> dctx{ZSTD_createDCtx(),
                                                                  &ZSTD_freeDCtx};
        std::vector<u8> in_buffer(ZSTD_DStreamInSize());
        std::vector<u8> out_buffer(ZSTD_DStreamOutSize());
        u64 read = 0;
        u64 written = 0;
        bool frame_done = false;
        while (!frame_done && read < extent_size) {
            const auto chunk =
                static_cast<std::size_t>(std::min<u64>(in_buffer.size(), extent_size - read));
            if (backing->Read(in_buffer.data(), chunk,
                              static_cast<std::size_t>(extent_offset + read)) != chunk) {
                return fail("short read of the compressed data");
            }
            read += chunk;
            ZSTD_inBuffer input{in_buffer.data(), chunk, 0};
            ZSTD_outBuffer output{};
            do {
                output = {out_buffer.data(), out_buffer.size(), 0};
                const std::size_t result = ZSTD_decompressStream(dctx.get(), &output, &input);
                if (ZSTD_isError(result) || output.pos > uncompressed_size - written) {
                    return fail("corrupt or oversized data");
                }
                if (out.WriteSpan(std::span<const u8>{out_buffer.data(), output.pos}) !=
                    output.pos) {
                    return fail("cannot write the temporary file");
                }
                written += output.pos;
                frame_done = result == 0;
            } while (!frame_done && (input.pos < input.size || output.pos == output.size));
        }
        if (!frame_done || written != uncompressed_size) {
            return fail("size mismatch");
        }
        out.Close();
        spill.file.Open(spill.path, Common::FS::FileAccessMode::Read);
        if (!spill.file.IsOpen()) {
            fail("cannot reopen the temporary file");
        }
    }

    std::vector<u8> Decompress() const {
        if (extent_size > backing->GetSize() || extent_offset > backing->GetSize() - extent_size) {
            return {};
        }
        const auto compressed = backing->ReadBytes(static_cast<std::size_t>(extent_size),
                                                   static_cast<std::size_t>(extent_offset));
        if (compressed.size() != extent_size) {
            return {};
        }
        std::vector<u8> out(static_cast<std::size_t>(uncompressed_size));
        const std::size_t result =
            ZSTD_decompress(out.data(), out.size(), compressed.data(), compressed.size());
        if (ZSTD_isError(result) || result != out.size()) {
            LOG_ERROR(Loader, "NUP zstd decompression failed for {}", name);
            return {};
        }
        return out;
    }

    VirtualFile backing;
    u64 extent_offset;
    u64 extent_size;
    u64 uncompressed_size;
    std::string name;
    std::shared_ptr<NupDecompressCache> cache;
    std::size_t cache_index;
    bool cache_enabled;
};

/// A member stored as independent zstd frames (CodecZstdFrames, the .nyaa layout): a read
/// decompresses only the frames it touches, each kept in the container's cache.
class NupFramesFile final : public VfsFile {
public:
    NupFramesFile(VirtualFile backing_, u64 extent_offset_, u64 extent_size_,
                  u64 uncompressed_size_, std::string name_,
                  std::shared_ptr<NupDecompressCache> cache_, std::size_t file_index_)
        : backing(std::move(backing_)), extent_offset(extent_offset_), extent_size(extent_size_),
          uncompressed_size(uncompressed_size_), name(std::move(name_)), cache(std::move(cache_)),
          file_index(file_index_) {}

    std::string GetName() const override {
        return name;
    }
    std::size_t GetSize() const override {
        return static_cast<std::size_t>(uncompressed_size);
    }
    bool Resize(std::size_t) override {
        return false;
    }
    VirtualDir GetContainingDirectory() const override {
        return nullptr;
    }
    bool IsWritable() const override {
        return false;
    }
    bool IsReadable() const override {
        return true;
    }
    std::size_t Write(const u8*, std::size_t, std::size_t) override {
        return 0;
    }
    bool Rename(std::string_view new_name) override {
        name = std::string(new_name);
        return true;
    }

    std::size_t Read(u8* data, std::size_t length, std::size_t offset) const override {
        if (offset >= uncompressed_size || !LoadTable()) {
            return 0;
        }
        length = std::min(length, static_cast<std::size_t>(uncompressed_size) - offset);
        std::size_t done = 0;
        while (done < length) {
            const u64 position = offset + done;
            const u32 frame = static_cast<u32>(position / frame_bytes);
            const u64 within = position % frame_bytes;
            const std::size_t count = static_cast<std::size_t>(
                std::min<u64>(length - done, FrameSize(frame) - within));
            if (!CopyFromFrame(frame, within, data + done, count)) {
                return done;
            }
            done += count;
        }
        return done;
    }

private:
    bool LoadTable() const {
        std::call_once(table_once, [this] {
            NupFrames::Header header{};
            if (extent_size < sizeof(header) ||
                backing->ReadObject(&header, static_cast<std::size_t>(extent_offset)) !=
                    sizeof(header) ||
                header.magic != NupFrames::Magic || header.frame_bytes == 0 ||
                header.frame_count !=
                    (uncompressed_size + header.frame_bytes - 1) / header.frame_bytes) {
                LOG_ERROR(Loader, "NUP frame table of {} is damaged", name);
                return;
            }
            std::vector<u64> table(header.frame_count + 1);
            const std::size_t bytes = table.size() * sizeof(u64);
            if (sizeof(header) + bytes > extent_size ||
                backing->Read(reinterpret_cast<u8*>(table.data()), bytes,
                              static_cast<std::size_t>(extent_offset + sizeof(header))) != bytes) {
                LOG_ERROR(Loader, "NUP frame table of {} is damaged", name);
                return;
            }
            for (std::size_t i = 0; i + 1 < table.size(); ++i) {
                if (table[i] > table[i + 1] || table[i + 1] > extent_size) {
                    LOG_ERROR(Loader, "NUP frame table of {} is damaged", name);
                    return;
                }
            }
            frame_bytes = header.frame_bytes;
            offsets = std::move(table);
        });
        return !offsets.empty();
    }

    u64 FrameSize(u32 frame) const {
        return std::min<u64>(frame_bytes, uncompressed_size - u64{frame} * frame_bytes);
    }

    std::size_t CacheKey(u32 frame) const {
        // Apart from the whole-file keys (member indices) of NupVfsFile.
        return (std::size_t{1} << 63) | (static_cast<std::size_t>(file_index) << 24) | frame;
    }

    bool CopyFromFrame(u32 frame, u64 within, u8* out, std::size_t count) const {
        const std::size_t key = CacheKey(frame);
        {
            std::scoped_lock lock{cache->mutex};
            if (const auto* cached = cache->Find(key)) {
                std::memcpy(out, cached->data() + within, count);
                if (NupTrace::Enabled()) {
                    NupTrace::NoteHit(count);
                }
                return true;
            }
        }
        const auto begin = std::chrono::steady_clock::now();
        const u64 stored = offsets[frame + 1] - offsets[frame];
        const auto compressed = backing->ReadBytes(
            static_cast<std::size_t>(stored), static_cast<std::size_t>(extent_offset + offsets[frame]));
        std::vector<u8> plain(static_cast<std::size_t>(FrameSize(frame)));
        if (compressed.size() != stored) {
            return false;
        }
        const std::size_t result =
            ZSTD_decompress(plain.data(), plain.size(), compressed.data(), compressed.size());
        if (ZSTD_isError(result) || result != plain.size()) {
            LOG_ERROR(Loader, "NUP zstd decompression failed for {} frame {}", name, frame);
            return false;
        }
        if (NupTrace::Enabled()) {
            NupTrace::NoteDecompress(cache.get(), key, name, stored, plain.size(),
                                     std::chrono::steady_clock::now() - begin, false);
        }
        std::memcpy(out, plain.data() + within, count);
        std::scoped_lock lock{cache->mutex};
        cache->Insert(key, plain);
        return true;
    }

    VirtualFile backing;
    u64 extent_offset;
    u64 extent_size;
    u64 uncompressed_size;
    std::string name;
    std::shared_ptr<NupDecompressCache> cache;
    std::size_t file_index;
    mutable std::once_flag table_once;
    mutable u32 frame_bytes{};
    mutable std::vector<u64> offsets; ///< Frame starts within the extent, then the extent end
};

} // namespace

NupSpillFile::~NupSpillFile() {
    file.Close();
    if (!path.empty()) {
        std::error_code ec;
        std::filesystem::remove(path, ec);
    }
}

bool NupContainer::IsNupFile(const VirtualFile& file) {
    if (file == nullptr || file->GetSize() < SuperblockSize) {
        return false;
    }
    std::array<u8, 4> magic{};
    file->ReadObject(&magic, 0);
    return magic == HeaderMagic;
}

NupContainer::NupContainer(VirtualFile file_, std::size_t memory_cache_cap,
                           std::filesystem::path spill_dir_)
    : file(std::move(file_)), cache(std::make_shared<NupDecompressCache>()) {
    cache->cap = memory_cache_cap;
    cache->spill_dir = spill_dir_.empty()
                           ? Common::FS::GetEdenPath(Common::FS::EdenPath::CacheDir) / "nup"
                           : std::move(spill_dir_);
    if (file == nullptr) {
        return;
    }
    if (Parse()) {
        status = Loader::ResultStatus::Success;
    } else if (status == Loader::ResultStatus::ErrorNullFile) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
    }
}

NupContainer::~NupContainer() = default;

bool NupContainer::Parse() {
    if (file->GetSize() < SuperblockSize) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }
    Superblock super{};
    if (file->ReadObject(&super, 0) != sizeof(Superblock)) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }
    if (super.magic != HeaderMagic || super.header_size < SuperblockSize || super.flags != 0) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }
    toc_offset = super.toc_offset;
    toc_size = super.toc_size;
    blob_offset = super.blob_offset;
    file_size = super.file_size;
    if (file_size != file->GetSize() || toc_offset < SuperblockSize || blob_offset > file_size ||
        toc_offset > blob_offset || toc_size > blob_offset - toc_offset ||
        blob_offset % 0x10 != 0) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }

    const auto toc = file->ReadBytes(static_cast<std::size_t>(toc_size),
                                     static_cast<std::size_t>(toc_offset));
    if (toc.size() != toc_size) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }
    std::array<u8, 0x20> toc_hash{};
    if (!Sha256(toc.data(), toc.size(), toc_hash) || toc_hash != super.toc_sha256) {
        LOG_ERROR(Loader, "NUP TOC SHA-256 mismatch, the file is corrupt or was modified");
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }

    TocHeader header{};
    if (toc.size() < sizeof(header)) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }
    std::memcpy(&header, toc.data(), sizeof(header));
    if (header.magic != TocMagic || header.package_count == 0 ||
        static_cast<u32>(header.package_count) != super.package_count) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }

    // The counts come from the file, so bound them by the TOC size before multiplying.
    if (header.dir_count > toc.size() / DirRecordSize ||
        header.file_count > toc.size() / FileRecordSize) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }
    const u64 needed = TocHeaderSize + static_cast<u64>(header.package_count) * PackageRecordSize +
                       header.dir_count * DirRecordSize + header.file_count * FileRecordSize;
    if (needed > toc.size() || header.pool_offset < needed || header.pool_offset > toc.size() ||
        header.pool_size > toc.size() - header.pool_offset ||
        (header.pool_offset - needed) % sizeof(u32) != 0) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }
    const u64 children_offset = needed;
    const u64 children_bytes = header.pool_offset - needed;
    const u8* base = toc.data();

    std::size_t pos = TocHeaderSize;
    packages.reserve(header.package_count);
    for (u32 i = 0; i < header.package_count; ++i) {
        PackageRecord record{};
        std::memcpy(&record, base + pos, sizeof(record));
        pos += PackageRecordSize;
        if (record.kind > static_cast<u8>(PackageKind::Addon) ||
            (record.flags & ~PackageFlagStandaloneUpdate) != 0 ||
            record.root_dir >= header.dir_count ||
            (record.exefs_dir != NoDir && record.exefs_dir >= header.dir_count) ||
            (record.romfs_dir != NoDir && record.romfs_dir >= header.dir_count)) {
            status = Loader::ResultStatus::ErrorNUPBadFormat;
            return false;
        }
        PackageInfo package;
        package.title_id = record.title_id;
        package.version = record.version;
        package.kind = static_cast<PackageKind>(record.kind);
        package.standalone_update = (record.flags & PackageFlagStandaloneUpdate) != 0;
        package.root_dir = record.root_dir;
        package.exefs_dir = record.exefs_dir;
        package.romfs_dir = record.romfs_dir;
        if (record.meta_size > 0 && record.meta_offset != 0 && record.meta_offset <= file_size &&
            record.meta_size <= file_size - record.meta_offset) {
            package.meta_offset = record.meta_offset;
            package.meta_size = record.meta_size;
        }
        packages.push_back(std::move(package));
    }

    dirs.resize(header.dir_count);
    for (u64 i = 0; i < header.dir_count; ++i) {
        DirRecord record{};
        std::memcpy(&record, base + pos, sizeof(record));
        pos += DirRecordSize;
        if (static_cast<u64>(record.first_child) + record.child_count >
                children_bytes / sizeof(u32) ||
            static_cast<u64>(record.name_offset) + record.name_length > header.pool_size) {
            status = Loader::ResultStatus::ErrorNUPBadFormat;
            return false;
        }
        dirs[i].name = std::string(reinterpret_cast<const char*>(base) + header.pool_offset +
                                       record.name_offset,
                                   record.name_length);
        dirs[i].first_child = record.first_child;
        dirs[i].child_count = record.child_count;
    }

    files.resize(header.file_count);
    for (u64 i = 0; i < header.file_count; ++i) {
        FileRecord record{};
        std::memcpy(&record, base + pos, sizeof(record));
        pos += FileRecordSize;
        if (record.codec > CodecZstdFrames || record.codec_flags != 0 ||
            (record.size == 0 && (record.extent_offset != 0 || record.extent_size != 0)) ||
            (record.size != 0 &&
             (record.extent_size == 0 || record.extent_offset < blob_offset ||
              record.extent_offset % 0x10 != 0 || record.extent_offset > file_size ||
              record.extent_size > file_size - record.extent_offset)) ||
            static_cast<u64>(record.name_offset) + record.name_length > header.pool_size) {
            status = Loader::ResultStatus::ErrorNUPBadFormat;
            return false;
        }
        files[i].name = std::string(reinterpret_cast<const char*>(base) + header.pool_offset +
                                        record.name_offset,
                                    record.name_length);
        files[i].size = record.size;
        files[i].sha256 = record.sha256;
        files[i].extent_offset = record.extent_offset;
        files[i].extent_size = record.extent_size;
        files[i].codec = record.codec;
    }

    children.resize(children_bytes / sizeof(u32));
    std::memcpy(children.data(), base + children_offset, children_bytes);
    for (const u32 child : children) {
        if (child >= header.dir_count + header.file_count) {
            status = Loader::ResultStatus::ErrorNUPBadFormat;
            return false;
        }
    }

    if (!ValidateDirTree()) {
        status = Loader::ResultStatus::ErrorNUPBadFormat;
        return false;
    }

    for (auto& package : packages) {
        if (!ParseMeta(package)) {
            status = Loader::ResultStatus::ErrorNUPBadFormat;
            return false;
        }
    }
    return true;
}

bool NupContainer::ValidateDirTree() const {
    enum : u8 { Unvisited, InProgress, Done };
    struct Frame {
        u32 dir;
        u32 next;
    };
    std::vector<u8> state(dirs.size(), Unvisited);
    std::vector<u32> height(dirs.size(), 0);
    std::vector<Frame> stack;
    for (u32 start = 0; start < dirs.size(); ++start) {
        if (state[start] != Unvisited) {
            continue;
        }
        state[start] = InProgress;
        stack.push_back({start, 0});
        while (!stack.empty()) {
            const Frame frame = stack.back();
            const DirNode& node = dirs[frame.dir];
            if (frame.next == node.child_count) {
                state[frame.dir] = Done;
                stack.pop_back();
                if (!stack.empty()) {
                    u32& parent_height = height[stack.back().dir];
                    parent_height = (std::max)(parent_height, height[frame.dir] + 1);
                }
                continue;
            }
            ++stack.back().next;
            const u32 child = children[static_cast<std::size_t>(node.first_child) + frame.next];
            if (child >= dirs.size()) {
                continue;
            }
            if (state[child] == InProgress) {
                LOG_ERROR(Loader, "NUP directory tree contains a cycle");
                return false;
            }
            if (stack.size() + height[child] + 1 > MaxDirDepth) {
                LOG_ERROR(Loader, "NUP directory tree is nested too deeply");
                return false;
            }
            if (state[child] == Done) {
                height[frame.dir] = (std::max)(height[frame.dir], height[child] + 1);
            } else {
                state[child] = InProgress;
                stack.push_back({child, 0});
            }
        }
    }
    return true;
}

bool NupContainer::ParseMeta(PackageInfo& package) {
    if (package.meta_offset == 0) {
        return true;
    }
    const auto block = file->ReadBytes(static_cast<std::size_t>(package.meta_size),
                                       static_cast<std::size_t>(package.meta_offset));
    if (block.size() != package.meta_size) {
        return false;
    }
    std::size_t pos = 0;
    while (pos + TlvHeaderSize <= block.size()) {
        TlvHeader header{};
        std::memcpy(&header, block.data() + pos, sizeof(header));
        pos += TlvHeaderSize;
        if (pos + header.size > block.size()) {
            return false;
        }
        const u8* payload = block.data() + pos;
        pos += header.size;
        switch (header.type) {
        case MetaNpdm:
            package.npdm.assign(payload, payload + header.size);
            break;
        case MetaIcon:
            package.icon.assign(payload, payload + header.size);
            break;
        case MetaTitleName:
            package.title_name = std::string(reinterpret_cast<const char*>(payload), header.size);
            break;
        case MetaPublisher:
            package.publisher = std::string(reinterpret_cast<const char*>(payload), header.size);
            break;
        case MetaSourceDump:
            package.source_dump = std::string(reinterpret_cast<const char*>(payload), header.size);
            break;
        case MetaDisplayVersion:
            package.display_version =
                std::string(reinterpret_cast<const char*>(payload), header.size);
            break;
        case MetaLocalizedTitles:
        case MetaLocalizedIcons: {
            const bool is_title = header.type == MetaLocalizedTitles;
            std::size_t record_pos = 0;
            while (record_pos < header.size) {
                const u8 lang_length = payload[record_pos++];
                if (record_pos + lang_length + sizeof(u32) > header.size) {
                    return false;
                }
                const std::string lang(reinterpret_cast<const char*>(payload) + record_pos,
                                       lang_length);
                record_pos += lang_length;
                u32 data_length = 0;
                std::memcpy(&data_length, payload + record_pos, sizeof(data_length));
                record_pos += sizeof(data_length);
                if (record_pos + data_length > header.size) {
                    return false;
                }
                if (is_title) {
                    package.localized_titles.emplace_back(
                        lang, std::string(reinterpret_cast<const char*>(payload) + record_pos,
                                          data_length));
                } else {
                    package.localized_icons.emplace_back(
                        lang, std::vector<u8>(payload + record_pos,
                                              payload + record_pos + data_length));
                }
                record_pos += data_length;
            }
            break;
        }
        default:
            break;
        }
    }
    return true;
}

std::optional<std::size_t> NupContainer::FindPackage(PackageKind kind,
                                                     std::optional<u64> title_id) const {
    for (std::size_t i = 0; i < packages.size(); ++i) {
        if (packages[i].kind != kind) {
            continue;
        }
        if (title_id && packages[i].title_id != *title_id) {
            continue;
        }
        return i;
    }
    return std::nullopt;
}

std::optional<std::size_t> NupContainer::FindBasePackage() const {
    return FindPackage(PackageKind::Base);
}

std::optional<std::size_t> NupContainer::FindBestUpdatePackage() const {
    std::optional<std::size_t> best;
    for (std::size_t i = 0; i < packages.size(); ++i) {
        if (packages[i].kind != PackageKind::Update) {
            continue;
        }
        if (!best || packages[i].version > packages[*best].version) {
            best = i;
        }
    }
    return best;
}

VirtualFile NupContainer::OpenFile(std::size_t file_index) {
    if (file_index >= files.size()) {
        return nullptr;
    }
    const FileNode& node = files[file_index];
    if (node.size == 0) {
        return std::make_shared<VectorVfsFile>(std::vector<u8>{}, node.name);
    }
    if (node.codec == CodecRaw) {
        return std::make_shared<OffsetVfsFile>(
            file, static_cast<std::size_t>(node.extent_size),
            static_cast<std::size_t>(node.extent_offset), node.name);
    }
    if (node.codec == CodecZstdFrames) {
        return std::make_shared<NupFramesFile>(file, node.extent_offset, node.extent_size,
                                               node.size, node.name, cache, file_index);
    }
    return std::make_shared<NupVfsFile>(file, node.extent_offset, node.extent_size, node.size,
                                        node.name, cache, file_index);
}

void NupContainer::BuildDirTree(u32 dir_index, VectorVfsDirectory& out) {
    const DirNode& node = dirs[dir_index];
    const u64 end = static_cast<u64>(node.first_child) + node.child_count;
    for (u64 slot = node.first_child; slot < end; ++slot) {
        const u32 child = children[slot];
        if (child >= dirs.size()) {
            out.AddFile(OpenFile(child - static_cast<u32>(dirs.size())));
        } else {
            auto sub = std::make_shared<VectorVfsDirectory>(std::vector<VirtualFile>{},
                                                            std::vector<VirtualDir>{},
                                                            dirs[child].name);
            BuildDirTree(child, *sub);
            out.AddDirectory(sub);
        }
    }
}

VirtualDir NupContainer::BuildPackageDir(u32 dir_index) {
    auto out = std::make_shared<VectorVfsDirectory>(std::vector<VirtualFile>{},
                                                    std::vector<VirtualDir>{}, dirs[dir_index].name);
    BuildDirTree(dir_index, *out);
    return out;
}

VirtualDir NupContainer::GetExeFSDir(std::size_t package_index) {
    if (package_index >= packages.size() || packages[package_index].exefs_dir == NoDir) {
        return nullptr;
    }
    if (exefs_dir_cache.size() <= package_index) {
        exefs_dir_cache.resize(packages.size());
    }
    if (exefs_dir_cache[package_index] == nullptr) {
        auto dir = BuildPackageDir(packages[package_index].exefs_dir);
        if (dir->GetFiles().empty() && dir->GetSubdirectories().empty()) {
            return nullptr;
        }
        exefs_dir_cache[package_index] = std::move(dir);
    }
    return exefs_dir_cache[package_index];
}

VirtualDir NupContainer::GetRomFSDir(std::size_t package_index) {
    if (package_index >= packages.size() || packages[package_index].romfs_dir == NoDir) {
        return nullptr;
    }
    if (romfs_dir_cache.size() <= package_index) {
        romfs_dir_cache.resize(packages.size());
    }
    if (romfs_dir_cache[package_index] == nullptr) {
        romfs_dir_cache[package_index] = BuildPackageDir(packages[package_index].romfs_dir);
    }
    return romfs_dir_cache[package_index];
}

VirtualDir NupContainer::GetPackageDir(std::size_t package_index) {
    if (package_index >= packages.size()) {
        return nullptr;
    }
    return BuildPackageDir(packages[package_index].root_dir);
}

VirtualDir NupContainer::MergeShadowDirs(const VirtualDir& base, const VirtualDir& overlay) {
    if (base == nullptr) {
        return overlay;
    }
    if (overlay == nullptr) {
        return base;
    }

    std::map<std::string, VirtualFile, std::less<>> file_map;
    for (const auto& entry : base->GetFiles()) {
        file_map.insert_or_assign(entry->GetName(), entry);
    }
    for (const auto& entry : overlay->GetFiles()) {
        file_map.insert_or_assign(entry->GetName(), entry);
    }

    std::map<std::string, VirtualDir, std::less<>> dir_map;
    for (const auto& entry : base->GetSubdirectories()) {
        dir_map.insert_or_assign(entry->GetName(), entry);
    }
    for (const auto& entry : overlay->GetSubdirectories()) {
        const auto it = dir_map.find(entry->GetName());
        if (it != dir_map.end()) {
            dir_map.insert_or_assign(entry->GetName(), MergeShadowDirs(it->second, entry));
        } else {
            dir_map.insert_or_assign(entry->GetName(), entry);
        }
    }

    std::vector<VirtualFile> merged_files;
    merged_files.reserve(file_map.size());
    for (const auto& [_, entry] : file_map) {
        merged_files.push_back(entry);
    }
    std::vector<VirtualDir> merged_dirs;
    merged_dirs.reserve(dir_map.size());
    for (const auto& [_, entry] : dir_map) {
        merged_dirs.push_back(entry);
    }
    return std::make_shared<VectorVfsDirectory>(std::move(merged_files), std::move(merged_dirs),
                                                base->GetName());
}

std::vector<u8> NupContainer::ReadExtentRaw(const FileNode& node) const {
    return file->ReadBytes(static_cast<std::size_t>(node.extent_size),
                           static_cast<std::size_t>(node.extent_offset));
}

Loader::ResultStatus NupContainer::VerifyIntegrity(
    std::function<bool(std::size_t, std::size_t)> progress_callback) const {
    const std::size_t total = files.size();
    for (std::size_t i = 0; i < total; ++i) {
        const FileNode& node = files[i];
        std::vector<u8> content;
        if (node.size == 0) {
            content = ReadExtentRaw(node);
        } else if (node.codec == CodecRaw) {
            content = ReadExtentRaw(node);
        } else if (node.codec == CodecZstdFrames) {
            NupFramesFile view(file, node.extent_offset, node.extent_size, node.size, node.name,
                               cache, i);
            content = view.ReadAllBytes();
        } else {
            NupVfsFile view(file, node.extent_offset, node.extent_size, node.size, node.name,
                            cache, i);
            content = view.ReadAllBytes();
        }
        if (content.size() != node.size) {
            LOG_ERROR(Loader, "NUP integrity check failed for {}: short read", node.name);
            return Loader::ResultStatus::ErrorIntegrityVerificationFailed;
        }
        std::array<u8, 0x20> hash{};
        if (!Sha256(content.data(), content.size(), hash) || hash != node.sha256) {
            LOG_ERROR(Loader, "NUP integrity check failed for {}: SHA-256 mismatch", node.name);
            return Loader::ResultStatus::ErrorIntegrityVerificationFailed;
        }
        if (progress_callback && !progress_callback(i + 1, total)) {
            return Loader::ResultStatus::ErrorIntegrityVerificationFailed;
        }
    }
    return Loader::ResultStatus::Success;
}

} // namespace FileSys
