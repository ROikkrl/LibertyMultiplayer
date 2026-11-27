// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CENativeCall.h"
#include <atomic>

namespace ivmp_ce
{
inline void* __fastcall ResolvePlayerSlotPad(void* ped, void*);

// The original branch writes only EAX. Preserve the volatile registers that
// surrounding game code expects to keep across this particular call.
inline __declspec(naked) void PlayerSlotPadTail()
{
    __asm
    {
        push ecx
        push edx
        call ResolvePlayerSlotPad
        pop edx
        pop ecx
        ret
    }
}

// Extra CPlayerPed slots use the same model as the local player. Ordinary
// civilian peds cannot use the component model stored in playerped.rpf.
class PlayerSlots
{
    struct Slot
    {
        std::atomic<void*> object{nullptr};
        void* playerInfo = nullptr;
        std::uint32_t handle = 0;
        bool occupied = false;
    };

    static constexpr unsigned SlotCount = 3;
    Slot slots[SlotCount]{};
    const std::uint8_t* image = nullptr;

    template<class T>
    T At(std::uint32_t rva) const { return reinterpret_cast<T>(reinterpret_cast<std::uintptr_t>(image) + rva); }

    void** Infos() const { return At<void**>(0xDA8808); }

    friend void* __fastcall ResolvePlayerSlotPad(void* ped, void*);

    bool Alive(unsigned index, const EntityPools& pools) const
    {
        const auto& slot = slots[index];
        return slot.playerInfo && Infos()[index + 1] == slot.playerInfo && slot.handle &&
            pools.ResolvePed(slot.handle) == slot.object.load(std::memory_order_acquire);
    }

    static void Park(const NativeCall& native, std::uint32_t handle)
    {
        std::uint32_t result = 0;
        native.Invoke(0x4AB470F3, result, handle);    // CLEAR_CHAR_TASKS
        native.Invoke(0x04CF0105, result, handle, 0u); // SET_CHAR_VISIBLE
        native.Invoke(0x2A7413EB, result, handle, 0u); // SET_CHAR_COLLISION
        native.Invoke(0x20266A86, result, handle, 1u); // FREEZE_CHAR_POSITION
    }

public:
    static PlayerSlots& Instance()
    {
        static PlayerSlots instance;
        return instance;
    }

    bool Available() const { return image != nullptr; }

    bool Install(const Engine& engine)
    {
        if (image) return image == engine.base;
        if (!engine) return false;

        struct Check { std::uint32_t rva; std::size_t length; const char* bytes; };
        constexpr Check checks[] = {
            {0x53E960, 23, "55 8B EC 83 E4 F0 83 EC 48 56 8B 75 0C 57 83 3C B5 ?? ?? ?? ?? 00 74"},
            {0x53E380, 12, "56 57 68 ?? ?? ?? ?? E8 F4 E2 09 00"},
            {0x5DC680, 12, "FF 74 24 04 E8 C7 87 EF FF 83 C4 04"},
            {0x5F19C0, 14, "8A 44 24 04 56 57 8B F9 88 87 60 0A 00 00"},
            {0x62B350, 27, "80 B9 18 02 00 00 00 75 0D 80 B9 19 02 00 00 00 0F 85 7A B6 EC FF 33 C0 C3 CC CC"},
            {0x4F6A1B, 6, "B8 ?? ?? ?? ?? C3"},
        };
        for (const auto& check : checks)
        {
            const auto code = engine.base + check.rva;
            if (!PageIsExecutable(code, check.length) || !Matches(code, check.length, check.bytes)) return false;
        }

        std::uint32_t slotsAddress = 0, padAddress = 0;
        std::memcpy(&slotsAddress, engine.base + 0x53E971, sizeof(slotsAddress));
        std::memcpy(&padAddress, engine.base + 0x4F6A1C, sizeof(padAddress));
        const auto base = reinterpret_cast<std::uintptr_t>(engine.base);
        if (slotsAddress != base + 0xDA8808 || padAddress != base + 0xD85C08) return false;

        const auto tail = const_cast<std::uint8_t*>(engine.base) + 0x62B366;
        DWORD protection = 0;
        if (!VirtualProtect(tail, 5, PAGE_EXECUTE_READWRITE, &protection)) return false;
        image = engine.base;
        std::uint8_t jump[] = {0xe9, 0, 0, 0, 0};
        const auto displacement = reinterpret_cast<std::uintptr_t>(&PlayerSlotPadTail) -
            reinterpret_cast<std::uintptr_t>(tail + sizeof(jump));
        std::memcpy(jump + 1, &displacement, sizeof(displacement));
        std::memcpy(tail, jump, sizeof(jump));
        FlushInstructionCache(GetCurrentProcess(), tail, sizeof(jump));
        DWORD ignored = 0;
        VirtualProtect(tail, sizeof(jump), protection, &ignored);
        return true;
    }

    std::uint32_t Acquire(const NativeCall& native, const EntityPools& pools, float x, float y, float z)
    {
        if (!image || !pools || !*pools.peds) return 0;
        for (unsigned index = 0; index < SlotCount; ++index)
        {
            auto& slot = slots[index];
            auto* const info = Infos()[index + 1];
            if (Alive(index, pools))
            {
                if (slot.occupied) continue;
                slot.occupied = true;
                return slot.handle;
            }
            // Never overwrite a game or mod-owned slot, even after a failed
            // allocation. Only reclaim our bookkeeping when the game clears it.
            if (info) continue;
            slot.object.store(nullptr, std::memory_order_release);
            slot.playerInfo = nullptr;
            slot.handle = 0;
            slot.occupied = false;

            using ModelIndex = int (__cdecl*)(const char*);
            using Create = void* (__cdecl*)(const std::uint8_t*, unsigned, int, std::uint8_t);
            using PopulationType = void (__thiscall*)(void*, unsigned);
            using Place = void (__cdecl*)(void*, const float*);
            const auto model = At<ModelIndex>(0x5DC680)("player");
            if (model < 0) return 0;
            constexpr std::uint8_t remoteFlags[] = {1, 1};
            auto* object = At<Create>(0x53E960)(remoteFlags, index + 1, model, 0);
            if (!object) return 0;

            slot.object.store(object, std::memory_order_release);
            slot.playerInfo = Infos()[index + 1];
            At<PopulationType>(0x5F19C0)(object, 2);
            alignas(16) const float position[] = {x, y, z, 0.0f};
            At<Place>(0x53E380)(object, position);
            slot.handle = pools.encode(*pools.peds, object);
            if (!Alive(index, pools)) return 0;

            std::uint32_t player = 0, result = 0;
            if (native.Invoke(0x5996315E, player, index + 1) && player != 0)
                native.Invoke(0x1A6203EA, result, player, 0u); // SET_PLAYER_CONTROL
            Park(native, slot.handle);
            slot.occupied = true;
            return slot.handle;
        }
        return 0;
    }

    bool Owns(const EntityPools& pools, std::uint32_t handle) const
    {
        if (!image || !handle) return false;
        for (unsigned index = 0; index < SlotCount; ++index)
            if (slots[index].occupied && slots[index].handle == handle && Alive(index, pools)) return true;
        return false;
    }

    void Release(const NativeCall& native, const EntityPools& pools, std::uint32_t handle)
    {
        if (!image || !handle) return;
        for (unsigned index = 0; index < SlotCount; ++index)
        {
            auto& slot = slots[index];
            if (!slot.occupied || slot.handle != handle) continue;
            if (Alive(index, pools)) Park(native, handle);
            slot.occupied = false;
            return;
        }
    }
};

// The game's local-pad selection and queries for peds belonging to other
// systems retain their original results.
inline void* __fastcall ResolvePlayerSlotPad(void* ped, void*)
{
    auto& registry = PlayerSlots::Instance();
    if (ped)
        for (auto& slot : registry.slots)
            if (slot.object.load(std::memory_order_acquire) == ped)
                return registry.At<void*>(0xD85C08);
    return nullptr;
}
}
