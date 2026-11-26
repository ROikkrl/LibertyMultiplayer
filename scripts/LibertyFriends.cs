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
        public CarPhysics Physics;
        public int Radio = -1;
    }

    private sealed class CarPhysics
    {
        public Quaternion Orientation;
        public float EngineHealth, TankHealth;
        public int BodyHealth;
        public int TireMask = -1;
    }

    private readonly Dictionary<string, Blip> markers = new Dictionary<string, Blip>();
    private readonly Dictionary<string, Vector3> positions = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, Ped> avatars = new Dictionary<string, Ped>();
    private readonly Dictionary<string, Vehicle> cars = new Dictionary<string, Vehicle>();
    private readonly Dictionary<string, CarPhysics> appliedDamage = new Dictionary<string, CarPhysics>();
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
    private readonly Dictionary<string, Vector3> worldTargets = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, Quaternion> worldOrientations = new Dictionary<string, Quaternion>();
    private readonly Dictionary<string, float> worldHeadings = new Dictionary<string, float>();
    private readonly Dictionary<string, int> avatarMotion = new Dictionary<string, int>();
    private readonly Dictionary<string, Vector3> previousAvatarTargets = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, long> avatarMotionTimes = new Dictionary<string, long>();
    private static readonly VehicleWheel[] replicatedWheels = {
        VehicleWheel.FrontLeft, VehicleWheel.FrontRight, VehicleWheel.RearLeft, VehicleWheel.RearRight
    };
    private readonly AnimationSet walkAnimation = new AnimationSet("amb@hurry_walk");
    private readonly AnimationSet runAnimation = new AnimationSet("amb@flee_a");
    private string tunedRide = "-";
    private int tunedStation = -1;

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
        nextSample = Math.Max(now + 1, nextSample + 33);
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
                    nextWorldSample = now + 100;
                    bool publishWorld = experimentalWorld && Leader() == ownId &&
                        ownId.Length == 16 && peers.Count > 0;
                    worldSample = publishWorld ? CaptureWorld(character, car) : "-";
                }
                string position = string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:F2} {2:F2} {3:F2} {4:F2} {5} {6} {7} {8} {9}{10}\n", now, p.X, p.Y, p.Z,
                    ((driving ? car.Heading : character.Heading) % 360 + 360) % 360,
                    character.Model.Hash, driving ? car.Model.Hash : 0, driving ? drivingToken : 0, ride, worldSample,
                    driving ? CaptureCarPhysics(car) + " " + CaptureRadio() : "");
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
            if ((columns.Length != 4 && (columns.Length < 9 || columns.Length > 12)) ||
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
                if (columns.Length >= 10) peer.World = columns[9];
                if (peer.CarModel != 0)
                {
                    peer.CarId = columns[0] + ":" + peer.CarHandle.ToString(CultureInfo.InvariantCulture);
                    if (columns.Length >= 11) peer.Physics = ParseCarPhysics(columns[10]);
                    int station;
                    if (columns.Length == 12 && int.TryParse(columns[11], out station) &&
                        station >= 0 && station <= (int)RadioStation.Integrity) peer.Radio = station;
                }
            }
            peers[columns[0]] = peer;
            if (experimentalEntities)
            {
                UpdateAvatar(columns[0], position, character, peer.PedModel);
                UpdateAvatarMotion(columns[0], peer);
            }
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
                avatarMotion.Remove(id);
                previousAvatarTargets.Remove(id);
                avatarMotionTimes.Remove(id);
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
            TunePassengerRadio(character);
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
            Quaternion q = car.RotationQuaternion;
            rows.Add(WorldRow("v", car.GetHashCode(), car.Model.Hash, car.Position, car.Heading) +
                string.Format(CultureInfo.InvariantCulture, ",{0},{1},{2},{3}",
                    (int)Math.Round(q.X * 1000), (int)Math.Round(q.Y * 1000),
                    (int)Math.Round(q.Z * 1000), (int)Math.Round(q.W * 1000)));
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
                if ((fields.Length != 7 && (fields.Length != 11 || fields[0] != "v")) ||
                    !int.TryParse(fields[1], out id) ||
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
                    SetPedPosition(ped, position);
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
                    worldTargets[key] = position;
                    worldHeadings[key] = heading;
                    int qx, qy, qz, qw;
                    if (fields.Length == 11 && int.TryParse(fields[7], out qx) &&
                        int.TryParse(fields[8], out qy) && int.TryParse(fields[9], out qz) &&
                        int.TryParse(fields[10], out qw))
                    {
                        var q = new Quaternion(qx / 1000f, qy / 1000f, qz / 1000f, qw / 1000f);
                        float norm = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
                        if (norm >= 0.5f && norm <= 1.5f)
                            worldOrientations[key] = Quaternion.Normalize(q);
                        else worldOrientations.Remove(key);
                    }
                    else worldOrientations.Remove(key);
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
                worldTargets.Remove(key);
                worldHeadings.Remove(key);
                worldOrientations.Remove(key);
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
        foreach (var entry in worldCars)
        {
            Vehicle car = entry.Value;
            if (!Exists(car)) continue;
            car.Position = Blend(car.Position, worldTargets[entry.Key], step);
            Quaternion orientation;
            if (worldOrientations.TryGetValue(entry.Key, out orientation))
                car.RotationQuaternion = BlendOrientation(car.RotationQuaternion, orientation, step);
            else
                car.Heading = BlendHeading(car.Heading, worldHeadings[entry.Key], step);
        }
        foreach (Peer peer in peers.Values)
        {
            Vehicle car;
            if (peer.CarId == null || !cars.TryGetValue(peer.CarId, out car) || !Exists(car)) continue;
            car.Position = Blend(car.Position, peer.Position, step);
            if (peer.Physics != null)
            {
                car.RotationQuaternion = BlendOrientation(car.RotationQuaternion, peer.Physics.Orientation, step);
            }
            else
            {
                car.Heading = BlendHeading(car.Heading, peer.Heading, step);
            }
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
            Vector3 current = avatar.Position;
            if (SquaredDistance(current, peer.Position) > 0.0001f)
                SetPedPosition(avatar, Blend(current, peer.Position, step));
            avatar.Heading = peer.Heading;
        }
    }

    private static float BlendHeading(float from, float to, float step)
    {
        var delta = (to - from + 540f) % 360f - 180f;
        return (from + delta * step + 360f) % 360f;
    }

    private void UpdateAvatarMotion(string id, Peer peer)
    {
        Ped avatar;
        if (!avatars.TryGetValue(id, out avatar) || !Exists(avatar) ||
            peer.CarId != null || peer.Ride != "-" || Exists(avatar.CurrentVehicle))
        {
            avatarMotion.Remove(id);
            previousAvatarTargets.Remove(id);
            avatarMotionTimes.Remove(id);
            return;
        }
        Vector3 previous;
        long now = clock.ElapsedMilliseconds;
        int motion = 0;
        if (previousAvatarTargets.TryGetValue(id, out previous))
        {
            float dx = previous.X - peer.Position.X, dy = previous.Y - peer.Position.Y;
            float distance = dx * dx + dy * dy;
            long elapsed = now - avatarMotionTimes[id];
            if (distance < 0.0004f && elapsed < 180) return;
            if (distance >= 0.0004f && distance < 9f && elapsed < 300)
            {
                float speed = (float)Math.Sqrt(distance) * 1000 / Math.Max(25, elapsed);
                if (speed > 2.7f) motion = 2;
                else if (speed > 0.3f) motion = 1;
            }
        }
        previousAvatarTargets[id] = peer.Position;
        avatarMotionTimes[id] = now;
        int oldMotion;
        if (avatarMotion.TryGetValue(id, out oldMotion) && oldMotion == motion) return;
        avatarMotion[id] = motion;
        if (motion == 0) avatar.Task.ClearAll();
        else if (motion == 1) avatar.Task.PlayAnimation(walkAnimation, "walk_a", 1f, AnimationFlags.Unknown01);
        else avatar.Task.PlayAnimation(runAnimation, "run_flee_lookaround_b", 1f, AnimationFlags.Unknown01);
    }

    private void TunePassengerRadio(Ped character)
    {
        Vehicle car;
        if (ride != "-" && Exists(character) && cars.TryGetValue(ride, out car) &&
            Exists(car) && Equals(character.CurrentVehicle, car))
        {
            foreach (Peer peer in peers.Values)
            {
                if (peer.CarId != ride || peer.Radio < 0) continue;
                if (ride != tunedRide || peer.Radio != tunedStation)
                {
                    Game.RadioStation = (RadioStation)peer.Radio;
                    tunedRide = ride;
                    tunedStation = peer.Radio;
                }
                return;
            }
        }
        tunedRide = "-";
        tunedStation = -1;
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
            if (!cars.TryGetValue(peer.CarId, out car) || !Exists(car))
            {
                Model model = new Model(peer.CarModel);
                if (!model.isVehicle) continue;
                Trace("creating remote player vehicle " + model.Hash);
                car = World.CreateVehicle(model, peer.Position);
                if (!Exists(car)) continue;
                if (peer.Physics != null) car.RotationQuaternion = peer.Physics.Orientation;
                else car.Heading = peer.Heading;
                car.FreezePosition = true;
                cars[peer.CarId] = car;
                appliedDamage.Remove(peer.CarId);
            }
            if (peer.Physics != null) ApplyDamage(peer.CarId, car, peer.Physics);
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
        appliedDamage.Remove(id);
    }

    private static void SetPedPosition(Ped ped, Vector3 p)
    {
        // The ordinary teleport adjusts Z, so reading its result and blending again can lift the avatar.
        GTA.Native.Function.Call("SET_CHAR_COORDINATES_NO_OFFSET", ped, p.X, p.Y, p.Z);
    }

    private static string CaptureCarPhysics(Vehicle car)
    {
        Quaternion q = car.RotationQuaternion;
        var state = string.Format(CultureInfo.InvariantCulture, " {0:F4},{1:F4},{2:F4},{3:F4},{4:F1},{5},{6:F1}",
            q.X, q.Y, q.Z, q.W, Math.Max(-4000, Math.Min(1000, car.EngineHealth)),
            Math.Max(0, Math.Min(1000, car.Health)), Math.Max(-4000, Math.Min(1000, car.PetrolTankHealth)));
        if (!car.Model.isCar) return state;
        int bits = 0;
        for (int i = 0; i < replicatedWheels.Length; i++)
            if (car.IsTireBurst(replicatedWheels[i])) bits |= 1 << i;
        return state + "," + bits.ToString(CultureInfo.InvariantCulture);
    }

    private static string CaptureRadio()
    {
        int station = (int)Game.RadioStation;
        return (station >= 0 && station <= (int)RadioStation.Integrity ? station : -1)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static CarPhysics ParseCarPhysics(string encoded)
    {
        string[] fields = encoded.Split(',');
        if (fields.Length != 7 && fields.Length != 8) return null;
        var values = new float[7];
        for (int i = 0; i < values.Length; i++)
            if (!TryCoordinate(fields[i], out values[i])) return null;
        var q = new Quaternion(values[0], values[1], values[2], values[3]);
        float lengthSquared = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        if (lengthSquared < 0.5f || lengthSquared > 1.5f ||
            values[4] < -4000 || values[4] > 1000 || values[5] < 0 || values[5] > 1000 ||
            values[6] < -4000 || values[6] > 1000) return null;
        int tires = -1;
        if (fields.Length == 8 && (!int.TryParse(fields[7], out tires) || tires < 0 || tires > 15)) return null;
        return new CarPhysics { Orientation = Quaternion.Normalize(q), TireMask = tires,
            EngineHealth = values[4], BodyHealth = (int)values[5], TankHealth = values[6] };
    }

    private void ApplyDamage(string id, Vehicle car, CarPhysics state)
    {
        CarPhysics previous;
        appliedDamage.TryGetValue(id, out previous);
        if (previous == null || previous.BodyHealth != state.BodyHealth)
            car.Health = state.BodyHealth;
        if (previous == null || previous.EngineHealth != state.EngineHealth)
            car.EngineHealth = state.EngineHealth;
        if (previous == null || previous.TankHealth != state.TankHealth)
            car.PetrolTankHealth = state.TankHealth;
        if (state.TireMask >= 0 && (previous == null || previous.TireMask != state.TireMask) && car.Model.isCar)
        {
            for (int i = 0; i < replicatedWheels.Length; i++)
            {
                VehicleWheel wheel = replicatedWheels[i];
                bool burst = (state.TireMask & (1 << i)) != 0;
                if (burst && !car.IsTireBurst(wheel)) car.BurstTire(wheel);
                if (!burst && car.IsTireBurst(wheel)) car.FixTire(wheel);
            }
        }
        appliedDamage[id] = state;
    }

    private static Quaternion BlendOrientation(Quaternion from, Quaternion to, float step)
    {
        float dot = from.X * to.X + from.Y * to.Y + from.Z * to.Z + from.W * to.W;
        if (dot < 0) to = new Quaternion(-to.X, -to.Y, -to.Z, -to.W);
        return Quaternion.Normalize(new Quaternion(
            from.X + (to.X - from.X) * step, from.Y + (to.Y - from.Y) * step,
            from.Z + (to.Z - from.Z) * step, from.W + (to.W - from.W) * step));
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
        avatarMotion.Clear();
        previousAvatarTargets.Clear();
        avatarMotionTimes.Clear();
        foreach (string id in new List<string>(cars.Keys)) RemoveCar(id);
        peers.Clear();
        UpdateWorld(null);
        worldSample = "-";
    }
}
