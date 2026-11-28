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
        unsigned generation = 0, revision = 0, hornRevision = 0;
        std::uint32_t handle = 0, model = 0;
        ULONGLONG nextSpawn = 0, poseAt = 0, poseUntil = 0;
        CVector3 position;
        float heading = 0.0f;
        bool requested = false;
    };
    static constexpr unsigned Limit = 24;
    std::map<EntityId, Replica> replicas;
    EntityId driven = INVALID_ENTITY_ID;

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

    static void ApplyState(const NativeCall& native, Replica& replica, const VehicleState& vehicle)
    {
        const auto car = replica.handle;
        std::uint32_t result = 0;
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

    static void ApplyPose(const NativeCall& native, Replica& replica, const VehicleState& state, ULONGLONG now)
    {
        const auto delta = state.position - replica.position;
        if (!replica.poseAt || delta.Length() > 15.0f) replica.poseUntil = now;
        const auto elapsed = now - replica.poseAt;
        const float blend = now >= replica.poseUntil ? 1.0f
            : static_cast<float>(elapsed) / static_cast<float>(replica.poseUntil - replica.poseAt);
        replica.position = replica.position + delta * blend;
        const float target = std::fmod(state.rotation.fZ, 360.0f);
        replica.heading = std::fmod(replica.heading + std::remainder(target - replica.heading, 360.0f) * blend + 360.0f, 360.0f);
        std::uint32_t result = 0;
        native.Invoke(0x12D64378, result, replica.handle, replica.position.fX, replica.position.fY, replica.position.fZ);
        native.Invoke(0x75E40528, result, replica.handle, replica.heading); // SET_CAR_HEADING
        replica.poseAt = now;
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
        replica.poseAt = 0;
        ApplyPose(native, replica, state, now);
        ApplyState(native, replica, state);
        OutputDebugStringW(L"IV:MP CE: streamed a server vehicle.\n");
    }

public:
    std::uint32_t HandleOf(EntityId id) const
    {
        const auto found = replicas.find(id);
        return found == replicas.end() ? 0 : found->second.handle;
    }

    EntityId IdOf(std::uint32_t handle) const
    {
        if (handle)
            for (const auto& entry : replicas)
                if (entry.second.handle == handle) return entry.first;
        return INVALID_ENTITY_ID;
    }

    void SetDriver(const Engine& engine, EntityId id)
    {
        if (id == driven) return;
        const auto pools = engine.Pools();
        const NativeCall native(engine);
        std::uint32_t result = 0;
        const auto previous = HandleOf(driven);
        if (pools.ResolveVehicle(previous))
        {
            native.Invoke(0x295C4C52, result, previous, 1u);
            replicas.at(driven).poseAt = 0;
        }
        const auto next = HandleOf(id);
        if (pools.ResolveVehicle(next)) native.Invoke(0x295C4C52, result, next, 0u);
        driven = id;
    }

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
            else if (entry.first != driven)
            {
                if (replica.revision != state.revision)
                {
                    replica.poseUntil = now + 65;
                    ApplyState(native, replica, state);
                }
                if (!replica.poseAt || replica.poseAt < replica.poseUntil) ApplyPose(native, replica, state, now);
            }
            if (replica.handle && replica.hornRevision != state.hornRevision)
            {
                replica.hornRevision = state.hornRevision;
                const auto elapsed = now >= state.hornAt ? now - state.hornAt : 0;
                if (elapsed < state.hornDuration)
                {
                    std::uint32_t result = 0;
                    native.Invoke(0x024859B5, result, replica.handle, static_cast<unsigned>(state.hornDuration - elapsed));
                }
            }
        }
    }

    void Clear(const Engine& engine)
    {
        driven = INVALID_ENTITY_ID;
        if (replicas.empty()) return;
        const auto pools = engine.Pools();
        const NativeCall native(engine);
        for (auto& entry : replicas) Delete(native, pools, entry.second);
        replicas.clear();
    }
};
}
