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

public sealed partial class LibertyFriends : Script
{
    private sealed class Peer
    {
        public Vector3 Position;
        public float Heading;
        public int PedModel, CarModel, CarHandle;
        public string Ride = "-";
        public string CarId;
        public CarPhysics Physics;
        public int Radio = -1;
        public string StationName = "-";
        public int VehicleFlags = -1;
        public int Steering, Gait = -1;
        public uint Horn;
        public bool HasRoadSignals;
        public long Sample = -1;
        public Vector3? Velocity;
        public int CombatFlags;
        public uint HitSequence;
        public int HitKind, ImpactX, ImpactY;
        public string VehicleOrigin;
        public uint Animation;
        public string Suspension = "-";
    }

    private sealed class ReplicaRagdoll
    {
        public uint HitSequence;
        public long HoldUntil, RetryAt;
        public bool Active;
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
        public long LastHorn, MotionSample = -1, SignalAt, SignalSample = -1, NextLights, NextControls;
        public float Steering;
        public long AlarmEnds;
    }

    private sealed class VehicleSignal
    {
        public uint HornSequence;
        public bool HornActive;
        public float Heading, Steering;
        public long SampledAt;
        public long PositionAt;
        public Vector3 Position, Velocity;
        public long DetailsUntil;
        public long SuspensionAt;
        public string Suspension = "-";
        public int Model, EngineHealth, BodyHealth, TankHealth, Tires;
        public uint Colors;
    }

    private sealed class AvatarGait
    {
        public Vector3 Sample;
        public long SampleAt;
        public long CheckAt;
        public float Speed;
        public int Clip = -1;
        public int Weapon = -1, ActionClip, ActionSerial;
        public long ActionCheckAt;
        public bool ActionUpper;
        public AnimationSet Movement;
        public string MovementClip;
    }

    private sealed class Pose
    {
        public long Sample, At;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Heading;
        public Quaternion? Orientation;
    }

    private sealed class PoseTrack
    {
        private readonly List<Pose> samples = new List<Pose>(24);
        private double sampleOffset;
        private long lastArrival;
        public long LastSeen, AppliedAt, NextIdleCheck, NextWheelSpeed;
        public long MissingSince, LastOccupied;
        public Vector3 Presented;
        public Quaternion? PresentedOrientation;
        public IntPtr Wheels;
        public float[] WheelRadii, WheelPhases;
        public float[] WheelTravel;
        private string suspension = "-";
        public byte[] Suspension;

        public void SetSuspension(string value)
        {
            if (value == suspension) return;
            suspension = value;
            if (value.Length != 12)
            {
                Suspension = null;
                return;
            }
            if (Suspension == null) Suspension = new byte[6];
            for (int i = 0; i < Suspension.Length; ++i)
            {
                byte travel;
                Suspension[i] = byte.TryParse(value.Substring(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out travel) ? travel : (byte)255;
            }
        }

        public void Add(long sample, long now, Vector3 position, float heading, Quaternion? rotation, Vector3? speed)
        {
            LastSeen = now;
            Pose previous = samples.Count == 0 ? null : samples[samples.Count - 1];
            if (sample < 0) sample = now;
            if (previous != null && sample <= previous.Sample) return;
            if (previous != null && sample - previous.Sample > 1800)
            {
                samples.Clear();
                previous = null;
            }
            // Recover the sender's clock from the quickest arrivals. Bursts do not become motion.
            double observation = now - sample;
            if (samples.Count != 0)
                sampleOffset = Math.Min(observation, sampleOffset + (now - lastArrival) / 250.0);
            else sampleOffset = observation;
            lastArrival = now;
            long at = Math.Min(now, sample + (long)sampleOffset);
            if (previous != null) at = Math.Max(previous.At + 1, at);
            Vector3 velocity = speed.GetValueOrDefault();
            if (previous != null && sample - previous.Sample >= 8)
            {
                Pose anchor = previous;
                for (int i = samples.Count - 2; i >= 0 && sample - anchor.Sample < 45; i--)
                    anchor = samples[i];
                Vector3 observed = VelocityBetween(anchor.Position, position, sample - anchor.Sample,
                    rotation.HasValue ? 120f : 12f);
                float tolerance = rotation.HasValue ? 9f : 5f;
                if (!speed.HasValue || SquaredDistance(velocity, observed) > tolerance * tolerance)
                    velocity = observed;
            }
            samples.Add(new Pose { Sample = sample, At = at, Position = position, Velocity = velocity,
                Heading = heading, Orientation = rotation });
            if (samples.Count > 24) samples.RemoveAt(0);
        }

        public Pose Read(long time, int extrapolation)
        {
            while (samples.Count > 1 && samples[1].At <= time) samples.RemoveAt(0);
            Pose first = samples[0];
            if (time <= first.At) return first;
            if (samples.Count == 1)
            {
                float lead = Math.Min(extrapolation, time - first.At) / 1000f;
                return new Pose {
                    Position = new Vector3(first.Position.X + first.Velocity.X * lead,
                        first.Position.Y + first.Velocity.Y * lead, first.Position.Z + first.Velocity.Z * lead),
                    Velocity = time - first.At < extrapolation ? first.Velocity : new Vector3(),
                    Heading = first.Heading, Orientation = first.Orientation,
                };
            }
            Pose next = samples[1];
            float t = (float)(time - first.At) / Math.Max(1, next.At - first.At);
            return new Pose { Position = Lerp(first.Position, next.Position, t),
                Velocity = Lerp(first.Velocity, next.Velocity, t),
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
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetWheelSteering(IntPtr wheel, float radians);
    private readonly SetWheelSteering setWheelSteering;
    private readonly byte[] wheelLayout = new byte[8];
    private readonly byte[] wheelSurface = new byte[0x78];
    private readonly byte[] suspensionSample = new byte[4];
    private readonly float[] wheelPhaseValue = new float[1];
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
    private int drivingToken;
    private long nextAvatarSpawn, nextNikoReport;
    private long nextMapUpdate;
    private DateTime lastPeerWrite, lastWorldWrite;
    private string sharedSource;
    private string globalWorldHost;
    private long lastSharedFrame, lastSharedAt, lastAmbientAt, nextSharedCapture, nextSharedSpawn, nextAmbientCleanup;
    private long nextPopulationCheck, nextAmbientDiscovery;
    private Vector3 lastAmbientDiscoveryPosition;
    private const int AmbientCarLimit = 28;
    private const int AmbientPedLimit = 17;
    private readonly List<Vehicle> ambientCars = new List<Vehicle>(AmbientCarLimit);
    private readonly List<Ped> ambientPeds = new List<Ped>(AmbientPedLimit);
    private int episode = -1;
    private bool suppressingPopulation, remoteAmbientConfirmed;
    private string pendingTake;
    private long takeUntil;
    private bool takeMounted, takeLeaving;
    private Vehicle acquiredTraffic;
    private string acquiredTrafficKey;
    private readonly Dictionary<string, Vehicle> sourceTraffic = new Dictionary<string, Vehicle>();
    private readonly HashSet<int> publishedTraffic = new HashSet<int>();
    private readonly Dictionary<string, long> delegatedTraffic = new Dictionary<string, long>();
    private readonly Dictionary<string, long> retiredTraffic = new Dictionary<string, long>();
    private readonly Dictionary<string, long> pendingVehicleCleanup = new Dictionary<string, long>();
    private readonly HashSet<string> occupiedCars = new HashSet<string>();
    private readonly HashSet<Vehicle> releasedCars = new HashSet<Vehicle>();
    private readonly Dictionary<string, Vehicle> releasedVehicleKeys = new Dictionary<string, Vehicle>();
    private bool hornHeld;
    private readonly Dictionary<string, Ped> worldPeds = new Dictionary<string, Ped>();
    private readonly Dictionary<string, Vehicle> worldCars = new Dictionary<string, Vehicle>();
    private readonly Dictionary<string, PoseTrack> worldTracks = new Dictionary<string, PoseTrack>();
    private readonly Dictionary<string, Ped> worldDrivers = new Dictionary<string, Ped>();
    private readonly Dictionary<string, VehicleDetails> vehicleDetails = new Dictionary<string, VehicleDetails>();
    private readonly Dictionary<string, int> worldGaits = new Dictionary<string, int>();
    private readonly Dictionary<int, long> requestedModels = new Dictionary<int, long>();
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
    private long nextDamageSample;
    private string damageSample;
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
        if (hasCeVehicleLayout && MatchesWheelLayout(module.BaseAddress))
            setWheelSteering = (SetWheelSteering)Marshal.GetDelegateForFunctionPointer(
                IntPtr.Add(module.BaseAddress, 0x6D05A0), typeof(SetWheelSteering));
        Interval = 0;
        KeyDown += HandleKey;
        KeyUp += ReleaseKey;
        Tick += Update;
        Trace("mod loaded; waiting for launcher and a loaded save");
        Trace("CE vehicle telemetry=" + (hasCeVehicleLayout ? "enabled" : "fallback"));
        Trace("CE wheel animation=" + (setWheelSteering != null ? "enabled" : "fallback"));
        Trace("vehicle movement uses buffered coordinate samples");
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
        if (BowlingKeyDown(e.Key)) return;
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
        if ((e.Key == Keys.OemOpenBrackets || e.Key == Keys.OemCloseBrackets ||
            e.Key == Keys.OemPipe || e.Key == Keys.OemBackslash) &&
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
        BowlingKeyUp(e.Key);
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
        sharedSource = null;
        globalWorldHost = null;
        lastSharedFrame = 0;
        lastPeerWrite = lastWorldWrite = default(DateTime);
        nextMapUpdate = nextPopulationCheck = nextAmbientDiscovery = 0;
        ambientCars.Clear();
        ambientPeds.Clear();
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
        publishedTraffic.Clear();
        delegatedTraffic.Clear();
        retiredTraffic.Clear();
        pendingVehicleCleanup.Clear();
        vehicleSignals.Clear();
        ResetPlayerVisual();
        ClearBowling();
        bowlingCommand = null;
        bowlingSendUntil = 0;
        indicators = indicatorKey = 0;
        hornHeld = false;
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
            RenderBowling(character, now);
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
                        : nativeKey;
                    nextRadioSample = 0;
                    nextDamageSample = 0;
                    indicators = 0;
                }
                if (!driving) drivingCar = null;
                if (driving) p = car.Position;
                Vehicle passengerCar;
                if (ride != "-" && (!cars.TryGetValue(ride, out passengerCar) ||
                    !Exists(passengerCar) || !Equals(car, passengerCar))) ride = "-";
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
                Vector3 speed = driving ? SampleVehicleVelocity(car, p, now) : character.Velocity;
                if (driving && (car.Model.isCar || car.Model.isBike))
                {
                    Vector3 direction = car.Direction;
                    ReadRoadSignals(car, speed.X * direction.X + speed.Y * direction.Y,
                        ref flags, ref horn, out steer, out alarm, now);
                }
                int gait = driving ? 0 : MovementGait(character, speed);
                uint animation = CapturePlayerVisual(character, car, now);
                CaptureCombat(character, now);
                int combatFlags = 1 | (character.isRagdoll ? 2 : 0) | (!character.isAlive ? 4 : 0);
                string position = string.Format(CultureInfo.InvariantCulture,
                    "{0} {17} {1:F2} {2:F2} {3:F2} {4:F2} {5} {6} {7} {8} -{9} ~{0},{0},1,{10},{11},{12},{18},{19},{20},{21},{22},{23},{24:x8},{25} !{13},{14},{15},{16}\n",
                    now, p.X, p.Y, p.Z,
                    ((driving ? car.Heading : character.Heading) % 360 + 360) % 360,
                    character.Model.Hash, driving ? car.Model.Hash : 0, driving ? drivingToken : 0, ride,
                    driving ? CaptureCarPhysics(car, now) + " " +
                        (sourceStation <= (int)RadioStation.Integrity ? sourceStation : -1).ToString(CultureInfo.InvariantCulture) : "",
                    flags, driving ? sourceStation : -1,
                    driving ? sourceStationName : "-", combatFlags, pendingAttack, pendingAttackKind,
                    pendingVictim, driving ? drivingOrigin : "-", steer, horn, gait,
                    SpeedUnits(speed.X, 150), SpeedUnits(speed.Y, 150), SpeedUnits(speed.Z, 60), animation,
                    driving ? CaptureSuspension(car, now) : "-");
                File.WriteAllText(Path.Combine(directory, "position.txt"), position);
                if (now >= nextSharedCapture)
                {
                    nextSharedCapture = now + 92;
                    PublishSharedWorld(now, character, car, driving);
                }
            }

            string path = Path.Combine(directory, "peers.txt");
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (written != lastPeerWrite && (DateTime.UtcNow - written).TotalMilliseconds < 1700)
            {
                string snapshot = File.ReadAllText(path);
                lastPeerWrite = written;
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
            ClearWorldPeds();
            Trace("shared world timed out");
        }
        UpdateLocalPopulation(character, now);
        MoveAvatars(now);
        RenderBowling(character, now);
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
        ApplyCombatResult(header, character);
        long now = clock.ElapsedMilliseconds;
        bool drawMap = now >= nextMapUpdate;
        if (drawMap) nextMapUpdate = now + 125;
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
            UpdateAvatar(columns[0], character, peer, now);
            PrepareCombatAvatar(columns[0], peer);
            UpdateAvatarMotion(columns[0], peer);
            Blip marker;
            if (markers.TryGetValue(columns[0], out marker) && Exists(marker))
            {
                if (!drawMap) continue;
                Vector3 previous = positions[columns[0]];
                if (SquaredDistance(previous, position) < 0.16f) continue;
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
                (kind == 1 && (peer.CarId != null || peer.Ride != "-" || Exists(character.CurrentVehicle))) ||
                !(kind == 1 ? IsMeleeContact(character, avatar, now) : avatar.HasBeenDamagedBy(character))) continue;

            // GTA decides whether the local attack made contact, including walls and props.
            GTA.Native.Function.Call("CLEAR_CHAR_LAST_DAMAGE_ENTITY", avatar);
            avatar.Health = 1000;
            if (kind == 1)
            {
                lastMeleeSerial = visualSerial;
                lastMeleeAt = now;
            }
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
            if (!character.isRagdoll) StartKnockdown(character, kind == 3 ? 1600 : kind == 1 ? 1050 : 650);
            if (kind == 3) character.ApplyForce(new Vector3(impactX / 100f, impactY / 100f, 0.33f));
        }
        Trace("pvp damage=" + damage + " kind=" + kind);
    }

    private static void ReadMetadata(Peer peer, string metadata)
    {
        string[] fields = metadata.Substring(1).Split(',');
        long sample;
        int flags, station;
        if ((fields.Length != 6 && fields.Length != 9 && fields.Length != 12 && fields.Length != 14) ||
            !long.TryParse(fields[0], out sample) || !int.TryParse(fields[3], out flags) ||
            !int.TryParse(fields[4], out station)) return;
        peer.Sample = sample;
        peer.VehicleFlags = flags;
        peer.Radio = station;
        peer.StationName = fields[5];
        if (fields.Length >= 9)
        {
            peer.HasRoadSignals = int.TryParse(fields[6], out peer.Steering) &&
                uint.TryParse(fields[7], out peer.Horn);
            int.TryParse(fields[8], out peer.Gait);
        }
        if (fields.Length >= 12)
        {
            Vector3 speed;
            if (ReadSpeed(fields, 9, out speed)) peer.Velocity = speed;
        }
        if (fields.Length == 14)
        {
            uint.TryParse(fields[12], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out peer.Animation);
            peer.Suspension = fields[13];
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

    private static string WorldRow(string kind, int id, int model, Vector3 p, float heading)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6}", kind, id, model,
            (int)Math.Round(p.X * 100), (int)Math.Round(p.Y * 100), (int)Math.Round(p.Z * 100),
            (int)((heading % 360 + 360) % 360));
    }

    private void PublishSharedWorld(long sample, Ped local, Vehicle localCar, bool driving)
    {
        if (episode < 0) episode = GTA.Native.Function.Call<int>("GET_CURRENT_EPISODE");
        var frame = new StringBuilder();
        if (vehicleSignals.Count > 192)
            foreach (int handle in new List<int>(vehicleSignals.Keys))
                if (sample - Math.Max(vehicleSignals[handle].SampledAt, vehicleSignals[handle].PositionAt) > 15000)
                    vehicleSignals.Remove(handle);
        int remaining = 60;
        frame.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} {2}\n", ownId, sample, episode);
        if (pendingTake != null && !takeMounted)
        {
            string[] take = pendingTake.Split(':');
            frame.AppendFormat(CultureInfo.InvariantCulture, "t,{0},{1},{2},{3}\n",
                take[0], take[1], take[2], take[3]);
        }
        WriteBowling(frame, sample);
        Vehicle playerCar = driving ? localCar : trackedPlayerCar;
        if (Exists(playerCar) && SquaredDistance(local.Position, playerCar.Position) <= 10000f)
        {
            frame.Append(CaptureVehicleDetails("d", drivingToken, playerCar, sourceStation,
                driving && hornHeld, 0, sample)).Append('\n');
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
                car, -1, false, driverModel, sample)).Append(',').Append(key[0]).Append('\n');
            if (--remaining == 0) break;
        }
        if (peers.Count != 0)
        {
            Vector3 origin = local.Position;
            if (sample >= nextAmbientDiscovery || SquaredDistance(origin, lastAmbientDiscoveryPosition) > 625f)
                DiscoverAmbient(local, localCar, origin, sample);
            foreach (Vehicle car in ambientCars)
            {
                if (remaining == 0) break;
                if (!CanPublishCar(car, localCar)) continue;
                string key = TrafficKey(ownId, car.GetHashCode(), car.Model.Hash);
                if (retiredTraffic.ContainsKey(key)) continue;
                sourceTraffic[key] = car;
                Ped driver = car.GetPedOnSeat(VehicleSeat.Driver);
                int driverModel = Exists(driver) && !avatars.ContainsValue(driver) ? driver.Model.Hash : 0;
                frame.Append(CaptureVehicleDetails("v", car.GetHashCode(), car, -1, false, driverModel, sample)).Append('\n');
                remaining--;
            }

            foreach (Ped ped in ambientPeds)
            {
                if (sharedSource != ownId || remaining == 0) break;
                if (!CanPublishPed(ped, local) || worldDrivers.ContainsValue(ped)) continue;
                Vector3 velocity = ped.Velocity;
                frame.Append(WorldRow("p", ped.GetHashCode(), ped.Model.Hash, ped.Position, ped.Heading))
                    .AppendFormat(CultureInfo.InvariantCulture, ",{0},{1},{2},{3}\n", MovementGait(ped, velocity),
                        SpeedUnits(velocity.X, 30), SpeedUnits(velocity.Y, 30), SpeedUnits(velocity.Z, 30));
                remaining--;
            }
        }
        string snapshot = Path.Combine(directory, "world-local.txt");
        string staging = Path.Combine(directory, "world-local.tmp");
        File.WriteAllText(staging, frame.ToString());
        if (File.Exists(snapshot)) File.Replace(staging, snapshot, null);
        else File.Move(staging, snapshot);
    }

    private void DiscoverAmbient(Ped local, Vehicle localCar, Vector3 origin, long now)
    {
        nextAmbientDiscovery = now + 700;
        lastAmbientDiscoveryPosition = origin;
        foreach (string key in new List<string>(sourceTraffic.Keys))
            if (!Exists(sourceTraffic[key])) sourceTraffic.Remove(key);

        var nearbyCars = new List<KeyValuePair<float, Vehicle>>();
        foreach (Vehicle car in World.GetVehicles(origin, 145f))
            if (CanPublishCar(car, localCar))
                nearbyCars.Add(new KeyValuePair<float, Vehicle>(SquaredDistance(origin, car.Position), car));
        nearbyCars.Sort((a, b) => a.Key.CompareTo(b.Key));
        ambientCars.Clear();
        for (int i = 0; i < Math.Min(AmbientCarLimit, nearbyCars.Count); i++) ambientCars.Add(nearbyCars[i].Value);

        var nearbyPeds = new List<KeyValuePair<float, Ped>>();
        ambientPeds.Clear();
        if (sharedSource != ownId) return;
        foreach (Ped ped in World.GetPeds(origin, 145f, 60))
            if (CanPublishPed(ped, local) && !worldDrivers.ContainsValue(ped))
                nearbyPeds.Add(new KeyValuePair<float, Ped>(SquaredDistance(origin, ped.Position), ped));
        nearbyPeds.Sort((a, b) => a.Key.CompareTo(b.Key));
        for (int i = 0; i < Math.Min(AmbientPedLimit, nearbyPeds.Count); i++) ambientPeds.Add(nearbyPeds[i].Value);
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
        bool horn, int driverModel, long now)
    {
        Model model = car.Model;
        Quaternion q = car.RotationQuaternion;
        Vector3 position = car.Position;
        Vector3 velocity = SampleVehicleVelocity(car, position, now);
        Vector3 direction = car.Direction;
        float forward = velocity.X * direction.X + velocity.Y * direction.Y;
        VehicleSignal details = SignalsFor(car);
        if (now >= details.DetailsUntil || details.Model != model.Hash)
        {
            details.Model = model.Hash;
            details.DetailsUntil = now + 400;
            details.Colors = (uint)(car.Color.Index & 255) | ((uint)(car.FeatureColor1.Index & 255) << 8) |
                ((uint)(car.SpecularColor.Index & 255) << 16) | ((uint)(car.FeatureColor2.Index & 255) << 24);
            details.EngineHealth = (int)Math.Max(-4000, Math.Min(1000, car.EngineHealth));
            details.BodyHealth = Math.Max(0, Math.Min(1000, car.Health));
            details.TankHealth = (int)Math.Max(-4000, Math.Min(1000, car.PetrolTankHealth));
            details.Tires = TireMask(car);
        }
        int flags = (car.EngineRunning ? Engine : 0) | (car.SirenActive ? Siren : 0) |
            (kind == "d" && Equals(car, drivingCar) ? indicators : 0) | (horn ? HornOn : 0);
        int steering = 0, alarm = 0;
        uint hornNumber = 0;
        if (model.isCar || model.isBike)
        {
            ReadRoadSignals(car, forward, ref flags, ref hornNumber, out steering, out alarm, now);
        }
        return WorldRow(kind, id, model.Hash, position, car.Heading) +
            string.Format(CultureInfo.InvariantCulture, ",{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17}",
                (int)Math.Round(q.X * 1000), (int)Math.Round(q.Y * 1000), (int)Math.Round(q.Z * 1000),
                (int)Math.Round(q.W * 1000), details.Colors, flags, hornNumber, station, driverModel,
                details.EngineHealth, details.BodyHealth, details.TankHealth, details.Tires,
                SpeedUnits(velocity.X, 150), SpeedUnits(velocity.Y, 150), SpeedUnits(velocity.Z, 60), steering, alarm);
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

    private static bool MatchesWheelLayout(IntPtr module)
    {
        // Complete Edition 1.2.0.59. Verify the wheel-array initializers, stride,
        // steering setter, and the fields read by the wheel bone renderer.
        return MatchesInstruction(module, 0x831D81, new byte[] { 0x8D, 0xBE, 0xD0, 0x14, 0, 0, 0x89, 0xBE, 0x80, 0x0F, 0, 0 }) &&
            MatchesInstruction(module, 0x8EAFFE, new byte[] { 0x8D, 0x8F, 0x50, 0x14, 0, 0, 0x89, 0x8F, 0x80, 0x0F, 0, 0 }) &&
            MatchesInstruction(module, 0x833FDD, new byte[] { 0x81, 0xC3, 0x70, 1, 0, 0, 0x3B, 0xB7, 0x84, 0x0F, 0, 0 }) &&
            MatchesInstruction(module, 0x6D05A0, new byte[] { 0x8B, 0x81, 0x3C, 1, 0, 0, 0x8B, 0x91, 0x64, 1, 0, 0 }) &&
            MatchesInstruction(module, 0x6CF91F, new byte[] { 0xF3, 0x0F, 0x10, 0x87, 0x3C, 1, 0, 0 }) &&
            MatchesInstruction(module, 0x6CFAD7, new byte[] { 0xF3, 0x0F, 0x10, 0x47, 0x7C }) &&
            MatchesInstruction(module, 0x6CF66C, new byte[] { 0xF3, 0x0F, 0x10, 0x4F, 0x78 });
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

    private VehicleSignal SignalsFor(Vehicle car)
    {
        VehicleSignal sample;
        int handle = car.GetHashCode();
        if (!vehicleSignals.TryGetValue(handle, out sample))
        {
            sample = new VehicleSignal();
            vehicleSignals[handle] = sample;
        }
        return sample;
    }

    private Vector3 SampleVehicleVelocity(Vehicle car, Vector3 position, long now)
    {
        VehicleSignal sample = SignalsFor(car);
        long elapsed = now - sample.PositionAt;
        if (sample.PositionAt != 0 && elapsed < 12) return sample.Velocity;
        if (sample.PositionAt == 0 || elapsed > 400 || SquaredDistance(position, sample.Position) > 3600f)
            sample.Velocity = new Vector3();
        else
            sample.Velocity = Lerp(sample.Velocity, VelocityBetween(sample.Position, position, elapsed, 120f), 0.65f);
        sample.Position = position;
        sample.PositionAt = now;
        return sample.Velocity;
    }

    private void ReadRoadSignals(Vehicle car, float forward, ref int flags,
        ref uint hornNumber, out int steering, out int alarm, long now)
    {
        VehicleSignal sample = SignalsFor(car);
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
        GTA.Native.Function.Call("SET_PED_DENSITY_MULTIPLIER", density);
        suppressingPopulation = value;
        Trace(value ? "local ambient population suppressed" : "local ambient population restored");
    }

    private void UpdateLocalPopulation(Ped character, long now)
    {
        if (now < nextPopulationCheck) return;
        nextPopulationCheck = now + 150;
        bool following = serverConnected && sharedSource != null && sharedSource != ownId &&
            now - lastSharedAt < 500;
        bool canClear = following && Player.CanControlCharacter && character.isAlive && !Player.isOnMission;
        SetPopulationSuppressed(canClear && (remoteAmbientConfirmed || (lastAmbientAt > 0 && now - lastAmbientAt < 900)));
        if ((!canClear && worldCars.Count == 0) || !serverConnected || peers.Count == 0 ||
            !Player.CanControlCharacter || !character.isAlive ||
            Player.isOnMission || now < nextAmbientCleanup || character.isGettingIntoAVehicle)
            return;

        nextAmbientCleanup = now + 1100;
        ClearLocalPopulation(character, canClear && remoteAmbientConfirmed, now);
    }

    private void ClearLocalPopulation(Ped character, bool clearPeds, long now)
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
        Vector3 center = character.Position;
        Ped[] loadedPeds = World.GetPeds(center, 90f, 55);
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
        var sharedPositions = new List<Vector3>();
        foreach (var entry in worldCars)
        {
            PoseTrack track;
            if (Exists(entry.Value) && worldTracks.TryGetValue(entry.Key, out track) && now - track.LastSeen < 650)
                sharedPositions.Add(entry.Value.Position);
        }
        foreach (Vehicle car in World.GetVehicles(center, 90f))
        {
            if (obsoleteCars.Count >= 8) break;
            if (!Exists(car) || protectedCars.Contains(car) || car.isRequiredForMission || car.Model.isTrain) continue;
            int handle = car.GetHashCode();
            if (publishedTraffic.Contains(handle)) continue;
            Vector3 position = car.Position;
            if (sharedPositions.Exists(point => SquaredDistance(point, position) < 9f)) obsoleteCars.Add(car);
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
            else if (clearPeds && pedestriansRemoved < 14)
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
        DateTime written = File.GetLastWriteTimeUtc(path);
        if (written == lastWorldWrite || (DateTime.UtcNow - written).TotalMilliseconds > 1100) return;
        string[] lines = File.ReadAllLines(path);
        lastWorldWrite = written;
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
        publishedTraffic.Clear();
        foreach (string key in new List<string>(retiredTraffic.Keys))
            if (now >= retiredTraffic[key]) retiredTraffic.Remove(key);
        string source = header[4];
        if (sharedSource != source)
        {
            sharedSource = source;
            lastAmbientAt = 0;
            nextSharedCapture = 0;
            nextAmbientDiscovery = 0;
            Trace("shared world source=" + source);
        }
        var seen = new HashSet<string>();
        bool hasAmbientFrame = false;
        for (int i = 1; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split(' ');
            long entitySample;
            if (parts.Length != 3 || parts[0].Length != 16 || !long.TryParse(parts[1], out entitySample)) continue;
            if (parts[0] == ownId && parts[2].StartsWith("+", StringComparison.Ordinal))
            {
                foreach (string handle in parts[2].Substring(1).Split(','))
                {
                    int value;
                    if (int.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                        publishedTraffic.Add(value);
                }
                continue;
            }
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
            if (fields[0] == "B")
            {
                ReadBowling(parts[0], fields, now);
                continue;
            }
            if (fields.Length != 3 && fields.Length != 11 && fields.Length != 25 && fields.Length != 26) continue;
            int id, model;
            if (!int.TryParse(fields[1], out id) || !int.TryParse(fields[2], out model)) continue;
            string kind = fields[0];
            if ((kind == "take" || kind == "drop" || kind == "hide" || kind == "own" ||
                kind == "takec" || kind == "dropc" || kind == "ownc") && fields.Length == 3)
            {
                ApplyTrafficTransfer(kind, parts[0], id, model, character);
                continue;
            }
            if (kind == "d" && (fields.Length == 25 || fields.Length == 26))
            {
                string carId = parts[0] + ":" + id.ToString(CultureInfo.InvariantCulture);
                string parkedId = string.Format(CultureInfo.InvariantCulture, "{0}:c:{1}:{2}", parts[0], id, model);
                if (parkedId == acquiredTrafficKey || retiredTraffic.ContainsKey(parkedId)) continue;
                int cx, cy, cz, yaw;
                if (int.TryParse(fields[3], out cx) && int.TryParse(fields[4], out cy) &&
                    int.TryParse(fields[5], out cz) && int.TryParse(fields[6], out yaw))
                {
                    if (fields.Length == 26) AdoptRemoteVehicle(carId, fields[25], model, character);
                    ApplySharedCar(carId, new Model(model), new Vector3(cx / 100f, cy / 100f, cz / 100f),
                        yaw, fields, entitySample, now, true);
                }
                continue;
            }
            bool traffic = kind == "v" || kind == "m";
            if (!traffic && (parts[0] != source || source == ownId)) continue;
            int x, y, z, heading;
            if (!int.TryParse(fields[3], out x) || !int.TryParse(fields[4], out y) ||
                !int.TryParse(fields[5], out z) || !int.TryParse(fields[6], out heading)) continue;
            var position = new Vector3(x / 100f, y / 100f, z / 100f);
            string key = traffic ? TrafficKey(parts[0], id, model) : source + ":p:" + fields[1] + ":" + fields[2];
            float radius = kind == "m" || worldTracks.ContainsKey(key) ? 124f : 118f;
            if (SquaredDistance(character.Position, position) > radius * radius) continue;
            if (kind == "p" && fields.Length == 11)
            {
                int gait;
                Vector3 speed;
                if (!int.TryParse(fields[7], out gait) || gait < 0 || gait > 4 ||
                    !ReadSpeed(fields, 8, out speed)) continue;
                seen.Add(key);
                ApplySharedPed(key, new Model(model), position, heading, speed, gait, entitySample, now);
            }
            else if (traffic && fields.Length == 25 && key != acquiredTrafficKey && !retiredTraffic.ContainsKey(key))
            {
                seen.Add(key);
                ApplySharedCar(key, new Model(model), position, heading, fields, entitySample, now, false, kind == "m");
            }
        }
        FinishVehicleTransfers(character, now);
        remoteAmbientConfirmed = hasAmbientFrame;
        if (seen.Count > 0) lastAmbientAt = now;
        foreach (string key in new List<string>(worldPeds.Keys))
            if (CanCullWorld(key, seen, now)) RemoveWorldEntity(key);
        foreach (string key in new List<string>(worldCars.Keys))
            if (!seen.Contains(key) && CanCullTraffic(key, character, now)) RemoveWorldEntity(key);
    }

    private bool ModelReady(Model model, long now)
    {
        if (model.isInMemory) return true;
        long retry;
        if (!requestedModels.TryGetValue(model.Hash, out retry) || now >= retry)
        {
            GTA.Native.Function.Call("REQUEST_MODEL", model.Hash);
            requestedModels[model.Hash] = now + 250;
        }
        return false;
    }

    private bool CanSpawnWorldModel(Model model, long now)
    {
        if (!ModelReady(model, now)) return false;
        if (now < nextSharedSpawn) return false;
        nextSharedSpawn = now + 45;
        return true;
    }

    private void ReleaseModelRequest(Model model)
    {
        if (requestedModels.Remove(model.Hash))
            GTA.Native.Function.Call("MARK_MODEL_AS_NO_LONGER_NEEDED", model.Hash);
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
            if (command != "hide")
            {
                long now = clock.ElapsedMilliseconds;
                RetireVehicle(key, now);
                if (pendingTake == key) takeUntil = 0;
                return;
            }
            if (source == ownId)
            {
                Vehicle original;
                if (sourceTraffic.TryGetValue(key, out original) && !worldCars.ContainsValue(original))
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
        if (!ClearSourceOccupants(car, character)) return;
        car.Delete();
        sourceTraffic.Remove(key);
        Trace("released shared vehicle " + key);
    }

    private bool ClearSourceOccupants(Vehicle car, Ped character)
    {
        var occupants = new List<Ped>();
        Group group = Player.Group;
        for (int seat = -1; seat < car.PassengerSeats; seat++)
        {
            Ped ped = car.GetPedOnSeat((VehicleSeat)seat);
            if (!Exists(ped)) continue;
            if (Equals(ped, character) && seat != -1) continue;
            if (Equals(ped, character) || ped.isRequiredForMission || avatars.ContainsValue(ped) ||
                (Exists(group) && group.isMember(ped))) return false;
            occupants.Add(ped);
        }
        foreach (Ped ped in occupants)
            if (Exists(ped)) ped.Delete();
        return true;
    }

    private bool FindTransferVehicle(string key, string[] origin, int model, out Vehicle car)
    {
        if (ResolveReplica(key, out car)) return true;
        string slot = origin[0] + ":" + origin[2];
        if (releasedVehicleKeys.TryGetValue(origin[1] == "v" ? key : slot, out car) && Exists(car) &&
            car.Model.Hash == model) return true;
        if (origin[0] != ownId) return false;
        if (origin[1] == "v") return sourceTraffic.TryGetValue(key, out car) && Exists(car);
        car = trackedPlayerCar;
        return origin[2] == drivingToken.ToString(CultureInfo.InvariantCulture) && Exists(car) && car.Model.Hash == model;
    }

    private void DetachTransferVehicle(string key, string[] origin, Vehicle car)
    {
        string slot = origin[0] + ":" + origin[2];
        if (origin[1] == "v") ForgetTrafficReplica(key);
        else ForgetPlayerReplica(slot);
        releasedVehicleKeys.Remove(origin[1] == "v" ? key : slot);
        releasedCars.Remove(car);
        sourceTraffic.Remove(key);
        if (Equals(trackedPlayerCar, car)) trackedPlayerCar = null;
        if (origin[1] == "c" && Equals(acquiredTraffic, car))
        {
            acquiredTraffic = null;
            acquiredTrafficKey = null;
        }
        pendingVehicleCleanup.Remove(key);
    }

    private void AdoptRemoteVehicle(string destination, string key, int model, Ped character)
    {
        string[] origin = key.Split(':');
        int claimedModel;
        if (origin.Length != 4 || origin[0].Length != 16 || (origin[1] != "v" && origin[1] != "c") ||
            !int.TryParse(origin[3], out claimedModel) || claimedModel != model ||
            (origin[1] == "c" && destination == origin[0] + ":" + origin[2])) return;
        RetireVehicle(key, clock.ElapsedMilliseconds);
        Vehicle former;
        if (!FindTransferVehicle(key, origin, model, out former) || (origin[1] == "v" && Equals(former, acquiredTraffic)) ||
            Equals(former, drivingCar)) return;
        if (origin[0] == ownId && !worldCars.ContainsValue(former) && !releasedCars.Contains(former) &&
            ((origin[1] == "v" && former.isRequiredForMission) || !ClearSourceOccupants(former, character))) return;
        Vehicle existing;
        cars.TryGetValue(destination, out existing);
        DetachTransferVehicle(key, origin, former);
        GTA.Native.Function.Call("SET_CAR_AS_MISSION_CAR", former);
        if (Exists(existing) && !Equals(existing, former))
        {
            if (Equals(existing, character.CurrentVehicle))
            {
                former.Delete();
                return;
            }
            existing.Delete();
            ForgetPlayerReplica(destination);
        }
        if (Equals(former, character.CurrentVehicle)) ride = destination;
        EnableVehiclePhysics(former);
        former.DoorLock = DoorLock.None;
        cars[destination] = former;
        carTracks[destination] = new PoseTrack();
        Trace("vehicle transfer reused " + key + " -> " + destination);
    }

    private void RetireVehicle(string key, long now)
    {
        if (!retiredTraffic.ContainsKey(key)) pendingVehicleCleanup[key] = now + 900;
        retiredTraffic[key] = now + 21000;
    }

    private void FinishVehicleTransfers(Ped character, long now)
    {
        foreach (string key in new List<string>(pendingVehicleCleanup.Keys))
        {
            if (now < pendingVehicleCleanup[key]) continue;
            string[] origin = key.Split(':');
            Vehicle car;
            if (FindTransferVehicle(key, origin, int.Parse(origin[3], CultureInfo.InvariantCulture), out car) &&
                !Equals(car, character.CurrentVehicle) && !(origin[1] == "v" && Equals(car, acquiredTraffic)))
            {
                if (origin[0] != ownId || worldCars.ContainsValue(car) || releasedCars.Contains(car))
                {
                    DetachTransferVehicle(key, origin, car);
                    if (Exists(car)) car.Delete();
                }
                else DropSourceTraffic(key, car, character, origin[1] == "c");
            }
            pendingVehicleCleanup.Remove(key);
        }
    }

    private void ApplySharedPed(string key, Model model, Vector3 position, int heading, Vector3 speed,
        int gait, long sample, long now)
    {
        if (!model.isPed) return;
        Ped ped;
        if (!worldPeds.TryGetValue(key, out ped) || !Exists(ped))
        {
            if (!CanSpawnWorldModel(model, now)) return;
            ped = World.CreatePed(model, position);
            ReleaseModelRequest(model);
            if (!Exists(ped)) return;
            ped.Invincible = true;
            ped.BlockPermanentEvents = true;
            ped.FreezePosition = true;
            worldPeds[key] = ped;
            worldGaits.Remove(key);
        }
        QueueWorldPose(key, sample, now, position, heading, null, speed);
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
        Vector3 speed;
        if (!ReadSpeed(fields, 20, out speed) || !int.TryParse(fields[7], out qx) || !int.TryParse(fields[8], out qy) ||
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
            PoseTrack previousTrack;
            if (worldTracks.TryGetValue(key, out previousTrack))
            {
                previousTrack.AppliedAt = 0;
                previousTrack.PresentedOrientation = null;
            }
        }
        Vehicle car;
        if (!vehicles.TryGetValue(key, out car) || !Exists(car))
        {
            PoseTrack missing;
            if (playerVehicle && car != null && carTracks.TryGetValue(key, out missing))
            {
                if (missing.MissingSince == 0) missing.MissingSince = now;
                if (now - missing.MissingSince < 475) return;
            }
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
            else if (!ReuseReplica(key, model, position, out car))
            {
                if (!CanSpawnWorldModel(model, now)) return;
                car = World.CreateVehicle(model, position);
                ReleaseModelRequest(model);
            }
            if (!Exists(car)) return;
            if (!playerVehicle && !worldDrivers.ContainsKey(key))
            {
                Ped attachedDriver = car.GetPedOnSeat(VehicleSeat.Driver);
                if (Exists(attachedDriver))
                {
                    attachedDriver.BlockPermanentEvents = true;
                    attachedDriver.Invincible = true;
                    attachedDriver.Task.ClearAll();
                    worldDrivers[key] = attachedDriver;
                }
            }
            wasSimulating = false;
            car.RotationQuaternion = rotation;
            EnableVehiclePhysics(car);
            car.DoorLock = playerVehicle && occupiedCars.Contains(key) ? DoorLock.ImpossibleToOpen : DoorLock.None;
            vehicles[key] = car;
            if (playerVehicle && carTracks.TryGetValue(key, out missing))
            {
                missing.MissingSince = 0;
                missing.AppliedAt = 0;
                missing.PresentedOrientation = null;
            }
            vehicleDetails.Remove(key);
            appliedDamage.Remove(key);
        }
        // A moving player's pose comes only from the fast stream. This row still carries damage and colour.
        if (!playerVehicle || !occupiedCars.Contains(key) || !carTracks.ContainsKey(key))
            QueueWorldPose(key, sample, now, position, heading, rotation, speed, playerVehicle);
        if (!simulate || !wasSimulating)
        {
            ApplyVehicleDetails(key, car, fields, sample, now);
            if (!playerVehicle || !occupiedCars.Contains(key))
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
        ReleaseModelRequest(driverModel);
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
        int flags, station, steer, alarm;
        if (!uint.TryParse(fields[11], out colors) || !int.TryParse(fields[12], out flags) ||
            !uint.TryParse(fields[13], out horn) || !int.TryParse(fields[14], out station) ||
            !int.TryParse(fields[23], out steer) || !int.TryParse(fields[24], out alarm)) return;
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
        old.MotionSample = sample;
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
        if (state == null || now - state.SignalAt > 750) return;
        int indicators = state.Flags & (LeftIndicator | RightIndicator);
        if (now < state.NextControls && now < state.NextLights && indicators == 0) return;
        Model model = car.Model;
        if (!(model.isCar || model.isBike)) return;
        if (now >= state.NextLights)
        {
            GTA.Native.Function.Call("FORCE_CAR_LIGHTS", car, (state.Flags & Headlights) != 0 ? 2 : 1);
            GTA.Native.Function.Call("SET_VEH_HAZARDLIGHTS", car, (state.Flags & Hazard) != 0);
            GTA.Native.Function.Call("SET_VEH_INDICATORLIGHTS", car,
                indicators == (LeftIndicator | RightIndicator) && (state.Flags & Hazard) == 0);
            state.NextLights = now + 120;
        }
        if (now >= state.NextControls)
        {
            state.NextControls = now + 33;
            IntPtr address;
            if (ReadRoadMemory(car, out address))
            {
                WriteRoadValue(address, 0x1088, state.Steering * 0.58f);
                WriteRoadValue(address, 0x107C, (state.Flags & Braking) != 0 ? 1f : 0f);
            }
            else GTA.Native.Function.Call("SET_VEHICLE_STEER_BIAS", car, state.Steering);
        }
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

    private bool CanCullWorld(string key, HashSet<string> seen, long now)
    {
        PoseTrack track;
        return !seen.Contains(key) && (!worldTracks.TryGetValue(key, out track) || now - track.LastSeen > 1500);
    }

    private bool CanCullTraffic(string key, Ped character, long now)
    {
        Vehicle car = worldCars[key];
        PoseTrack track;
        return !Exists(car) || (!Equals(car, character.CurrentVehicle) &&
            (!worldTracks.TryGetValue(key, out track) || now - track.LastSeen > 4500));
    }

    private void QueueWorldPose(string key, long sample, long now, Vector3 position, float heading, Quaternion? orientation,
        Vector3 speed, bool playerVehicle = false)
    {
        Dictionary<string, PoseTrack> tracks = playerVehicle ? carTracks : worldTracks;
        PoseTrack track;
        if (!tracks.TryGetValue(key, out track))
        {
            track = new PoseTrack();
            tracks[key] = track;
        }
        track.Add(sample, now, position, heading, orientation, speed);
    }

    private void UpdateAvatar(string id, Ped character, Peer peer, long now)
    {
        Ped avatar;
        avatars.TryGetValue(id, out avatar);
        bool spawned = Exists(avatar);
        float radius = spawned ? 120f : 100f;
        bool nearby = Exists(character) && SquaredDistance(character.Position, peer.Position) <= radius * radius;
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
        PoseTrack walk = null;
        if (peer.CarId == null && peer.Ride == "-" && (peer.CombatFlags & 6) == 0)
        {
            if (!avatarTracks.TryGetValue(id, out walk))
                avatarTracks[id] = walk = new PoseTrack();
            walk.Add(peer.Sample, now, peer.Position, peer.Heading, null, peer.Velocity);
        }
        else avatarTracks.Remove(id);
        if ((spawned && avatar.isAlive) || now < nextAvatarSpawn || (peer.CombatFlags & 4) != 0) return;
        // The installer mounts the ordinary IG_Niko ped from the local Complete Edition archives.
        var model = new Model(episode <= 0 ? "lf_niko" : "ig_niko");
        if (!model.isPed)
        {
            if (now >= nextNikoReport)
            {
                nextNikoReport = now + 10000;
                Trace("Niko ped not registered; install the current launcher patch and restart GTA IV");
            }
            return;
        }
        if (!ModelReady(model, now)) return;
        if (spawned) avatar.Delete();
        vulnerableAvatars.Remove(id);
        avatarRagdolls.Remove(id);
        avatarGaits.Remove(id);
        nextAvatarSpawn = now + 150;
        Vector3 spawnPosition = walk == null ? peer.Position : walk.Read(now - 100, 90).Position;
        avatar = World.CreatePed(model, spawnPosition);
        ReleaseModelRequest(model);
        if (!Exists(avatar))
        {
            Trace("remote player model could not be spawned " + id);
            return;
        }
        avatar.BlockPermanentEvents = true;
        avatar.Invincible = true;
        avatar.FreezePosition = true;
        avatar.PreventRagdoll = true;
        avatars[id] = avatar;
        Trace("streamed in remote player " + id);
    }

    private void MoveAvatars(long now)
    {
        if (Exists(drivingCar)) DrawTurnIndicators(drivingCar, indicators, now);
        foreach (var entry in worldCars)
        {
            Vehicle car = entry.Value;
            if (!Exists(car) || retiredTraffic.ContainsKey(entry.Key)) continue;
            long delegatedUntil;
            if (delegatedTraffic.TryGetValue(entry.Key, out delegatedUntil) && now - delegatedUntil <= 1200) continue;
            PoseTrack track;
            if (worldTracks.TryGetValue(entry.Key, out track)) SetCarPose(entry.Key, car, track, now, 160, 32);
        }
        foreach (var entry in worldPeds)
        {
            Ped ped = entry.Value;
            if (!Exists(ped)) continue;
            PoseTrack track = worldTracks[entry.Key];
            if (now - track.AppliedAt < 33) continue;
            track.AppliedAt = now;
            Pose pose = track.Read(now - 160, 110);
            if (SquaredDistance(ped.Position, pose.Position) > 0.0001f)
                SetPedPosition(ped, pose.Position);
            ped.Heading = pose.Heading;
        }
        foreach (var entry in cars)
        {
            PoseTrack track;
            if (Exists(entry.Value) && carTracks.TryGetValue(entry.Key, out track))
            {
                bool occupied = occupiedCars.Contains(entry.Key);
                SetCarPose(entry.Key, entry.Value, track, now, occupied ? 90 : 160, occupied ? 0 : 33);
            }
        }

        foreach (var entry in avatars)
        {
            Ped avatar = entry.Value;
            Peer peer;
            if (!Exists(avatar) || !peers.TryGetValue(entry.Key, out peer)) continue;
            ReplicaRagdoll ragdoll;
            avatarRagdolls.TryGetValue(entry.Key, out ragdoll);
            if ((peer.CombatFlags & 6) != 0 || ragdoll != null && (now < ragdoll.HoldUntil ||
                ragdoll.Active && (avatar.isRagdoll || GTA.Native.Function.Call<bool>("IS_CHAR_GETTING_UP", avatar))))
            {
                if (Exists(avatar.CurrentVehicle)) avatar.LeaveVehicle();
                else UpdateRemoteRagdoll(entry.Key, avatar, peer, now);
                continue;
            }
            if (ragdoll != null)
            {
                ragdoll.HoldUntil = 0;
                if (ragdoll.Active) avatar.PreventRagdoll = true;
                ragdoll.Active = false;
            }
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
                ? walking.Read(now - 100, 90) : new Pose { Position = peer.Position, Heading = peer.Heading };
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
        if (!state.Active || newHit)
        {
            state.Active = true;
            state.HitSequence = peer.HitSequence;
            int duration = carImpact ? 1600 : newHit && peer.HitKind == 1 ? 1050 : 650;
            state.HoldUntil = now + duration;
            state.RetryAt = now + 120;
            avatar.Task.ClearAll();
            StartKnockdown(avatar, duration);
            if (carImpact)
            {
                avatar.ApplyForce(new Vector3(peer.ImpactX / 100f, peer.ImpactY / 100f, 0.33f));
                Trace("remote ragdoll after vehicle impact player=" + id);
            }
        }
        else if (!avatar.isRagdoll && now >= state.RetryAt && now + 450 < state.HoldUntil)
        {
            StartKnockdown(avatar, (int)(state.HoldUntil - now));
            state.RetryAt = long.MaxValue;
        }
        if (avatar.isRagdoll && SquaredDistance(avatar.Position, peer.Position) > 36f)
            SetPedPosition(avatar, peer.Position);
    }

    private static void StartKnockdown(Ped ped, int duration)
    {
        ped.PreventRagdoll = false;
        GTA.Native.Function.Call("SWITCH_PED_TO_RAGDOLL", ped, 250, duration, 0, 1, 1, 0);
    }

    private static float BlendHeading(float from, float to, float step)
    {
        var delta = (to - from + 540f) % 360f - 180f;
        return (from + delta * step + 360f) % 360f;
    }

    private static int MovementGait(Ped ped, Vector3 velocity)
    {
        if (ped.isRagdoll) return 4;
        float speed = velocity.X * velocity.X + velocity.Y * velocity.Y;
        return speed > 28f ? 3 : speed > 6f ? 2 : speed > 0.16f ? 1 : 0;
    }

    private void UpdateAvatarMotion(string id, Peer peer)
    {
        Ped avatar;
        ReplicaRagdoll ragdoll;
        if (!avatars.TryGetValue(id, out avatar) || !Exists(avatar) ||
            (avatarRagdolls.TryGetValue(id, out ragdoll) && ragdoll.Active) ||
            (peer.CombatFlags & 6) != 0 || avatar.isRagdoll)
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
        ApplyPlayerVisual(avatar, peer, gait, motion, now, peer.CarId != null || peer.Ride != "-" || Exists(avatar.CurrentVehicle));
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
        long now = clock.ElapsedMilliseconds;
        var seen = new HashSet<string>();
        foreach (Peer peer in peers.Values)
        {
            if (peer.CarId == null || !Exists(character) ||
                (ride != peer.CarId && SquaredDistance(character.Position, peer.Position) >
                    (cars.ContainsKey(peer.CarId) ? 14400f : 10000f))) continue;
            seen.Add(peer.CarId);
            if (peer.VehicleOrigin != null)
                AdoptRemoteVehicle(peer.CarId, peer.VehicleOrigin, peer.CarModel, character);
            bool newlyOccupied = occupiedCars.Add(peer.CarId);
            Vehicle car;
            bool existing = cars.TryGetValue(peer.CarId, out car) && Exists(car);
            PoseTrack carTrack;
            carTracks.TryGetValue(peer.CarId, out carTrack);
            if (carTrack != null) carTrack.LastOccupied = now;
            if (!existing && car != null && carTrack != null)
            {
                if (carTrack.MissingSince == 0) carTrack.MissingSince = now;
                if (now - carTrack.MissingSince < 475) continue;
            }
            if (!existing)
            {
                Model model = new Model(peer.CarModel);
                if (!model.isVehicle) continue;
                if (!Exists(car) && !ReuseReplica(peer.CarId, model, peer.Position, out car))
                {
                    if (!CanSpawnWorldModel(model, clock.ElapsedMilliseconds)) continue;
                    Trace("creating remote player vehicle " + model.Hash);
                    car = World.CreateVehicle(model, peer.Position);
                    ReleaseModelRequest(model);
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
                if (carTrack != null)
                {
                    carTrack.AppliedAt = 0;
                    carTrack.PresentedOrientation = null;
                }
            }
            if (carTrack == null) carTracks[peer.CarId] = carTrack = new PoseTrack();
            carTrack.LastOccupied = now;
            carTrack.MissingSince = 0;
            if (newlyOccupied) car.DoorLock = DoorLock.ImpossibleToOpen;
            Quaternion? orientation = peer.Physics == null ? (Quaternion?)null : peer.Physics.Orientation;
            carTrack.Add(peer.Sample, now, peer.Position, peer.Heading, orientation, peer.Velocity);
            carTrack.SetSuspension(peer.Suspension);
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
            if (!Exists(car))
            {
                if (!carTracks.TryGetValue(id, out track) || now - track.LastOccupied >= 900) ForgetPlayerReplica(id);
                continue;
            }
            string owner = id.Split(':')[0];
            string old = ParkedKey(id, car);
            if (pendingVehicleCleanup.ContainsKey(old)) continue;
            bool retired = retiredTraffic.ContainsKey(old);
            if (!retired && carTracks.TryGetValue(id, out track) && now - track.LastOccupied < 900) continue;
            if (retired || (carTracks.TryGetValue(id, out track) &&
                now - track.LastSeen > 5200))
                RemoveCar(id, !retired && !peers.ContainsKey(owner) && Exists(character) &&
                    SquaredDistance(character.Position, car.Position) < 8100f);
            else if (occupiedCars.Remove(id)) car.DoorLock = DoorLock.None;
        }
        occupiedCars.IntersectWith(seen);
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
        if (keep) ReleaseReplica(id, car);
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

    private bool ReuseReplica(string key, Model model, Vector3 position, out Vehicle car)
    {
        if (releasedVehicleKeys.TryGetValue(key, out car))
        {
            releasedVehicleKeys.Remove(key);
            if (Exists(car) && car.Model.Hash == model.Hash && SquaredDistance(car.Position, position) < 324f &&
                !Equals(car, Player.Character.CurrentVehicle) && !Equals(car, Player.LastVehicle) &&
                (key.Contains(":v:") || car.isSeatFree(VehicleSeat.Driver)))
            {
                releasedCars.Remove(car);
                GTA.Native.Function.Call("SET_CAR_AS_MISSION_CAR", car);
                return true;
            }
        }
        car = null;
        return false;
    }

    private void ReleaseReplica(string key, Vehicle car)
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
        releasedVehicleKeys[key] = car;
    }

    private static void SetPedPosition(Ped ped, Vector3 p)
    {
        // The ordinary teleport adjusts Z, so reading its result and blending again can lift the avatar.
        GTA.Native.Function.Call("SET_CHAR_COORDINATES_NO_OFFSET", ped, p.X, p.Y, p.Z);
    }

    private string CaptureCarPhysics(Vehicle car, long now)
    {
        Quaternion q = car.RotationQuaternion;
        if (now >= nextDamageSample)
        {
            damageSample = string.Format(CultureInfo.InvariantCulture, ",{0:F1},{1},{2:F1}",
                Math.Max(-4000, Math.Min(1000, car.EngineHealth)), Math.Max(0, Math.Min(1000, car.Health)),
                Math.Max(-4000, Math.Min(1000, car.PetrolTankHealth)));
            int tires = TireMask(car);
            if (tires >= 0) damageSample += "," + tires.ToString(CultureInfo.InvariantCulture);
            nextDamageSample = now + 100;
        }
        return string.Format(CultureInfo.InvariantCulture, " {0:F4},{1:F4},{2:F4},{3:F4}", q.X, q.Y, q.Z, q.W) +
            damageSample;
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

    private static Vector3 VelocityBetween(Vector3 first, Vector3 second, long elapsed, float cap)
    {
        float scale = 1000f / Math.Max(1, elapsed);
        return LimitVector(new Vector3((second.X - first.X) * scale, (second.Y - first.Y) * scale,
            (second.Z - first.Z) * scale), cap);
    }

    private static int SpeedUnits(float value, float limit)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
        return (int)Math.Round(Math.Max(-limit, Math.Min(limit, value)) * 100f);
    }

    private static bool ReadSpeed(string[] fields, int index, out Vector3 velocity)
    {
        int x, y, z;
        velocity = new Vector3();
        if (!int.TryParse(fields[index], out x) || !int.TryParse(fields[index + 1], out y) ||
            !int.TryParse(fields[index + 2], out z)) return false;
        velocity = new Vector3(x / 100f, y / 100f, z / 100f);
        return true;
    }

    private static Vector3 LimitVector(Vector3 value, float limit)
    {
        float lengthSquared = value.X * value.X + value.Y * value.Y + value.Z * value.Z;
        if (lengthSquared <= limit * limit) return value;
        float scale = limit / (float)Math.Sqrt(lengthSquared);
        return new Vector3(value.X * scale, value.Y * scale, value.Z * scale);
    }

    private static void EnableVehiclePhysics(Vehicle car)
    {
        car.FreezePosition = false;
        GTA.Native.Function.Call("SET_CAR_COLLISION", car, true);
    }

    private static Vector3 AdvanceCarPosition(Vector3 presented, Pose pose, long elapsed)
    {
        Vector3 delta = new Vector3(pose.Position.X - presented.X, pose.Position.Y - presented.Y,
            pose.Position.Z - presented.Z);
        float speed = (float)Math.Sqrt(pose.Velocity.X * pose.Velocity.X + pose.Velocity.Y * pose.Velocity.Y +
            pose.Velocity.Z * pose.Velocity.Z);
        // Follow the sampled trajectory and bound each correction even when a packet arrives late.
        float limit = Math.Max(16f, Math.Min(140f, speed + 13f)) * elapsed / 1000f;
        Vector3 step = LimitVector(delta, limit);
        return new Vector3(presented.X + step.X, presented.Y + step.Y, presented.Z + step.Z);
    }

    private bool TryRoadWheels(Vehicle car, out IntPtr wheels, out int count)
    {
        wheels = IntPtr.Zero;
        count = 0;
        if (setWheelSteering == null) return false;
        bool automobile = car.Model.isCar;
        if (!automobile && !car.Model.isBike) return false;
        IntPtr address = new IntPtr(((GTA.@base.iAddressableObject)car).MemoryAddress);
        if (address == IntPtr.Zero || !ReadGameMemory(IntPtr.Add(address, 0xF80), wheelLayout)) return false;
        wheels = new IntPtr(BitConverter.ToInt32(wheelLayout, 0));
        count = BitConverter.ToInt32(wheelLayout, 4);
        return wheels == IntPtr.Add(address, automobile ? 0x14D0 : 0x1450) && count >= 1 && count <= (automobile ? 6 : 2);
    }

    private string CaptureSuspension(Vehicle car, long now)
    {
        VehicleSignal sample = SignalsFor(car);
        if (now < sample.SuspensionAt) return sample.Suspension;
        sample.SuspensionAt = now + 30;
        IntPtr wheels;
        int count;
        if (!TryRoadWheels(car, out wheels, out count)) return sample.Suspension = "-";
        var result = new StringBuilder(12);
        for (int index = 0; index < 6; ++index)
        {
            float travel = index < count && ReadGameMemory(IntPtr.Add(wheels, index * 0x170 + 0x78), suspensionSample)
                ? BitConverter.ToSingle(suspensionSample, 0) : -1f;
            int centimeters = travel >= 0f && travel <= 2.54f ? (int)Math.Round(travel * 100f) : 255;
            result.Append(centimeters.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sample.Suspension = result.ToString();
    }

    private void AnimateReplicaWheels(Vehicle car, PoseTrack track, VehicleDetails detail, float distance, long now, long elapsed)
    {
        IntPtr wheels;
        int count;
        if (!TryRoadWheels(car, out wheels, out count)) return;
        if (track.Wheels != wheels || track.WheelRadii == null || track.WheelRadii.Length != count)
        {
            var radii = new float[count];
            var phases = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!ReadGameMemory(IntPtr.Add(wheels, 0x170 * i + 8), wheelSurface)) return;
                float radius = BitConverter.ToSingle(wheelSurface, 0);
                if (!(radius >= 0.10f && radius <= 2f)) return;
                radii[i] = radius;
                float phase = BitConverter.ToSingle(wheelSurface, 0x7C - 8);
                phases[i] = Math.Abs(phase) <= 3.15f ? phase : 0f;
            }
            track.Wheels = wheels;
            track.WheelRadii = radii;
            track.WheelPhases = phases;
            track.WheelTravel = new float[count];
            for (int i = 0; i < count; i++) track.WheelTravel[i] = -1f;
        }
        float steering = detail != null && now - detail.SignalAt < 750 ? detail.Steering * 0.58f : 0f;
        for (int i = 0; i < count; i++)
        {
            IntPtr wheel = IntPtr.Add(wheels, 0x170 * i);
            setWheelSteering(wheel, steering);
            if (track.Suspension != null && track.Suspension[i] != 255)
            {
                float target = track.Suspension[i] / 100f;
                float current = track.WheelTravel[i];
                track.WheelTravel[i] = current < 0f ? target : current + (target - current) * Math.Min(1f, elapsed / 65f);
                wheelPhaseValue[0] = track.WheelTravel[i];
                Marshal.Copy(wheelPhaseValue, 0, IntPtr.Add(wheel, 0x78), 1);
            }
            if (distance == 0f) continue;
            float phase = (float)Math.IEEERemainder(track.WheelPhases[i] - distance / track.WheelRadii[i], 2 * Math.PI);
            track.WheelPhases[i] = phase;
            wheelPhaseValue[0] = phase;
            Marshal.Copy(wheelPhaseValue, 0, IntPtr.Add(wheel, 0x7C), 1);
        }
    }

    private void SetCarPose(string key, Vehicle car, PoseTrack track, long now, int buffer, int cadence)
    {
        if (Equals(car, drivingCar)) return;
        VehicleDetails detail;
        vehicleDetails.TryGetValue(key, out detail);
        DriveReplicaControls(car, detail, now);
        bool initial = track.AppliedAt == 0;
        long frameTime = initial ? 16 : now - track.AppliedAt;
        if (!initial && (frameTime <= 0 || frameTime < cadence)) return;
        long elapsed = Math.Min(frameTime, 55);
        track.AppliedAt = now;
        Pose pose = track.Read(now - buffer, 100);
        if (initial) track.Presented = car.Position;
        Vector3 presented = track.Presented;
        Vector3 physical = cadence == 0 || now >= track.NextIdleCheck ? car.Position : presented;
        if (cadence == 0 && SquaredDistance(physical, presented) < 6.25f &&
            SquaredDistance(physical, Player.Character.Position) < 144f)
            presented = Lerp(presented, physical, 0.75f);
        Vector3 next = AdvanceCarPosition(presented, pose, elapsed);
        bool moving = SquaredDistance(next, presented) > 0.0004f;
        if (moving || now >= track.NextIdleCheck)
        {
            if (SquaredDistance(physical, next) > (moving ? 0.0016f : 0.04f))
                GTA.Native.Function.Call("SET_CAR_COORDINATES_NO_OFFSET", car, next.X, next.Y, next.Z);
            track.NextIdleCheck = now + 220;
            track.Presented = next;
        }
        if (pose.Orientation.HasValue)
        {
            Quaternion from = track.PresentedOrientation ?? car.RotationQuaternion;
            Quaternion to = pose.Orientation.Value;
            float dot = from.X * to.X + from.Y * to.Y + from.Z * to.Z + from.W * to.W;
            if (Math.Abs(dot) < 0.99998f)
            {
                Quaternion rotation = BlendOrientation(from, to, Math.Min(1f, elapsed / 45f));
                car.RotationQuaternion = rotation;
                track.PresentedOrientation = rotation;
            }
            else track.PresentedOrientation = from;
        }
        else if (moving) car.Heading = pose.Heading;

        if (moving || track.Suspension != null || now >= track.NextWheelSpeed)
        {
            track.NextWheelSpeed = now + 100;
            float distance = 0f;
            if (moving)
            {
                Vector3 direction = car.Direction;
                distance = (next.X - presented.X) * direction.X + (next.Y - presented.Y) * direction.Y +
                    (next.Z - presented.Z) * direction.Z;
            }
            AnimateReplicaWheels(car, track, detail, distance, now, elapsed);
        }
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
        ClearWorldPeds();
    }

    private void ClearWorldPeds()
    {
        foreach (string key in new List<string>(worldPeds.Keys)) RemoveWorldEntity(key);
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
            ReleaseReplica(key, car);
        }
    }
}
