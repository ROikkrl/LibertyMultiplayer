local positionPath = MOD_DIR .. "/position.txt"
local peersPath = MOD_DIR .. "/peers.txt"
local markers = {}
local lastSnapshot
local lastSnapshotAt = 0

local function clearMarkers()
    for id, marker in pairs(markers) do
        RemoveBlip(marker.handle)
        markers[id] = nil
    end
end

local function updateMarkers(snapshot)
    local seen = {}
    for id, x, y, z in snapshot:gmatch("\n(%w+) ([^%s]+) ([^%s]+) ([^%s]+)") do
        x, y, z = tonumber(x), tonumber(y), tonumber(z)
        if x and y and z then
            seen[id] = true
            local marker = markers[id]
            if not marker or marker.x ~= x or marker.y ~= y or marker.z ~= z then
                if marker then RemoveBlip(marker.handle) end
                local handle = AddBlipForCoord(x, y, z)
                if handle and handle ~= 0 then
                    ChangeBlipColour(handle, 2)
                    markers[id] = { handle = handle, x = x, y = y, z = z }
                else
                    markers[id] = nil
                end
            end
        end
    end
    for id, marker in pairs(markers) do
        if not seen[id] then
            RemoveBlip(marker.handle)
            markers[id] = nil
        end
    end
end

Thread.Create(function()
    while true do
        local now = GetGameTimer()
        local ped = GetPlayerChar(GetPlayerId())
        if ped and ped ~= 0 and DoesCharExist(ped) then
            local x, y, z = GetCharCoordinates(ped)
            local file = io.open(positionPath, "w")
            if file then
                file:write(string.format("%d %.2f %.2f %.2f\n", now, x, y, z))
                file:close()
            end
        end

        local file = io.open(peersPath, "r")
        local snapshot
        if file then
            snapshot = file:read("*a")
            file:close()
        end
        if snapshot and snapshot:match("^%d+\n") and snapshot ~= lastSnapshot then
            lastSnapshot = snapshot
            lastSnapshotAt = now
            updateMarkers(snapshot)
        elseif now - lastSnapshotAt > 2500 then
            clearMarkers()
        end
        Thread.Wait(200)
    end
end)

function onUnload()
    clearMarkers()
end
