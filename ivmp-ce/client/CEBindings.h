// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CEProfile.h"
#include <windows.h>
#include <cstring>

namespace ivmp_ce
{
struct Engine
{
    const std::uint8_t* base = nullptr;

    explicit operator bool() const { return base != nullptr; }

    std::uintptr_t Address(Binding id) const
    {
        return base ? reinterpret_cast<std::uintptr_t>(base) + Rva(id) : 0;
    }

    using Lookup = void* (__stdcall*)(std::uint32_t);
    using ActiveThread = void* (__cdecl*)();
    using MotionSetter = void (__thiscall*)(void*, const void*);
    using MotionGetter = void* (__thiscall*)(void*, void*);

    void* NativeHandler(std::uint32_t hash) const
    {
        const auto find = reinterpret_cast<Lookup>(Address(Binding::FindNative));
        return find ? find(hash) : nullptr;
    }

    void* ScriptThread() const
    {
        const auto get = reinterpret_cast<ActiveThread>(Address(Binding::ActiveScriptThread));
        return get ? get() : nullptr;
    }

    // The CE collider uses aligned 16-byte vectors. IV:MP's CVector3 occupies
    // 12 bytes, so passing its address directly can read past the packet vector.
    void WriteSpeed(void* physical, Binding id, float x, float y, float z) const
    {
        if (!physical || (id != Binding::SetMoveSpeed && id != Binding::SetTurnSpeed)) return;
        const auto set = reinterpret_cast<MotionSetter>(Address(id));
        alignas(16) const float speed[4] = { x, y, z, 0.0f };
        if (set) set(physical, speed);
    }

    bool ReadSpeed(void* physical, Binding id, float& x, float& y, float& z) const
    {
        if (!physical || (id != Binding::GetMoveSpeed && id != Binding::GetTurnSpeed)) return false;
        const auto get = reinterpret_cast<MotionGetter>(Address(id));
        alignas(16) float speed[4] = {};
        if (!get || get(physical, speed) != speed) return false;
        x = speed[0]; y = speed[1]; z = speed[2];
        return true;
    }
};

inline const IMAGE_NT_HEADERS32* PeHeader(const std::uint8_t* base)
{
    if (!base) return nullptr;
    const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < static_cast<LONG>(sizeof(IMAGE_DOS_HEADER)) || dos->e_lfanew > 0xd00)
        return nullptr;
    const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_I386 ||
        nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC ||
        nt->FileHeader.SizeOfOptionalHeader != sizeof(IMAGE_OPTIONAL_HEADER32)) return nullptr;
    return nt;
}

inline bool IsCompleteEditionBuild(const IMAGE_NT_HEADERS32* header)
{
    return header && header->FileHeader.TimeDateStamp == Timestamp &&
        header->OptionalHeader.SizeOfImage == ImageSize && header->OptionalHeader.AddressOfEntryPoint == EntryPoint;
}

inline bool PageIsExecutable(const std::uint8_t* begin, std::size_t size)
{
    MEMORY_BASIC_INFORMATION info{};
    if (!VirtualQuery(begin, &info, sizeof(info)) || info.State != MEM_COMMIT || (info.Protect & PAGE_GUARD)) return false;
    const auto access = info.Protect & 0xff;
    if (access != PAGE_EXECUTE_READ && access != PAGE_EXECUTE_READWRITE && access != PAGE_EXECUTE_WRITECOPY) return false;
    return begin - static_cast<const std::uint8_t*>(info.BaseAddress) + size <= info.RegionSize;
}

inline bool IsLegacyHookLayout(const std::uint8_t* base, const IMAGE_NT_HEADERS32* header)
{
    constexpr std::uint32_t legacySignature = 0x208C34;
    if (!base || !header || header->OptionalHeader.SizeOfImage < legacySignature + 4) return false;
    const auto location = base + legacySignature;
    return PageIsExecutable(location, 4) && Matches(location, 4, "57 E8 06 10");
}

inline Engine ResolveLoadedModule(HMODULE module)
{
    static_assert(sizeof(void*) == 4, "IV:MP runs in a 32-bit process");
    const auto base = reinterpret_cast<const std::uint8_t*>(module);
    if (!IsCompleteEditionBuild(PeHeader(base))) return {};
    for (const auto& signature : Signatures)
    {
        const auto length = (std::strlen(signature.bytes) + 1) / 3;
        if (signature.rva >= ImageSize || length > ImageSize - signature.rva ||
            !PageIsExecutable(base + signature.rva, length) ||
            !Matches(base + signature.rva, length, signature.bytes)) return {};
    }
    return { base };
}
}
