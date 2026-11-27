// SPDX-License-Identifier: GPL-3.0-only
#include <winsock2.h>
#include <windows.h>
#include <Network/CNetworkModule.h>
#include <Network/PacketIdentifiers.h>
#include <cstdio>
#include <cstdlib>
#include <initializer_list>

namespace
{
bool joined[2]{};
bool refused = false;

void Handle(unsigned index, CPacket* packet)
{
    if (packet->packetId != PACKET_RPC) return;
    CBitStream reader(packet->ucData, packet->uiLength, false);
    RPCIdentifier id = 0;
    if (!reader.Read(id)) return;
    if (id == RPC_JoinedGame) joined[index] = true;
    if (id == RPC_ConnectionRefused) refused = true;
}

void HandleFirst(CPacket* packet) { Handle(0, packet); }
void HandleSecond(CPacket* packet) { Handle(1, packet); }

bool Connect(CNetClientInterface& peer, unsigned short port, PacketHandler_t handler)
{
    if (!peer.Startup()) return false;
    peer.SetHost("127.0.0.1");
    peer.SetPort(port);
    peer.SetPacketHandler(handler);
    return peer.Connect() == CONNECTION_ATTEMPT_STARTED;
}

bool Authorize(CNetClientInterface& peer, const char* name)
{
    CBitStream connect;
    connect.Write(NETWORK_VERSION);
    connect.Write(String(name));
    CheckGTAFiles files{};
    connect.Write(reinterpret_cast<const char*>(&files), sizeof(files));
    return peer.RPC(RPC_PlayerConnect, &connect, PRIORITY_HIGH, RELIABILITY_RELIABLE_ORDERED) != 0;
}

template<typename Ready>
bool Wait(CNetClientInterface& first, CNetClientInterface& second, Ready ready)
{
    const auto start = GetTickCount64();
    while (!refused && GetTickCount64() - start < 5000)
    {
        first.Process();
        second.Process();
        if (ready()) return true;
        Sleep(5);
    }
    return false;
}
}

int main(int argc, char** argv)
{
    char* end = nullptr;
    const auto port = argc == 2 ? std::strtol(argv[1], &end, 10) : 0;
    if (port <= 0 || port > 65535 || *end)
    {
        std::fputs("Usage: ivmp-ce-peers.exe local-server-port\n", stderr);
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
    const bool connected = first && second && Connect(*first, serverPort, HandleFirst) &&
        Connect(*second, serverPort, HandleSecond) &&
        Wait(*first, *second, [&] { return first->IsConnected() && second->IsConnected(); });
    const bool authorized = connected && Authorize(*first, "PortAlpha") && Authorize(*second, "PortBeta") &&
        Wait(*first, *second, [] { return joined[0] && joined[1]; });

    for (auto peer : { first, second })
    {
        if (!peer) continue;
        if (peer->IsConnected()) peer->Disconnect();
        peer->Shutdown(100);
        destroy(peer);
    }
    FreeLibrary(module);

    if (!connected) std::fputs("Two local IV:MP clients did not complete the transport handshake.\n", stderr);
    else if (!authorized) std::fputs("Two local IV:MP clients did not receive JoinedGame.\n", stderr);
    else std::puts("Two local IV:MP clients completed the handshake and received JoinedGame.");
    return authorized ? 0 : 1;
}
