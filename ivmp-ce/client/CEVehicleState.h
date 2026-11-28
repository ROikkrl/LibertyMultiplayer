// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include <Common.h>
#include <Network/CBitStream.h>
#include <Network/RPCIdentifiers.h>
#include <cmath>
#include <map>

namespace ivmp_ce
{
struct VehicleState
{
    int model = -1;
    unsigned generation = 0, revision = 0, health = 0;
    unsigned hornDuration = 0, hornRevision = 0;
    ULONGLONG hornAt = 0;
    float petrol = 0.0f, dirt = 0.0f;
    CVector3 position, rotation, movement, spin;
    unsigned char colours[4]{};
    bool engine = false, lights = false, siren = false;

    static bool Finite(const CVector3& value)
    {
        return std::isfinite(value.fX) && std::isfinite(value.fY) && std::isfinite(value.fZ);
    }

    static bool OptionalVector(CBitStream& packet, CVector3& value)
    {
        return packet.GetNumberOfUnreadBits() && (!packet.ReadBit() || (packet.Read(value) && Finite(value)));
    }

    static bool ReadFlag(CBitStream& packet, bool& value)
    {
        unsigned char byte;
        if (!packet.Read(byte) || byte > 1) return false;
        value = byte != 0;
        return true;
    }

    static bool ReadSpawn(CBitStream& packet, VehicleState& state)
    {
        VehicleState vehicle;
        int respawnDelay, hornDuration, lockState;
        if (!packet.Read(vehicle.model) || vehicle.model < 0 || vehicle.model > 126 ||
            !packet.Read(vehicle.health) || !packet.Read(vehicle.petrol) || !std::isfinite(vehicle.petrol) ||
            !packet.Read(vehicle.position) || !Finite(vehicle.position) || !OptionalVector(packet, vehicle.rotation) ||
            !OptionalVector(packet, vehicle.spin) || !OptionalVector(packet, vehicle.movement) ||
            !packet.Read(respawnDelay) || !packet.Read(reinterpret_cast<char*>(vehicle.colours), sizeof(vehicle.colours)) ||
            !packet.Read(vehicle.dirt) || !std::isfinite(vehicle.dirt) || packet.GetNumberOfUnreadBits() < 13)
            return false;

        packet.IgnoreBits(13); // Four indicators and nine component flags.
        if (!packet.Read(hornDuration) || !ReadFlag(packet, vehicle.siren) || !packet.Read(lockState) ||
            !ReadFlag(packet, vehicle.engine) || !ReadFlag(packet, vehicle.lights)) return false;
        if (hornDuration > 0) vehicle.SoundHorn(static_cast<unsigned>(hornDuration));
        for (unsigned i = 0; i != 6; ++i)
        {
            float angle;
            if (!packet.Read(angle)) return false;
        }
        for (unsigned i = 0; i != 6; ++i)
        {
            bool flag;
            if (!ReadFlag(packet, flag)) return false; // Four windows, taxi light and GPS.
        }
        if (!packet.GetNumberOfUnreadBits()) return false;
        if (packet.ReadBit())
        {
            unsigned char variation;
            if (!packet.Read(variation)) return false;
        }
        state = vehicle;
        return true;
    }

    void SoundHorn(unsigned duration)
    {
        hornDuration = duration > 5000 ? 5000 : duration;
        hornAt = GetTickCount64();
        ++hornRevision;
    }

    bool ApplyDriver(const InVehicleSyncData& frame)
    {
        const auto lightByte = reinterpret_cast<const unsigned char*>(&frame)[offsetof(InVehicleSyncData, bLights)];
        if (!Finite(frame.vecPos) || !Finite(frame.vecRotation) || !Finite(frame.vecMoveSpeed) ||
            !Finite(frame.vecTurnSpeed) || !std::isfinite(frame.fPetrolHealth) ||
            !std::isfinite(frame.fDirtLevel) || lightByte > 1) return false;
        position = frame.vecPos;
        rotation = frame.vecRotation;
        movement = frame.vecMoveSpeed;
        spin = frame.vecTurnSpeed;
        health = frame.uiHealth;
        petrol = frame.fPetrolHealth;
        dirt = frame.fDirtLevel;
        for (unsigned i = 0; i != 4; ++i) colours[i] = frame.byteColors[i];
        engine = frame.bEngineStatus;
        lights = lightByte != 0;
        siren = frame.bSirenState;
        ++revision;
        return true;
    }
};

class VehicleRegistry
{
    std::map<EntityId, VehicleState> vehicles;
    unsigned nextGeneration = 0;

public:
    bool Handle(RPCIdentifier rpc, CBitStream& packet)
    {
        const bool compressed = rpc == RPC_NewVehicle || rpc == RPC_DeleteVehicle || rpc == RPC_ScriptingSetVehicleHealth;
        switch (rpc)
        {
        case RPC_NewVehicle:
        case RPC_DeleteVehicle:
        case RPC_ScriptingSetVehicleCoordinates:
        case RPC_ScriptingSetVehicleRotation:
        case RPC_ScriptingSetVehicleColor:
        case RPC_ScriptingSetVehicleHealth:
        case RPC_ScriptingSetCarLights:
        case RPC_ScriptingSoundVehicleHorn:
            break;
        default: return false;
        }
        EntityId id;
        if (!(compressed ? packet.ReadCompressed(id) : packet.Read(id)) || id >= MAX_VEHICLES) return true;
        if (rpc == RPC_NewVehicle)
        {
            VehicleState vehicle;
            if (VehicleState::ReadSpawn(packet, vehicle))
            {
                vehicle.generation = ++nextGeneration;
                vehicles[id] = vehicle;
            }
            return true;
        }
        if (rpc == RPC_DeleteVehicle)
        {
            vehicles.erase(id);
            return true;
        }
        auto* vehicle = Find(id);
        if (!vehicle) return true;
        if (rpc == RPC_ScriptingSetVehicleCoordinates || rpc == RPC_ScriptingSetVehicleRotation)
        {
            CVector3 value;
            if (!packet.Read(value) || !VehicleState::Finite(value)) return true;
            (rpc == RPC_ScriptingSetVehicleCoordinates ? vehicle->position : vehicle->rotation) = value;
        }
        else if (rpc == RPC_ScriptingSetVehicleColor)
        {
            unsigned char colours[4];
            if (!packet.Read(reinterpret_cast<char*>(colours), sizeof(colours))) return true;
            for (unsigned i = 0; i != 4; ++i) vehicle->colours[i] = colours[i];
        }
        else if (rpc == RPC_ScriptingSetCarLights)
        {
            if (!VehicleState::ReadFlag(packet, vehicle->lights)) return true;
        }
        else if (rpc == RPC_ScriptingSoundVehicleHorn)
        {
            unsigned duration = 0;
            if (packet.Read(duration)) vehicle->SoundHorn(duration);
            return true;
        }
        else
        {
            unsigned value;
            if (!packet.Read(value)) return true;
            vehicle->health = value;
        }
        ++vehicle->revision;
        return true;
    }

    VehicleState* Find(EntityId id)
    {
        const auto found = vehicles.find(id);
        return found == vehicles.end() ? nullptr : &found->second;
    }

    const std::map<EntityId, VehicleState>& Entries() const { return vehicles; }
    void Clear() { vehicles.clear(); }
};
}
