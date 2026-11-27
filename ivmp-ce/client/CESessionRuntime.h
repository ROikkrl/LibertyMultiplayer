// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CELocalPlayer.h"
#include "CERemoteActors.h"
#include "CESession.h"
#include <Network/CNetworkModule.h>
#include <cwchar>
#include <string>

namespace ivmp_ce
{
class SessionRuntime
{
    struct Settings
    {
        char host[256]{}, nickname[MAX_NAME_LENGTH + 1]{}, password[128]{};
        unsigned short port = 0;
        bool autoConnect = false;
        bool renderPeers = false;
    };

    std::wstring settingsPath, networkPath;
    HMODULE library = nullptr;
    DestroyNetClientInterface_t destroy = nullptr;
    CNetClientInterface* peer = nullptr;
    Session session;
    RemoteActors actors;
    ULONGLONG deadline = 0, nextSync = 0;
    bool f9Down = false, autoAttempted = false, sentPosition = false, renderPeers = false;

    static void Report(const wchar_t* message)
    {
        const auto line = std::wstring(L"IV:MP CE: ") + message + L"\n";
        OutputDebugStringW(line.c_str());
    }

    template<std::size_t Size>
    bool ReadText(const wchar_t* key, const wchar_t* fallback, char (&value)[Size]) const
    {
        wchar_t input[256]{};
        const DWORD length = GetPrivateProfileStringW(L"session", key, fallback, input,
            _countof(input), settingsPath.c_str());
        if (length >= _countof(input) - 1) return false;
        return WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, input, -1,
            value, static_cast<int>(Size), nullptr, nullptr) > 0;
    }

    Settings ReadSettings() const
    {
        Settings settings;
        wchar_t portText[12]{};
        if (settingsPath.empty() ||
            !ReadText(L"host", L"", settings.host) || !settings.host[0] ||
            !ReadText(L"nickname", L"Niko", settings.nickname) || !settings.nickname[0] ||
            !ReadText(L"password", L"", settings.password) ||
            GetPrivateProfileStringW(L"session", L"port", L"9999", portText,
                _countof(portText), settingsPath.c_str()) >= _countof(portText) - 1) return settings;
        wchar_t* end = nullptr;
        const auto port = std::wcstoul(portText, &end, 10);
        if (*end || !port || port > 65535) return settings;
        settings.port = static_cast<unsigned short>(port);
        settings.autoConnect = GetPrivateProfileIntW(L"session", L"auto_connect", 0, settingsPath.c_str()) == 1;
        settings.renderPeers = GetPrivateProfileIntW(L"session", L"render_peers", 0, settingsPath.c_str()) == 1;
        return settings;
    }

    static bool IsGameForeground()
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(GetForegroundWindow(), &pid);
        return pid == GetCurrentProcessId();
    }

    static void OnPacket(CPacket* packet)
    {
        if (packet) Instance().session.Handle(*packet);
    }

    void Stop()
    {
        session.Reset();
        if (peer)
        {
            peer->Shutdown(20);
            destroy(peer);
            peer = nullptr;
        }
        if (library)
        {
            FreeLibrary(library);
            library = nullptr;
        }
        destroy = nullptr;
        renderPeers = false;
    }

    void Start(const Settings& settings, ULONGLONG now)
    {
        if (!settings.port)
        {
            Report(L"Set a valid host and port in ivmp-ce.ini.");
            return;
        }
        library = LoadLibraryExW(networkPath.c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!library)
        {
            Report(L"Could not load Network.Core.dll beside GTAIV.exe.");
            return;
        }
        const auto verify = reinterpret_cast<VerifyVersion_t>(GetProcAddress(library, "VerifyVersion"));
        const auto create = reinterpret_cast<GetNetClientInterface_t>(GetProcAddress(library, "GetNetClientInterface"));
        destroy = reinterpret_cast<DestroyNetClientInterface_t>(GetProcAddress(library, "DestroyNetClientInterface"));
        if (!verify || !verify(NETWORK_MODULE_VERSION) || !create || !destroy)
        {
            Report(L"Incompatible IV:MP network module.");
            Stop();
            return;
        }
        peer = create();
        if (!peer || !peer->Startup())
        {
            Report(L"Could not initialize the IV:MP transport.");
            Stop();
            return;
        }
        peer->SetHost(settings.host);
        peer->SetPort(settings.port);
        peer->SetPassword(settings.password);
        peer->SetPacketHandler(OnPacket);
        session.Begin(*peer, settings.nickname);
        if (peer->Connect() != CONNECTION_ATTEMPT_STARTED)
        {
            Report(L"Could not start the IV:MP connection.");
            Stop();
            return;
        }
        deadline = now + 10000;
        sentPosition = false;
        renderPeers = settings.renderPeers;
        Report(L"Connecting to the IV:MP server.");
    }

    void SyncWorld(const Engine& engine, ULONGLONG now)
    {
        if (now < nextSync) return;
        nextSync = now + 16;
        const LocalPlayerReader reader(engine);
        LocalPlayer player;
        if (!reader.Read(player) || player.episode != 0)
        {
            actors.Clear(engine);
            return;
        }
        if (renderPeers) actors.Tick(engine, session, player, now);
        OnFootMotion motion;
        if (!reader.ReadOnFoot(player, motion)) return;
        OnFootSyncData data{};
        data.vecPos = CVector3(player.x, player.y, player.z);
        data.vecMoveSpeed = CVector3(motion.vx, motion.vy, motion.vz);
        data.fHeading = motion.heading;
        data.uHealthArmour = ((motion.health & 0xffff) << 16) | (motion.armour & 0xffff);
        data.bDuckState = motion.ducking;
        if (session.SendOnFoot(data) && !sentPosition)
        {
            sentPosition = true;
            Report(L"Sent the first on-foot update from GTA IV.");
        }
    }

    SessionRuntime()
    {
        wchar_t executable[MAX_PATH]{};
        const auto length = GetModuleFileNameW(nullptr, executable, MAX_PATH);
        if (!length || length == MAX_PATH) return;
        const std::wstring path(executable, length);
        const auto slash = path.find_last_of(L"\\/");
        if (slash == std::wstring::npos) return;
        const auto directory = path.substr(0, slash + 1);
        settingsPath = directory + L"ivmp-ce.ini";
        networkPath = directory + L"Network.Core.dll";
    }

public:
    static SessionRuntime& Instance()
    {
        // The ASI is pinned, and process exit tears down RakNet with the game.
        // Explicit disconnects release the peer outside the Windows loader.
        static auto* runtime = new SessionRuntime();
        return *runtime;
    }

    void Tick(const Engine& engine, ULONGLONG now)
    {
        const bool down = (GetAsyncKeyState(VK_F9) & 0x8000) != 0;
        const bool toggle = down && !f9Down && IsGameForeground();
        f9Down = down;

        Settings settings;
        if (!autoAttempted || (toggle && !peer)) settings = ReadSettings();
        const bool shouldConnect = !autoAttempted && settings.autoConnect;
        autoAttempted = true;
        if (toggle && peer)
        {
            Report(L"Disconnected from the IV:MP server.");
            actors.Clear(engine);
            Stop();
            return;
        }
        if (!peer && (toggle || shouldConnect)) Start(settings, now);
        if (!peer) return;

        peer->Process();
        if (session.CompleteJoin()) Report(L"Joined the IV:MP server.");
        const auto state = session.GetState();
        if (state == Session::State::Joined) SyncWorld(engine, now);
        else if (state == Session::State::Failed || now >= deadline)
        {
            Report(state == Session::State::Failed ? session.Status() : L"The IV:MP connection timed out.");
            actors.Clear(engine);
            Stop();
        }
    }
};
}
