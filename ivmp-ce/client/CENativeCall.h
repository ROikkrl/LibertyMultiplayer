// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CEBindings.h"

namespace ivmp_ce
{
class NativeCall
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
    static std::uint32_t Argument(float value)
    {
        std::uint32_t bits = 0;
        std::memcpy(&bits, &value, sizeof(bits));
        return bits;
    }

    template<typename T>
    static std::uint32_t Argument(T* value) { return reinterpret_cast<std::uint32_t>(value); }

public:
    explicit NativeCall(Engine resolved) : engine(resolved) {}

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
};
}
