// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include <Common.h>
#include <Network/CNetClientInterface.h>
#include <Network/PacketIdentifiers.h>
#include <array>
#include <cmath>

namespace ivmp_ce
{
class Session
{
public:
    enum class State { Offline, Connecting, Authorizing, Joined, Failed };
    struct Remote
    {
        bool present = false;
        bool hasPosition = false;
        unsigned generation = 0;
        ULONGLONG receivedAt = 0;
        OnFootSyncData onFoot{};
    };

private:
    CNetClientInterface* peer = nullptr;
    String nickname;
    State state = State::Offline;
    EntityId localId = INVALID_ENTITY_ID;
    unsigned fileLists = 0;
    const wchar_t* status = L"";
    std::array<Remote, MAX_PLAYERS> remotes{};

    void Fail(const wchar_t* reason)
    {
        state = State::Failed;
        status = reason;
    }

    static bool Finite(const CVector3& value)
    {
        return std::isfinite(value.fX) && std::isfinite(value.fY) && std::isfinite(value.fZ);
    }

    bool Send(RPCIdentifier id, CBitStream* payload)
    {
        return peer->RPC(id, payload, PRIORITY_HIGH, RELIABILITY_RELIABLE_ORDERED) != 0;
    }

    void Authorize()
    {
        CBitStream data;
        data.Write(NETWORK_VERSION);
        data.Write(nickname);
        CheckGTAFiles files{};
        data.Write(reinterpret_cast<const char*>(&files), sizeof(files));
        if (Send(RPC_PlayerConnect, &data)) state = State::Authorizing;
        else Fail(L"Could not send IV:MP authorization.");
    }

    void HandleRpc(CBitStream& data)
    {
        RPCIdentifier rpc;
        if (!data.Read(rpc)) return;
        if (rpc == RPC_ConnectionRefused)
        {
            Fail(L"The IV:MP server refused authorization.");
            return;
        }
        if (rpc == RPC_NewFile)
        {
            if (data.GetNumberOfUnreadBits() != 0)
                Fail(L"This Complete Edition client does not load server scripts or resources yet.");
            else if (state == State::Authorizing)
                ++fileLists;
            return;
        }
        if (state == State::Authorizing && rpc == RPC_JoinedGame)
        {
            EntityId id;
            if (data.Read(id) && id < MAX_PLAYERS) localId = id;
            return;
        }
        if (state != State::Authorizing && state != State::Joined) return;

        EntityId id;
        if (rpc != RPC_NewPlayer && rpc != RPC_DeletePlayer && rpc != RPC_OnFootSync) return;
        if (!data.ReadCompressed(id) || id >= MAX_PLAYERS || id == localId) return;
        auto& remote = remotes[id];
        if (rpc == RPC_NewPlayer)
        {
            if (!remote.present)
            {
                ++remote.generation;
                remote.present = true;
            }
        }
        else if (rpc == RPC_DeletePlayer)
        {
            remote.present = false;
            remote.hasPosition = false;
        }
        else if (remote.present)
        {
            unsigned short ping;
            bool helmet;
            OnFootSyncData frame{};
            if (data.ReadCompressed(ping) && data.ReadCompressed(helmet) &&
                data.Read(reinterpret_cast<char*>(&frame), sizeof(frame)) &&
                Finite(frame.vecPos) && Finite(frame.vecMoveSpeed) && std::isfinite(frame.fHeading))
            {
                remote.onFoot = frame;
                remote.hasPosition = true;
                remote.receivedAt = GetTickCount64();
            }
        }
    }

public:
    void Begin(CNetClientInterface& client, const String& name)
    {
        peer = &client;
        nickname = name;
        state = State::Connecting;
        localId = INVALID_ENTITY_ID;
        fileLists = 0;
        status = L"";
        remotes = {};
    }

    void Reset()
    {
        peer = nullptr;
        state = State::Offline;
        localId = INVALID_ENTITY_ID;
        fileLists = 0;
        status = L"";
        remotes = {};
    }

    void Handle(CPacket& packet)
    {
        if (!peer || state == State::Failed) return;
        switch (packet.packetId)
        {
        case PACKET_CONNECTION_SUCCEEDED:
            if (state == State::Connecting) Authorize();
            break;
        case PACKET_ALREADY_CONNECTED:
        case PACKET_CONNECTION_REJECTED:
        case PACKET_CONNECTION_FAILED:
        case PACKET_SERVER_FULL:
        case PACKET_BANNED:
        case PACKET_PASSWORD_INVALID:
            Fail(L"Could not connect to the IV:MP server.");
            break;
        case PACKET_DISCONNECTED:
        case PACKET_LOST_CONNECTION:
            Fail(L"Disconnected from the IV:MP server.");
            break;
        case PACKET_RPC:
            if (packet.ucData && packet.uiLength)
            {
                CBitStream data(packet.ucData, packet.uiLength, false);
                HandleRpc(data);
            }
            break;
        }
    }

    // IV:MP sends one resource list and one client script list after JoinedGame.
    // Both lists must be present and empty before the CE client can spawn.
    bool CompleteJoin()
    {
        if (state != State::Authorizing || localId == INVALID_ENTITY_ID || fileLists != 2) return false;
        CBitStream model;
        model.Write(0); // IV:MP's model ID for Niko.
        if (!Send(RPC_PlayerJoinComplete, nullptr) || !Send(RPC_PlayerSpawn, &model))
        {
            Fail(L"Could not complete the IV:MP join.");
            return false;
        }
        state = State::Joined;
        return true;
    }

    bool SendOnFoot(const OnFootSyncData& frame)
    {
        if (state != State::Joined || !Finite(frame.vecPos)) return false;
        CBitStream data;
        data.Write(reinterpret_cast<const char*>(&frame), sizeof(frame));
        data.Write0();
        return peer->RPC(RPC_OnFootSync, &data, PRIORITY_LOW, RELIABILITY_UNRELIABLE_SEQUENCED) != 0;
    }

    State GetState() const { return state; }
    const wchar_t* Status() const { return status; }
    EntityId LocalId() const { return localId; }
    const Remote* GetRemote(EntityId id) const { return id < MAX_PLAYERS ? &remotes[id] : nullptr; }
};
}
