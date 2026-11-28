// SPDX-License-Identifier: GPL-3.0-only
#include <winsock2.h>
#include <windows.h>
#include <Network/CNetworkModule.h>
#include "../client/CESession.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <initializer_list>

namespace
{
using ivmp_ce::Session;
Session sessions[2];

void HandleFirst(CPacket* packet) { if (packet) sessions[0].Handle(*packet); }
void HandleSecond(CPacket* packet) { if (packet) sessions[1].Handle(*packet); }

bool Connect(CNetClientInterface& peer, Session& session, const char* name, unsigned short port, PacketHandler_t handler)
{
    if (!peer.Startup()) return false;
    peer.SetHost("127.0.0.1");
    peer.SetPort(port);
    peer.SetPacketHandler(handler);
    session.Begin(peer, name);
    return peer.Connect() == CONNECTION_ATTEMPT_STARTED;
}

template<typename Ready>
bool Wait(Ready ready)
{
    const auto start = GetTickCount64();
    while (GetTickCount64() - start < 5000)
    {
        if (ready()) return true;
        if (sessions[0].GetState() == Session::State::Failed ||
            sessions[1].GetState() == Session::State::Failed) return false;
        Sleep(5);
    }
    return false;
}
}

int main(int argc, char** argv)
{
    char* end = nullptr;
    const auto port = argc >= 2 ? std::strtol(argv[1], &end, 10) : 0;
    const bool testVehicles = argc == 3 && std::strcmp(argv[2], "--vehicles") == 0;
    if (port <= 0 || port > 65535 || *end || (argc != 2 && !testVehicles))
    {
        std::fputs("Usage: ivmp-ce-peers.exe local-server-port [--vehicles]\n", stderr);
        return 2;
    }

    const auto module = LoadLibraryW(L".\\Network.Core.dll");
    if (!module) return 3;
    const auto verify = reinterpret_cast<VerifyVersion_t>(GetProcAddress(module, "VerifyVersion"));
    const auto create = reinterpret_cast<GetNetClientInterface_t>(GetProcAddress(module, "GetNetClientInterface"));
    const auto destroy = reinterpret_cast<DestroyNetClientInterface_t>(GetProcAddress(module, "DestroyNetClientInterface"));
    if (!verify || !verify(NETWORK_MODULE_VERSION) || !create || !destroy)
    {
        FreeLibrary(module);
        return 4;
    }

    auto first = create();
    auto second = create();
    const auto serverPort = static_cast<unsigned short>(port);
    const auto pump = [&]
    {
        first->Process();
        second->Process();
        for (auto& session : sessions) session.CompleteJoin();
    };
    const bool authorized = first && second && Connect(*first, sessions[0], "PortAlpha", serverPort, HandleFirst) &&
        Connect(*second, sessions[1], "PortBeta", serverPort, HandleSecond) &&
        Wait([&]
        {
            pump();
            return sessions[0].GetState() == Session::State::Joined &&
                sessions[1].GetState() == Session::State::Joined &&
                sessions[0].LocalId() != sessions[1].LocalId();
        });
    OnFootSyncData frame{};
    frame.vecPos = CVector3(24.0f, -50.0f, 16.0f);
    frame.fHeading = 82.0f;
    frame.vecMoveSpeed = CVector3(1.0f, 2.0f, 0.0f);
    frame.uHealthArmour = (91u << 16) | 23u;
    auto reply = frame;
    reply.vecPos.fY = 75.0f;
    const bool synced = authorized && Wait([&]
    {
        sessions[0].SendOnFoot(frame);
        sessions[1].SendOnFoot(reply);
        pump();
        const auto remote = sessions[1].GetRemote(sessions[0].LocalId());
        const auto returned = sessions[0].GetRemote(sessions[1].LocalId());
        return remote && remote->present && remote->hasPosition &&
            remote->onFoot.vecPos.fY == frame.vecPos.fY &&
            remote->onFoot.vecMoveSpeed.fX == frame.vecMoveSpeed.fX &&
            remote->onFoot.fHeading == frame.fHeading &&
            remote->onFoot.uHealthArmour == frame.uHealthArmour &&
            returned && returned->present && returned->hasPosition &&
            returned->onFoot.vecPos.fY == reply.vecPos.fY;
    });
    bool vehiclesSynced = !testVehicles;
    const auto& cars = sessions[0].Vehicles().Entries();
    if (synced && testVehicles && !cars.empty())
    {
        const auto id = cars.begin()->first;
        const auto& initial = cars.begin()->second;
        InVehicleSyncData car{};
        car.vecPos = initial.position + CVector3(3.0f, 0.0f, 2.0f);
        car.vecRotation = CVector3(3.0f, 12.0f, 54.0f);
        car.vecMoveSpeed = CVector3(0.5f, 0.0f, 0.0f);
        car.uiHealth = 821;
        car.byteColors[0] = 12;
        car.byteColors[3] = 77;
        car.bLights = true;
        car.bSirenState = true;
        vehiclesSynced = Wait([&] {
            CBitStream packet;
            packet.WriteCompressed(id);
            packet.Write(reinterpret_cast<const char*>(&car), sizeof(car));
            packet.Write0();
            first->RPC(RPC_InVehicleSync, &packet, PRIORITY_LOW, RELIABILITY_UNRELIABLE_SEQUENCED);
            pump();
            const auto& received = sessions[1].Vehicles().Entries();
            const auto found = received.find(id);
            if (found == received.end()) return false;
            const auto& state = found->second;
            return state.model == initial.model && state.revision && state.position.fX == car.vecPos.fX &&
                state.rotation.fY == car.vecRotation.fY && state.health == car.uiHealth &&
                state.colours[0] == 12 && state.colours[3] == 77 && state.lights && state.siren &&
                !sessions[1].GetRemote(sessions[0].LocalId())->hasPosition;
        });
    }
    bool removed = false;
    if (synced && vehiclesSynced)
    {
        first->Shutdown(50);
        removed = Wait([&]
        {
            second->Process();
            return !sessions[1].GetRemote(sessions[0].LocalId())->present;
        });
    }

    for (auto peer : { first, second })
    {
        if (!peer) continue;
        peer->Shutdown(50);
        destroy(peer);
    }
    FreeLibrary(module);

    if (!authorized) std::fputs("Two local IV:MP clients did not join.\n", stderr);
    else if (!synced) std::fputs("A local IV:MP client did not receive the other player's on-foot update.\n", stderr);
    else if (!vehiclesSynced) std::fputs("A local IV:MP client did not receive the server vehicle and driver update.\n", stderr);
    else if (!removed) std::fputs("A disconnected IV:MP player remained in the remote session.\n", stderr);
    else std::puts(testVehicles ? "Two IV:MP clients joined, relayed on-foot and vehicle state, and handled disconnect."
                               : "Two IV:MP clients joined, relayed on-foot motion and removed the disconnected peer.");
    return removed ? 0 : 1;
}
