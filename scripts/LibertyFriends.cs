using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using GTA;

public sealed class LibertyFriends : Script
{
    private sealed class Peer
    {
        public Vector3 Position;
        public float Heading;
        public int PedModel, CarModel, CarHandle;
        public string Ride = "-";
        public string CarId;
        public string World = "-";
    }

    private readonly Dictionary<string, Blip> markers = new Dictionary<string, Blip>();
    private readonly Dictionary<string, Vector3> positions = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, Ped> avatars = new Dictionary<string, Ped>();
    private readonly Dictionary<string, Vehicle> cars = new Dictionary<string, Vehicle>();
    private readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly string directory;
    private bool active;
    private bool experimentalEntities;
    private bool experimentalWorld;
    private bool reportedPeers;
    private string lastSnapshot;
    private long lastUpdate;
    private long nextSample;
    private long lastFrame;
    private string ownId = "";
    private string ride = "-";
    private Vehicle drivingCar;
    private int drivingToken;
    private long nextWorldSample;
    private long nextAvatarSpawn;
    private string worldSample = "-";
    private readonly Dictionary<string, Ped> worldPeds = new Dictionary<string, Ped>();
    private readonly Dictionary<string, Vehicle> worldCars = new Dictionary<string, Vehicle>();

    public LibertyFriends()
    {
        string game = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        directory = Path.Combine(game, "mods", "LibertyFriends");
        Interval = 0;
        KeyDown += Toggle;
        Tick += Update;
    }

    private void Toggle(object sender, GTA.KeyEventArgs e)
    {
        if (active && e.Key == Keys.F11)
        {
            experimentalEntities = !experimentalEntities;
            Trace(experimentalEntities ? "experimental entities enabled" : "experimental entities disabled");
            if (!experimentalEntities)
            {
                experimentalWorld = false;
                ClearMarkers();
            }
            lastSnapshot = null;
            Game.DisplayText(experimentalEntities ? "Liberty Friends: experimental 3D" : "Liberty Friends: map only");
            return;
        }
        if (active && experimentalEntities && e.Key == Keys.F8)
        {
            experimentalWorld = !experimentalWorld;
            if (!experimentalWorld) UpdateWorld(null);
            Trace(experimentalWorld ? "experimental world enabled" : "experimental world disabled");
            Game.DisplayText(experimentalWorld ? "Liberty Friends: world ON" : "Liberty Friends: world OFF");
            return;
        }
        if (active && experimentalEntities && e.Key == Keys.F10)
        {
            ToggleRide();
            return;
        }
        if (e.Key != Keys.F9) return;
        if (!active && (!Directory.Exists(directory) || !Player.CanControlCharacter)) return;
        active = !active;
        if (!active)
        {
            ClearMarkers();
            experimentalEntities = false;
            experimentalWorld = false;
            reportedPeers = false;
        }
        Trace(active ? "session enabled (map only)" : "session disabled");
        Game.DisplayText(active ? "Liberty Friends: ON" : "Liberty Friends: OFF");
    }

    private void Update(object sender, EventArgs e)
    {
        if (!active) return;
        long now = clock.ElapsedMilliseconds;
        float step = Math.Min(1f, (now - lastFrame) / 80f);
        lastFrame = now;
        Ped character = Player.Character;
        if (now < nextSample)
        {
            MoveAvatars(step);
            return;
        }
        nextSample = now + 50;
        try
        {
            if (Exists(character))
            {
                Vector3 p = character.Position;
                Vehicle car = experimentalEntities ? character.CurrentVehicle : null;
                bool driving = Exists(car) && Equals(car.GetPedOnSeat(VehicleSeat.Driver), character);
                if (driving && !Equals(drivingCar, car))
                {
                    drivingCar = car;
                    drivingToken++;
                }
                if (!driving) drivingCar = null;
                if (driving) p = car.Position;
                Vehicle passengerCar;
                if (ride != "-" && (!cars.TryGetValue(ride, out passengerCar) ||
                    !Exists(passengerCar) || !Equals(car, passengerCar))) ride = "-";
                string identity = Path.Combine(directory, "player.txt");
                if (File.Exists(identity)) ownId = File.ReadAllText(identity).Trim();
                if (now >= nextWorldSample)
                {
                    nextWorldSample = now + 200;
                    bool publishWorld = experimentalWorld && Leader() == ownId &&
                        ownId.Length == 16 && peers.Count > 0;
                    worldSample = publishWorld ? CaptureWorld(character, car) : "-";
                }
                string position = string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:F2} {2:F2} {3:F2} {4:F2} {5} {6} {7} {8} {9}\n", now, p.X, p.Y, p.Z,
                    ((driving ? car.Heading : character.Heading) % 360 + 360) % 360,
                    character.Model.Hash, driving ? car.Model.Hash : 0, driving ? drivingToken : 0, ride, worldSample);
                File.WriteAllText(Path.Combine(directory, "position.txt"), position);
            }

            string path = Path.Combine(directory, "peers.txt");
            if (File.Exists(path))
            {
                string snapshot = File.ReadAllText(path);
                if (snapshot != lastSnapshot && snapshot.EndsWith("\n", StringComparison.Ordinal))
                {
                    lastSnapshot = snapshot;
                    lastUpdate = now;
                    UpdateMarkers(snapshot, character);
                }
            }
        }
        catch (IOException)
        {
            // The network client may be replacing the snapshot during this tick.
        }
        if (now - lastUpdate > 2500) ClearMarkers();
        MoveAvatars(step);
    }

    private void UpdateMarkers(string snapshot, Ped character)
    {
        peers.Clear();
        var seen = new HashSet<string>();
        string[] lines = snapshot.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string[] columns = lines[i].Split(' ');
            float x, y, z;
            if ((columns.Length != 4 && columns.Length != 9 && columns.Length != 10) ||
                !TryCoordinate(columns[1], out x) ||
                !TryCoordinate(columns[2], out y) ||
                !TryCoordinate(columns[3], out z)) continue;
            seen.Add(columns[0]);
            var position = new Vector3(x, y, z);
            var peer = new Peer { Position = position };
            if (columns.Length >= 9 &&
                TryCoordinate(columns[4], out peer.Heading) &&
                int.TryParse(columns[5], out peer.PedModel) &&
                int.TryParse(columns[6], out peer.CarModel) &&
                int.TryParse(columns[7], out peer.CarHandle))
            {
                peer.Ride = columns[8];
                if (columns.Length == 10) peer.World = columns[9];
                if (peer.CarModel != 0)
                    peer.CarId = columns[0] + ":" + peer.CarHandle.ToString(CultureInfo.InvariantCulture);
            }
            peers[columns[0]] = peer;
            if (experimentalEntities) UpdateAvatar(columns[0], position, character, peer.PedModel);
            Blip marker;
            if (markers.TryGetValue(columns[0], out marker) && Exists(marker))
            {
                Vector3 previous = positions[columns[0]];
                if (previous.X == x && previous.Y == y && previous.Z == z) continue;
                marker.Delete();
            }
            marker = Blip.AddBlip(position);
            if (!Exists(marker)) continue;
            marker.Color = BlipColor.Green;
            markers[columns[0]] = marker;
            positions[columns[0]] = position;
        }

        foreach (string id in new List<string>(markers.Keys))
        {
            if (seen.Contains(id)) continue;
            if (Exists(markers[id])) markers[id].Delete();
            markers.Remove(id);
            positions.Remove(id);
        }
        foreach (string id in new List<string>(avatars.Keys))
        {
            if (!seen.Contains(id))
            {
                if (Exists(avatars[id])) avatars[id].Delete();
                avatars.Remove(id);
            }
        }
        if (!reportedPeers && seen.Count > 0)
        {
            reportedPeers = true;
            Trace("first peer snapshot received");
        }
        if (experimentalEntities)
        {
            UpdateCars(character);
            if (experimentalWorld) UpdateWorld(character);
        }
    }

    private void Trace(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(directory, "client.log"),
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
        }
        catch (IOException)
        {
            // A diagnostic write should not interrupt the game.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Leader()
    {
        string leader = ownId;
        foreach (string id in peers.Keys)
            if (string.CompareOrdinal(id, leader) < 0) leader = id;
        return leader;
    }

    private string CaptureWorld(Ped local, Vehicle localCar)
    {
        var rows = new List<string>(4);
        Vector3 origin = local.Position;
        foreach (Ped ped in World.GetPeds(origin, 40f, 20))
        {
            if (!Exists(ped) || Equals(ped, local) || !ped.isAlive ||
                ped.isRequiredForMission || Exists(ped.CurrentVehicle) ||
                avatars.ContainsValue(ped) || worldPeds.ContainsValue(ped)) continue;
            rows.Add(WorldRow("p", ped.GetHashCode(), ped.Model.Hash, ped.Position, ped.Heading));
            if (rows.Count == 2) break;
        }
        int vehicles = 0;
        foreach (Vehicle car in World.GetVehicles(origin, 40f))
        {
            if (!Exists(car) || Equals(car, localCar) || car.isRequiredForMission ||
                !car.isAlive || cars.ContainsValue(car) || worldCars.ContainsValue(car)) continue;
            rows.Add(WorldRow("v", car.GetHashCode(), car.Model.Hash, car.Position, car.Heading));
            if (++vehicles == 2) break;
        }
        return rows.Count == 0 ? "-" : string.Join(";", rows.ToArray());
    }

    private static string WorldRow(string kind, int id, int model, Vector3 p, float heading)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6}", kind, id, model,
            (int)(p.X * 10), (int)(p.Y * 10), (int)(p.Z * 10), (int)heading);
    }

    private void UpdateWorld(Ped character)
    {
        var seen = new HashSet<string>();
        string leader = Leader();
        Peer owner;
        if (leader != ownId && peers.TryGetValue(leader, out owner) && Exists(character))
        {
            foreach (string row in owner.World.Split(';'))
            {
                string[] fields = row.Split(',');
                int id, model, x, y, z, heading;
                if (fields.Length != 7 || !int.TryParse(fields[1], out id) ||
                    !int.TryParse(fields[2], out model) || !int.TryParse(fields[3], out x) ||
                    !int.TryParse(fields[4], out y) || !int.TryParse(fields[5], out z) ||
                    !int.TryParse(fields[6], out heading)) continue;
                Vector3 position = new Vector3(x / 10f, y / 10f, z / 10f);
                if (SquaredDistance(character.Position, position) > 3600f) continue;
                string key = leader + ":" + fields[0] + ":" + id + ":" + model;
                if (!seen.Add(key)) continue;
                Model entityModel = new Model(model);
                if (fields[0] == "p" && entityModel.isPed)
                {
                    Ped ped;
                    if (!worldPeds.TryGetValue(key, out ped) || !Exists(ped))
                    {
                        Trace("creating world pedestrian " + model);
                        ped = World.CreatePed(entityModel, position);
                        if (!Exists(ped)) continue;
                        ped.Invincible = true;
                        ped.BlockPermanentEvents = true;
                        ped.FreezePosition = true;
                        worldPeds[key] = ped;
                    }
                    ped.Position = position;
                    ped.Heading = heading;
                }
                else if (fields[0] == "v" && entityModel.isVehicle)
                {
                    Vehicle car;
                    if (!worldCars.TryGetValue(key, out car) || !Exists(car))
                    {
                        Trace("creating world vehicle " + model);
                        car = World.CreateVehicle(entityModel, position);
                        if (!Exists(car)) continue;
                        car.FreezePosition = true;
                        car.DoorLock = DoorLock.ImpossibleToOpen;
                        worldCars[key] = car;
                    }
                    car.Position = position;
                    car.Heading = heading;
                }
            }
        }
        foreach (string key in new List<string>(worldPeds.Keys))
            if (!seen.Contains(key))
            {
                if (Exists(worldPeds[key])) worldPeds[key].Delete();
                worldPeds.Remove(key);
            }
        foreach (string key in new List<string>(worldCars.Keys))
            if (!seen.Contains(key))
            {
                if (Exists(worldCars[key])) worldCars[key].Delete();
                worldCars.Remove(key);
            }
    }

    private void UpdateAvatar(string id, Vector3 position, Ped character, int modelHash)
    {
        Ped avatar;
        avatars.TryGetValue(id, out avatar);
        bool nearby = Exists(character) && SquaredDistance(character.Position, position) < 10000f;
        if (!nearby)
        {
            if (Exists(avatar)) avatar.Delete();
            avatars.Remove(id);
            return;
        }
        if (Exists(avatar) || clock.ElapsedMilliseconds < nextAvatarSpawn) return;
        nextAvatarSpawn = clock.ElapsedMilliseconds + 500;
        // The player model is special and the scripted CreateChar path crashed on CE.
        Trace("creating remote player with ambient native; advertised model=" + modelHash);
        avatar = World.CreatePed(position, Gender.Male);
        if (!Exists(avatar))
        {
            Trace("ambient native did not return a remote player");
            return;
        }
        Trace("remote player created");
        avatar.BlockPermanentEvents = true;
        avatar.Invincible = true;
        avatar.FreezePosition = true;
        avatars[id] = avatar;
        Trace("remote player configured");
    }

    private void MoveAvatars(float step)
    {
        foreach (Peer peer in peers.Values)
        {
            Vehicle car;
            if (peer.CarId == null || !cars.TryGetValue(peer.CarId, out car) || !Exists(car)) continue;
            car.Position = Blend(car.Position, peer.Position, step);
            var delta = (peer.Heading - car.Heading + 540f) % 360f - 180f;
            car.Heading = (car.Heading + delta * step + 360f) % 360f;
        }

        foreach (var entry in avatars)
        {
            Ped avatar = entry.Value;
            Peer peer;
            if (!Exists(avatar) || !peers.TryGetValue(entry.Key, out peer)) continue;
            Vehicle car;
            VehicleSeat seat = peer.CarId != null ? VehicleSeat.Driver : VehicleSeat.RightFront;
            if (FindCar(peer.CarId ?? peer.Ride, out car) &&
                (seat == VehicleSeat.Driver || WinningPassenger(peer.Ride) == entry.Key))
            {
                if (Equals(car.GetPedOnSeat(seat), avatar)) continue;
                if (car.isSeatFree(seat))
                {
                    avatar.FreezePosition = false;
                    avatar.WarpIntoVehicle(car, seat);
                    continue;
                }
            }
            if (Exists(avatar.CurrentVehicle))
            {
                avatar.LeaveVehicle();
                continue;
            }
            avatar.FreezePosition = true;
            avatar.Position = Blend(avatar.Position, peer.Position, step);
            avatar.Heading = peer.Heading;
        }
    }

    private void UpdateCars(Ped character)
    {
        var seen = new HashSet<string>();
        foreach (Peer peer in peers.Values)
        {
            if (peer.CarId == null || !Exists(character) ||
                (ride != peer.CarId && SquaredDistance(character.Position, peer.Position) > 10000f)) continue;
            seen.Add(peer.CarId);
            Vehicle car;
            if (cars.TryGetValue(peer.CarId, out car) && Exists(car)) continue;
            Model model = new Model(peer.CarModel);
            if (!model.isVehicle) continue;
            Trace("creating remote player vehicle " + model.Hash);
            car = World.CreateVehicle(model, peer.Position);
            if (!Exists(car)) continue;
            car.Heading = peer.Heading;
            car.FreezePosition = true;
            cars[peer.CarId] = car;
        }
        foreach (string id in new List<string>(cars.Keys))
        {
            if (!seen.Contains(id)) RemoveCar(id);
        }
        if (ride != "-" && WinningPassenger(ride) != ownId && Exists(character))
        {
            character.LeaveVehicle();
            ride = "-";
        }
    }

    private string WinningPassenger(string carId)
    {
        string winner = ride == carId && ownId.Length == 16 ? ownId : null;
        foreach (var entry in peers)
            if (entry.Value.Ride == carId && (winner == null || string.CompareOrdinal(entry.Key, winner) < 0))
                winner = entry.Key;
        return winner;
    }

    private bool FindCar(string id, out Vehicle car)
    {
        if (cars.TryGetValue(id, out car) && Exists(car)) return true;
        Ped character = Player.Character;
        car = Exists(character) ? character.CurrentVehicle : null;
        return ownId.Length == 16 && Exists(car) &&
            Equals(car.GetPedOnSeat(VehicleSeat.Driver), character) &&
            id == ownId + ":" + drivingToken.ToString(CultureInfo.InvariantCulture);
    }

    private void ToggleRide()
    {
        Ped character = Player.Character;
        if (!Exists(character) || !Player.CanControlCharacter) return;
        if (ride != "-")
        {
            character.LeaveVehicle();
            ride = "-";
            return;
        }
        if (Exists(character.CurrentVehicle) || ownId.Length != 16) return;
        string nearest = null;
        float distance = 64f;
        foreach (var entry in cars)
        {
            if (!Exists(entry.Value) || WinningPassenger(entry.Key) != null ||
                !entry.Value.isSeatFree(VehicleSeat.RightFront)) continue;
            float candidate = SquaredDistance(character.Position, entry.Value.Position);
            if (candidate >= distance) continue;
            distance = candidate;
            nearest = entry.Key;
        }
        if (nearest == null) return;
        character.WarpIntoVehicle(cars[nearest], VehicleSeat.RightFront);
        ride = nearest;
    }

    private void RemoveCar(string id)
    {
        Vehicle car = cars[id];
        if (ride == id)
        {
            Ped character = Player.Character;
            if (Exists(character) && Equals(character.CurrentVehicle, car)) character.LeaveVehicle();
            ride = "-";
        }
        if (Exists(car)) car.Delete();
        cars.Remove(id);
    }

    private static Vector3 Blend(Vector3 current, Vector3 target, float step)
    {
        if (SquaredDistance(current, target) > 225f) return target;
        return new Vector3(current.X + (target.X - current.X) * step,
            current.Y + (target.Y - current.Y) * step, current.Z + (target.Z - current.Z) * step);
    }

    private static float SquaredDistance(Vector3 a, Vector3 b)
    {
        float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return x * x + y * y + z * z;
    }

    private static bool TryCoordinate(string value, out float coordinate)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate)
            && coordinate >= -20000 && coordinate <= 20000;
    }

    private void ClearMarkers()
    {
        foreach (Blip marker in markers.Values)
            if (Exists(marker)) marker.Delete();
        markers.Clear();
        positions.Clear();
        foreach (Ped avatar in avatars.Values)
            if (Exists(avatar)) avatar.Delete();
        avatars.Clear();
        foreach (string id in new List<string>(cars.Keys)) RemoveCar(id);
        peers.Clear();
        UpdateWorld(null);
        worldSample = "-";
    }
}
