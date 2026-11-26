using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
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
        public int CombatFlags;
    }

    private sealed class CarPhysics
    {
        public Quaternion Orientation;
        public float EngineHealth, TankHealth;
        public int BodyHealth;
        public int TireMask = -1;
    }

    private sealed class VehicleDetails
    {
        public uint Colors, Horn;
        public int Flags, Station = -1;
        public long LastHorn;
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
    private bool serverConnected;
    private bool reportedPeers;
    private string lastSnapshot;
    private long lastUpdate;
    private long nextSample;
    private long lastFrame;
    private long nextBridgeCheck;
    private string ownId = "";
    private string pausedSession;
    private bool pauseWritten;
    private long resumeSample;
    private string ride = "-";
    private Vehicle drivingCar;
    private int drivingToken;
    private long nextWorldSample;
    private long worldSampleAt;
    private long nextAvatarSpawn;
    private string worldSample = "-";
    private string worldOwner;
    private string sharedSource;
    private long lastSharedFrame, lastSharedAt, lastAmbientAt, nextSharedCapture, nextSharedSpawn, nextAmbientCleanup;
    private int episode = -1;
    private bool suppressingPopulation, remoteAmbientConfirmed;
    private bool hornHeld, hornWasPressed;
    private uint hornSequence;
    private readonly Dictionary<string, Ped> worldPeds = new Dictionary<string, Ped>();
    private readonly Dictionary<string, Vehicle> worldCars = new Dictionary<string, Vehicle>();
    private readonly Dictionary<string, PoseTrack> worldTracks = new Dictionary<string, PoseTrack>();
    private readonly Dictionary<string, Ped> worldDrivers = new Dictionary<string, Ped>();
    private readonly Dictionary<string, VehicleDetails> vehicleDetails = new Dictionary<string, VehicleDetails>();
    private readonly Dictionary<string, int> worldGaits = new Dictionary<string, int>();
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
    private long pendingAttack, pendingAttackUntil;
    private int pendingAttackKind;
    private string pendingVictim = "-";
    private uint receivedDamage;
    private bool combatHeaderSeen;
    private readonly HashSet<string> vulnerableAvatars = new HashSet<string>();
    private readonly Dictionary<string, long> avatarRagdollUntil = new Dictionary<string, long>();

    public LibertyFriends()
    {
        string game = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        directory = Path.Combine(game, "mods", "LibertyFriends");
        Interval = 0;
        KeyDown += HandleKey;
        Tick += Update;
        Trace("mod loaded; waiting for launcher and a loaded save");
    }

    private void HandleKey(object sender, GTA.KeyEventArgs e)
    {
        if (e.Key == Keys.F9)
        {
            CheckLauncher();
            if (ownId.Length == 0)
            {
                Game.DisplayText("Connect in the launcher first");
                return;
            }
            if (pausedSession == ownId)
            {
                pausedSession = null;
                resumeSample = clock.ElapsedMilliseconds;
                nextSample = 0;
                Trace("F9: synchronization requested");
            }
            else
            {
                pausedSession = ownId;
                pauseWritten = false;
                PublishPause();
                StopSynchronization("F9");
                Game.DisplayText("Disconnected");
            }
            return;
        }
        if (active && serverConnected && e.Key == Keys.F10) ToggleRide();
    }

    private void PublishPause()
    {
        if (pauseWritten) return;
        try
        {
            File.WriteAllText(Path.Combine(directory, "position.txt"), "PAUSED " + ownId + "\n");
            pauseWritten = true;
        }
        catch (IOException)
        {
            // Retry on the next tick if the bridge is reading the position file.
        }
    }

    private void CheckLauncher()
    {
        string lease = Path.Combine(directory, "bridge-live.txt");
        try
        {
            double age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(lease)).TotalMilliseconds;
            string id = age >= -1000 && age <= 3500 ? File.ReadAllText(lease).Trim() : "";
            if (id != "" && (id.Length != 16 ||
                Array.Exists(id.ToCharArray(), c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))))
                return;
            if (id == ownId) return;
            StopSynchronization(id == "" ? "launcher stopped" : "launcher changed");
            ownId = id;
            resumeSample = 0;
            pauseWritten = false;
            if (id != "") Trace("launcher detected; waiting for a loaded save");
        }
        catch (IOException)
        {
            // A launcher update may briefly hold the lease file.
        }
    }

    private void StopSynchronization(string reason)
    {
        if (!active) return;
        active = false;
        ResetConnection();
        Trace("synchronization stopped: " + reason);
    }

    private void ResetConnection()
    {
        serverConnected = false;
        combatHeaderSeen = false;
        reportedPeers = false;
        worldOwner = null;
        sharedSource = null;
        lastSharedFrame = 0;
        nextSharedCapture = 0;
        remoteAmbientConfirmed = false;
        nextAmbientCleanup = 0;
        episode = -1;
        SetPopulationSuppressed(false);
        drivingCar = null;
        hornHeld = false;
        hornWasPressed = false;
        tunedRide = "-";
        ClearPendingAttack();
        ClearMarkers();
    }

    private void Update(object sender, EventArgs e)
    {
        long now = clock.ElapsedMilliseconds;
        if (now >= nextBridgeCheck)
        {
            nextBridgeCheck = now + 300;
            CheckLauncher();
        }
        if (ownId.Length == 0) return;
        if (pausedSession == ownId)
        {
            PublishPause();
            return;
        }
        if (!active)
        {
            if (now < nextSample) return;
            nextSample = now + 100;
            if (!Player.CanControlCharacter || !Exists(Player.Character)) return;
            active = true;
            nextSample = now;
            lastFrame = now;
            Trace("save ready; sending local state and waiting for server");
        }
        float step = Math.Min(1f, (now - lastFrame) / 80f);
        lastFrame = now;
        Ped character = Player.Character;
        if (!Exists(character))
        {
            StopSynchronization("left gameplay");
            return;
        }
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
                Vehicle car = character.CurrentVehicle;
                bool driving = Exists(car) && Equals(car.GetPedOnSeat(VehicleSeat.Driver), character);
                if (driving && !Equals(drivingCar, car))
                {
                    drivingCar = car;
                    drivingToken++;
                    nextRadioSample = 0;
                    hornSequence = 0;
                    hornWasPressed = false;
                }
                if (!driving) drivingCar = null;
                if (driving) p = car.Position;
                Vehicle passengerCar;
                if (ride != "-" && (!cars.TryGetValue(ride, out passengerCar) ||
                    !Exists(passengerCar) || !Equals(car, passengerCar))) ride = "-";
                if (now >= nextWorldSample)
                {
                    nextWorldSample = now + 67;
                    bool publishWorld = serverConnected && worldOwner == ownId && peers.Count > 0;
                    worldSample = publishWorld ? CaptureWorld(character, car) : "-";
                    worldSampleAt = now;
                }
                if (driving && now >= nextRadioSample)
                {
                    ReadRadio(out sourceStation, out sourceStationName);
                    nextRadioSample = now + 150;
                }
                hornHeld = driving && (car.Model.isCar || car.Model.isBike) &&
                    GTA.Native.Function.Call<bool>("IS_PLAYER_PRESSING_HORN", Player.ID);
                if (hornHeld && !hornWasPressed) hornSequence++;
                hornWasPressed = hornHeld;
                int flags = driving ? ((car.EngineRunning ? 1 : 0) | (car.SirenActive ? 2 : 0)) : 0;
                CaptureCombat(character, now);
                int combatFlags = 1 | (character.isRagdoll ? 2 : 0) | (!character.isAlive ? 4 : 0);
                string position = string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:F2} {2:F2} {3:F2} {4:F2} {5} {6} {7} {8} {9}{10} ~{0},{11},{12},{13},{14},{15} !{16},{17},{18},{19}\n",
                    now, p.X, p.Y, p.Z,
                    ((driving ? car.Heading : character.Heading) % 360 + 360) % 360,
                    character.Model.Hash, driving ? car.Model.Hash : 0, driving ? drivingToken : 0, ride, worldSample,
                    driving ? CaptureCarPhysics(car) + " " +
                        (sourceStation <= (int)RadioStation.Integrity ? sourceStation : -1).ToString(CultureInfo.InvariantCulture) : "",
                    worldSampleAt, 1, flags, driving ? sourceStation : -1,
                    driving ? sourceStationName : "-", combatFlags, pendingAttack, pendingAttackKind, pendingVictim);
                File.WriteAllText(Path.Combine(directory, "position.txt"), position);
                if (now >= nextSharedCapture)
                {
                    nextSharedCapture = now + 67;
                    PublishSharedWorld(now, character, car, driving);
                }
            }

            string path = Path.Combine(directory, "peers.txt");
            if (File.Exists(path))
            {
                string snapshot = File.ReadAllText(path);
                string serverAddress;
                if (snapshot != lastSnapshot && IsCurrentSnapshot(snapshot, path, out serverAddress))
                {
                    lastSnapshot = snapshot;
                    lastUpdate = now;
                    if (!serverConnected)
                    {
                        serverConnected = true;
                        IPAddress address;
                        string destination = IPAddress.TryParse(serverAddress, out address) ? address.ToString() : "server";
                        Game.DisplayText("Connected to " + destination, 3000);
                        Trace("server connected; synchronization enabled: map, avatars, vehicles, environment, radio, PvP and ragdoll");
                    }
                    UpdateMarkers(snapshot, character);
                }
            }
            if (serverConnected) ReadSharedWorld(character, now);
        }
        catch (IOException)
        {
            // The network client may be replacing the snapshot during this tick.
        }
        if (serverConnected && now - lastUpdate > 2500)
        {
            ResetConnection();
            Trace("server timed out; remote entities removed");
        }
        if (sharedSource != null && now - lastSharedAt > 1200)
        {
            sharedSource = null;
            SetPopulationSuppressed(false);
            UpdateWorld(null);
            Trace("shared world timed out");
        }
        UpdateLocalPopulation(character, now);
        MoveAvatars(step, now);
    }

    private bool IsCurrentSnapshot(string snapshot, string path, out string serverAddress)
    {
        serverAddress = null;
        int endOfHeader = snapshot.IndexOf(' ');
        if (endOfHeader < 0 || !snapshot.EndsWith("\n", StringComparison.Ordinal)) return false;
        string[] source = snapshot.Substring(0, endOfHeader).Split(':');
        if (source[source.Length - 1] != ownId) return false;
        if (source.Length == 4)
        {
            long sample;
            if (!long.TryParse(source[1], NumberStyles.None, CultureInfo.InvariantCulture, out sample) ||
                sample < resumeSample) return false;
            serverAddress = source[2];
        }
        else if (source.Length != 2) return false;
        return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalMilliseconds <= 2500;
    }

    private void UpdateMarkers(string snapshot, Ped character)
    {
        peers.Clear();
        var seen = new HashSet<string>();
        string[] lines = snapshot.Split('\n');
        string[] header = lines[0].Split(' ');
        string newOwner = header.Length >= 2 ? header[1] : null;
        ApplyCombatResult(header, character);
        if (newOwner != worldOwner)
        {
            worldOwner = newOwner;
            ResetWorldSource();
            Trace("world owner=" + (worldOwner ?? "-"));
        }
        for (int i = 1; i < lines.Length; i++)
        {
            string[] columns = lines[i].Split(' ');
            int combatFlags = 0;
            string combat = columns[columns.Length - 1];
            if (combat.StartsWith("!", StringComparison.Ordinal))
            {
                int.TryParse(combat.Substring(1), out combatFlags);
                Array.Resize(ref columns, columns.Length - 1);
            }
            if (columns.Length == 0) continue;
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
            var peer = new Peer { Position = position, CombatFlags = combatFlags };
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
            UpdateAvatar(columns[0], position, character, peer.PedModel);
            PrepareCombatAvatar(columns[0], peer);
            UpdateAvatarMotion(columns[0], peer);
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
                vulnerableAvatars.Remove(id);
                avatarRagdollUntil.Remove(id);
            }
        }
        if (!reportedPeers && seen.Count > 0)
        {
            reportedPeers = true;
            Trace("first peer snapshot received");
        }
        UpdateCars(character);
        TunePassengerRadio(character);
        if (sharedSource == null) UpdateWorld(character);
    }

    private void ClearPendingAttack()
    {
        pendingAttack = 0;
        pendingAttackKind = 0;
        pendingVictim = "-";
    }

    private void CaptureCombat(Ped character, long now)
    {
        if (!serverConnected || !character.isAlive || character.isRagdoll)
        {
            ClearPendingAttack();
            return;
        }
        if (pendingAttack != 0 && now < pendingAttackUntil) return;
        ClearPendingAttack();

        int weapon = (int)character.Weapons.CurrentType;
        int kind = weapon >= 0 && weapon <= 3 ? 1
            : weapon >= 7 && weapon <= 17 && weapon != 8 ? 2 : 0;
        if (kind == 0) return;
        foreach (var pair in avatars)
        {
            Peer peer;
            Ped avatar = pair.Value;
            if (!vulnerableAvatars.Contains(pair.Key) || !Exists(avatar) ||
                !peers.TryGetValue(pair.Key, out peer) || (peer.CombatFlags & 5) != 1 ||
                !avatar.HasBeenDamagedBy(character)) continue;

            // GTA decides whether the local attack made contact, including walls and props.
            GTA.Native.Function.Call("CLEAR_CHAR_LAST_DAMAGE_ENTITY", avatar);
            avatar.Health = 1000;
            pendingAttack = DateTime.UtcNow.Ticks;
            pendingAttackUntil = now + 600;
            pendingAttackKind = kind;
            pendingVictim = pair.Key;
            return;
        }
    }

    private void PrepareCombatAvatar(string id, Peer peer)
    {
        Ped avatar;
        if (!avatars.TryGetValue(id, out avatar) || !Exists(avatar)) return;
        bool vulnerable = (peer.CombatFlags & 5) == 1;
        if (vulnerable && vulnerableAvatars.Add(id))
        {
            avatar.MaxHealth = 1000;
            avatar.Health = 1000;
            GTA.Native.Function.Call("CLEAR_CHAR_LAST_DAMAGE_ENTITY", avatar);
            avatar.Invincible = false;
        }
        else if (!vulnerable && vulnerableAvatars.Remove(id))
        {
            avatar.Invincible = true;
        }
        if (vulnerable && avatar.isAlive && avatar.Health < 750) avatar.Health = 1000;
    }

    private void ApplyCombatResult(string[] header, Ped character)
    {
        long acknowledged;
        uint total;
        int kind;
        if (header.Length != 5 || !long.TryParse(header[2], out acknowledged) ||
            !uint.TryParse(header[3], out total) || !int.TryParse(header[4], out kind)) return;
        if (pendingAttack != 0 && acknowledged >= pendingAttack) ClearPendingAttack();
        uint old = receivedDamage;
        receivedDamage = total;
        if (!combatHeaderSeen || total < old)
        {
            combatHeaderSeen = true;
            return;
        }
        if (total == old || !Exists(character) || !character.isAlive) return;
        int damage = (int)Math.Min(500u, total - old);
        GTA.Native.Function.Call("DAMAGE_CHAR", character, damage, true);
        if (character.isAlive && !character.isInVehicle() && !character.isRagdoll)
            character.ForceRagdoll(kind == 3 ? 1300 : 650, false);
        Trace("pvp damage=" + damage + " kind=" + kind);
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
            (int)Math.Round(p.X * 10), (int)Math.Round(p.Y * 10), (int)Math.Round(p.Z * 10),
            (int)((heading % 360 + 360) % 360));
    }

    private void PublishSharedWorld(long sample, Ped local, Vehicle localCar, bool driving)
    {
        if (episode < 0) episode = GTA.Native.Function.Call<int>("GET_CURRENT_EPISODE");
        var frame = new StringBuilder();
        frame.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} {2}\n", ownId, sample, episode);
        if (driving)
            frame.Append(CaptureVehicleDetails("d", drivingToken, localCar, sourceStation,
                hornHeld, hornSequence, 0)).Append('\n');
        if (sharedSource == ownId)
        {
            Vector3 origin = local.Position;
            var nearbyCars = new List<KeyValuePair<float, Vehicle>>();
            foreach (Vehicle car in World.GetVehicles(origin, 145f))
                if (CanPublishCar(car, localCar))
                    nearbyCars.Add(new KeyValuePair<float, Vehicle>(SquaredDistance(origin, car.Position), car));
            nearbyCars.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < Math.Min(10, nearbyCars.Count); i++)
            {
                Vehicle car = nearbyCars[i].Value;
                Ped driver = car.GetPedOnSeat(VehicleSeat.Driver);
                int driverModel = Exists(driver) && !avatars.ContainsValue(driver) ? driver.Model.Hash : 0;
                frame.Append(CaptureVehicleDetails("v", car.GetHashCode(), car, -1, false, 0, driverModel)).Append('\n');
            }

            var nearbyPeds = new List<KeyValuePair<float, Ped>>();
            foreach (Ped ped in World.GetPeds(origin, 145f, 60))
                if (CanPublishPed(ped, local) && !worldDrivers.ContainsValue(ped))
                    nearbyPeds.Add(new KeyValuePair<float, Ped>(SquaredDistance(origin, ped.Position), ped));
            nearbyPeds.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < Math.Min(17, nearbyPeds.Count); i++)
            {
                Ped ped = nearbyPeds[i].Value;
                Vector3 velocity = ped.Velocity;
                float speedSquared = velocity.X * velocity.X + velocity.Y * velocity.Y;
                int gait = ped.isRagdoll ? 4 : speedSquared > 28f ? 3
                    : speedSquared > 6f ? 2 : speedSquared > 0.16f ? 1 : 0;
                frame.Append(WorldRow("p", ped.GetHashCode(), ped.Model.Hash, ped.Position, ped.Heading))
                    .Append(',').Append(gait).Append('\n');
            }
        }
        File.WriteAllText(Path.Combine(directory, "world-local.txt"), frame.ToString());
    }

    private static int TireMask(Vehicle car)
    {
        if (!car.Model.isCar) return -1;
        int bits = 0;
        for (int i = 0; i < replicatedWheels.Length; i++)
            if (car.IsTireBurst(replicatedWheels[i])) bits |= 1 << i;
        return bits;
    }

    private static string CaptureVehicleDetails(string kind, int id, Vehicle car, int station,
        bool horn, uint hornNumber, int driverModel)
    {
        Model model = car.Model;
        Quaternion q = car.RotationQuaternion;
        uint colors = (uint)(car.Color.Index & 255) | ((uint)(car.FeatureColor1.Index & 255) << 8) |
            ((uint)(car.SpecularColor.Index & 255) << 16) | ((uint)(car.FeatureColor2.Index & 255) << 24);
        int flags = (car.EngineRunning ? 1 : 0) | (car.SirenActive ? 2 : 0) |
            ((model.isCar || model.isBike) && car.LightsOn ? 4 : 0) | (horn ? 8 : 0);
        return WorldRow(kind, id, model.Hash, car.Position, car.Heading) +
            string.Format(CultureInfo.InvariantCulture, ",{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12}",
                (int)Math.Round(q.X * 1000), (int)Math.Round(q.Y * 1000), (int)Math.Round(q.Z * 1000),
                (int)Math.Round(q.W * 1000), colors, flags, hornNumber, station, driverModel,
                (int)Math.Max(-4000, Math.Min(1000, car.EngineHealth)),
                Math.Max(0, Math.Min(1000, car.Health)),
                (int)Math.Max(-4000, Math.Min(1000, car.PetrolTankHealth)), TireMask(car));
    }

    private void SetPopulationSuppressed(bool value)
    {
        if (value == suppressingPopulation) return;
        float density = value ? 0f : 1f;
        GTA.Native.Function.Call("SET_CAR_DENSITY_MULTIPLIER", density);
        GTA.Native.Function.Call("SET_PED_DENSITY_MULTIPLIER", density);
        suppressingPopulation = value;
        Trace(value ? "local ambient population suppressed" : "local ambient population restored");
    }

    private void UpdateLocalPopulation(Ped character, long now)
    {
        bool following = serverConnected && sharedSource != null && sharedSource != ownId &&
            now - lastSharedAt < 500;
        bool canClear = following && Player.CanControlCharacter && character.isAlive && !Player.isOnMission;
        SetPopulationSuppressed(canClear && (remoteAmbientConfirmed || (lastAmbientAt > 0 && now - lastAmbientAt < 900)));
        if (!canClear || !remoteAmbientConfirmed || now < nextAmbientCleanup || character.isGettingIntoAVehicle)
            return;

        nextAmbientCleanup = now + 300;
        ClearLocalPopulation(character);
    }

    private void ClearLocalPopulation(Ped character)
    {
        var protectedPeds = new HashSet<Ped>(avatars.Values);
        protectedPeds.UnionWith(worldPeds.Values);
        protectedPeds.UnionWith(worldDrivers.Values);
        protectedPeds.Add(character);

        var protectedCars = new HashSet<Vehicle>(cars.Values);
        protectedCars.UnionWith(worldCars.Values);
        protectedCars.Add(character.CurrentVehicle);
        protectedCars.Add(Player.LastVehicle);

        Group group = Player.Group;
        bool hasGroup = Exists(group);
        Ped[] loadedPeds = World.GetAllPeds();
        foreach (Ped ped in loadedPeds)
        {
            if (!Exists(ped)) continue;
            bool protectedPed = protectedPeds.Contains(ped) || ped.isRequiredForMission ||
                (hasGroup && group.isMember(ped));
            if (!protectedPed) continue;
            protectedPeds.Add(ped);
            protectedCars.Add(ped.CurrentVehicle);
        }

        var obsoleteCars = new HashSet<Vehicle>();
        foreach (Vehicle car in World.GetAllVehicles())
        {
            if (obsoleteCars.Count >= 16) break;
            if (Exists(car) && !protectedCars.Contains(car) && !car.isRequiredForMission && !car.Model.isTrain)
                obsoleteCars.Add(car);
        }
        int pedestriansRemoved = 0;
        foreach (Ped ped in loadedPeds)
        {
            if (!Exists(ped) || protectedPeds.Contains(ped)) continue;
            Vehicle vehicle = ped.CurrentVehicle;
            if (Exists(vehicle))
            {
                if (obsoleteCars.Contains(vehicle)) ped.Delete();
            }
            else if (pedestriansRemoved < 24)
            {
                ped.Delete();
                pedestriansRemoved++;
            }
        }
        foreach (Vehicle car in obsoleteCars)
            if (Exists(car)) car.Delete();
    }

    private void ReadSharedWorld(Ped character, long now)
    {
        string path = Path.Combine(directory, "world-remote.txt");
        if (!File.Exists(path) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalMilliseconds > 1100) return;
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) return;
        string[] header = lines[0].Split(' ');
        long frame, sample;
        int currentEpisode;
        if (header.Length != 5 || header[0] != ownId || !long.TryParse(header[1], out frame) ||
            frame <= lastSharedFrame || !long.TryParse(header[2], out sample) || sample < resumeSample ||
            !int.TryParse(header[3], out currentEpisode) || currentEpisode != episode || header[4].Length != 16)
            return;

        lastSharedFrame = frame;
        lastSharedAt = now;
        string source = header[4];
        if (sharedSource != source)
        {
            UpdateWorld(null);
            sharedSource = source;
            lastAmbientAt = 0;
            nextSharedCapture = 0;
            Trace("shared world source=" + source);
        }
        var seen = new HashSet<string>();
        bool hasAmbientFrame = false;
        for (int i = 1; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split(' ');
            long entitySample;
            if (parts.Length != 3 || parts[0].Length != 16 || !long.TryParse(parts[1], out entitySample)) continue;
            if (parts[2] == "@" && parts[0] == source && source != ownId)
            {
                hasAmbientFrame = true;
                continue;
            }
            string[] fields = parts[2].Split(',');
            if (fields.Length != 8 && fields.Length != 20) continue;
            int id, model;
            if (!int.TryParse(fields[1], out id) || !int.TryParse(fields[2], out model)) continue;
            string kind = fields[0];
            if (kind == "d" && fields.Length == 20)
            {
                string carId = parts[0] + ":" + id.ToString(CultureInfo.InvariantCulture);
                Vehicle remoteCar;
                if (cars.TryGetValue(carId, out remoteCar) && Exists(remoteCar) && remoteCar.Model.Hash == model)
                    ApplyVehicleDetails(carId, remoteCar, fields, now);
                continue;
            }
            if (parts[0] != source || source == ownId) continue;
            int x, y, z, heading;
            if (!int.TryParse(fields[3], out x) || !int.TryParse(fields[4], out y) ||
                !int.TryParse(fields[5], out z) || !int.TryParse(fields[6], out heading)) continue;
            var position = new Vector3(x / 10f, y / 10f, z / 10f);
            if (SquaredDistance(character.Position, position) > 110f * 110f) continue;
            string key = source + ":" + kind + ":" + fields[1] + ":" + fields[2];
            if (kind == "p" && fields.Length == 8)
            {
                int gait;
                if (!int.TryParse(fields[7], out gait) || gait < 0 || gait > 4) continue;
                seen.Add(key);
                ApplySharedPed(key, new Model(model), position, heading, gait, entitySample, now);
            }
            else if (kind == "v" && fields.Length == 20)
            {
                seen.Add(key);
                ApplySharedCar(key, new Model(model), position, heading, fields, entitySample, now);
            }
        }
        remoteAmbientConfirmed = hasAmbientFrame;
        if (seen.Count > 0) lastAmbientAt = now;
        foreach (string key in new List<string>(worldPeds.Keys))
            if (CanCullWorld(key, seen, source != ownId, source, now)) RemoveWorldEntity(key);
        foreach (string key in new List<string>(worldCars.Keys))
            if (CanCullWorld(key, seen, source != ownId, source, now)) RemoveWorldEntity(key);
    }

    private bool CanSpawnWorldModel(Model model, long now)
    {
        if (!model.isInMemory)
        {
            GTA.Native.Function.Call("REQUEST_MODEL", model.Hash);
            return false;
        }
        if (now < nextSharedSpawn) return false;
        nextSharedSpawn = now + 45;
        return true;
    }

    private void ApplySharedPed(string key, Model model, Vector3 position, int heading, int gait, long sample, long now)
    {
        if (!model.isPed) return;
        Ped ped;
        if (!worldPeds.TryGetValue(key, out ped) || !Exists(ped))
        {
            if (!CanSpawnWorldModel(model, now)) return;
            ped = World.CreatePed(model, position);
            if (!Exists(ped)) return;
            ped.Invincible = true;
            ped.BlockPermanentEvents = true;
            ped.FreezePosition = true;
            worldPeds[key] = ped;
            worldGaits.Remove(key);
        }
        QueueWorldPose(key, sample, now, position, heading, null);
        int previous;
        if (worldGaits.TryGetValue(key, out previous) && previous == gait) return;
        if (gait == 4)
        {
            ped.FreezePosition = false;
            ped.ForceRagdoll(500, false);
        }
        else
        {
            ped.FreezePosition = true;
            ped.Task.PlayAnimation(playerMovement, gaitClips[gait], 1f, AnimationFlags.Unknown01);
        }
        worldGaits[key] = gait;
    }

    private void ApplySharedCar(string key, Model model, Vector3 position, int heading, string[] fields,
        long sample, long now)
    {
        if (!model.isVehicle) return;
        int qx, qy, qz, qw, driver, engine, body, tank, tires;
        if (!int.TryParse(fields[7], out qx) || !int.TryParse(fields[8], out qy) ||
            !int.TryParse(fields[9], out qz) || !int.TryParse(fields[10], out qw) ||
            !int.TryParse(fields[15], out driver) || !int.TryParse(fields[16], out engine) ||
            !int.TryParse(fields[17], out body) || !int.TryParse(fields[18], out tank) ||
            !int.TryParse(fields[19], out tires)) return;
        var rotation = new Quaternion(qx / 1000f, qy / 1000f, qz / 1000f, qw / 1000f);
        if (rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z +
            rotation.W * rotation.W < 0.5f) return;
        rotation = Quaternion.Normalize(rotation);
        Vehicle car;
        if (!worldCars.TryGetValue(key, out car) || !Exists(car))
        {
            if (!CanSpawnWorldModel(model, now)) return;
            car = World.CreateVehicle(model, position);
            if (!Exists(car)) return;
            car.FreezePosition = true;
            GTA.Native.Function.Call("SET_CAR_COLLISION", car, false);
            car.DoorLock = DoorLock.ImpossibleToOpen;
            worldCars[key] = car;
            vehicleDetails.Remove(key);
            appliedDamage.Remove(key);
        }
        QueueWorldPose(key, sample, now, position, heading, rotation);
        ApplyVehicleDetails(key, car, fields, now);
        ApplyDamage(key, car, new CarPhysics { Orientation = rotation, EngineHealth = engine,
            BodyHealth = body, TankHealth = tank, TireMask = tires });
        Ped actor;
        if (worldDrivers.TryGetValue(key, out actor) &&
            (!Exists(actor) || actor.Model.Hash != driver || !Equals(actor.CurrentVehicle, car)))
        {
            if (Exists(actor)) actor.Delete();
            worldDrivers.Remove(key);
        }
        if (driver == 0 || worldDrivers.ContainsKey(key)) return;
        var driverModel = new Model(driver);
        if (!driverModel.isPed || !CanSpawnWorldModel(driverModel, now)) return;
        actor = car.CreatePedOnSeat(VehicleSeat.Driver, driverModel);
        if (!Exists(actor)) return;
        actor.Invincible = true;
        actor.BlockPermanentEvents = true;
        worldDrivers[key] = actor;
    }

    private void ApplyVehicleDetails(string key, Vehicle car, string[] fields, long now)
    {
        uint colors, horn;
        int flags, station;
        if (!uint.TryParse(fields[11], out colors) || !int.TryParse(fields[12], out flags) ||
            !uint.TryParse(fields[13], out horn) || !int.TryParse(fields[14], out station)) return;
        VehicleDetails old;
        vehicleDetails.TryGetValue(key, out old);
        if (old == null || old.Colors != colors)
        {
            GTA.Native.Function.Call("CHANGE_CAR_COLOUR", car, (int)(colors & 255), (int)(colors >> 8 & 255));
            GTA.Native.Function.Call("SET_EXTRA_CAR_COLOURS", car, (int)(colors >> 16 & 255), (int)(colors >> 24));
        }
        if (old == null || ((old.Flags ^ flags) & 1) != 0) car.EngineRunning = (flags & 1) != 0;
        if (old == null || ((old.Flags ^ flags) & 2) != 0) car.SirenActive = (flags & 2) != 0;
        if ((old == null || ((old.Flags ^ flags) & 4) != 0) && (car.Model.isCar || car.Model.isBike))
            GTA.Native.Function.Call("FORCE_CAR_LIGHTS", car, (flags & 4) == 0 ? 1 : 2);
        long lastHorn = old == null ? 0 : old.LastHorn;
        if ((old != null && horn != old.Horn) || ((flags & 8) != 0 && now - lastHorn >= 150))
        {
            car.SoundHorn(170);
            lastHorn = now;
        }
        vehicleDetails[key] = new VehicleDetails { Colors = colors, Flags = flags, Horn = horn,
            Station = station, LastHorn = lastHorn };
    }

    private void RemoveWorldEntity(string key)
    {
        Ped ped;
        Vehicle car;
        if (worldDrivers.TryGetValue(key, out ped) && Exists(ped)) ped.Delete();
        if (worldPeds.TryGetValue(key, out ped) && Exists(ped)) ped.Delete();
        if (worldCars.TryGetValue(key, out car) && Exists(car)) car.Delete();
        worldDrivers.Remove(key);
        worldPeds.Remove(key);
        worldCars.Remove(key);
        worldTracks.Remove(key);
        worldGaits.Remove(key);
        appliedDamage.Remove(key);
        vehicleDetails.Remove(key);
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
                RemoveWorldEntity(key);
        foreach (string key in new List<string>(worldCars.Keys))
            if (CanCullWorld(key, seen, hasOwner, leader, now))
                RemoveWorldEntity(key);
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
            vulnerableAvatars.Remove(id);
            avatarRagdollUntil.Remove(id);
            return;
        }
        if ((Exists(avatar) && avatar.isAlive) || clock.ElapsedMilliseconds < nextAvatarSpawn) return;
        if (Exists(avatar)) avatar.Delete();
        vulnerableAvatars.Remove(id);
        avatarRagdollUntil.Remove(id);
        avatarGaits.Remove(id);
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
            if ((peer.CombatFlags & 6) != 0)
            {
                UpdateRemoteRagdoll(entry.Key, avatar, peer, now);
                continue;
            }
            avatarRagdollUntil.Remove(entry.Key);
            avatar.FreezePosition = true;
            Vector3 current = avatar.Position;
            if (SquaredDistance(current, peer.Position) > 0.0001f)
                SetPedPosition(avatar, Blend(current, peer.Position, step));
            avatar.Heading = peer.Heading;
        }
    }

    private void UpdateRemoteRagdoll(string id, Ped avatar, Peer peer, long now)
    {
        long next;
        if (!avatarRagdollUntil.TryGetValue(id, out next) || now >= next)
        {
            avatar.FreezePosition = false;
            if (!avatar.isRagdoll) avatar.ForceRagdoll(950, false);
            avatarRagdollUntil[id] = now + 750;
        }
        Vector3 current = avatar.Position;
        if (SquaredDistance(current, peer.Position) > 1.0f)
            SetPedPosition(avatar, peer.Position);
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
            (peer.CombatFlags & 6) != 0 || peer.CarId != null || peer.Ride != "-" || Exists(avatar.CurrentVehicle))
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
                if (peer.CarId != ride) continue;
                VehicleDetails details;
                int station = vehicleDetails.TryGetValue(ride, out details) && details.Station >= 0 ?
                    details.Station : peer.Radio;
                string name = station == peer.Radio ? peer.StationName : "-";
                if (station < 0 && name == "-") continue;
                long now = clock.ElapsedMilliseconds;
                if (ride != tunedRide || station != tunedStation || name != tunedName)
                {
                    tunedRide = ride;
                    tunedStation = station;
                    tunedName = name;
                    tuneAttempts = 0;
                    radioConfirmed = false;
                    tuneAfter = now + 120;
                }
                if (now < tuneAfter) return;
                tuneAfter = now + 600;
                int localIndex;
                string localName;
                ReadRadio(out localIndex, out localName);
                bool agrees = (tunedStation >= 0 && tunedStation == localIndex) ||
                    (tunedName != "-" && tunedName == localName);
                if (agrees)
                {
                    if (!radioConfirmed) Trace("radio confirmed station=" + tunedName + " index=" + tunedStation);
                    radioConfirmed = true;
                    tuneAttempts = 0;
                    return;
                }
                if (radioConfirmed) tuneAttempts = 0;
                radioConfirmed = false;
                if (GTA.Native.Function.Call<bool>("IS_RADIO_RETUNING")) return;
                if (tuneAttempts < 4) tuneAttempts++;
                if (tunedName != "-" && (tuneAttempts == 2 || tunedStation < 0))
                    GTA.Native.Function.Call("RETUNE_RADIO_TO_STATION_NAME", tunedName);
                else if (tunedStation >= 0)
                    GTA.Native.Function.Call("RETUNE_RADIO_TO_STATION_INDEX", tunedStation);
                if (tuneAttempts < 4)
                    Trace("radio retune attempt=" + tuneAttempts + " requested=" + tunedName + "/" + tunedStation +
                        " local=" + localName + "/" + localIndex);
                if (tuneAttempts >= 3) tuneAfter = now + 2000;
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
                GTA.Native.Function.Call("SET_CAR_COLLISION", car, false);
                cars[peer.CarId] = car;
                appliedDamage.Remove(peer.CarId);
                appliedVehicleFlags.Remove(peer.CarId);
                vehicleDetails.Remove(peer.CarId);
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
        vehicleDetails.Remove(id);
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
        int tires = TireMask(car);
        return tires < 0 ? state : state + "," + tires.ToString(CultureInfo.InvariantCulture);
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
        vulnerableAvatars.Clear();
        avatarRagdollUntil.Clear();
        foreach (string id in new List<string>(cars.Keys)) RemoveCar(id);
        peers.Clear();
        UpdateWorld(null);
        ResetWorldSource();
    }
}
