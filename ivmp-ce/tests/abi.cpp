// SPDX-License-Identifier: GPL-3.0-only
#include "../client/CEBindings.h"
#include <cstdio>
#include <cstdlib>
#include <initializer_list>
#include <utility>

namespace
{
using ivmp_ce::Binding;
using ivmp_ce::Engine;

struct Vehicle
{
    float x, y, z;
};

void Check(bool condition, const char* message)
{
    if (condition) return;
    std::fprintf(stderr, "FAIL: %s\n", message);
    std::exit(1);
}

void* __stdcall FindNative(std::uint32_t hash)
{
    static char handler;
    return hash == 0x7D7619D2 ? &handler : nullptr;
}

void __thiscall WriteSpeed(void* raw, const void* argument)
{
    const auto speed = static_cast<const float*>(argument);
    Check(reinterpret_cast<std::uintptr_t>(speed) % 16 == 0 && speed[3] == 0.0f, "aligned setter vector");
    *static_cast<Vehicle*>(raw) = { speed[0], speed[1], speed[2] };
}

void* __thiscall ReadSpeed(void* raw, void* argument)
{
    auto result = static_cast<float*>(argument);
    const auto vehicle = static_cast<Vehicle*>(raw);
    Check(reinterpret_cast<std::uintptr_t>(result) % 16 == 0, "aligned getter vector");
    result[0] = vehicle->x;
    result[1] = vehicle->y;
    result[2] = vehicle->z;
    return result;
}

template<class Function>
Engine ForTest(Binding id, Function function)
{
    const auto base = reinterpret_cast<std::uintptr_t>(function) - ivmp_ce::Rva(id);
    return { reinterpret_cast<const std::uint8_t*>(base) };
}
}

int main()
{
    const auto native = ForTest(Binding::FindNative, FindNative);
    for (int repeat = 0; repeat != 128; ++repeat)
    {
        Check(native.NativeHandler(0) == nullptr, "unknown native hash");
        Check(native.NativeHandler(0x7D7619D2) != nullptr, "known native hash");
    }
    for (const auto bindings : { std::pair<Binding, Binding>{ Binding::SetMoveSpeed, Binding::GetMoveSpeed },
                                { Binding::SetTurnSpeed, Binding::GetTurnSpeed } })
    {
        Vehicle vehicle{};
        const auto writer = ForTest(bindings.first, WriteSpeed);
        writer.WriteSpeed(&vehicle, bindings.first, 12.5f, -8.0f, 1.25f);
        float x = 0.0f, y = 0.0f, z = 0.0f;
        const auto reader = ForTest(bindings.second, ReadSpeed);
        Check(reader.ReadSpeed(&vehicle, bindings.second, x, y, z), "x86 getter return ABI");
        Check(x == 12.5f && y == -8.0f && z == 1.25f, "velocity round trip");
    }
    const std::uint8_t sample[]{ 0xe8, 0x33, 0xc3 };
    Check(ivmp_ce::Matches(sample, 3, "E8 ?? C3"), "masked signature");
    Check(!ivmp_ce::Matches(sample, 2, "E8 ?? C3"), "truncated signature");
    Check(!ivmp_ce::Matches(sample, 3, "E8 ?? C2"), "changed byte");
    std::puts("x86 stdcall native lookup and thiscall vehicle ABI passed");
}
