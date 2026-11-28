// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include <Common.h>
#include <Network/CNetClientInterface.h>
#include <Network/PacketIdentifiers.h>
#include <array>
#include <cmath>
#include "CEVehicleState.h"

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
        EntityId vehicle = INVALID_ENTITY_ID;
        unsigned char seat = 0;
        OnFootSyncData onFoot{};
    };
    struct EntryReply
    {
        bool pending = false, accepted = false;
        EntityId vehicle = INVALID_ENTITY_ID;
        unsigned char seat = 0;
    };
    struct Relocation
    {
        bool pending = false, hasHeading = false;
        CVector3 position;
        float heading = 0.0f;
    };

private:
    CNetClientInterface* peer = nullptr;
    String nickname;
    State state = State::Offline;
    EntityId localId = INVALID_ENTITY_ID;
    unsigned fileLists = 0;
    const wchar_t* status = L"";
    std::array<Remote, MAX_PLAYERS> remotes{};
    VehicleRegistry vehicles;
    EntryReply entryReply;
    Relocation spawn, relocation;
    EntryReply seatCommand;

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
        if (vehicles.Handle(rpc, data)) return;
        if (rpc == RPC_ScriptingWarpPlayerIntoVehicle)
        {
            EntityId vehicle;
            unsigned char seat;
            if (data.Read(vehicle) && vehicle < MAX_VEHICLES && data.Read(seat) &&
                seat <= MAX_VEHICLE_PASSENGERS)
                seatCommand = {true, true, vehicle, seat};
            return;
        }
        if (rpc == RPC_ScriptingRemovePlayerFromVehicle)
        {
            if (data.GetNumberOfUnreadBits())
                seatCommand = {true, data.ReadBit(), INVALID_ENTITY_ID, 0};
            return;
        }
        if (rpc == RPC_ScriptingSetPlayerCoordinates || rpc == RPC_ScriptingSetSpawnLocation)
        {
            Relocation move;
            move.hasHeading = rpc == RPC_ScriptingSetSpawnLocation;
            if (!data.Read(move.position) || !Finite(move.position) ||
                std::fabs(move.position.fX) > 10000.0f || std::fabs(move.position.fY) > 10000.0f ||
                std::fabs(move.position.fZ) > 2000.0f ||
                (move.hasHeading && (!data.Read(move.heading) || !std::isfinite(move.heading)))) return;
            move.pending = true;
            (move.hasHeading ? spawn : relocation) = move;
            return;
        }

        EntityId id;
        if (rpc == RPC_VehicleEnterExit)
        {
            if (!data.ReadCompressed(id) || id != localId || !data.GetNumberOfUnreadBits()) return;
            if (!data.ReadBit())
            {
                entryReply = {true, false, INVALID_ENTITY_ID, 0};
                return;
            }
            unsigned char action, seat;
            EntityId vehicle;
            if (data.Read(action) && action == VEHICLE_ENTRY_RETURN && data.Read(vehicle) && data.Read(seat))
                entryReply = {true, true, vehicle, seat};
            return;
        }
        if (rpc == RPC_InVehicleSync || rpc == RPC_PassengerSync)
        {
            EntityId vehicleId;
            unsigned short ping;
            bool helmet;
            if (!data.ReadCompressed(id) || !data.ReadCompressed(vehicleId) || id >= MAX_PLAYERS ||
                id == localId || !remotes[id].present || !data.ReadCompressed(ping) ||
                !data.GetNumberOfUnreadBits() || !data.ReadCompressed(helmet)) return;
            auto* vehicle = vehicles.Find(vehicleId);
            if (!vehicle) return;
            unsigned char seat = 0;
            if (rpc == RPC_InVehicleSync)
            {
                InVehicleSyncData frame{};
                if (!data.Read(reinterpret_cast<char*>(&frame), sizeof(frame)) || !vehicle->ApplyDriver(frame)) return;
            }
            else
            {
                PassengerSyncData frame{};
                if (!data.Read(reinterpret_cast<char*>(&frame), sizeof(frame)) ||
                    frame.byteSeatId == 0 || frame.byteSeatId > MAX_VEHICLE_PASSENGERS) return;
                seat = frame.byteSeatId;
            }
            auto& remote = remotes[id];
            remote.vehicle = vehicleId;
            remote.seat = seat;
            remote.hasPosition = false;
            remote.receivedAt = GetTickCount64();
            return;
        }
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
            remote.vehicle = INVALID_ENTITY_ID;
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
                remote.vehicle = INVALID_ENTITY_ID;
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
        vehicles.Clear();
        entryReply = {};
        seatCommand = {};
        spawn = {};
        relocation = {};
    }

    void Reset()
    {
        peer = nullptr;
        state = State::Offline;
        localId = INVALID_ENTITY_ID;
        fileLists = 0;
        status = L"";
        remotes = {};
        vehicles.Clear();
        entryReply = {};
        seatCommand = {};
        spawn = {};
        relocation = {};
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

    bool VehicleTransition(eVehicleEntryExit action, EntityId vehicle, unsigned char seat)
    {
        if (state != State::Joined || vehicle == INVALID_ENTITY_ID) return false;
        CBitStream packet;
        packet.WriteCompressed(localId);
        packet.Write(static_cast<unsigned char>(action));
        packet.WriteCompressed(vehicle);
        if (action == VEHICLE_ENTRY_REQUEST || action == VEHICLE_ENTRY_COMPLETE || action == VEHICLE_ENTRY_CANCELLED)
            packet.Write(seat);
        return Send(RPC_VehicleEnterExit, &packet);
    }

    EntryReply TakeEntryReply()
    {
        const auto reply = entryReply;
        entryReply = {};
        return reply;
    }

    EntryReply TakeSeatCommand()
    {
        const auto command = seatCommand;
        seatCommand = {};
        return command;
    }

    Relocation TakeRelocation(bool useSpawn)
    {
        const auto move = relocation.pending || !useSpawn ? relocation : spawn;
        spawn = {};
        relocation = {};
        return move;
    }

    template<class Payload>
    bool SendVehicle(RPCIdentifier rpc, EntityId vehicle, const Payload& frame)
    {
        if (state != State::Joined || vehicle == INVALID_ENTITY_ID) return false;
        CBitStream packet;
        packet.WriteCompressed(vehicle);
        packet.Write(reinterpret_cast<const char*>(&frame), sizeof(frame));
        packet.Write0();
        return peer->RPC(rpc, &packet, PRIORITY_LOW, RELIABILITY_UNRELIABLE_SEQUENCED) != 0;
    }

    bool SendDriver(EntityId vehicle, const InVehicleSyncData& frame)
    {
        auto* state = vehicles.Find(vehicle);
        if (!state || !state->ApplyDriver(frame)) return false;
        return SendVehicle(RPC_InVehicleSync, vehicle, frame);
    }

    State GetState() const { return state; }
    const wchar_t* Status() const { return status; }
    EntityId LocalId() const { return localId; }
    const Remote* GetRemote(EntityId id) const { return id < MAX_PLAYERS ? &remotes[id] : nullptr; }
    const VehicleRegistry& Vehicles() const { return vehicles; }
};
}
