// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CELocalPlayer.h"
#include "CEVehicleState.h"
#include "../CModelManager.h"

namespace ivmp_ce
{
class RemoteVehicles
{
    struct Replica
    {
        unsigned generation = 0, revision = 0;
        std::uint32_t handle = 0, model = 0;
        ULONGLONG nextSpawn = 0;
        bool requested = false;
    };
    static constexpr unsigned Limit = 24;
    std::map<EntityId, Replica> replicas;

    static bool Nearby(const VehicleState& state, const CVector3& local)
    {
        return (state.position - local).Length() <= 140.0f;
    }

    static void ReleaseModel(const NativeCall& native, Replica& replica)
    {
        if (!replica.requested) return;
        std::uint32_t result = 0;
        native.Invoke(0x0FA0E33, result, replica.model); // MARK_MODEL_AS_NO_LONGER_NEEDED
        replica.requested = false;
    }

    static void Delete(const NativeCall& native, const EntityPools& pools, Replica& replica)
    {
        std::uint32_t result = 0;
        if (replica.handle && pools.ResolveVehicle(replica.handle))
            native.Invoke(0x7F71342D, result, &replica.handle); // DELETE_CAR
        ReleaseModel(native, replica);
    }

    static void Apply(const NativeCall& native, Replica& replica, const VehicleState& vehicle, bool newVehicle)
    {
        const auto car = replica.handle;
        std::uint32_t result = 0;
        if (!newVehicle)
            native.Invoke(0x12D64378, result, car, vehicle.position.fX, vehicle.position.fY, vehicle.position.fZ);
        native.Invoke(0x75E40528, result, car, std::fmod(vehicle.rotation.fZ, 360.0f)); // SET_CAR_HEADING
        native.Invoke(0x06441EAF, result, car, unsigned(vehicle.colours[0]), unsigned(vehicle.colours[1]));
        native.Invoke(0x6CB14354, result, car, unsigned(vehicle.colours[2]), unsigned(vehicle.colours[3]));
        native.Invoke(0x49B6525C, result, car, vehicle.health > 2000 ? 2000u : vehicle.health);
        native.Invoke(0x17E2319C, result, car, vehicle.petrol);
        native.Invoke(0x02A57428, result, car, vehicle.dirt);
        native.Invoke(0x0CAA42D0, result, car, unsigned(vehicle.engine), 1u);
        native.Invoke(0x71B81DE7, result, car, vehicle.lights ? 2u : 1u); // FORCE_CAR_LIGHTS
        native.Invoke(0x7781290F, result, car, unsigned(vehicle.siren));
        replica.revision = vehicle.revision;
    }

    static void Spawn(const NativeCall& native, const EntityPools& pools, Replica& replica, const VehicleState& state,
                      ULONGLONG now)
    {
        if (now < replica.nextSpawn) return;
        replica.nextSpawn = now + 200;
        if (!replica.model) return;
        std::uint32_t loaded = 0, result = 0;
        if (!native.Invoke(0x4E61480A, loaded, replica.model)) return;
        if (!loaded)
        {
            if (!replica.requested)
                replica.requested = native.Invoke(0x502B5185, result, replica.model); // REQUEST_MODEL
            return;
        }
        if (!native.Invoke(0x2F1D6843, result, replica.model, state.position.fX, state.position.fY, state.position.fZ,
            &replica.handle, 1u) || !pools.ResolveVehicle(replica.handle))
        {
            replica.handle = 0;
            return;
        }
        ReleaseModel(native, replica);
        native.Invoke(0x295C4C52, result, replica.handle, 1u); // FREEZE_CAR_POSITION
        native.Invoke(0x6702757C, result, replica.handle, 2u); // LOCK_CAR_DOORS
        Apply(native, replica, state, true);
        OutputDebugStringW(L"IV:MP CE: streamed a server vehicle.\n");
    }

public:
    void Tick(const Engine& engine, const VehicleRegistry& vehicles, const LocalPlayer& player, ULONGLONG now)
    {
        const auto pools = engine.Pools();
        if (!pools || !*pools.vehicles) return;
        const NativeCall native(engine);
        const auto& entries = vehicles.Entries();
        const CVector3 local(player.x, player.y, player.z);
        for (auto it = replicas.begin(); it != replicas.end();)
        {
            const auto state = entries.find(it->first);
            if (state == entries.end() || state->second.generation != it->second.generation || !Nearby(state->second, local))
            {
                Delete(native, pools, it->second);
                it = replicas.erase(it);
            }
            else ++it;
        }
        for (const auto& entry : entries)
        {
            const auto& state = entry.second;
            auto it = replicas.find(entry.first);
            if (it == replicas.end())
            {
                if (replicas.size() >= Limit || !Nearby(state, local)) continue;
                CModelManager models;
                const auto model = models.VehicleIdToModelHash(state.model);
                std::uint32_t available = 0;
                if (!model || !native.Invoke(0x771C2838, available, static_cast<std::uint32_t>(model)) || !available) continue;
                Replica replica;
                replica.generation = state.generation;
                replica.model = model;
                it = replicas.emplace(entry.first, replica).first;
            }
            auto& replica = it->second;
            if (!pools.ResolveVehicle(replica.handle))
            {
                replica.handle = 0;
                Spawn(native, pools, replica, state, now);
            }
            else if (replica.revision != state.revision) Apply(native, replica, state, false);
        }
    }

    void Clear(const Engine& engine)
    {
        if (replicas.empty()) return;
        const auto pools = engine.Pools();
        const NativeCall native(engine);
        for (auto& entry : replicas) Delete(native, pools, entry.second);
        replicas.clear();
    }
};
}
