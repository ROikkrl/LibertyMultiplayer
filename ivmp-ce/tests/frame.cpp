// SPDX-License-Identifier: GPL-3.0-only
#include "../client/CEFrame.h"
#include <cstdio>
#include <cstdlib>

namespace
{
void Check(bool ok, const char* message)
{
    if (ok) return;
    std::fprintf(stderr, "%s\n", message);
    std::exit(1);
}

std::uint32_t __cdecl Replacement(std::uint32_t value) { return value * 5; }

const std::uint8_t* observedImage = nullptr;
unsigned frames = 0;

void Observe(const ivmp_ce::Engine& engine)
{
    observedImage = engine.base;
    ++frames;
}

void TestFramePump()
{
    using ivmp_ce::Binding;
    auto image = static_cast<std::uint8_t*>(VirtualAlloc(nullptr, ivmp_ce::ImageSize, MEM_RESERVE, PAGE_NOACCESS));
    Check(image != nullptr, "could not reserve a test image");
    auto dispatch = image + ivmp_ce::Rva(Binding::RunScriptThreads);
    auto call = image + ivmp_ce::Rva(Binding::RunScriptCallSite);
    auto activeThreads = reinterpret_cast<std::uint16_t*>(image + 0x174af08);
    Check(VirtualAlloc(dispatch, 16, MEM_COMMIT, PAGE_READWRITE) != nullptr, "could not commit game dispatcher");
    Check(VirtualAlloc(call - 4, 16, MEM_COMMIT, PAGE_READWRITE) != nullptr, "could not commit frame caller");
    Check(VirtualAlloc(activeThreads, sizeof(*activeThreads), MEM_COMMIT, PAGE_READWRITE) != nullptr,
        "could not commit script thread count");

    // movzx eax, word ptr [threadCount]; add eax, [esp+4]; ret
    const std::uint8_t dispatcher[] = { 0x0f, 0xb7, 0x05, 0, 0, 0, 0, 0x03, 0x44, 0x24, 0x04, 0xc3 };
    const auto threadCountAddress = reinterpret_cast<std::uintptr_t>(activeThreads);
    std::memcpy(dispatch, dispatcher, sizeof(dispatcher));
    std::memcpy(dispatch + 3, &threadCountAddress, sizeof(threadCountAddress));
    const std::uint8_t caller[] = { 0xff, 0x74, 0x24, 0x04, 0xe8, 0, 0, 0, 0, 0x83, 0xc4, 0x04, 0xc3 };
    const auto displacement = reinterpret_cast<std::uintptr_t>(dispatch) - reinterpret_cast<std::uintptr_t>(call + 5);
    std::memcpy(call - 4, caller, sizeof(caller));
    std::memcpy(call + 1, &displacement, sizeof(displacement));
    DWORD old = 0;
    Check(VirtualProtect(dispatch, sizeof(dispatcher), PAGE_EXECUTE_READ, &old), "dispatcher protection");
    Check(VirtualProtect(call - 4, sizeof(caller), PAGE_EXECUTE_READ, &old), "caller protection");
    FlushInstructionCache(GetCurrentProcess(), dispatch, sizeof(dispatcher));
    FlushInstructionCache(GetCurrentProcess(), call - 4, sizeof(caller));

    auto& pump = ivmp_ce::FramePump::Instance();
    *activeThreads = 1;
    Check(!pump.Install({ image }, Observe), "running scheduler accepted");
    *activeThreads = 0;
    Check(pump.Install({ image }, Observe), "could not install frame callback");
    *activeThreads = 2;
    const auto run = reinterpret_cast<ivmp_ce::ScriptDispatch>(call - 4);
    Check(run(20) == 22 && frames == 1 && observedImage == image, "frame callback did not preserve the game result");
    Check(pump.Remove(), "frame callback restoration failed");
    Check(run(21) == 23 && frames == 1, "callback remained active after restoration");
    Check(VirtualFree(image, 0, MEM_RELEASE), "test image release failed");
}
}

int main()
{
    auto code = static_cast<std::uint8_t*>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    Check(code != nullptr, "VirtualAlloc failed");
    const std::uint8_t caller[] = { 0xff, 0x74, 0x24, 0x04, 0xe8, 55, 0, 0, 0, 0x83, 0xc4, 0x04, 0xc3 };
    const std::uint8_t original[] = { 0x8b, 0x44, 0x24, 0x04, 0x83, 0xc0, 0x07, 0xc3 };
    std::memcpy(code, caller, sizeof(caller));
    std::memcpy(code + 64, original, sizeof(original));
    DWORD old = 0;
    Check(VirtualProtect(code, 4096, PAGE_EXECUTE_READ, &old), "VirtualProtect failed");
    FlushInstructionCache(GetCurrentProcess(), code, 4096);
    const auto run = reinterpret_cast<std::uint32_t (__cdecl*)(std::uint32_t)>(code);
    Check(run(3) == 10, "original call");

    ivmp_ce::RelativeCall hook;
    const auto prev = reinterpret_cast<std::uintptr_t>(code + 64);
    const auto next = reinterpret_cast<std::uintptr_t>(&Replacement);
    Check(!hook.Install(code + 4, prev + 1, next), "wrong original target accepted");
    Check(hook.Install(code + 4, prev, next), "valid original target rejected");
    Check(run(3) == 15, "replacement call");
    Check(hook.Remove(), "failed to restore original call");
    Check(run(3) == 10, "restored original call");
    Check(VirtualFree(code, 0, MEM_RELEASE), "VirtualFree failed");
    TestFramePump();
    std::puts("Win32 frame observer preserved the original call and return value and restored the call site");
}
