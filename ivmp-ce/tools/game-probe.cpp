// SPDX-License-Identifier: GPL-3.0-only
#include "../client/CEFrame.h"
#include "../client/CELocalPlayer.h"

namespace
{
void Report(const char* line)
{
    wchar_t path[MAX_PATH]{};
    const auto size = GetTempPathW(MAX_PATH, path);
    constexpr wchar_t name[] = L"IVMP.CE-Probe.log";
    if (size == 0 || size + (sizeof(name) / sizeof(name[0])) > MAX_PATH) return;
    std::memcpy(path + size, name, sizeof(name));
    const auto file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(file, line, static_cast<DWORD>(std::strlen(line)), &written, nullptr);
    CloseHandle(file);
}

void Observe(const ivmp_ce::Engine& engine)
{
    static bool frames = false, local = false, vehicles = false;
    if (!frames)
    {
        frames = true;
        Report("Original CE script scheduler returned on the game thread.\r\n");
    }
    if (local && vehicles) return;
    const auto pools = engine.Pools();
    if (!pools || !*pools.peds || !*pools.vehicles) return;
    ivmp_ce::LocalPlayer player;
    if (!ivmp_ce::LocalPlayerReader(engine).Read(player)) return;
    const auto actor = pools.ResolvePed(player.ped);
    if (!local && actor && pools.encode(*pools.peds, actor) == player.ped)
    {
        local = true;
        Report("Local actor and position read. Ped handle round trip passed.\r\n");
    }
    if (!vehicles && player.vehicle)
    {
        const auto car = pools.ResolveVehicle(player.vehicle);
        if (car && pools.encode(*pools.vehicles, car) == player.vehicle)
        {
            float x = 0.f, y = 0.f, z = 0.f;
            if (engine.ReadSpeed(car, ivmp_ce::Binding::GetMoveSpeed, x, y, z))
            {
                vehicles = true;
                Report("Current vehicle handle and linear speed read.\r\n");
            }
        }
    }
}
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
        const auto engine = ivmp_ce::ResolveLoadedModule(GetModuleHandleW(nullptr));
        if (!engine) Report("Probing skipped because the loaded executable signatures differ.\r\n");
        else if (!ivmp_ce::FramePump::Instance().Install(engine, Observe)) Report("Script call site is busy or the game has already started.\r\n");
        else
        {
            Report("CE profile matched and observational frame pump installed.\r\n");
            HMODULE pinned = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                reinterpret_cast<LPCWSTR>(&Observe), &pinned);
        }
    }
    else if (reason == DLL_PROCESS_DETACH && !reserved) ivmp_ce::FramePump::Instance().Remove();
    return TRUE;
}
