using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
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
        public int Steering, Gait = -1;
        public uint Horn;
        public bool HasRoadSignals;
        public long Sample = -1, WorldSample = -1;
        public int CombatFlags;
        public uint HitSequence;
        public int HitKind, ImpactX, ImpactY;
        public string VehicleOrigin;
    }

    private sealed class ReplicaRagdoll
    {
        public uint HitSequence;
        public long HoldUntil, RetryAt;
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
        public long LastHorn, MotionAt, MotionSample = -1, NextPhysics, SignalAt, SignalSample = -1, NextLights;
        public float ForwardSpeed, VerticalSpeed, Steering;
        public long AlarmEnds;
    }

    private sealed class VehicleSignal
    {
        public uint HornSequence;
        public bool HornActive;
        public float Heading, Steering;
        public long SampledAt;
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

        public Pose Read(long time, bool predictWalking = false)
        {
            while (samples.Count > 2 && samples[1].At <= time) samples.RemoveAt(0);
            Pose first = samples[0];
            if (samples.Count == 1 || time <= first.At) return first;
            Pose next = samples[1];
            if (time >= next.At)
            {
                long interval = next.Sample - first.Sample;
                if (!predictWalking || interval < 15 || interval > 300) return next;
                float dx = next.Position.X - first.Position.X;
                float dy = next.Position.Y - first.Position.Y;
                float maxDistance = interval * 0.010f;
                if (dx * dx + dy * dy > maxDistance * maxDistance) return next;
                float advance = (float)Math.Min(100, time - next.At) / interval;
                return new Pose { Position = new Vector3(next.Position.X + dx * advance,
                    next.Position.Y + dy * advance, next.Position.Z), Heading = next.Heading };
            }
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
    private readonly bool hasCeVehicleLayout;
    private readonly byte[] vehicleMemory = new byte[0x3DC];
    private readonly Dictionary<int, Vector3[]> lampPositions = new Dictionary<int, Vector3[]>();
    private const int Engine = 1, Siren = 2, Headlights = 4, HornOn = 8,
        LeftIndicator = 32, RightIndicator = 64, Braking = 128, Hazard = 256;
    private const int KnownRoadFlags = Headlights | LeftIndicator | RightIndicator | Braking | Hazard;
    private int indicators;
    private int indicatorKey;
    private readonly Dictionary<int, VehicleSignal> vehicleSignals = new Dictionary<int, VehicleSignal>();
    private bool active;
    private bool serverConnected;
    private bool reportedPeers;
    private string lastSnapshot;
    private long lastUpdate;
    private double nextSample;
    private long nextBridgeCheck;
    private string ownId = "";
    private string pausedSession;
    private bool pauseWritten;
    private long resumeSample;
    private string ride = "-";
    private Vehicle drivingCar;
    private Vehicle trackedPlayerCar;
    private string drivingOrigin = "-";
    private int previousGameClock;
    private bool previousGameActive;
    private long activeGameMillis, nextGameClockCheck;
    private int drivingToken;
    private long nextWorldSample;
    private long worldSampleAt;
    private long nextAvatarSpawn;
    private string worldSample = "-";
    private string worldOwner;
    private string sharedSource;
    private string globalWorldHost;
    private long lastSharedFrame, lastSharedAt, lastAmbientAt, nextSharedCapture, nextSharedSpawn, nextAmbientCleanup;
    private int episode = -1;
    private bool suppressingPopulation, remoteAmbientConfirmed;
    private string pendingTake;
    private long takeUntil;
    private bool takeMounted, takeLeaving;
    private Vehicle acquiredTraffic;
    private string acquiredTrafficKey;
    private readonly Dictionary<string, Vehicle> sourceTraffic = new Dictionary<string, Vehicle>();
    private readonly Dictionary<string, long> delegatedTraffic = new Dictionary<string, long>();
    private readonly Dictionary<string, long> retiredTraffic = new Dictionary<string, long>();
    private readonly HashSet<string> occupiedCars = new HashSet<string>();
    private readonly HashSet<Vehicle> releasedCars = new HashSet<Vehicle>();
    private bool hornHeld;
    private readonly Dictionary<string, Ped> worldPeds = new Dictionary<string, Ped>();
    private readonly Dictionary<string, Vehicle> worldCars = new Dictionary<string, Vehicle>();
    private readonly Dictionary<string, PoseTrack> worldTracks = new Dictionary<string, PoseTrack>();
    private readonly Dictionary<string, Ped> worldDrivers = new Dictionary<string, Ped>();
    private readonly Dictionary<string, VehicleDetails> vehicleDetails = new Dictionary<string, VehicleDetails>();
    private readonly Dictionary<string, int> worldGaits = new Dictionary<string, int>();
    private readonly List<Ped> publishedPeds = new List<Ped>(2);
    private readonly List<Vehicle> publishedCars = new List<Vehicle>(2);
    private readonly Dictionary<string, AvatarGait> avatarGaits = new Dictionary<string, AvatarGait>();
    private readonly Dictionary<string, PoseTrack> avatarTracks = new Dictionary<string, PoseTrack>();
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
    private readonly Dictionary<string, ReplicaRagdoll> avatarRagdolls = new Dictionary<string, ReplicaRagdoll>();

    public LibertyFriends()
    {
        ProcessModule module = Process.GetCurrentProcess().MainModule;
        string game = Path.GetDirectoryName(module.FileName);
        directory = Path.Combine(game, "mods", "LibertyFriends");
        hasCeVehicleLayout = MatchesVehicleLayout(module.BaseAddress);
        Interval = 0;
        KeyDown += HandleKey;
        KeyUp += ReleaseKey;
        Tick += Update;
        Trace("mod loaded; waiting for launcher and a loaded save");
        Trace("CE vehicle telemetry=" + (hasCeVehicleLayout ? "enabled" : "fallback"));
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
        if (!active || !serverConnected) return;
        if (e.Key == Keys.F10) ToggleRide();
        if (e.Key == Keys.F)
        {
            Ped character = Player.Character;
            if (Exists(character) && Player.CanControlCharacter && !Exists(character.CurrentVehicle))
            {
                float distance = 36f;
                string key = NearestSharedCar(character, ref distance);
                if (key != null)
                {
                    foreach (Vehicle vehicle in World.GetVehicles(character.Position, 6f))
                        if (Exists(vehicle) && !cars.ContainsValue(vehicle) && !worldCars.ContainsValue(vehicle) &&
                            SquaredDistance(character.Position, vehicle.Position) < distance) return;
                    RequestSharedCar(key);
                }
            }
        }
        if ((e.Key == Keys.OemOpenBrackets || e.Key == Keys.OemCloseBrackets || e.Key == Keys.OemBackslash) &&
            Exists(drivingCar) && indicatorKey == 0)
        {
            indicatorKey = (int)e.Key;
            int requested = e.Key == Keys.OemOpenBrackets ? LeftIndicator :
                e.Key == Keys.OemCloseBrackets ? RightIndicator : LeftIndicator | RightIndicator;
            indicators = indicators == requested ? 0 : requested;
        }
    }

    private void ReleaseKey(object sender, GTA.KeyEventArgs e)
    {
        if (indicatorKey == (int)e.Key) indicatorKey = 0;
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
        globalWorldHost = null;
        lastSharedFrame = 0;
        nextSharedCapture = 0;
        remoteAmbientConfirmed = false;
        nextAmbientCleanup = 0;
        episode = -1;
        SetPopulationSuppressed(false);
        drivingCar = null;
        drivingOrigin = "-";
        trackedPlayerCar = null;
        pendingTake = null;
        acquiredTrafficKey = null;
        sourceTraffic.Clear();
        delegatedTraffic.Clear();
        retiredTraffic.Clear();
        vehicleSignals.Clear();
        indicators = indicatorKey = 0;
        hornHeld = false;
        tunedRide = "-";
        ClearPendingAttack();
        ClearMarkers();
    }

    private void Update(object sender, EventArgs e)
    {
        long now = clock.ElapsedMilliseconds;
        CountActiveGame(now);
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
            Trace("save ready; sending local state and waiting for server");
        }
        Ped character = Player.Character;
        if (!Exists(character))
        {
            StopSynchronization("left gameplay");
            return;
        }
        if (now < nextSample)
        {
            MoveAvatars(now);
            return;
        }
        nextSample = Math.Max(now + 8, nextSample + 1000.0 / 60);
        try
        {
            if (Exists(character))
            {
                ExpireTake(character, now);
                Vector3 p = character.Position;
                Vehicle car = character.CurrentVehicle;
                bool driving = Exists(car) && Equals(car.GetPedOnSeat(VehicleSeat.Driver), character);
                if (driving && !Equals(drivingCar, car))
                {
                    string replica = ReplicaKey(car);
                    if (replica != null)
                    {
                        TakeReplica(replica, car);
                    }
                    drivingCar = car;
                    if (!Equals(trackedPlayerCar, car)) drivingToken++;
                    trackedPlayerCar = car;
                    string nativeKey = TrafficKey(ownId, car.GetHashCode(), car.Model.Hash);
                    drivingOrigin = Equals(acquiredTraffic, car) && acquiredTrafficKey != null ? acquiredTrafficKey
                        : sourceTraffic.ContainsKey(nativeKey) ? nativeKey : "-";
                    nextRadioSample = 0;
                    indicators = 0;
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
                int flags = driving ? ((car.EngineRunning ? Engine : 0) | (car.SirenActive ? Siren : 0) |
                    (hornHeld ? HornOn : 0) | indicators) : 0;
                int steer = 0, alarm = 0;
                uint horn = 0;
                if (driving && (car.Model.isCar || car.Model.isBike))
                {
                    Vector3 speed = ReadVehicleVelocity(car);
                    Vector3 direction = car.Direction;
                    ReadRoadSignals(car, speed.X * direction.X + speed.Y * direction.Y,
                        ref flags, ref horn, out steer, out alarm, now);
                }
                int gait = driving ? 0 : MovementGait(character);
                CaptureCombat(character, now);
                int combatFlags = 1 | (character.isRagdoll ? 2 : 0) | (!character.isAlive ? 4 : 0);
                string position = string.Format(CultureInfo.InvariantCulture,
                    "{0} {20} {21} {1:F2} {2:F2} {3:F2} {4:F2} {5} {6} {7} {8} {9}{10} ~{0},{11},{12},{13},{14},{15},{22},{23},{24} !{16},{17},{18},{19}\n",
                    now, p.X, p.Y, p.Z,
                    ((driving ? car.Heading : character.Heading) % 360 + 360) % 360,
                    character.Model.Hash, driving ? car.Model.Hash : 0, driving ? drivingToken : 0, ride, worldSample,
                    driving ? CaptureCarPhysics(car) + " " +
                        (sourceStation <= (int)RadioStation.Integrity ? sourceStation : -1).ToString(CultureInfo.InvariantCulture) : "",
                    worldSampleAt, 1, flags, driving ? sourceStation : -1,
                    driving ? sourceStationName : "-", combatFlags, pendingAttack, pendingAttackKind,
                    pendingVictim, activeGameMillis, driving ? drivingOrigin : "-", steer, horn, gait);
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
        if (serverConnected && now - lastUpdate > 5000)
        {
            ResetConnection();
            Trace("server timed out; shared vehicles released");
        }
        if (sharedSource != null && now - lastSharedAt > 5000)
        {
            sharedSource = null;
            SetPopulationSuppressed(false);
            ReleaseWorldVehicles();
            UpdateWorld(null);
            Trace("shared world timed out");
        }
        UpdateLocalPopulation(character, now);
        MoveAvatars(now);
    }

    private void CountActiveGame(long now)
    {
        if (now < nextGameClockCheck || activeGameMillis >= 3600000) return;
        nextGameClockCheck = now + 100;
        bool playing = Player.isPlaying && !Game.Paused && Exists(Player.Character);
        if (!playing)
        {
            previousGameActive = false;
            return;
        }
        int time = Game.GameTime;
        int elapsed = time - previousGameClock;
        if (previousGameActive && elapsed > 0 && elapsed <= 400)
            activeGameMillis = Math.Min(3600000L, activeGameMillis + elapsed);
        previousGameActive = true;
        previousGameClock = time;
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
                int.TryParse(combat.Substring(1).Split(',')[0], out combatFlags);
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
            if (combat.StartsWith("!", StringComparison.Ordinal))
            {
                string[] hit = combat.Substring(1).Split(',');
                if (hit.Length == 6)
                {
                    uint.TryParse(hit[1], out peer.HitSequence);
                    int.TryParse(hit[2], out peer.HitKind);
                    int.TryParse(hit[3], out peer.ImpactX);
                    int.TryParse(hit[4], out peer.ImpactY);
                    if (hit[5] != "-") peer.VehicleOrigin = hit[5];
                }
            }
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
            if (avatars.ContainsKey(columns[0]) && peer.CarId == null && peer.Ride == "-" &&
                (peer.CombatFlags & 6) == 0)
            {
                PoseTrack walk;
                if (!avatarTracks.TryGetValue(columns[0], out walk))
                {
                    walk = new PoseTrack();
                    avatarTracks[columns[0]] = walk;
                }
                walk.Add(peer.Sample, clock.ElapsedMilliseconds, peer.Position, peer.Heading, null);
            }
            else avatarTracks.Remove(columns[0]);
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
                avatarTracks.Remove(id);
                vulnerableAvatars.Remove(id);
                avatarRagdolls.Remove(id);
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
        int kind, impactX, impactY;
        if (header.Length != 7 || !long.TryParse(header[2], out acknowledged) ||
            !uint.TryParse(header[3], out total) || !int.TryParse(header[4], out kind) ||
            !int.TryParse(header[5], out impactX) || !int.TryParse(header[6], out impactY)) return;
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
        if (character.isAlive && !character.isInVehicle())
        {
            if (!character.isRagdoll) character.ForceRagdoll(kind == 3 ? 1600 : 650, false);
            if (kind == 3) character.ApplyForce(new Vector3(impactX / 100f, impactY / 100f, 0.33f));
        }
        Trace("pvp damage=" + damage + " kind=" + kind);
    }

    private static void ReadMetadata(Peer peer, string metadata)
    {
        string[] fields = metadata.Substring(1).Split(',');
        long sample, worldSample;
        int flags, station;
        if ((fields.Length != 6 && fields.Length != 9) || !long.TryParse(fields[0], out sample) ||
            !long.TryParse(fields[1], out worldSample) || !int.TryParse(fields[3], out flags) ||
            !int.TryParse(fields[4], out station)) return;
        peer.Sample = sample;
        peer.WorldSample = worldSample;
        peer.VehicleFlags = flags;
        peer.Radio = station;
        peer.StationName = fields[5];
        if (fields.Length == 9)
        {
            peer.HasRoadSignals = int.TryParse(fields[6], out peer.Steering) &&
                uint.TryParse(fields[7], out peer.Horn);
            int.TryParse(fields[8], out peer.Gait);
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
            (!Equals(car, trackedPlayerCar) || SquaredDistance(Player.Character.Position, car.Position) > 10000f) &&
            !cars.ContainsValue(car) && !worldCars.ContainsValue(car);
    }

    private static string WorldRow(string kind, int id, int model, Vector3 p, float heading, int scale = 10)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6}", kind, id, model,
            (int)Math.Round(p.X * scale), (int)Math.Round(p.Y * scale), (int)Math.Round(p.Z * scale),
            (int)((heading % 360 + 360) % 360));
    }

    private void PublishSharedWorld(long sample, Ped local, Vehicle localCar, bool driving)
    {
        if (episode < 0) episode = GTA.Native.Function.Call<int>("GET_CURRENT_EPISODE");
        var frame = new StringBuilder();
        if (vehicleSignals.Count > 192)
            foreach (int handle in new List<int>(vehicleSignals.Keys))
                if (sample - vehicleSignals[handle].SampledAt > 15000) vehicleSignals.Remove(handle);
        int remaining = 29;
        frame.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} {2}\n", ownId, sample, episode);
        if (pendingTake != null && !takeMounted)
        {
            string[] take = pendingTake.Split(':');
            frame.AppendFormat(CultureInfo.InvariantCulture, "t,{0},{1},{2},{3}\n",
                take[0], take[1], take[2], take[3]);
        }
        Vehicle playerCar = driving ? localCar : trackedPlayerCar;
        if (Exists(playerCar) && SquaredDistance(local.Position, playerCar.Position) <= 10000f)
        {
            frame.Append(CaptureVehicleDetails("d", drivingToken, playerCar, sourceStation,
                driving && hornHeld, 0, 0)).Append('\n');
            remaining--;
        }
        foreach (var entry in delegatedTraffic)
        {
            Vehicle car;
            if (sample - entry.Value > 1200 || !worldCars.TryGetValue(entry.Key, out car) || !Exists(car) ||
                SquaredDistance(local.Position, car.Position) > 115f * 115f) continue;
            string[] key = entry.Key.Split(':');
            Ped driver = car.GetPedOnSeat(VehicleSeat.Driver);
            int driverModel = Exists(driver) ? driver.Model.Hash : 0;
            frame.Append(CaptureVehicleDetails("m", int.Parse(key[2], CultureInfo.InvariantCulture),
                car, -1, false, 0, driverModel)).Append(',').Append(key[0]).Append('\n');
            if (--remaining == 0) break;
        }
        if (sharedSource == ownId)
        {
            foreach (string key in new List<string>(sourceTraffic.Keys))
                if (!Exists(sourceTraffic[key])) sourceTraffic.Remove(key);
            Vector3 origin = local.Position;
            var nearbyCars = new List<KeyValuePair<float, Vehicle>>();
            foreach (Vehicle car in World.GetVehicles(origin, 145f))
                if (CanPublishCar(car, localCar))
                    nearbyCars.Add(new KeyValuePair<float, Vehicle>(SquaredDistance(origin, car.Position), car));
            nearbyCars.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < Math.Min(10, nearbyCars.Count) && remaining > 0; i++, remaining--)
            {
                Vehicle car = nearbyCars[i].Value;
                sourceTraffic[TrafficKey(ownId, car.GetHashCode(), car.Model.Hash)] = car;
                Ped driver = car.GetPedOnSeat(VehicleSeat.Driver);
                int driverModel = Exists(driver) && !avatars.ContainsValue(driver) ? driver.Model.Hash : 0;
                frame.Append(CaptureVehicleDetails("v", car.GetHashCode(), car, -1, false, 0, driverModel)).Append('\n');
            }

            var nearbyPeds = new List<KeyValuePair<float, Ped>>();
            foreach (Ped ped in World.GetPeds(origin, 145f, 60))
                if (CanPublishPed(ped, local) && !worldDrivers.ContainsValue(ped))
                    nearbyPeds.Add(new KeyValuePair<float, Ped>(SquaredDistance(origin, ped.Position), ped));
            nearbyPeds.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < Math.Min(17, nearbyPeds.Count) && remaining > 0; i++, remaining--)
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

    private string CaptureVehicleDetails(string kind, int id, Vehicle car, int station,
        bool horn, uint hornNumber, int driverModel)
    {
        Model model = car.Model;
        Quaternion q = car.RotationQuaternion;
        Vector3 velocity = ReadVehicleVelocity(car);
        Vector3 direction = car.Direction;
        float forward = velocity.X * direction.X + velocity.Y * direction.Y;
        uint colors = (uint)(car.Color.Index & 255) | ((uint)(car.FeatureColor1.Index & 255) << 8) |
            ((uint)(car.SpecularColor.Index & 255) << 16) | ((uint)(car.FeatureColor2.Index & 255) << 24);
        int flags = (car.EngineRunning ? Engine : 0) | (car.SirenActive ? Siren : 0) |
            (kind == "d" && Equals(car, drivingCar) ? indicators : 0) | (horn ? HornOn : 0);
        int steering = 0, alarm = 0;
        if (model.isCar || model.isBike)
        {
            ReadRoadSignals(car, forward, ref flags, ref hornNumber, out steering, out alarm,
                clock.ElapsedMilliseconds);
        }
        return WorldRow(kind, id, model.Hash, car.Position, car.Heading, 100) +
            string.Format(CultureInfo.InvariantCulture, ",{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16}",
                (int)Math.Round(q.X * 1000), (int)Math.Round(q.Y * 1000), (int)Math.Round(q.Z * 1000),
                (int)Math.Round(q.W * 1000), colors, flags, hornNumber, station, driverModel,
                (int)Math.Max(-4000, Math.Min(1000, car.EngineHealth)),
                Math.Max(0, Math.Min(1000, car.Health)),
                (int)Math.Max(-4000, Math.Min(1000, car.PetrolTankHealth)), TireMask(car),
                (int)Math.Round(100 * Math.Max(-150, Math.Min(150, forward))),
                (int)Math.Round(100 * Math.Max(-60, Math.Min(60, velocity.Z))), steering, alarm);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer,
        UIntPtr count, out UIntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer,
        UIntPtr count, out UIntPtr written);

    private static bool ReadGameMemory(IntPtr address, byte[] buffer)
    {
        UIntPtr read;
        return address != IntPtr.Zero && ReadProcessMemory(new IntPtr(-1), address, buffer,
            new UIntPtr((uint)buffer.Length), out read) && read.ToUInt32() == buffer.Length;
    }

    private static bool MatchesInstruction(IntPtr module, int offset, byte[] expected)
    {
        var actual = new byte[expected.Length];
        if (!ReadGameMemory(IntPtr.Add(module, offset), actual)) return false;
        for (int i = 0; i < expected.Length; i++)
            if (actual[i] != expected[i]) return false;
        return true;
    }

    private static bool MatchesVehicleLayout(IntPtr module)
    {
        // GTA IV 1.2.0.59: light renderer, steering bias, alarm duration, horn and colour.
        return IntPtr.Size == 4 &&
            MatchesInstruction(module, 0x6446A2, new byte[] { 0xF3, 0x0F, 0x10, 0x86, 0x7C, 0x10, 0, 0 }) &&
            MatchesInstruction(module, 0x7CECC5, new byte[] { 0xF3, 0x0F, 0x11, 0x80, 0x84, 0x10, 0, 0 }) &&
            MatchesInstruction(module, 0x7CE998, new byte[] { 0x66, 0x89, 0x88, 0x48, 0x0F, 0, 0 }) &&
            MatchesInstruction(module, 0x7CEF44, new byte[] { 0x89, 0x86, 0xEC, 0x12, 0, 0 }) &&
            MatchesInstruction(module, 0x7CA4DF, new byte[] { 0x0F, 0xB6, 0x90, 0x94, 0x0F, 0, 0 });
    }

    private bool ReadRoadMemory(Vehicle car, out IntPtr address)
    {
        address = hasCeVehicleLayout ? new IntPtr(((GTA.@base.iAddressableObject)car).MemoryAddress) : IntPtr.Zero;
        return address != IntPtr.Zero &&
            ReadGameMemory(IntPtr.Add(address, 0xF14), vehicleMemory) &&
            vehicleMemory[0xF94 - 0xF14] == (car.Color.Index & 255);
    }

    private void WriteRoadValue(IntPtr address, int offset, float value)
    {
        UIntPtr written;
        WriteProcessMemory(new IntPtr(-1), IntPtr.Add(address, offset), BitConverter.GetBytes(value),
            new UIntPtr(4), out written);
    }

    private void ReadRoadSignals(Vehicle car, float forward, ref int flags,
        ref uint hornNumber, out int steering, out int alarm, long now)
    {
        VehicleSignal sample;
        int handle = car.GetHashCode();
        if (!vehicleSignals.TryGetValue(handle, out sample))
        {
            sample = new VehicleSignal();
            vehicleSignals[handle] = sample;
        }
        const int start = 0xF14;
        IntPtr address;
        bool read = ReadRoadMemory(car, out address);
        float heading = car.Heading;
        long elapsed = now - sample.SampledAt;
        float yaw = (heading - sample.Heading + 540f) % 360f - 180f;
        float estimate = sample.SampledAt != 0 && elapsed >= 10 && elapsed <= 300 && Math.Abs(forward) > 1f
            ? (float)Math.Atan(47.12f * yaw / (elapsed * forward)) : 0f;
        float angle = read ? BitConverter.ToSingle(vehicleMemory, 0x1088 - start) : estimate;
        if (float.IsNaN(angle) || float.IsInfinity(angle) || Math.Abs(angle) > 1.5f) angle = estimate;
        float target = Math.Max(-1f, Math.Min(1f, angle / 0.58f));
        if (elapsed > 0) sample.Steering += (target - sample.Steering) * 0.70f;
        steering = (int)Math.Round(sample.Steering * 1000);
        sample.Heading = heading;
        sample.SampledAt = now;
        if (car.LightsOn || read && (vehicleMemory[0xF15 - start] & 1) != 0) flags |= Headlights;
        if (read)
        {
            int lamps = vehicleMemory[0xF1C - start];
            if ((lamps & 2) != 0) flags |= Hazard;
            if ((lamps & 4) != 0 && (flags & (LeftIndicator | RightIndicator)) == 0)
                flags |= LeftIndicator | RightIndicator;
            if (BitConverter.ToSingle(vehicleMemory, 0x107C - start) > 0.1f) flags |= Braking;
        }
        int timer = read ? BitConverter.ToUInt16(vehicleMemory, 0xF48 - start) : 0;
        alarm = timer == ushort.MaxValue ? 0 : Math.Min(timer, 60000);
        uint horn = read ? BitConverter.ToUInt32(vehicleMemory, 0x12EC - start) : 0;
        if (horn > 0 && horn <= 600) flags |= HornOn;
        bool sounding = (flags & HornOn) != 0;
        if (sounding && !sample.HornActive) sample.HornSequence++;
        sample.HornActive = sounding;
        hornNumber = sample.HornSequence;
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
        protectedCars.Add(acquiredTraffic);
        releasedCars.RemoveWhere(car => !Exists(car) || SquaredDistance(car.Position, character.Position) > 110f * 110f);
        protectedCars.UnionWith(releasedCars);

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
        foreach (string key in new List<string>(retiredTraffic.Keys))
            if (now >= retiredTraffic[key]) retiredTraffic.Remove(key);
        string source = header[4];
        if (sharedSource != source)
        {
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
            if (parts[2] == "#")
            {
                if (globalWorldHost != parts[0])
                {
                    globalWorldHost = parts[0];
                    Trace("global world host=" + globalWorldHost);
                }
                continue;
            }
            if (parts[2] == "@" && parts[0] == source && source != ownId)
            {
                hasAmbientFrame = true;
                continue;
            }
            string[] fields = parts[2].Split(',');
            if (fields.Length != 3 && fields.Length != 8 && fields.Length != 24) continue;
            int id, model;
            if (!int.TryParse(fields[1], out id) || !int.TryParse(fields[2], out model)) continue;
            string kind = fields[0];
            if ((kind == "take" || kind == "drop" || kind == "hide" || kind == "own" ||
                kind == "takec" || kind == "dropc" || kind == "ownc") && fields.Length == 3)
            {
                ApplyTrafficTransfer(kind, parts[0], id, model, character);
                continue;
            }
            if (kind == "d" && fields.Length == 24)
            {
                string carId = parts[0] + ":" + id.ToString(CultureInfo.InvariantCulture);
                string parkedId = string.Format(CultureInfo.InvariantCulture, "{0}:c:{1}:{2}", parts[0], id, model);
                if (parkedId == acquiredTrafficKey || retiredTraffic.ContainsKey(parkedId)) continue;
                int cx, cy, cz, yaw;
                if (int.TryParse(fields[3], out cx) && int.TryParse(fields[4], out cy) &&
                    int.TryParse(fields[5], out cz) && int.TryParse(fields[6], out yaw))
                    ApplySharedCar(carId, new Model(model), new Vector3(cx / 100f, cy / 100f, cz / 100f),
                        yaw, fields, entitySample, now, true);
                continue;
            }
            bool traffic = kind == "v" || kind == "m";
            if (!traffic && (parts[0] != source || source == ownId)) continue;
            int x, y, z, heading;
            if (!int.TryParse(fields[3], out x) || !int.TryParse(fields[4], out y) ||
                !int.TryParse(fields[5], out z) || !int.TryParse(fields[6], out heading)) continue;
            float scale = traffic ? 100f : 10f;
            var position = new Vector3(x / scale, y / scale, z / scale);
            float radius = kind == "m" ? 120f : 110f;
            if (SquaredDistance(character.Position, position) > radius * radius) continue;
            string key = traffic ? TrafficKey(parts[0], id, model) : source + ":p:" + fields[1] + ":" + fields[2];
            if (kind == "p" && fields.Length == 8)
            {
                int gait;
                if (!int.TryParse(fields[7], out gait) || gait < 0 || gait > 4) continue;
                seen.Add(key);
                ApplySharedPed(key, new Model(model), position, heading, gait, entitySample, now);
            }
            else if (traffic && fields.Length == 24 && key != acquiredTrafficKey && !retiredTraffic.ContainsKey(key))
            {
                seen.Add(key);
                ApplySharedCar(key, new Model(model), position, heading, fields, entitySample, now, false, kind == "m");
            }
        }
        remoteAmbientConfirmed = hasAmbientFrame;
        if (seen.Count > 0) lastAmbientAt = now;
        foreach (string key in new List<string>(worldPeds.Keys))
            if (CanCullWorld(key, seen, source != ownId, source, now)) RemoveWorldEntity(key);
        foreach (string key in new List<string>(worldCars.Keys))
            if (!seen.Contains(key) && CanCullTraffic(key, character, now)) RemoveWorldEntity(key);
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

    private static string TrafficKey(string source, int handle, int model)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0}:v:{1}:{2}", source, handle, model);
    }

    private static string ParkedKey(string carId, Vehicle car)
    {
        return carId.Replace(":", ":c:") + ":" + car.Model.Hash.ToString(CultureInfo.InvariantCulture);
    }

    private string ReplicaKey(Vehicle car)
    {
        foreach (var entry in worldCars)
            if (Equals(car, entry.Value)) return entry.Key;
        foreach (var entry in cars)
            if (Equals(car, entry.Value) && !occupiedCars.Contains(entry.Key)) return ParkedKey(entry.Key, car);
        return null;
    }

    private bool ResolveReplica(string key, out Vehicle car)
    {
        string[] parts = key.Split(':');
        if (parts[1] == "c") return cars.TryGetValue(parts[0] + ":" + parts[2], out car) && Exists(car);
        return worldCars.TryGetValue(key, out car) && Exists(car);
    }

    private void ApplyTrafficTransfer(string command, string source, int handle, int model, Ped character)
    {
        bool parked = command == "takec" || command == "dropc" || command == "ownc";
        string key = parked ? string.Format(CultureInfo.InvariantCulture, "{0}:c:{1}:{2}", source, handle, model)
            : TrafficKey(source, handle, model);
        if (command == "own" || command == "ownc")
        {
            retiredTraffic[key] = clock.ElapsedMilliseconds + 21000;
            if (pendingTake == key && takeMounted)
            {
                pendingTake = null;
                Trace("server confirmed shared vehicle " + key);
            }
            return;
        }
        if (command == "hide" || command == "drop" || command == "dropc")
        {
            if (command != "hide") retiredTraffic[key] = clock.ElapsedMilliseconds + 21000;
            if (command != "hide" && pendingTake == key) takeUntil = 0;
            if (source == ownId)
            {
                Vehicle original;
                if (parked && handle == drivingToken && Exists(trackedPlayerCar) &&
                    trackedPlayerCar.Model.Hash == model && !Equals(trackedPlayerCar, character.CurrentVehicle))
                {
                    original = trackedPlayerCar;
                    trackedPlayerCar = null;
                    DropSourceTraffic(key, original, character, true);
                }
                else if (sourceTraffic.TryGetValue(key, out original) && !worldCars.ContainsValue(original))
                    DropSourceTraffic(key, original, character);
            }
            return;
        }
        if (pendingTake != key || takeMounted || clock.ElapsedMilliseconds >= takeUntil ||
            !Player.CanControlCharacter || Exists(character.CurrentVehicle)) return;
        Vehicle car;
        if (!ResolveReplica(key, out car) ||
            SquaredDistance(character.Position, car.Position) > 81f) return;
        Ped driver;
        if (worldDrivers.TryGetValue(key, out driver) && Exists(driver)) driver.Delete();
        worldDrivers.Remove(key);
        if (!car.isSeatFree(VehicleSeat.Driver)) return;
        car.DoorLock = DoorLock.None;
        character.WarpIntoVehicle(car, VehicleSeat.Driver);
        if (!Equals(car.GetPedOnSeat(VehicleSeat.Driver), character)) return;
        TakeReplica(key, car);
        Trace("took shared vehicle " + key);
    }

    private void TakeReplica(string key, Vehicle car)
    {
        acquiredTraffic = car;
        acquiredTrafficKey = key;
        pendingTake = key;
        takeMounted = true;
        takeLeaving = false;
        takeUntil = clock.ElapsedMilliseconds + 5000;
        drivingOrigin = key;
        string[] parts = key.Split(':');
        if (parts[1] == "c") ForgetPlayerReplica(parts[0] + ":" + parts[2]);
        else ForgetTrafficReplica(key);
    }

    private void ExpireTake(Ped character, long now)
    {
        if (pendingTake == null || now < takeUntil || now - lastUpdate > 5000) return;
        if (takeMounted && Exists(acquiredTraffic))
        {
            if (Equals(character.CurrentVehicle, acquiredTraffic))
            {
                if (!takeLeaving) character.LeaveVehicle();
                takeLeaving = true;
                return;
            }
            if (Equals(trackedPlayerCar, acquiredTraffic)) trackedPlayerCar = null;
            acquiredTraffic.Delete();
            acquiredTraffic = null;
            acquiredTrafficKey = null;
        }
        Trace("shared vehicle unavailable " + pendingTake);
        Game.DisplayText("Vehicle unavailable");
        pendingTake = null;
    }

    private void ForgetTrafficReplica(string key)
    {
        Ped driver;
        if (worldDrivers.TryGetValue(key, out driver) && Exists(driver)) driver.Delete();
        worldDrivers.Remove(key);
        worldCars.Remove(key);
        delegatedTraffic.Remove(key);
        worldTracks.Remove(key);
        appliedDamage.Remove(key);
        vehicleDetails.Remove(key);
    }

    private void DropSourceTraffic(string key, Vehicle car, Ped character, bool parked = false)
    {
        if (!Exists(car))
        {
            sourceTraffic.Remove(key);
            return;
        }
        if (car.isRequiredForMission || Equals(car, character.CurrentVehicle) ||
            (!parked && Equals(car, Player.LastVehicle)) || cars.ContainsValue(car) || worldCars.ContainsValue(car)) return;
        var occupants = new List<Ped>();
        Group group = Player.Group;
        for (int seat = -1; seat < car.PassengerSeats; seat++)
        {
            Ped ped = car.GetPedOnSeat((VehicleSeat)seat);
            if (!Exists(ped)) continue;
            if (Equals(ped, character) || ped.isRequiredForMission || avatars.ContainsValue(ped) ||
                (Exists(group) && group.isMember(ped))) return;
            occupants.Add(ped);
        }
        foreach (Ped ped in occupants)
            if (Exists(ped)) ped.Delete();
        car.Delete();
        sourceTraffic.Remove(key);
        Trace("released shared vehicle " + key);
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
        long sample, long now, bool playerVehicle = false, bool simulate = false)
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
        Dictionary<string, Vehicle> vehicles = playerVehicle ? cars : worldCars;
        bool wasSimulating = delegatedTraffic.ContainsKey(key);
        if (!simulate && delegatedTraffic.Remove(key))
        {
            Ped previousDriver;
            if (worldDrivers.TryGetValue(key, out previousDriver) && Exists(previousDriver))
                previousDriver.Task.ClearAll();
        }
        Vehicle car;
        if (!vehicles.TryGetValue(key, out car) || !Exists(car))
        {
            Vehicle original;
            if (simulate && sourceTraffic.TryGetValue(key, out original) && Exists(original) &&
                SquaredDistance(original.Position, position) < 100f && !original.isRequiredForMission &&
                !Equals(original, Player.Character.CurrentVehicle) && !Equals(original, Player.LastVehicle))
            {
                car = original;
                Ped originalDriver = car.GetPedOnSeat(VehicleSeat.Driver);
                if (Exists(originalDriver) && !originalDriver.isRequiredForMission)
                    worldDrivers[key] = originalDriver;
            }
            else
            {
                if (!CanSpawnWorldModel(model, now)) return;
                car = World.CreateVehicle(model, position);
            }
            if (!Exists(car)) return;
            wasSimulating = false;
            car.RotationQuaternion = rotation;
            EnableVehiclePhysics(car);
            car.DoorLock = playerVehicle && occupiedCars.Contains(key) ? DoorLock.ImpossibleToOpen : DoorLock.None;
            vehicles[key] = car;
            vehicleDetails.Remove(key);
            appliedDamage.Remove(key);
        }
        QueueWorldPose(key, sample, now, position, heading, rotation, playerVehicle);
        if (!simulate || !wasSimulating)
        {
            ApplyVehicleDetails(key, car, fields, sample, now);
            ApplyDamage(key, car, new CarPhysics { Orientation = rotation, EngineHealth = engine,
                BodyHealth = body, TankHealth = tank, TireMask = tires });
        }
        if (simulate) delegatedTraffic[key] = now;
        if (playerVehicle) return;
        // Once delegated, the running game owns its driver's task and suspension.
        if (simulate && wasSimulating && worldDrivers.ContainsKey(key) && Exists(worldDrivers[key])) return;
        Ped actor;
        if (worldDrivers.TryGetValue(key, out actor) &&
            (!Exists(actor) || actor.Model.Hash != driver || !Equals(actor.CurrentVehicle, car)))
        {
            if (Exists(actor)) actor.Delete();
            worldDrivers.Remove(key);
        }
        if (driver == 0) return;
        if (worldDrivers.TryGetValue(key, out actor) && Exists(actor))
        {
            if (simulate && !wasSimulating) StartTrafficCruise(actor, car);
            return;
        }
        var driverModel = new Model(driver);
        if (!driverModel.isPed || !CanSpawnWorldModel(driverModel, now)) return;
        actor = car.CreatePedOnSeat(VehicleSeat.Driver, driverModel);
        if (!Exists(actor)) return;
        actor.Invincible = true;
        actor.BlockPermanentEvents = true;
        worldDrivers[key] = actor;
        if (simulate) StartTrafficCruise(actor, car);
    }

    private static void StartTrafficCruise(Ped driver, Vehicle car)
    {
        // The IV native takes metres per second despite the managed parameter name.
        driver.Task.CruiseWithVehicle(car, 10f, true);
    }

    private void ApplyVehicleDetails(string key, Vehicle car, string[] fields, long sample, long now)
    {
        uint colors, horn;
        int flags, station, forward, vertical, steer, alarm;
        if (!uint.TryParse(fields[11], out colors) || !int.TryParse(fields[12], out flags) ||
            !uint.TryParse(fields[13], out horn) || !int.TryParse(fields[14], out station) ||
            !int.TryParse(fields[20], out forward) || !int.TryParse(fields[21], out vertical) ||
            !int.TryParse(fields[22], out steer) || !int.TryParse(fields[23], out alarm)) return;
        VehicleDetails old;
        if (!vehicleDetails.TryGetValue(key, out old))
            vehicleDetails[key] = old = new VehicleDetails();
        if (sample <= old.MotionSample) return;
        if (old.MotionSample < 0 || old.Colors != colors)
        {
            GTA.Native.Function.Call("CHANGE_CAR_COLOUR", car, (int)(colors & 255), (int)(colors >> 8 & 255));
            GTA.Native.Function.Call("SET_EXTRA_CAR_COLOURS", car, (int)(colors >> 16 & 255), (int)(colors >> 24));
        }
        if (car.Model.isCar || car.Model.isBike)
        {
            if (alarm > 0 && Math.Abs(old.AlarmEnds - (now + alarm)) > 650 ||
                alarm == 0 && old.AlarmEnds > now)
                GTA.Native.Function.Call("SET_VEH_ALARM_DURATION", car, alarm);
        }
        old.Colors = colors;
        old.Station = station;
        old.MotionAt = now;
        old.MotionSample = sample;
        old.ForwardSpeed = forward / 100f;
        old.VerticalSpeed = vertical / 100f;
        old.AlarmEnds = now + alarm;
        ApplyRoadSignals(old, car, sample, now, flags, steer, horn);
    }

    private void ApplyRoadSignals(VehicleDetails state, Vehicle car, long sample, long now,
        int flags, int steer, uint horn)
    {
        if (sample <= state.SignalSample) return;
        int changed = state.SignalSample < 0 ? 511 : state.Flags ^ flags;
        if ((changed & Engine) != 0) car.EngineRunning = (flags & Engine) != 0;
        if ((changed & Siren) != 0) car.SirenActive = (flags & Siren) != 0;
        if ((changed & KnownRoadFlags) != 0) state.NextLights = 0;
        if ((horn != state.Horn && (horn != 0 || (flags & HornOn) != 0)) ||
            ((flags & HornOn) != 0 && now >= state.LastHorn + 120))
        {
            car.SoundHorn(175);
            state.LastHorn = now;
        }
        state.Flags = flags;
        state.Horn = horn;
        state.Steering = steer / 1000f;
        state.SignalSample = sample;
        state.SignalAt = now;
    }

    private void DriveReplicaControls(Vehicle car, VehicleDetails state, long now)
    {
        if (state == null || now - state.SignalAt > 750 || !(car.Model.isCar || car.Model.isBike)) return;
        int indicators = state.Flags & (LeftIndicator | RightIndicator);
        if (now >= state.NextLights)
        {
            GTA.Native.Function.Call("FORCE_CAR_LIGHTS", car, (state.Flags & Headlights) != 0 ? 2 : 1);
            GTA.Native.Function.Call("SET_VEH_HAZARDLIGHTS", car, (state.Flags & Hazard) != 0);
            GTA.Native.Function.Call("SET_VEH_INDICATORLIGHTS", car,
                indicators == (LeftIndicator | RightIndicator) && (state.Flags & Hazard) == 0);
            state.NextLights = now + 120;
        }
        IntPtr address;
        if (ReadRoadMemory(car, out address))
        {
            // Bias influences AI. The angle and pedal drive the displayed wheels and brake lamps.
            WriteRoadValue(address, 0x1088, state.Steering * 0.58f);
            WriteRoadValue(address, 0x107C, (state.Flags & Braking) != 0 ? 1f : 0f);
        }
        else GTA.Native.Function.Call("SET_VEHICLE_STEER_BIAS", car, state.Steering);
        if ((state.Flags & Hazard) == 0 && indicators != (LeftIndicator | RightIndicator))
            DrawTurnIndicators(car, indicators, now);
    }

    private void DrawTurnIndicators(Vehicle car, int indicators, long now)
    {
        if (indicators == 0 || now % 720 >= 370) return;
        Model model = car.Model;
        Vector3[] offsets;
        if (!lampPositions.TryGetValue(model.Hash, out offsets))
        {
            Vector3 min = new Vector3(), max = new Vector3();
            model.GetDimensions(ref min, ref max);
            float x = Math.Min(1.35f, Math.Max(0.18f, max.X * 0.87f));
            float z = min.Z + (max.Z - min.Z) * 0.44f;
            offsets = new[] { new Vector3(-x, max.Y - 0.13f, z), new Vector3(-x, min.Y + 0.13f, z),
                new Vector3(x, max.Y - 0.13f, z), new Vector3(x, min.Y + 0.13f, z) };
            lampPositions[model.Hash] = offsets;
        }
        for (int i = 0; i < offsets.Length; i++)
            if ((indicators & (i < 2 ? LeftIndicator : RightIndicator)) != 0)
            {
                Vector3 lamp = car.GetOffsetPosition(offsets[i]);
                GTA.Native.Function.Call("DRAW_CORONA", lamp.X, lamp.Y, lamp.Z, 0.055f, 0, 0, 255, 140, 15);
            }
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
        delegatedTraffic.Remove(key);
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

    private bool CanCullTraffic(string key, Ped character, long now)
    {
        Vehicle car = worldCars[key];
        PoseTrack track;
        return !Exists(car) || (!Equals(car, character.CurrentVehicle) &&
            (!worldTracks.TryGetValue(key, out track) || now - track.LastSeen > 3100));
    }

    private void QueueWorldPose(string key, long sample, long now, Vector3 position, float heading, Quaternion? orientation,
        bool playerVehicle = false)
    {
        Dictionary<string, PoseTrack> tracks = playerVehicle ? carTracks : worldTracks;
        PoseTrack track;
        if (!tracks.TryGetValue(key, out track))
        {
            track = new PoseTrack();
            tracks[key] = track;
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
            avatarTracks.Remove(id);
            vulnerableAvatars.Remove(id);
            avatarRagdolls.Remove(id);
            return;
        }
        if ((Exists(avatar) && avatar.isAlive) || clock.ElapsedMilliseconds < nextAvatarSpawn) return;
        if (Exists(avatar)) avatar.Delete();
        vulnerableAvatars.Remove(id);
        avatarRagdolls.Remove(id);
        avatarGaits.Remove(id);
        avatarTracks.Remove(id);
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

    private void MoveAvatars(long now)
    {
        if (Exists(drivingCar)) DrawTurnIndicators(drivingCar, indicators, now);
        foreach (var entry in worldCars)
        {
            Vehicle car = entry.Value;
            if (!Exists(car)) continue;
            long delegatedUntil;
            if (delegatedTraffic.TryGetValue(entry.Key, out delegatedUntil) && now - delegatedUntil <= 1200)
            {
                if (car.Model.isCar && car.Speed > 21f) car.Speed = 18f;
                continue;
            }
            SetCarPose(entry.Key, car, worldTracks[entry.Key].Read(now - 140), now, true);
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
            if (Exists(entry.Value)) SetCarPose(entry.Key, entry.Value, carTracks[entry.Key].Read(now - 85), now);

        foreach (var entry in avatars)
        {
            Ped avatar = entry.Value;
            Peer peer;
            if (!Exists(avatar) || !peers.TryGetValue(entry.Key, out peer)) continue;
            ReplicaRagdoll ragdoll;
            avatarRagdolls.TryGetValue(entry.Key, out ragdoll);
            if ((peer.CombatFlags & 6) != 0 || ragdoll != null && now < ragdoll.HoldUntil)
            {
                if (Exists(avatar.CurrentVehicle)) avatar.LeaveVehicle();
                else UpdateRemoteRagdoll(entry.Key, avatar, peer, now);
                continue;
            }
            if (ragdoll != null) ragdoll.HoldUntil = 0;
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
            PoseTrack walking;
            Pose pose = avatarTracks.TryGetValue(entry.Key, out walking)
                ? walking.Read(now - 75, true) : new Pose { Position = peer.Position, Heading = peer.Heading };
            if (SquaredDistance(current, pose.Position) > 0.0001f)
                SetPedPosition(avatar, pose.Position);
            avatar.Heading = pose.Heading;
        }
    }

    private void UpdateRemoteRagdoll(string id, Ped avatar, Peer peer, long now)
    {
        ReplicaRagdoll state;
        if (!avatarRagdolls.TryGetValue(id, out state))
        {
            state = new ReplicaRagdoll();
            avatarRagdolls[id] = state;
        }
        bool newHit = peer.HitSequence != 0 && peer.HitSequence != state.HitSequence;
        bool carImpact = newHit && peer.HitKind == 3;
        avatar.FreezePosition = false;
        if (state.HoldUntil == 0 || newHit)
        {
            state.HitSequence = peer.HitSequence;
            state.HoldUntil = now + (carImpact ? 1600 : 550);
            state.RetryAt = now + 180;
            avatar.Task.ClearAll();
            avatar.ForceRagdoll(carImpact ? 1600 : 950, false);
            if (carImpact)
            {
                avatar.ApplyForce(new Vector3(peer.ImpactX / 100f, peer.ImpactY / 100f, 0.33f));
                Trace("remote ragdoll after vehicle impact player=" + id);
            }
        }
        else if (!avatar.isRagdoll && now >= state.RetryAt)
        {
            avatar.ForceRagdoll(750, false);
            state.RetryAt = now + 325;
        }
        if (SquaredDistance(avatar.Position, peer.Position) > 16f)
            SetPedPosition(avatar, peer.Position);
    }

    private static float BlendHeading(float from, float to, float step)
    {
        var delta = (to - from + 540f) % 360f - 180f;
        return (from + delta * step + 360f) % 360f;
    }

    private static int MovementGait(Ped ped)
    {
        if (ped.isRagdoll) return 4;
        Vector3 velocity = ped.Velocity;
        float speed = velocity.X * velocity.X + velocity.Y * velocity.Y;
        return speed > 28f ? 3 : speed > 6f ? 2 : speed > 0.16f ? 1 : 0;
    }

    private void UpdateAvatarMotion(string id, Peer peer)
    {
        Ped avatar;
        ReplicaRagdoll ragdoll;
        if (!avatars.TryGetValue(id, out avatar) || !Exists(avatar) ||
            (avatarRagdolls.TryGetValue(id, out ragdoll) && clock.ElapsedMilliseconds < ragdoll.HoldUntil) ||
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
        int motion = peer.Gait >= 0 && peer.Gait <= 3 ? peer.Gait : gait.Speed >= (gait.Clip == 3 ? 4.7f : 5.4f) ? 3
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
        occupiedCars.Clear();
        foreach (Peer peer in peers.Values)
        {
            if (peer.CarId == null || !Exists(character) ||
                (ride != peer.CarId && SquaredDistance(character.Position, peer.Position) > 10000f)) continue;
            seen.Add(peer.CarId);
            occupiedCars.Add(peer.CarId);
            Vehicle car;
            bool existing = cars.TryGetValue(peer.CarId, out car) && Exists(car);
            Vehicle former;
            if (peer.VehicleOrigin != null)
            {
                retiredTraffic[peer.VehicleOrigin] = clock.ElapsedMilliseconds + 21000;
                if (ResolveReplica(peer.VehicleOrigin, out former) && !Equals(former, car))
                {
                    string[] origin = peer.VehicleOrigin.Split(':');
                    if (origin[1] == "c") ForgetPlayerReplica(origin[0] + ":" + origin[2]);
                    else ForgetTrafficReplica(peer.VehicleOrigin);
                    // Keep the entity the player saw before the transfer.
                    if (existing && !Equals(character.CurrentVehicle, car)) car.Delete();
                    if (!existing || !Exists(car)) { car = former; existing = false; }
                    else former.Delete();
                }
            }
            if (!existing)
            {
                Model model = new Model(peer.CarModel);
                if (!model.isVehicle) continue;
                if (!Exists(car))
                {
                    Trace("creating remote player vehicle " + model.Hash);
                    car = World.CreateVehicle(model, peer.Position);
                }
                if (!Exists(car)) continue;
                if (peer.Physics != null) car.RotationQuaternion = peer.Physics.Orientation;
                else car.Heading = peer.Heading;
                EnableVehiclePhysics(car);
                car.DoorLock = DoorLock.ImpossibleToOpen;
                cars[peer.CarId] = car;
                appliedDamage.Remove(peer.CarId);
                appliedVehicleFlags.Remove(peer.CarId);
                vehicleDetails.Remove(peer.CarId);
                carTracks[peer.CarId] = new PoseTrack();
            }
            car.DoorLock = DoorLock.ImpossibleToOpen;
            Quaternion? orientation = peer.Physics == null ? (Quaternion?)null : peer.Physics.Orientation;
            carTracks[peer.CarId].Add(peer.Sample, clock.ElapsedMilliseconds, peer.Position, peer.Heading, orientation);
            if (peer.Physics != null) ApplyDamage(peer.CarId, car, peer.Physics);
            if (peer.HasRoadSignals)
            {
                VehicleDetails state;
                if (!vehicleDetails.TryGetValue(peer.CarId, out state))
                    vehicleDetails[peer.CarId] = state = new VehicleDetails();
                ApplyRoadSignals(state, car, peer.Sample, clock.ElapsedMilliseconds, peer.VehicleFlags,
                    peer.Steering, peer.Horn);
            }
            else
            {
                int priorFlags;
                if (peer.VehicleFlags >= 0 &&
                (!appliedVehicleFlags.TryGetValue(peer.CarId, out priorFlags) || priorFlags != peer.VehicleFlags))
                {
                    car.EngineRunning = (peer.VehicleFlags & Engine) != 0;
                    car.SirenActive = (peer.VehicleFlags & Siren) != 0;
                    appliedVehicleFlags[peer.CarId] = peer.VehicleFlags;
                }
            }
        }
        foreach (string id in new List<string>(cars.Keys))
        {
            if (seen.Contains(id)) continue;
            PoseTrack track;
            Vehicle car = cars[id];
            if (!Exists(car)) { ForgetPlayerReplica(id); continue; }
            string owner = id.Split(':')[0];
            bool retired = retiredTraffic.ContainsKey(ParkedKey(id, car));
            if (retired || (carTracks.TryGetValue(id, out track) &&
                clock.ElapsedMilliseconds - track.LastSeen > 3000))
                RemoveCar(id, !retired && !peers.ContainsKey(owner) && Exists(character) &&
                    SquaredDistance(character.Position, car.Position) < 8100f);
            else car.DoorLock = DoorLock.None;
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
            if (!occupiedCars.Contains(entry.Key) || !Exists(entry.Value) || WinningPassenger(entry.Key) != null ||
                !entry.Value.isSeatFree(VehicleSeat.RightFront)) continue;
            float candidate = SquaredDistance(character.Position, entry.Value.Position);
            if (candidate >= distance) continue;
            distance = candidate;
            nearest = entry.Key;
        }
        string traffic = NearestSharedCar(character, ref distance);
        if (traffic != null)
        {
            RequestSharedCar(traffic);
            return;
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

    private string NearestSharedCar(Ped character, ref float distance)
    {
        string nearest = null;
        foreach (var entry in worldCars)
        {
            if (!Exists(entry.Value) || retiredTraffic.ContainsKey(entry.Key)) continue;
            float candidate = SquaredDistance(character.Position, entry.Value.Position);
            if (candidate >= distance) continue;
            distance = candidate;
            nearest = entry.Key;
        }
        foreach (var entry in cars)
        {
            if (!Exists(entry.Value) || occupiedCars.Contains(entry.Key)) continue;
            string parked = ParkedKey(entry.Key, entry.Value);
            if (retiredTraffic.ContainsKey(parked)) continue;
            float candidate = SquaredDistance(character.Position, entry.Value.Position);
            if (candidate >= distance) continue;
            distance = candidate;
            nearest = parked;
        }
        return nearest;
    }

    private void RequestSharedCar(string key)
    {
        long now = clock.ElapsedMilliseconds;
        if (pendingTake != null && now < takeUntil) return;
        pendingTake = key;
        takeUntil = now + 3000;
        takeMounted = false;
        Trace("requested shared vehicle " + key);
    }

    private void RemoveCar(string id, bool keep = false)
    {
        Vehicle car = cars[id];
        if (ride == id)
        {
            Ped character = Player.Character;
            if (!keep && Exists(character) && Equals(character.CurrentVehicle, car)) character.LeaveVehicle();
            ride = "-";
        }
        if (keep) ReleaseReplica(car);
        else if (Exists(car)) car.Delete();
        ForgetPlayerReplica(id);
    }

    private void ForgetPlayerReplica(string id)
    {
        cars.Remove(id);
        occupiedCars.Remove(id);
        carTracks.Remove(id);
        appliedDamage.Remove(id);
        appliedVehicleFlags.Remove(id);
        vehicleDetails.Remove(id);
    }

    private void ReleaseReplica(Vehicle car)
    {
        if (!Exists(car)) return;
        EnableVehiclePhysics(car);
        car.DoorLock = DoorLock.None;
        if (car.Model.isCar || car.Model.isBike)
        {
            GTA.Native.Function.Call("FORCE_CAR_LIGHTS", car, 0);
            GTA.Native.Function.Call("SET_VEH_INDICATORLIGHTS", car, false);
        }
        car.NoLongerNeeded();
        releasedCars.Add(car);
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

    private static Vector3 Lerp(Vector3 current, Vector3 target, float step)
    {
        return new Vector3(current.X + (target.X - current.X) * step,
            current.Y + (target.Y - current.Y) * step, current.Z + (target.Z - current.Z) * step);
    }

    private static Vector3 ReadVehicleVelocity(Vehicle car)
    {
        GTA.Native.Pointer velocity = typeof(Vector3);
        GTA.Native.Function.Call("GET_CAR_SPEED_VECTOR", car, velocity, false);
        return velocity;
    }

    private static void EnableVehiclePhysics(Vehicle car)
    {
        car.FreezePosition = false;
        GTA.Native.Function.Call("SET_CAR_COLLISION", car, true);
    }

    private void SetCarPose(string key, Vehicle car, Pose pose, long now, bool trafficReplica = false)
    {
        VehicleDetails detail;
        vehicleDetails.TryGetValue(key, out detail);
        DriveReplicaControls(car, detail, now);
        bool currentMotion = detail != null && detail.MotionSample >= 0 && now - detail.MotionAt < 450;
        Vector3 actual = car.Position;
        Vector3 target = pose.Position;
        float error = SquaredDistance(actual, target);
        bool snap = currentMotion && (error > 81f || Math.Abs(actual.Z - target.Z) > 3f);
        if (snap) GTA.Native.Function.Call("SET_CAR_COORDINATES_NO_OFFSET", car, target.X, target.Y, target.Z);
        if (pose.Orientation.HasValue)
        {
            Quaternion local = car.RotationQuaternion;
            Quaternion wanted = pose.Orientation.Value;
            float dot = local.X * wanted.X + local.Y * wanted.Y + local.Z * wanted.Z + local.W * wanted.W;
            if (snap || Math.Abs(dot) < 0.9997f)
                car.RotationQuaternion = snap ? wanted : BlendOrientation(local, wanted, 0.20f);
        }
        else if (snap || Math.Abs(BlendHeading(car.Heading, pose.Heading, 1f) - car.Heading) > 4f)
            car.Heading = BlendHeading(car.Heading, pose.Heading, snap ? 1f : 0.18f);

        if (!currentMotion || now < detail.NextPhysics || !(car.Model.isCar || car.Model.isBike)) return;
        detail.NextPhysics = now + 55;
        Vector3 velocity = ReadVehicleVelocity(car);
        Vector3 direction = car.Direction;
        if (trafficReplica)
        {
            float desired = Math.Max(-22f, Math.Min(22f, detail.ForwardSpeed));
            float measured = car.Speed;
            if (measured > Math.Abs(desired) + 2.5f)
                car.Speed = desired;
            else if (Math.Abs(desired) > 0.5f && measured + 0.6f < Math.Abs(desired))
            {
                float push = Math.Sign(desired) * Math.Min(0.18f, (Math.Abs(desired) - measured) * 0.075f);
                car.ApplyForce(new Vector3(direction.X * push, direction.Y * push, 0f));
            }
            if (!snap && error > 2.56f && Math.Abs(actual.Z - target.Z) < 1f)
            {
                Vector3 corrected = Lerp(actual, new Vector3(target.X, target.Y, actual.Z), 0.10f);
                GTA.Native.Function.Call("SET_CAR_COORDINATES_NO_OFFSET", car, corrected.X, corrected.Y, corrected.Z);
            }
            return;
        }
        float x = (direction.X * detail.ForwardSpeed + (target.X - actual.X) * 1.25f - velocity.X) * 0.30f;
        float y = (direction.Y * detail.ForwardSpeed + (target.Y - actual.Y) * 1.25f - velocity.Y) * 0.30f;
        float horizontal = (float)Math.Sqrt(x * x + y * y);
        float scale = horizontal > 1.2f ? 1.2f / horizontal : 1f;
        // Leave the suspension to the engine during normal wheel contact.
        float z = Math.Abs(detail.VerticalSpeed) > 1.1f && Math.Abs(target.Z - actual.Z) > 0.45f
            ? Math.Max(-0.10f, Math.Min(0.10f, (detail.VerticalSpeed - velocity.Z) * 0.10f)) : 0f;
        if (horizontal > 0.08f || z != 0f)
            car.ApplyForce(new Vector3(x * scale, y * scale, z));
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
        avatarTracks.Clear();
        vulnerableAvatars.Clear();
        avatarRagdolls.Clear();
        foreach (string id in new List<string>(cars.Keys)) RemoveCar(id, true);
        ReleaseWorldVehicles();
        peers.Clear();
        UpdateWorld(null);
        ResetWorldSource();
    }

    private void ReleaseWorldVehicles()
    {
        foreach (string key in new List<string>(worldCars.Keys))
        {
            Vehicle car = worldCars[key];
            Ped driver;
            if (worldDrivers.TryGetValue(key, out driver) && Exists(driver) && Exists(car))
            {
                driver.Invincible = false;
                driver.BlockPermanentEvents = false;
                StartTrafficCruise(driver, car);
                driver.NoLongerNeeded();
                worldDrivers.Remove(key);
            }
            ForgetTrafficReplica(key);
            ReleaseReplica(car);
        }
    }
}
