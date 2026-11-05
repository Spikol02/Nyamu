// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

#pragma once

#include <cstdlib>

#include "common/alignment.h"
#include "common/common_types.h"
#include "shader_recompiler/shader_info.h"
#include "video_core/memory_manager.h"

namespace VideoCommon {

/// EDEN_NX2_HANDLE_ALIGN=1 (zortos293 dfb0b6b544): a handle program's storage offsets are relative
/// to the buffer address aligned down to the host storage alignment, as StorageOffset computes
/// them; read the words there instead of at the unaligned address.
inline bool HandleAlignEnabled() {
    static const bool enabled = [] {
        const char* const value = std::getenv("EDEN_NX2_HANDLE_ALIGN");
        return value != nullptr && value[0] == '1';
    }();
    return enabled;
}

/// The texture handle a shader reads from a storage buffer, computed from the current constant
/// buffer and storage buffer words (Shader::Info::handle_programs). cbuf_address(index) is the
/// bound constant buffer's address, 0 when none is bound. Storage words are read from the buffer
/// address aligned down to storage_alignment.
template <typename CbufAddress>
u32 EvaluateTextureHandle(const Shader::Info& info, u32 program, Tegra::MemoryManager& gpu_memory,
                          CbufAddress&& cbuf_address, u64 storage_alignment = 1) {
    return Shader::EvaluateHandleProgram(
               info.handle_programs[program],
               [&](u32 index, u32 offset) -> u32 {
                   const GPUVAddr base{cbuf_address(index)};
                   return base != 0 ? gpu_memory.Read<u32>(base + offset) : 0U;
               },
               [&](u32 binding, u32 offset) -> u32 {
                   if (binding >= info.storage_buffers_descriptors.size()) {
                       return 0U;
                   }
                   const auto& descriptor{info.storage_buffers_descriptors[binding]};
                   const GPUVAddr cbuf{cbuf_address(descriptor.cbuf_index)};
                   if (cbuf == 0) {
                       return 0U;
                   }
                   GPUVAddr base{gpu_memory.Read<u64>(cbuf + descriptor.cbuf_offset)};
                   if (base != 0 && descriptor.indirect_offset != ~0U) {
                       base = gpu_memory.Read<u64>(base + descriptor.indirect_offset);
                   }
                   return base != 0 ? gpu_memory.Read<u32>(
                                          Common::AlignDown(base, storage_alignment) + offset)
                                    : 0U;
               })
        .value_or(0U);
}

} // namespace VideoCommon
