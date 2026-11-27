// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CEBindings.h"
#include <cmath>

namespace ivmp_ce
{
struct LocalPlayer
{
    std::uint32_t id = 0;
    std::uint32_t ped = 0;
    std::uint32_t episode = 0;
    std::uint32_t vehicle = 0;
    float x = 0.0f, y = 0.0f, z = 0.0f;
};

// Read-only bootstrap calls. Once CPools and the local player are available,
// the upstream client can begin its normal spawn and session initialization.
class LocalPlayerReader
{
    struct ScriptCall
    {
        void* result;
        std::uint32_t count;
        void* arguments;
        std::uint32_t vectorCount;
        void* vectorOutputs[4];
        alignas(16) float vectors[4][4];
    };
    static_assert(offsetof(ScriptCall, vectors) == 0x20, "CE native context vector offset");
    using Handler = void (__cdecl*)(ScriptCall*);

    Engine engine;

    static std::uint32_t Argument(std::uint32_t value) { return value; }

    template<typename T>
    static std::uint32_t Argument(T* value) { return reinterpret_cast<std::uint32_t>(value); }

    template<typename... Arguments>
    bool Invoke(std::uint32_t hash, std::uint32_t& result, Arguments... arguments) const
    {
        static_assert(sizeof...(arguments) < 16, "Too many native arguments");
        auto handler = static_cast<const std::uint8_t*>(engine.NativeHandler(hash));
        const auto begin = reinterpret_cast<std::uintptr_t>(engine.base);
        const auto value = reinterpret_cast<std::uintptr_t>(handler);
        if (value < begin || value - begin >= ImageSize || !PageIsExecutable(handler, 1)) return false;
        std::uint32_t stack[16] = { Argument(arguments)... };
        result = 0;
        ScriptCall call{};
        call.result = &result;
        call.count = sizeof...(arguments);
        call.arguments = stack;
        reinterpret_cast<Handler>(const_cast<std::uint8_t*>(handler))(&call);
        return true;
    }

public:
    explicit LocalPlayerReader(Engine resolved) : engine(resolved) {}

    bool Read(LocalPlayer& snapshot) const
    {
        LocalPlayer state;
        std::uint32_t result = 0;
        if (!engine || !Invoke(0x62E319C6, state.id) || state.id >= 32 ||             // GET_PLAYER_ID
            !Invoke(0x08274BA4, result, state.id) || result == 0 ||                  // IS_PLAYER_PLAYING
            !Invoke(0x511454A9, result, state.id, &state.ped) || state.ped == 0 ||    // GET_PLAYER_CHAR
            !Invoke(0x46531797, result, state.ped) || result == 0 ||                 // DOES_CHAR_EXIST
            !Invoke(0x2B5C06E6, result, state.ped, &state.x, &state.y, &state.z) ||  // GET_CHAR_COORDINATES
            !Invoke(0x7D7619D2, state.episode) || state.episode > 2 ||               // GET_CURRENT_EPISODE
            !std::isfinite(state.x) || !std::isfinite(state.y) || !std::isfinite(state.z)) return false;
        if (Invoke(0x71184DA3, result, state.ped) && result)                       // IS_CHAR_IN_ANY_CAR
            Invoke(0x1B067237, result, state.ped, &state.vehicle);                  // GET_CAR_CHAR_IS_USING
        snapshot = state;
        return true;
    }
};
}
