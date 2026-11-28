// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CELocalPlayer.h"
#include "CEPlayerSlots.h"
#include "CESession.h"
#include "CERemoteVehicles.h"

namespace ivmp_ce
{
class RemoteActors
{
    struct Replica
    {
        std::uint32_t handle = 0;
        std::uint32_t vehicle = 0;
        unsigned char seat = 0;
        unsigned generation = 0;
        ULONGLONG updatedAt = 0, nextSpawn = 0, settleUntil = 0;
        CVector3 position;
        float heading = 0.0f;
    };

    static constexpr std::uint32_t Niko = 0x6F0783F5;
    std::array<Replica, MAX_PLAYERS> actors{};
    ULONGLONG nextModelRequest = 0;
    bool requestedModel = false;

    static void LeaveSeat(const NativeCall& native, Replica& actor, const CVector3& position)
    {
        std::uint32_t result = 0;
        if (native.Invoke(0x71184DA3, result, actor.handle) && result)
            native.Invoke(0x6A77506A, result, actor.handle, position.fX, position.fY, position.fZ);
        actor.vehicle = 0;
    }

    static void Delete(const NativeCall& native, const EntityPools& pools, Replica& actor)
    {
        if (actor.vehicle && PlayerSlots::Instance().Owns(pools, actor.handle))
            LeaveSeat(native, actor, actor.position + CVector3(2.0f, 0.0f, 0.0f));
        PlayerSlots::Instance().Release(native, pools, actor.handle);
        actor = {};
    }

    bool ModelIsReady(const NativeCall& native, ULONGLONG now)
    {
        std::uint32_t loaded = 0;
        if (!native.Invoke(0x4E61480A, loaded, Niko)) return false; // HAS_MODEL_LOADED
        if (loaded) return true;
        if (now >= nextModelRequest)
        {
            std::uint32_t result = 0;
            requestedModel = native.Invoke(0x502B5185, result, Niko) || requestedModel; // REQUEST_MODEL
            nextModelRequest = now + 500;
        }
        return false;
    }

    static bool Spawn(const NativeCall& native, const EntityPools& pools, Replica& actor, const Session::Remote& remote)
    {
        const auto& position = remote.onFoot.vecPos;
        actor.handle = PlayerSlots::Instance().Acquire(native, pools, position.fX, position.fY, position.fZ);
        if (!actor.handle) return false;
        actor.generation = remote.generation;
        actor.position = position;
        actor.heading = std::fmod(remote.onFoot.fHeading, 360.0f);
        return true;
    }

    static void SetPose(const NativeCall& native, Replica& actor, const OnFootSyncData& frame, ULONGLONG now)
    {
        const auto delta = frame.vecPos - actor.position;
        const auto elapsed = now - actor.updatedAt;
        const float blend = !actor.updatedAt || elapsed >= 80 || delta.Length() >= 8.0f
            ? 1.0f : static_cast<float>(elapsed) / 80.0f;
        actor.position = actor.position + delta * blend;
        const float target = std::fmod(frame.fHeading, 360.0f);
        float turn = std::fmod(target - actor.heading, 360.0f);
        if (turn > 180.0f) turn -= 360.0f;
        if (turn < -180.0f) turn += 360.0f;
        actor.heading = std::fmod(actor.heading + turn * blend + 360.0f, 360.0f);
        std::uint32_t result = 0;
        native.Invoke(0x57C758F0, result, actor.handle, actor.position.fX,
            actor.position.fY, actor.position.fZ); // SET_CHAR_COORDINATES_NO_OFFSET
        native.Invoke(0x46B5523B, result, actor.handle, actor.heading); // SET_CHAR_HEADING
        if (!actor.updatedAt)
        {
            native.Invoke(0x04CF0105, result, actor.handle, 1u); // SET_CHAR_VISIBLE
            OutputDebugStringW(L"IV:MP CE: remote Niko is visible in a player slot.\n");
        }
        actor.updatedAt = now;
    }

    static void SetSeat(const NativeCall& native, Replica& actor, std::uint32_t car, unsigned char seat,
                        const CVector3& position)
    {
        actor.position = position;
        if (actor.vehicle == car && actor.seat == seat) return;
        std::uint32_t result = 0, occupant = 0;
        if (seat)
        {
            std::uint32_t capacity = 0;
            if (!native.Invoke(0x554014F1, result, car, &capacity) || seat > capacity ||
                !native.Invoke(0x5E756B51, result, car, unsigned(seat - 1), &occupant)) return;
        }
        else
        {
            if (!native.Invoke(0x22457083, result, car, &occupant)) return;
        }
        if (occupant && occupant != actor.handle) return;
        native.Invoke(0x20266A86, result, actor.handle, 0u); // FREEZE_CHAR_POSITION
        const bool warped = seat ? native.Invoke(0x172376FE, result, actor.handle, car, unsigned(seat - 1))
            : native.Invoke(0x73D3504A, result, actor.handle, car);
        std::uint32_t actualCar = 0;
        if (!warped || !native.Invoke(0x71184DA3, result, actor.handle) || !result ||
            !native.Invoke(0x1B067237, result, actor.handle, &actualCar) || actualCar != car)
        {
            native.Invoke(0x20266A86, result, actor.handle, 1u);
            return;
        }
        native.Invoke(0x04CF0105, result, actor.handle, 1u); // SET_CHAR_VISIBLE
        actor.vehicle = car;
        actor.seat = seat;
    }

public:
    void Tick(const Engine& engine, const Session& session, const RemoteVehicles& vehicles,
              const LocalPlayer& player, ULONGLONG now)
    {
        const auto pools = engine.Pools();
        if (!pools || !*pools.peds) return;
        const NativeCall native(engine);
        const CVector3 local(player.x, player.y, player.z);
        for (EntityId id = 0; id < MAX_PLAYERS; ++id)
        {
            auto& actor = actors[id];
            auto remote = *session.GetRemote(id);
            const auto car = vehicles.HandleOf(remote.vehicle);
            if (car)
            {
                const auto& entries = session.Vehicles().Entries();
                const auto found = entries.find(remote.vehicle);
                if (found != entries.end())
                {
                    remote.onFoot.vecPos = found->second.position;
                    remote.onFoot.fHeading = found->second.rotation.fZ;
                    remote.hasPosition = true;
                }
            }
            if (!remote.present || !remote.hasPosition ||
                (now >= remote.receivedAt && now - remote.receivedAt > 3000) ||
                (remote.onFoot.vecPos - local).Length() > 160.0f)
            {
                if (actor.handle) Delete(native, pools, actor);
                continue;
            }
            if (actor.handle && actor.generation != remote.generation) Delete(native, pools, actor);
            if (!PlayerSlots::Instance().Owns(pools, actor.handle))
            {
                if (actor.handle) Delete(native, pools, actor);
                actor.handle = 0;
                if (now < actor.nextSpawn || !ModelIsReady(native, now)) continue;
                actor.nextSpawn = now + 500;
                if (!Spawn(native, pools, actor, remote)) continue;
                actor.updatedAt = 0;
                actor.settleUntil = now + 100;
            }
            if (now < actor.settleUntil) continue;
            if (car)
                SetSeat(native, actor, car, remote.seat, remote.onFoot.vecPos);
            else
            {
                if (actor.vehicle)
                {
                    std::uint32_t result = 0;
                    LeaveSeat(native, actor, remote.onFoot.vecPos);
                    native.Invoke(0x20266A86, result, actor.handle, 1u);
                    actor.updatedAt = 0;
                }
                SetPose(native, actor, remote.onFoot, now);
            }
        }
    }

    void Clear(const Engine& engine)
    {
        const auto pools = engine.Pools();
        const NativeCall native(engine);
        for (auto& actor : actors)
            if (actor.handle) Delete(native, pools, actor);
        std::uint32_t result = 0;
        if (requestedModel)
            native.Invoke(0x0FA0E33, result, Niko); // MARK_MODEL_AS_NO_LONGER_NEEDED
        requestedModel = false;
        nextModelRequest = 0;
    }
};
}
