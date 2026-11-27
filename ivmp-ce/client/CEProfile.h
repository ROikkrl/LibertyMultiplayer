// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include <cstddef>
#include <cstdint>

namespace ivmp_ce
{
enum class Binding : std::size_t
{
    FindNative,
    ActiveScriptThread,
    RunScriptThreads,
    RunScriptCallSite,
    GetMoveSpeed,
    GetTurnSpeed,
    SetMoveSpeed,
    SetTurnSpeed,
    MoveSpeedColliderDispatch,
    TurnSpeedColliderDispatch,
    SetWheelSteering,
    ReadWheelSuspensionInRenderer,
    Count
};

struct Signature
{
    const char* name;
    std::uint32_t rva;
    const char* bytes;
};

// The PE timestamp and image layout identify the Steam 1.2.0.59 build. The
// byte checks below guard each entry point, including the scheduler call site.
constexpr std::uint32_t Timestamp = 0x63D3E735;
constexpr std::uint32_t ImageSize = 0x01BE6400;
constexpr std::uint32_t EntryPoint = 0x01AEB000;
constexpr Signature Signatures[] = {
    { "scrVm::FindNativeHandler (__stdcall)", 0x46FCE0, "56 8B 35 ?? ?? ?? ?? 85 F6 75 06 33 C0 5E C2 04 00 53 57 8B 7C 24 10" },
    { "scrThread::GetActiveThread", 0x46D060, "A1 ?? ?? ?? ?? C3 CC CC CC CC CC CC CC CC CC CC 8B 44 24 04 56 8B F1" },
    { "scrThread::UpdateAll (__cdecl)", 0x46E630, "66 8B 15 ?? ?? ?? ?? 53 56 57 8B 7C 24 10 32 DB B8 E0 C8 10 00" },
    { "Game script dispatch call", 0x54DEA9, "E8 82 07 F2 FF A0 ?? ?? ?? ?? 83 C4 04 84 C0" },
    { "CPhysical::GetMoveSpeed", 0x725190, "E8 2B C9 F0 FF 8B D0 8B 44 24 04 85 D2 74 1D 8B 8A 10 01 00 00 89 08" },
    { "CPhysical::GetTurnSpeed", 0x7251E0, "E8 DB C8 F0 FF 8B D0 8B 44 24 04 85 D2 74 1D 8B 8A 20 01 00 00 89 08" },
    { "CPhysical::SetMoveSpeed", 0x725F10, "56 8B F1 8B 46 38 85 C0 0F 84 83 00 00 00 0F B7 40 08 53 BB FF FF 00 00" },
    { "CPhysical::SetTurnSpeed", 0x726060, "56 8B F1 8B 46 38 85 C0 0F 84 83 00 00 00 0F B7 40 08 53 BB FF FF 00 00" },
    { "Linear velocity collider dispatch", 0x725F94, "8B 10 57 8B C8 FF 92 A8 00 00 00" },
    { "Angular velocity collider dispatch", 0x7260E4, "8B 10 57 8B C8 FF 92 B0 00 00 00" },
    { "CWheel::SetSteering", 0x6D05A0, "8B 81 3C 01 00 00 8B 91 64 01 00 00 89 81 4C 01 00 00" },
    { "Wheel suspension read in renderer", 0x6CF66C, "F3 0F 10 4F 78 76 54 0F 2F E5 72 0C" },
};
static_assert(sizeof(Signatures) / sizeof(Signatures[0]) == static_cast<std::size_t>(Binding::Count), "One signature per binding");

constexpr std::uint32_t Rva(Binding id)
{
    return Signatures[static_cast<std::size_t>(id)].rva;
}

inline int HexDigit(char value)
{
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

inline bool Matches(const std::uint8_t* data, std::size_t length, const char* signature)
{
    for (; *signature != '\0'; ++data, --length)
    {
        if (length == 0 || signature[1] == '\0') return false;
        if (signature[0] != '?' || signature[1] != '?')
        {
            const auto hi = HexDigit(signature[0]), lo = HexDigit(signature[1]);
            if (hi < 0 || lo < 0 || *data != (hi << 4 | lo)) return false;
        }
        signature += 2;
        if (*signature != '\0' && *signature++ != ' ') return false;
    }
    return true;
}
}
