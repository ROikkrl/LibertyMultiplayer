// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CEBindings.h"
#include <array>

namespace ivmp_ce
{
using ScriptDispatch = std::uint8_t (__cdecl*)(std::uint32_t);
using FrameObserver = void (*)(const Engine&);

// Changes one already validated CALL. Kept independent so the actual machine
// code round trip can be tested in Win32 without starting the game.
class RelativeCall
{
    std::uint8_t* site = nullptr;
    std::array<std::uint8_t, 5> original{}, redirected{};

    static bool Write(std::uint8_t* where, const std::array<std::uint8_t, 5>& bytes)
    {
        DWORD access = 0;
        if (!VirtualProtect(where, bytes.size(), PAGE_EXECUTE_READWRITE, &access)) return false;
        std::memcpy(where, bytes.data(), bytes.size());
        FlushInstructionCache(GetCurrentProcess(), where, bytes.size());
        DWORD ignored = 0;
        VirtualProtect(where, bytes.size(), access, &ignored);
        return true;
    }

public:
    RelativeCall() = default;
    RelativeCall(const RelativeCall&) = delete;
    RelativeCall& operator=(const RelativeCall&) = delete;

    bool Install(std::uint8_t* where, std::uintptr_t expected, std::uintptr_t replacement)
    {
        if (site || !PageIsExecutable(where, original.size()) || where[0] != 0xe8) return false;
        std::uint32_t oldDisplacement = 0;
        std::memcpy(&oldDisplacement, where + 1, sizeof(oldDisplacement));
        const auto next = reinterpret_cast<std::uintptr_t>(where) + original.size();
        if (next + oldDisplacement != expected || replacement == 0) return false;

        std::memcpy(original.data(), where, original.size());
        const auto newDisplacement = static_cast<std::uint32_t>(replacement - next);
        redirected[0] = 0xe8;
        std::memcpy(redirected.data() + 1, &newDisplacement, sizeof(newDisplacement));
        if (!Write(where, redirected)) return false;
        site = where;
        return true;
    }

    bool Remove()
    {
        if (!site) return true;
        if (!PageIsExecutable(site, original.size()) ||
            std::memcmp(site, redirected.data(), redirected.size()) != 0 || !Write(site, original)) return false;
        site = nullptr;
        return true;
    }
};

class FramePump
{
    RelativeCall call;
    ScriptDispatch original = nullptr;
    FrameObserver callback = nullptr;
    Engine engine;

    static std::uint8_t __cdecl Tick(std::uint32_t operationLimit)
    {
        auto& pump = Instance();
        const auto result = pump.original(operationLimit);
        if (pump.callback) pump.callback(pump.engine);
        return result;
    }

    bool BeforeFirstScript(const Engine& resolved) const
    {
        const auto address = reinterpret_cast<std::uintptr_t>(resolved.base);
        constexpr std::uint32_t threadCountRva = 0x174AF08;
        std::uint32_t operand = 0;
        std::memcpy(&operand, resolved.base + Rva(Binding::RunScriptThreads) + 3, sizeof(operand));
        if (operand != address + threadCountRva) return false;
        std::uint16_t count = 0;
        std::memcpy(&count, reinterpret_cast<const void*>(operand), sizeof(count));
        return count == 0;
    }

public:
    static FramePump& Instance()
    {
        static FramePump instance;
        return instance;
    }

    bool Install(const Engine& resolved, FrameObserver observe)
    {
        if (!resolved || !observe || original || !BeforeFirstScript(resolved)) return false;
        engine = resolved;
        original = reinterpret_cast<ScriptDispatch>(engine.Address(Binding::RunScriptThreads));
        callback = observe;
        const auto dispatchSite = reinterpret_cast<std::uint8_t*>(engine.Address(Binding::RunScriptCallSite));
        if (call.Install(dispatchSite, reinterpret_cast<std::uintptr_t>(original), reinterpret_cast<std::uintptr_t>(&Tick)))
            return true;
        original = nullptr;
        callback = nullptr;
        return false;
    }

    bool Remove()
    {
        callback = nullptr;
        if (!call.Remove()) return false;
        original = nullptr;
        engine = {};
        return true;
    }
};
}
