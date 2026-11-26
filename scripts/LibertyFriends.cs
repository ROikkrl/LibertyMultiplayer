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
        public string StationName = "-";
        public int VehicleFlags = -1;
        public long Sample = -1, WorldSample = -1;
    }

    private sealed class CarPhysics
    {
        public Quaternion Orientation;
        public float EngineHealth, TankHealth;
        public int BodyHealth;
        public int TireMask = -1;
    }

    private sealed class AvatarGait
    {
        public Vector3 Sample;
        public long SampleAt;
        public long CheckAt;
        public float Speed;
        public int Clip = -1;
    }

    private sealed class Pose
    {
        public long Sample, At;
        public Vector3 Position;
        public float Heading;
        public Quaternion? Orientation;
    }

    private sealed class PoseTrack
    {
        private readonly List<Pose> samples = new List<Pose>(12);
        public long LastSeen;

        public void Add(long sample, long now, Vector3 position, float heading, Quaternion? rotation)
        {
            LastSeen = now;
            if (samples.Count != 0)
            {
                Pose previous = samples[samples.Count - 1];
                if (sample >= 0 && sample <= previous.Sample)
                {
                    if (sample == previous.Sample || now - previous.At < 500) return;
                    samples.Clear();
                }
                else if (now - previous.At > 500 || SquaredDistance(previous.Position, position) > 900f)
                {
                    samples.Clear();
                }
            }
            samples.Add(new Pose { Sample = sample, At = now, Position = position,
                Heading = heading, Orientation = rotation });
            if (samples.Count > 12) samples.RemoveAt(0);
        }

        public Pose Read(long time)
        {
            while (samples.Count > 2 && samples[1].At <= time) samples.RemoveAt(0);
            Pose first = samples[0];
            if (samples.Count == 1 || time <= first.At) return first;
            Pose next = samples[1];
            if (time >= next.At) return next;
            float t = (float)(time - first.At) / Math.Max(1, next.At - first.At);
            return new Pose { Position = Lerp(first.Position, next.Position, t),
                Heading = BlendHeading(first.Heading, next.Heading, t),
                Orientation = first.Orientation.HasValue && next.Orientation.HasValue
                    ? (Quaternion?)BlendOrientation(first.Orientation.Value, next.Orientation.Value, t) : null };
        }
    }

    private readonly Dictionary<string, Blip> markers = new Dictionary<string, Blip>();
    private readonly Dictionary<string, Vector3> positions = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, Ped> avatars = new Dictionary<string, Ped>();
    private readonly Dictionary<string, Vehicle> cars = new Dictionary<string, Vehicle>();
    private readonly Dictionary<string, PoseTrack> carTracks = new Dictionary<string, PoseTrack>();
    private readonly Dictionary<string, CarPhysics> appliedDamage = new Dictionary<string, CarPhysics>();
    private readonly Dictionary<string, int> appliedVehicleFlags = new Dictionary<string, int>();
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
    private long worldSampleAt;
    private long nextAvatarSpawn;
    private string worldSample = "-";
    private string worldOwner;
    private readonly Dictionary<string, Ped> worldPeds = new Dictionary<string, Ped>();
    private readonly Dictionary<string, Vehicle> worldCars = new Dictionary<string, Vehicle>();
    private readonly Dictionary<string, PoseTrack> worldTracks = new Dictionary<string, PoseTrack>();
    private readonly List<Ped> publishedPeds = new List<Ped>(2);
    private readonly List<Vehicle> publishedCars = new List<Vehicle>(2);
    private readonly Dictionary<string, AvatarGait> avatarGaits = new Dictionary<string, AvatarGait>();
    private static readonly VehicleWheel[] replicatedWheels = {
        VehicleWheel.FrontLeft, VehicleWheel.FrontRight, VehicleWheel.RearLeft, VehicleWheel.RearRight
    };
    private readonly AnimationSet playerMovement = new AnimationSet("move_player");
    private static readonly string[] gaitClips = { "idle", "walk", "run", "sprint" };
    private string tunedRide = "-";
    private int tunedStation = -1;
    private string tunedName = "-";
    private long tuneAfter;
    private int tuneAttempts;
    private bool radioConfirmed;
    private string sourceStationName = "-";
    private int sourceStation = -1;
    private long nextRadioSample;

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
            ResetWorldSource();
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
            MoveAvatars(step, now);
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
                    nextRadioSample = 0;
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
                    nextWorldSample = now + 67;
                    bool publishWorld = experimentalWorld && worldOwner == ownId &&
                        ownId.Length == 16 && peers.Count > 0;
                    worldSample = publishWorld ? CaptureWorld(character, car) : "-";
                    worldSampleAt = now;
                }
                if (driving && now >= nextRadioSample)
                {
                    ReadRadio(out sourceStation, out sourceStationName);
                    nextRadioSample = now + 150;
                }
                int flags = driving ? ((car.EngineRunning ? 1 : 0) | (car.SirenActive ? 2 : 0)) : 0;
                string position = string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:F2} {2:F2} {3:F2} {4:F2} {5} {6} {7} {8} {9}{10} ~{0},{11},{12},{13},{14},{15}\n",
                    now, p.X, p.Y, p.Z,
                    ((driving ? car.Heading : character.Heading) % 360 + 360) % 360,
                    character.Model.Hash, driving ? car.Model.Hash : 0, driving ? drivingToken : 0, ride, worldSample,
                    driving ? CaptureCarPhysics(car) + " " +
                        (sourceStation <= (int)RadioStation.Integrity ? sourceStation : -1).ToString(CultureInfo.InvariantCulture) : "",
                    worldSampleAt, experimentalWorld ? 1 : 0, flags, driving ? sourceStation : -1,
                    driving ? sourceStationName : "-");
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
        MoveAvatars(step, now);
    }

    private void UpdateMarkers(string snapshot, Ped character)
    {
        peers.Clear();
        var seen = new HashSet<string>();
        string[] lines = snapshot.Split('\n');
        string[] header = lines[0].Split(' ');
        string newOwner = header.Length == 2 ? header[1] : null;
        if (newOwner != worldOwner)
        {
            worldOwner = newOwner;
            ResetWorldSource();
            Trace("world owner=" + (worldOwner ?? "-"));
        }
        for (int i = 1; i < lines.Length; i++)
        {
            string[] columns = lines[i].Split(' ');
            string metadata = columns[columns.Length - 1];
            if (metadata.StartsWith("~", StringComparison.Ordinal))
                Array.Resize(ref columns, columns.Length - 1);
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
            if (metadata.StartsWith("~", StringComparison.Ordinal)) ReadMetadata(peer, metadata);
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
                avatarGaits.Remove(id);
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

    private static void ReadMetadata(Peer peer, string metadata)
    {
        string[] fields = metadata.Substring(1).Split(',');
        long sample, worldSample;
        int flags, station;
        if (fields.Length != 6 || !long.TryParse(fields[0], out sample) ||
            !long.TryParse(fields[1], out worldSample) || !int.TryParse(fields[3], out flags) ||
            !int.TryParse(fields[4], out station)) return;
        peer.Sample = sample;
        peer.WorldSample = worldSample;
        peer.VehicleFlags = flags;
        peer.Radio = station;
        peer.StationName = fields[5];
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

    private void ResetWorldSource()
    {
        publishedPeds.Clear();
        publishedCars.Clear();
        worldSample = "-";
        nextWorldSample = 0;
    }

    private string CaptureWorld(Ped local, Vehicle localCar)
    {
        var rows = new List<string>(4);
        Vector3 origin = local.Position;
        publishedPeds.RemoveAll(ped => !CanPublishPed(ped, local) ||
            SquaredDistance(origin, ped.Position) > 2500f);
        if (publishedPeds.Count < 2)
        {
            foreach (Ped ped in World.GetPeds(origin, 40f, 20))
            {
                if (CanPublishPed(ped, local) && !publishedPeds.Contains(ped)) publishedPeds.Add(ped);
                if (publishedPeds.Count == 2) break;
            }
        }
        publishedCars.RemoveAll(car => !CanPublishCar(car, localCar) ||
            SquaredDistance(origin, car.Position) > 2500f);
        if (publishedCars.Count < 2)
        {
            foreach (Vehicle car in World.GetVehicles(origin, 40f))
            {
                if (CanPublishCar(car, localCar) && !publishedCars.Contains(car)) publishedCars.Add(car);
                if (publishedCars.Count == 2) break;
            }
        }
        foreach (Vehicle car in publishedCars)
        {
            Quaternion q = car.RotationQuaternion;
            rows.Add(WorldRow("v", car.GetHashCode(), car.Model.Hash, car.Position, car.Heading) +
                string.Format(CultureInfo.InvariantCulture, ",{0},{1},{2},{3}",
                    (int)Math.Round(q.X * 1000), (int)Math.Round(q.Y * 1000),
                    (int)Math.Round(q.Z * 1000), (int)Math.Round(q.W * 1000)));
        }
        foreach (Ped ped in publishedPeds)
            rows.Add(WorldRow("p", ped.GetHashCode(), ped.Model.Hash, ped.Position, ped.Heading));
        return rows.Count == 0 ? "-" : string.Join(";", rows.ToArray());
    }

    private bool CanPublishPed(Ped ped, Ped local)
    {
        return Exists(ped) && !Equals(ped, local) && ped.isAlive && !ped.isRequiredForMission &&
            !Exists(ped.CurrentVehicle) && !avatars.ContainsValue(ped) && !worldPeds.ContainsValue(ped);
    }

    private bool CanPublishCar(Vehicle car, Vehicle localCar)
    {
        return Exists(car) && !Equals(car, localCar) && !car.isRequiredForMission && car.isAlive &&
            !cars.ContainsValue(car) && !worldCars.ContainsValue(car);
    }

    private static string WorldRow(string kind, int id, int model, Vector3 p, float heading)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6}", kind, id, model,
            (int)(p.X * 10), (int)(p.Y * 10), (int)(p.Z * 10), (int)heading);
    }

    private void UpdateWorld(Ped character)
    {
        var seen = new HashSet<string>();
        string leader = worldOwner;
        long now = clock.ElapsedMilliseconds;
        Peer owner;
        bool hasOwner = leader != null && leader != "-" && leader != ownId && Exists(character);
        if (hasOwner && peers.TryGetValue(leader, out owner))
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
                    QueueWorldPose(key, owner.WorldSample, now, position, heading, null);
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
                    Quaternion? orientation = null;
                    int qx, qy, qz, qw;
                    if (fields.Length == 11 && int.TryParse(fields[7], out qx) &&
                        int.TryParse(fields[8], out qy) && int.TryParse(fields[9], out qz) &&
                        int.TryParse(fields[10], out qw))
                    {
                        var q = new Quaternion(qx / 1000f, qy / 1000f, qz / 1000f, qw / 1000f);
                        float norm = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
                        if (norm >= 0.5f && norm <= 1.5f)
                            orientation = Quaternion.Normalize(q);
                    }
                    QueueWorldPose(key, owner.WorldSample, now, position, heading, orientation);
                }
            }
        }
        foreach (string key in new List<string>(worldPeds.Keys))
            if (CanCullWorld(key, seen, hasOwner, leader, now))
            {
                if (Exists(worldPeds[key])) worldPeds[key].Delete();
                worldPeds.Remove(key);
                worldTracks.Remove(key);
            }
        foreach (string key in new List<string>(worldCars.Keys))
            if (CanCullWorld(key, seen, hasOwner, leader, now))
            {
                if (Exists(worldCars[key])) worldCars[key].Delete();
                worldCars.Remove(key);
                worldTracks.Remove(key);
            }
    }

    private bool CanCullWorld(string key, HashSet<string> seen, bool hasOwner, string owner, long now)
    {
        PoseTrack track;
        return !seen.Contains(key) && (!hasOwner || !key.StartsWith(owner + ":", StringComparison.Ordinal) ||
            !worldTracks.TryGetValue(key, out track) || now - track.LastSeen > 450);
    }

    private void QueueWorldPose(string key, long sample, long now, Vector3 position, float heading, Quaternion? orientation)
    {
        PoseTrack track;
        if (!worldTracks.TryGetValue(key, out track))
        {
            track = new PoseTrack();
            worldTracks[key] = track;
        }
        track.Add(sample, now, position, heading, orientation);
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
            avatarGaits.Remove(id);
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

    private void MoveAvatars(float step, long now)
    {
        foreach (var entry in worldCars)
        {
            Vehicle car = entry.Value;
            if (!Exists(car)) continue;
            SetCarPose(car, worldTracks[entry.Key].Read(now - 140));
        }
        foreach (var entry in worldPeds)
        {
            Ped ped = entry.Value;
            if (!Exists(ped)) continue;
            Pose pose = worldTracks[entry.Key].Read(now - 140);
            if (SquaredDistance(ped.Position, pose.Position) > 0.0001f)
                SetPedPosition(ped, pose.Position);
            ped.Heading = pose.Heading;
        }
        foreach (var entry in cars)
            if (Exists(entry.Value)) SetCarPose(entry.Value, carTracks[entry.Key].Read(now - 85));

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
            avatarGaits.Remove(id);
            return;
        }
        long now = clock.ElapsedMilliseconds;
        AvatarGait gait;
        if (!avatarGaits.TryGetValue(id, out gait))
        {
            gait = new AvatarGait { Sample = peer.Position, SampleAt = now };
            avatarGaits[id] = gait;
        }
        float dx = gait.Sample.X - peer.Position.X, dy = gait.Sample.Y - peer.Position.Y;
        float distance = dx * dx + dy * dy;
        long elapsed = now - gait.SampleAt;
        if (elapsed >= 60 && (distance >= 0.0025f || elapsed >= 200))
        {
            if (distance >= 0.0025f && distance < 9f && elapsed < 350)
            {
                float measured = (float)Math.Sqrt(distance) * 1000 / elapsed;
                gait.Speed = gait.Speed * 0.4f + measured * 0.6f;
            }
            else gait.Speed = 0;
            gait.Sample = peer.Position;
            gait.SampleAt = now;
        }

        // Hysteresis avoids switching back and forth near a gait boundary.
        int motion = gait.Speed >= (gait.Clip == 3 ? 4.7f : 5.4f) ? 3
            : gait.Speed >= (gait.Clip >= 2 ? 2.2f : 2.8f) ? 2
            : gait.Speed >= (gait.Clip >= 1 ? 0.25f : 0.45f) ? 1 : 0;
        string clip = gaitClips[motion];
        if (motion == gait.Clip)
        {
            if (now < gait.CheckAt) return;
            gait.CheckAt = now + 750;
            if (playerMovement.isInMemory && playerMovement.isPedPlayingAnimation(avatar, clip)) return;
        }
        gait.Clip = motion;
        gait.CheckAt = now + 750;
        avatar.Task.PlayAnimation(playerMovement, clip, 1f, AnimationFlags.Unknown01);
    }

    private void TunePassengerRadio(Ped character)
    {
        Vehicle car;
        if (ride != "-" && Exists(character) && cars.TryGetValue(ride, out car) &&
            Exists(car) && Equals(character.CurrentVehicle, car))
        {
            foreach (Peer peer in peers.Values)
            {
                if (peer.CarId != ride || (peer.Radio < 0 && peer.StationName == "-")) continue;
                long now = clock.ElapsedMilliseconds;
                if (ride != tunedRide || peer.Radio != tunedStation || peer.StationName != tunedName)
                {
                    tunedRide = ride;
                    tunedStation = peer.Radio;
                    tunedName = peer.StationName;
                    tuneAttempts = 0;
                    radioConfirmed = false;
                    tuneAfter = now + 300;
                }
                if (now < tuneAfter) return;
                tuneAfter = now + 600;
                int localIndex;
                string localName;
                ReadRadio(out localIndex, out localName);
                bool agrees = tunedName != "-" ? tunedName == localName
                    : tunedStation >= 0 && tunedStation == localIndex;
                if (agrees)
                {
                    if (!radioConfirmed) Trace("radio confirmed station=" + tunedName + " index=" + tunedStation);
                    radioConfirmed = true;
                    tuneAttempts = 0;
                    return;
                }
                if (radioConfirmed) tuneAttempts = 0;
                radioConfirmed = false;
                if (tuneAttempts >= 3) return;
                tuneAttempts++;
                if (tuneAttempts == 1 && tunedName != "-")
                    GTA.Native.Function.Call("RETUNE_RADIO_TO_STATION_NAME", tunedName);
                else if (tunedStation >= 0)
                    GTA.Native.Function.Call("RETUNE_RADIO_TO_STATION_INDEX", tunedStation);
                else
                    GTA.Native.Function.Call("RETUNE_RADIO_TO_STATION_NAME", tunedName);
                Trace("radio retune attempt=" + tuneAttempts + " requested=" + tunedName + "/" + tunedStation +
                    " local=" + localName + "/" + localIndex);
                return;
            }
        }
        tunedRide = "-";
        tunedStation = -1;
        tunedName = "-";
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
                appliedVehicleFlags.Remove(peer.CarId);
                carTracks[peer.CarId] = new PoseTrack();
            }
            Quaternion? orientation = peer.Physics == null ? (Quaternion?)null : peer.Physics.Orientation;
            carTracks[peer.CarId].Add(peer.Sample, clock.ElapsedMilliseconds, peer.Position, peer.Heading, orientation);
            if (peer.Physics != null) ApplyDamage(peer.CarId, car, peer.Physics);
            int priorFlags;
            if (peer.VehicleFlags >= 0 &&
                (!appliedVehicleFlags.TryGetValue(peer.CarId, out priorFlags) || priorFlags != peer.VehicleFlags))
            {
                car.EngineRunning = (peer.VehicleFlags & 1) != 0;
                car.SirenActive = (peer.VehicleFlags & 2) != 0;
                appliedVehicleFlags[peer.CarId] = peer.VehicleFlags;
            }
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
        foreach (Peer peer in peers.Values)
        {
            if (peer.CarId != nearest || peer.StationName == "-" || peer.Radio == 255) continue;
            GTA.Native.Function.Call("FORCE_INITIAL_PLAYER_STATION", peer.StationName);
            break;
        }
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
        carTracks.Remove(id);
        appliedDamage.Remove(id);
        appliedVehicleFlags.Remove(id);
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

    private static void ReadRadio(out int index, out string station)
    {
        index = (int)Game.RadioStation;
        if (index < 0 || index > 255) index = -1;
        station = GTA.Native.Function.Call<string>("GET_PLAYER_RADIO_STATION_NAME");
        if (string.IsNullOrEmpty(station) || station.Length > 32 || index == 255)
        {
            station = "-";
            return;
        }
        foreach (char c in station)
        {
            if (c == '_' || (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) continue;
            station = "-";
            return;
        }
        station = station.ToUpperInvariant();
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
        return Lerp(current, target, step);
    }

    private static Vector3 Lerp(Vector3 current, Vector3 target, float step)
    {
        return new Vector3(current.X + (target.X - current.X) * step,
            current.Y + (target.Y - current.Y) * step, current.Z + (target.Z - current.Z) * step);
    }

    private static void SetCarPose(Vehicle car, Pose pose)
    {
        Vector3 p = pose.Position;
        if (SquaredDistance(car.Position, p) > 0.0001f)
            GTA.Native.Function.Call("SET_CAR_COORDINATES_NO_OFFSET", car, p.X, p.Y, p.Z);
        if (pose.Orientation.HasValue) car.RotationQuaternion = pose.Orientation.Value;
        else car.Heading = pose.Heading;
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
        avatarGaits.Clear();
        foreach (string id in new List<string>(cars.Keys)) RemoveCar(id);
        peers.Clear();
        UpdateWorld(null);
        ResetWorldSource();
    }
}
