// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "CERemoteVehicles.h"

namespace ivmp_ce
{
class VehicleControl
{
    EntityId current = INVALID_ENTITY_ID;
    unsigned char seat = 0;
    ULONGLONG deadline = 0;
    bool confirmed = false;
    Session::EntryReply pendingCommand;
    ULONGLONG commandDeadline = 0;

    static int SeatOf(const NativeCall& native, const LocalPlayer& player)
    {
        std::uint32_t result = 0, ped = 0, capacity = 0;
        if (native.Invoke(0x22457083, result, player.vehicle, &ped) && ped == player.ped) return 0;
        if (!native.Invoke(0x554014F1, result, player.vehicle, &capacity)) return -1;
        for (unsigned index = 0; index < capacity && index < MAX_VEHICLE_PASSENGERS; ++index)
        {
            ped = 0;
            if (native.Invoke(0x5E756B51, result, player.vehicle, index, &ped) && ped == player.ped)
                return static_cast<int>(index + 1);
        }
        return -1;
    }

    static void ReadDriver(const Engine& engine, const NativeCall& native, const LocalPlayer& player,
                           const VehicleState& baseline, InVehicleSyncData& frame)
    {
        const auto car = player.vehicle;
        std::uint32_t result = 0, first = 0, second = 0;
        frame.vecPos = baseline.position;
        frame.vecRotation = baseline.rotation;
        frame.uiHealth = baseline.health;
        frame.fPetrolHealth = baseline.petrol;
        frame.fDirtLevel = baseline.dirt;
        frame.bLights = baseline.lights;
        frame.bEngineStatus = true;
        for (unsigned i = 0; i != 4; ++i) frame.byteColors[i] = baseline.colours[i];
        native.Invoke(0x2D432EAB, result, car, &frame.vecPos.fX, &frame.vecPos.fY, &frame.vecPos.fZ);
        native.Invoke(0x46803CFA, result, car, &frame.vecRotation.fZ);
        native.Invoke(0x4D417CD3, result, car, &frame.uiHealth);
        if (native.Invoke(0x2C835642, result, car))
            std::memcpy(&frame.fPetrolHealth, &result, sizeof(result));
        native.Invoke(0x571152F5, result, car, &frame.fDirtLevel);
        if (native.Invoke(0x129A1569, result, car)) frame.bSirenState = result != 0;
        if (native.Invoke(0x6CAC3D62, result, car, &first, &second))
        {
            frame.byteColors[0] = static_cast<unsigned char>(first);
            frame.byteColors[1] = static_cast<unsigned char>(second);
        }
        if (native.Invoke(0x25B87BCA, result, car, &first, &second))
        {
            frame.byteColors[2] = static_cast<unsigned char>(first);
            frame.byteColors[3] = static_cast<unsigned char>(second);
        }
        const auto object = engine.Pools().ResolveVehicle(car);
        engine.ReadSpeed(object, Binding::GetMoveSpeed, frame.vecMoveSpeed.fX, frame.vecMoveSpeed.fY, frame.vecMoveSpeed.fZ);
        engine.ReadSpeed(object, Binding::GetTurnSpeed, frame.vecTurnSpeed.fX, frame.vecTurnSpeed.fY, frame.vecTurnSpeed.fZ);
    }

public:
    void EnterNearestPassengerSeat(const Engine& engine, const LocalPlayer& player, const Session& session,
                                   const RemoteVehicles& vehicles)
    {
        if (player.vehicle) return;
        const NativeCall native(engine);
        std::uint32_t result = 0;
        if (!native.Invoke(0x5C8C2E39, result, player.ped) || result) return; // IS_CHAR_GETTING_IN_TO_A_CAR
        const auto pools = engine.Pools();
        const CVector3 local(player.x, player.y, player.z);
        float nearest = 6.0f;
        std::uint32_t selectedCar = 0, selectedSeat = 0;
        for (const auto& entry : session.Vehicles().Entries())
        {
            auto distance = (entry.second.position - local).Length();
            if (distance >= nearest) continue;
            const auto car = vehicles.HandleOf(entry.first);
            std::uint32_t capacity = 0;
            if (!pools.ResolveVehicle(car) || !native.Invoke(0x554014F1, result, car, &capacity)) continue;
            for (unsigned index = 0; index < capacity && index < MAX_VEHICLE_PASSENGERS; ++index)
            {
                if (!native.Invoke(0x1BDA0DA5, result, car, index) || !result) continue;
                nearest = distance;
                selectedCar = car;
                selectedSeat = index;
                break;
            }
        }
        if (selectedCar)
            native.Invoke(0x0A2C70AF, result, player.ped, selectedCar, 4000u, selectedSeat);
    }

    void ApplyCommand(const Engine& engine, const LocalPlayer& player, Session& session,
                      RemoteVehicles& vehicles, ULONGLONG now)
    {
        const auto command = session.TakeSeatCommand();
        if (command.pending)
        {
            pendingCommand = command;
            commandDeadline = now + 3000;
        }
        if (!pendingCommand.pending) return;
        if (now >= commandDeadline)
        {
            pendingCommand = {};
            return;
        }
        const NativeCall native(engine);
        std::uint32_t result = 0;
        const auto id = pendingCommand.vehicle;
        if (id == INVALID_ENTITY_ID)
        {
            if (player.vehicle && pendingCommand.accepted)
                native.Invoke(0x6B85214E, result, player.ped, player.vehicle); // TASK_LEAVE_CAR
            else if (player.vehicle)
                native.Invoke(0x6A77506A, result, player.ped, player.x + 2.0f, player.y, player.z);
            pendingCommand = {};
            return;
        }
        const auto car = vehicles.HandleOf(id);
        if (!engine.Pools().ResolveVehicle(car)) return;
        const auto requestedSeat = pendingCommand.seat;
        pendingCommand = {};
        std::uint32_t occupant = 0;
        if (requestedSeat)
        {
            std::uint32_t capacity = 0;
            if (!native.Invoke(0x554014F1, result, car, &capacity) || requestedSeat > capacity ||
                !native.Invoke(0x5E756B51, result, car, unsigned(requestedSeat - 1), &occupant)) return;
        }
        else if (!native.Invoke(0x22457083, result, car, &occupant)) return;
        if (occupant && occupant != player.ped) return;
        if (id == current && requestedSeat == seat && confirmed) return;
        Reset(engine, session, vehicles);
        if (occupant != player.ped)
        {
            if (player.vehicle)
                native.Invoke(0x6A77506A, result, player.ped, player.x + 2.0f, player.y, player.z);
            const bool warped = requestedSeat
                ? native.Invoke(0x172376FE, result, player.ped, car, unsigned(requestedSeat - 1))
                : native.Invoke(0x73D3504A, result, player.ped, car);
            if (!warped) return;
        }
        LocalPlayer seated = player;
        seated.vehicle = car;
        std::uint32_t actualCar = 0;
        if (!native.Invoke(0x71184DA3, result, player.ped) || !result ||
            !native.Invoke(0x1B067237, result, player.ped, &actualCar) ||
            actualCar != car || SeatOf(native, seated) != requestedSeat ||
            !session.VehicleTransition(VEHICLE_ENTRY_COMPLETE, id, requestedSeat)) return;
        current = id;
        seat = requestedSeat;
        confirmed = true;
        if (!seat) vehicles.SetDriver(engine, id);
        OutputDebugStringW(L"IV:MP CE: applied the server vehicle seat.\n");
    }

    void Clear(const Engine& engine, Session& session, RemoteVehicles& vehicles)
    {
        Reset(engine, session, vehicles);
        pendingCommand = {};
    }

    void Reset(const Engine& engine, Session& session, RemoteVehicles& vehicles)
    {
        if (current != INVALID_ENTITY_ID)
        {
            session.VehicleTransition(confirmed ? VEHICLE_EXIT_COMPLETE : VEHICLE_ENTRY_CANCELLED, current, seat);
            vehicles.SetDriver(engine, INVALID_ENTITY_ID);
        }
        current = INVALID_ENTITY_ID;
        confirmed = false;
    }

    void Tick(const Engine& engine, const LocalPlayer& player, Session& session, RemoteVehicles& vehicles, ULONGLONG now)
    {
        const auto id = vehicles.IdOf(player.vehicle);
        const auto response = session.TakeEntryReply();
        if (id != current) Reset(engine, session, vehicles);
        if (id == INVALID_ENTITY_ID) return;
        const NativeCall native(engine);
        if (current == INVALID_ENTITY_ID)
        {
            const int index = SeatOf(native, player);
            if (index < 0) return;
            current = id;
            seat = static_cast<unsigned char>(index);
            deadline = now + 2500;
            session.VehicleTransition(VEHICLE_ENTRY_REQUEST, id, seat);
            return;
        }
        if (!confirmed)
        {
            if (response.pending && response.accepted && response.vehicle == current && response.seat == seat)
            {
                if (!session.VehicleTransition(VEHICLE_ENTRY_COMPLETE, id, seat)) return;
                confirmed = true;
                if (!seat) vehicles.SetDriver(engine, id);
                OutputDebugStringW(L"IV:MP CE: server confirmed the vehicle seat.\n");
            }
            else if ((response.pending && !response.accepted) || now >= deadline)
            {
                std::uint32_t result = 0;
                native.Invoke(0x6A77506A, result, player.ped, player.x + 2.0f, player.y, player.z);
                Reset(engine, session, vehicles);
                return;
            }
            else return;
        }
        std::uint32_t result = 0, health = 0, armour = 0;
        native.Invoke(0x4B6C2256, result, player.ped, &health);
        native.Invoke(0x3C756E54, result, player.ped, &armour);
        const auto vitals = ((health & 0xffff) << 16) | (armour & 0xffff);
        if (seat)
        {
            PassengerSyncData frame{};
            frame.byteSeatId = seat;
            frame.uPlayerHealthArmour = vitals;
            session.SendVehicle(RPC_PassengerSync, id, frame);
        }
        else
        {
            const auto& entries = session.Vehicles().Entries();
            const auto found = entries.find(id);
            if (found == entries.end()) return;
            InVehicleSyncData frame{};
            ReadDriver(engine, native, player, found->second, frame);
            frame.uPlayerHealthArmour = vitals;
            if (VehicleState::Finite(frame.vecPos) && VehicleState::Finite(frame.vecRotation))
                session.SendDriver(id, frame);
        }
    }
};
}
