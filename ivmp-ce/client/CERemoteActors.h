// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CELocalPlayer.h"
#include "CESession.h"

namespace ivmp_ce
{
class RemoteActors
{
    struct Replica
    {
        std::uint32_t handle = 0;
        unsigned generation = 0;
        ULONGLONG updatedAt = 0, nextSpawn = 0;
        CVector3 position;
        float heading = 0.0f;
    };

    static constexpr std::uint32_t Niko = 0x6F0783F5;
    std::array<Replica, MAX_PLAYERS> actors{};
    ULONGLONG nextModelRequest = 0;
    bool requestedModel = false;

    static void Delete(const NativeCall& native, const EntityPools& pools, Replica& actor)
    {
        std::uint32_t result = 0;
        if (actor.handle && pools.ResolvePed(actor.handle))
            native.Invoke(0x0E3B49BF, result, &actor.handle); // DELETE_CHAR
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
        std::uint32_t result = 0;
        const auto& position = remote.onFoot.vecPos;
        if (!native.Invoke(0x4A673763, result, 1u, Niko, position.fX, position.fY, position.fZ,
            &actor.handle, 1u) || !pools.ResolvePed(actor.handle)) // CREATE_CHAR
        {
            actor.handle = 0;
            return false;
        }
        actor.generation = remote.generation;
        actor.position = position;
        actor.heading = std::fmod(remote.onFoot.fHeading, 360.0f);
        native.Invoke(0x76247429, result, actor.handle, 1u); // SET_BLOCKING_OF_NON_TEMPORARY_EVENTS
        native.Invoke(0x20266A86, result, actor.handle, 1u); // FREEZE_CHAR_POSITION
        OutputDebugStringW(L"IV:MP CE: created a remote Niko in GTA IV.\n");
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
        actor.updatedAt = now;
        std::uint32_t result = 0;
        native.Invoke(0x57C758F0, result, actor.handle, actor.position.fX,
            actor.position.fY, actor.position.fZ); // SET_CHAR_COORDINATES_NO_OFFSET
        native.Invoke(0x46B5523B, result, actor.handle, actor.heading); // SET_CHAR_HEADING
    }

public:
    void Tick(const Engine& engine, const Session& session, const LocalPlayer& player, ULONGLONG now)
    {
        const auto pools = engine.Pools();
        if (!pools || !*pools.peds) return;
        const NativeCall native(engine);
        const CVector3 local(player.x, player.y, player.z);
        for (EntityId id = 0; id < MAX_PLAYERS; ++id)
        {
            auto& actor = actors[id];
            const auto remote = session.GetRemote(id);
            if (!remote->present || !remote->hasPosition ||
                (now >= remote->receivedAt && now - remote->receivedAt > 3000) ||
                (remote->onFoot.vecPos - local).Length() > 160.0f)
            {
                if (actor.handle) Delete(native, pools, actor);
                continue;
            }
            if (actor.handle && actor.generation != remote->generation) Delete(native, pools, actor);
            if (!actor.handle || !pools.ResolvePed(actor.handle))
            {
                actor.handle = 0;
                if (now < actor.nextSpawn || !ModelIsReady(native, now)) continue;
                actor.nextSpawn = now + 500;
                if (!Spawn(native, pools, actor, *remote)) continue;
                actor.updatedAt = 0;
            }
            SetPose(native, actor, remote->onFoot, now);
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
