// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CENativeCall.h"
#include <cmath>

namespace ivmp_ce
{
struct LocalPlayer
{
    std::uint32_t id = 0;
    std::uint32_t ped = 0;
    std::uint32_t episode = 0;
    std::uint32_t vehicle = 0;
    float x = 0.0f, y = 0.0f, z = 0.0f;
};

struct OnFootMotion
{
    float heading = 0.0f, vx = 0.0f, vy = 0.0f, vz = 0.0f;
    std::uint32_t health = 0, armour = 0;
    bool ducking = false;
};

class LocalPlayerReader : private NativeCall
{
public:
    explicit LocalPlayerReader(Engine resolved) : NativeCall(resolved) {}

    bool Read(LocalPlayer& snapshot) const
    {
        LocalPlayer state;
        std::uint32_t result = 0;
        if (!Invoke(0x62E319C6, state.id) || state.id >= 32 ||                       // GET_PLAYER_ID
            !Invoke(0x08274BA4, result, state.id) || result == 0 ||                  // IS_PLAYER_PLAYING
            !Invoke(0x511454A9, result, state.id, &state.ped) || state.ped == 0 ||    // GET_PLAYER_CHAR
            !Invoke(0x46531797, result, state.ped) || result == 0 ||                 // DOES_CHAR_EXIST
            !Invoke(0x2B5C06E6, result, state.ped, &state.x, &state.y, &state.z) ||  // GET_CHAR_COORDINATES
            !Invoke(0x7D7619D2, state.episode) || state.episode > 2 ||               // GET_CURRENT_EPISODE
            !std::isfinite(state.x) || !std::isfinite(state.y) || !std::isfinite(state.z)) return false;
        if (Invoke(0x71184DA3, result, state.ped) && result)                       // IS_CHAR_IN_ANY_CAR
            Invoke(0x1B067237, result, state.ped, &state.vehicle);                  // GET_CAR_CHAR_IS_USING
        snapshot = state;
        return true;
    }

    bool ReadOnFoot(const LocalPlayer& player, OnFootMotion& snapshot) const
    {
        if (!player.ped || player.vehicle) return false;
        OnFootMotion state;
        std::uint32_t result = 0;
        if (!Invoke(0x057A3AC7, result, player.ped, &state.heading) ||           // GET_CHAR_HEADING
            !Invoke(0x3B977FD4, result, player.ped, &state.vx, &state.vy, &state.vz) || // GET_CHAR_VELOCITY
            !Invoke(0x4B6C2256, result, player.ped, &state.health) ||             // GET_CHAR_HEALTH
            !Invoke(0x3C756E54, result, player.ped, &state.armour) ||             // GET_CHAR_ARMOUR
            !Invoke(0x495D6021, result, player.ped) ||                           // IS_CHAR_DUCKING
            !std::isfinite(state.heading) || !std::isfinite(state.vx) ||
            !std::isfinite(state.vy) || !std::isfinite(state.vz)) return false;
        state.ducking = result != 0;
        snapshot = state;
        return true;
    }
};
}
