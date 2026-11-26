use hmac::{Hmac, Mac};
use sha2::Sha256;
use std::collections::HashMap;
use std::env;
use std::error::Error;
use std::fmt::Write as _;
use std::fs;
use std::io::{self, Write};
use std::net::{Ipv4Addr, UdpSocket};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

type PacketMac = Hmac<Sha256>;

const MAX_PLAYERS: usize = 4;
const PACKET_LIMIT: usize = 1200;
const SIGNATURE_LEN: usize = 32;
const PLAYER_TIMEOUT: Duration = Duration::from_secs(3);

type Position = [f64; 3];
type Sector = (i32, i32);

struct Player {
    state: String,
    seen: Instant,
    modern: bool,
    shares_world: bool,
    position: Position,
    sample: u64,
    driving: bool,
    vehicle_token: i32,
    seated: bool,
    combat: Option<CombatInput>,
    result: CombatResult,
}

#[derive(Clone, Copy)]
struct CombatInput {
    enabled: bool,
    ragdoll: bool,
    dead: bool,
    sequence: u64,
    kind: u8,
}

#[derive(Default)]
struct CombatResult {
    acknowledged: u64,
    damage: u32,
    hit_kind: u8,
    hit_until: Option<Instant>,
    last_attack: Option<Instant>,
    last_ram: Option<Instant>,
}

struct Update<'a> {
    id: &'a str,
    challenge: &'a str,
    player: Player,
    target: &'a str,
}

fn valid_id(value: &str) -> bool {
    value.len() == 16
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn coordinate(value: &str) -> Option<f64> {
    let number: f64 = value.parse().ok()?;
    (-20_000.0..=20_000.0).contains(&number).then_some(number)
}

fn authenticate<'a>(packet: &'a [u8], signer: &PacketMac) -> Option<&'a str> {
    if !(SIGNATURE_LEN..=PACKET_LIMIT).contains(&packet.len()) {
        return None;
    }
    let (signature, payload) = packet.split_at(SIGNATURE_LEN);
    let mut mac = signer.clone();
    mac.update(payload);
    mac.verify_slice(signature).ok()?;
    std::str::from_utf8(payload).ok()
}

fn parse_update(payload: &str, now: Instant) -> Option<Update<'_>> {
    let (payload, combat_data) = match payload.rsplit_once(" !") {
        Some((state, data)) => (state, Some(data)),
        None => (payload, None),
    };
    let (payload, metadata) = match payload.rsplit_once(" ~") {
        Some((state, meta)) => (state, Some(meta)),
        None => (payload, None),
    };
    let mut fields = payload.split_ascii_whitespace();
    if fields.next()? != "LF1" {
        return None;
    }
    let player = fields.next()?;
    let challenge = fields.next()?;
    if !valid_id(player) || !valid_id(challenge) {
        return None;
    }
    let position = [
        coordinate(fields.next()?)?,
        coordinate(fields.next()?)?,
        coordinate(fields.next()?)?,
    ];
    let [x, y, z] = position;
    let mut state = format!("{x:.2} {y:.2} {z:.2}");
    let mut driving = false;
    let mut vehicle_token = 0;
    let mut seated = false;
    if let Some(raw_heading) = fields.next() {
        let heading: f64 = raw_heading.parse().ok()?;
        let ped: i32 = fields.next()?.parse().ok()?;
        let car: i32 = fields.next()?.parse().ok()?;
        let handle: i32 = fields.next()?.parse().ok()?;
        let ride = fields.next()?;
        let valid_ride = ride == "-" || ride.split_once(':').map_or(false, |(owner, token)| {
            valid_id(owner) && token.parse::<i32>().is_ok() && token.len() <= 11
        });
        if !(0.0..=360.0).contains(&heading) || !valid_ride ||
            (car != 0 && (handle == 0 || ride != "-")) {
            return None;
        }
        driving = car != 0;
        vehicle_token = handle;
        seated = driving || ride != "-";
        write!(&mut state, " {heading:.2} {ped} {car} {handle} {ride}").ok()?;
        if let Some(world) = fields.next() {
            if world.len() > 250 {
                return None;
            }
            if world != "-" {
                for (index, item) in world.split(';').enumerate() {
                    let mut parts = item.split(',');
                    if index >= 4 || !matches!(parts.next(), Some("p" | "v")) {
                        return None;
                    }
                    for _ in 0..6 {
                        let value = parts.next()?;
                        if value.len() > 12 || value.parse::<i32>().is_err() {
                            return None;
                        }
                    }
                    let extra: Vec<&str> = parts.collect();
                    if !extra.is_empty() && (!item.starts_with("v,") || extra.len() != 4 ||
                        !extra.iter().all(|part| part.len() <= 5 &&
                            part.parse::<i32>().map_or(false, |value| (-1000..=1000).contains(&value)))) {
                        return None;
                    }
                }
            }
            write!(&mut state, " {world}").ok()?;
            if let Some(physics) = fields.next() {
                if car == 0 || physics.len() > 80 {
                    return None;
                }
                let mut parts = physics.split(',');
                let mut values = [0.0_f64; 7];
                for value in &mut values {
                    *value = parts.next()?.parse().ok()?;
                }
                let length_squared: f64 = values[..4].iter().map(|value| value * value).sum();
                if let Some(mask) = parts.next() {
                    if !mask.parse::<u8>().map_or(false, |value| value <= 15) {
                        return None;
                    }
                }
                if parts.next().is_some() || !(0.5..=1.5).contains(&length_squared) ||
                    !(-4000.0..=1000.0).contains(&values[4]) ||
                    !(0.0..=1000.0).contains(&values[5]) ||
                    !(-4000.0..=1000.0).contains(&values[6]) {
                    return None;
                }
                write!(&mut state, " {physics}").ok()?;
                if let Some(station) = fields.next() {
                    let index: i32 = station.parse().ok()?;
                    if !(-1..=19).contains(&index) {
                        return None;
                    }
                    write!(&mut state, " {index}").ok()?;
                }
            }
        }
    }
    let mut shares_world = false;
    let mut sample = 0;
    if let Some(meta) = metadata {
        if state.split_ascii_whitespace().count() < 9 {
            return None;
        }
        let mut parts = meta.split(',');
        for index in 0..2 {
            let value = parts.next()?;
            if value.is_empty() || value.len() > 12 || !value.bytes().all(|byte| byte.is_ascii_digit()) {
                return None;
            }
            if index == 0 {
                sample = value.parse().ok()?;
            }
        }
        shares_world = match parts.next()? {
            "0" => false,
            "1" => true,
            _ => return None,
        };
        if !matches!(parts.next()?, "0" | "1" | "2" | "3") {
            return None;
        }
        let station: i16 = parts.next()?.parse().ok()?;
        let name = parts.next()?;
        if !(-1..=255).contains(&station) || name.is_empty() || name.len() > 32 ||
            (name != "-" && !name.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')) ||
            parts.next().is_some() {
            return None;
        }
        write!(&mut state, " ~{meta}").ok()?;
    }
    if fields.next().is_some() || state.len() > 332 {
        return None;
    }
    let mut target = "-";
    let combat = if let Some(data) = combat_data {
        metadata?;
        let mut parts = data.split(',');
        let flags: u8 = parts.next()?.parse().ok()?;
        let sequence: u64 = parts.next()?.parse().ok()?;
        let kind: u8 = parts.next()?.parse().ok()?;
        target = parts.next()?;
        if flags > 7 || kind > 2 || parts.next().is_some() ||
            (kind == 0 && target != "-") || (kind != 0 && (!valid_id(target) || sequence == 0)) {
            return None;
        }
        Some(CombatInput { enabled: flags & 1 != 0, ragdoll: flags & 2 != 0,
            dead: flags & 4 != 0, sequence, kind })
    } else {
        None
    };
    Some(Update { id: player, challenge, target, player: Player {
        state, seen: now, modern: metadata.is_some(), shares_world, position, sample,
        driving, vehicle_token, seated, combat, result: CombatResult::default(),
    } })
}

fn sector(position: Position) -> Sector {
    ((position[0] / 160.0).floor() as i32, (position[1] / 160.0).floor() as i32)
}

fn distance_squared(a: Position, b: Position) -> f64 {
    (a[0] - b[0]).powi(2) + (a[1] - b[1]).powi(2) + (a[2] - b[2]).powi(2)
}

fn ram_speed(previous: (Position, u64), new: &Player, victim: Position) -> Option<f64> {
    let milliseconds = new.sample.checked_sub(previous.1)?;
    if !(15..=300).contains(&milliseconds) {
        return None;
    }
    let a = previous.0;
    let b = new.position;
    let dx = b[0] - a[0];
    let dy = b[1] - a[1];
    let length_squared = dx * dx + dy * dy;
    let speed = length_squared.sqrt() * 1_000.0 / milliseconds as f64;
    if !(5.0..=100.0).contains(&speed) {
        return None;
    }
    let t = (((victim[0] - a[0]) * dx + (victim[1] - a[1]) * dy) / length_squared).clamp(0.0, 1.0);
    let z = a[2] + (b[2] - a[2]) * t;
    let lateral = (victim[0] - a[0] - dx * t).powi(2) + (victim[1] - a[1] - dy * t).powi(2);
    (lateral <= 1.55_f64.powi(2) && (z - victim[2]).abs() <= 1.5).then_some(speed)
}

fn ready(previous: Option<Instant>, now: Instant, millis: u64) -> bool {
    previous.map_or(true, |time| now.duration_since(time) >= Duration::from_millis(millis))
}

fn eligible(player: &Player, now: Instant) -> bool {
    player.combat.map_or(false, |state| state.enabled && !state.dead) &&
        now.duration_since(player.seen) < Duration::from_millis(800)
}

fn apply_hit(player: &mut Player, kind: u8, damage: u32, now: Instant) {
    player.result.damage = player.result.damage.saturating_add(damage);
    player.result.hit_kind = kind;
    player.result.hit_until = Some(now + Duration::from_millis(if kind == 3 { 1300 } else { 650 }));
    if kind == 3 {
        player.result.last_ram = Some(now);
    }
}

fn simulate_hits(players: &mut HashMap<String, Player>, id: &str, target: &str,
                 previous: Option<(Position, u64)>, now: Instant, log: &mut Option<fs::File>) {
    let attack = players.get(id).and_then(|player| player.combat);
    if let Some(input) = attack {
        let player = &mut players.get_mut(id).expect("sender exists");
        if input.sequence > player.result.acknowledged {
            player.result.acknowledged = input.sequence;
            let interval = if input.kind == 1 { 330 } else { 95 };
            if input.kind != 0 && !input.ragdoll && eligible(player, now) &&
                ready(player.result.last_attack, now, interval) && target != id {
                player.result.last_attack = Some(now);
                let origin = player.position;
                let range = if input.kind == 1 { 3.2_f64 } else { 180.0_f64 };
                if let Some(victim) = players.get_mut(target) {
                    if eligible(victim, now) && distance_squared(origin, victim.position) <= range.powi(2) {
                        apply_hit(victim, input.kind, if input.kind == 1 { 12 } else { 24 }, now);
                        event(log, &format!("pvp_hit kind={} source={id} target={target}", input.kind));
                    }
                }
            }
        }
    }
    if let Some(previous) = previous {
        let driver = &players[id];
        if !driver.driving || !eligible(driver, now) {
            return;
        }
        let hits: Vec<_> = players.iter().filter_map(|(peer_id, victim)| {
            if peer_id == id || victim.seated || !eligible(victim, now) ||
                !ready(victim.result.last_ram, now, 1600) {
                return None;
            }
            ram_speed(previous, driver, victim.position).map(|speed| (peer_id.clone(), speed))
        }).collect();
        for (peer, speed) in hits {
            apply_hit(players.get_mut(&peer).expect("victim exists"), 3,
                (speed * 2.0).round().clamp(10.0, 75.0) as u32, now);
            event(log, &format!("pvp_hit kind=3 source={id} target={peer}"));
        }
    }
}

fn event(log: &mut Option<fs::File>, message: &str) {
    let timestamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs();
    println!("{timestamp} {message}");
    if let Some(file) = log {
        if let Err(error) = writeln!(file, "{timestamp} {message}") {
            eprintln!("Could not write server log: {error}");
        }
    }
}

fn reply(socket: &UdpSocket, signer: &PacketMac, payload: &[u8], sender: std::net::SocketAddr,
         outgoing: &mut [u8], log: &mut Option<fs::File>) {
    let size = SIGNATURE_LEN + payload.len();
    if size > outgoing.len() {
        return;
    }
    let mut mac = signer.clone();
    mac.update(payload);
    outgoing[..SIGNATURE_LEN].copy_from_slice(&mac.finalize().into_bytes());
    outgoing[SIGNATURE_LEN..size].copy_from_slice(payload);
    if let Err(error) = socket.send_to(&outgoing[..size], sender) {
        if !matches!(error.kind(), io::ErrorKind::Interrupted | io::ErrorKind::ConnectionReset) {
            event(log, &format!("send_error {sender} {error}"));
        }
    }
}

fn serve(socket: UdpSocket, signer: PacketMac, mut log: Option<fs::File>) -> io::Result<()> {
    socket.set_read_timeout(Some(Duration::from_millis(500)))?;
    let mut players = HashMap::<String, Player>::with_capacity(MAX_PLAYERS);
    let mut incoming = [0u8; 65_536];
    let mut outgoing = [0u8; PACKET_LIMIT];
    let mut snapshot = String::with_capacity(PACKET_LIMIT - SIGNATURE_LEN);
    let mut region_owners = HashMap::<Sector, String>::new();
    loop {
        let now = Instant::now();
        players.retain(|id, player| {
            let alive = now.duration_since(player.seen) < PLAYER_TIMEOUT;
            if !alive {
                event(&mut log, &format!("disconnect player={id} reason=timeout"));
            }
            alive
        });
        let (received, sender) = match socket.recv_from(&mut incoming) {
            Ok(message) => message,
            Err(error) if matches!(error.kind(), io::ErrorKind::Interrupted |
                io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut | io::ErrorKind::ConnectionReset) => continue,
            Err(error) => return Err(error),
        };
        let payload = match authenticate(&incoming[..received], &signer) {
            Some(payload) => payload,
            None => continue,
        };
        if let Some(challenge) = payload.strip_prefix("LF-PING ") {
            if valid_id(challenge) {
                let pong = format!("LF-PONG {challenge}");
                reply(&socket, &signer, pong.as_bytes(), sender, &mut outgoing, &mut log);
            }
            continue;
        }
        let now = Instant::now();
        let Update { id, challenge, target, player: update } = match parse_update(payload, now) {
            Some(update) => update,
            None => continue,
        };

        let modern = update.modern;
        let modern_combat = update.combat.is_some();
        let region = sector(update.position);
        let mut previous = None;
        if let Some(player) = players.get_mut(id) {
            if player.driving && update.driving && player.vehicle_token == update.vehicle_token {
                previous = Some((player.position, player.sample));
            }
            let result = std::mem::take(&mut player.result);
            *player = Player { result, ..update };
        } else if players.len() < MAX_PLAYERS {
            players.insert(id.to_owned(), update);
            event(&mut log, &format!("connect player={id} online={}/{}", players.len(), MAX_PLAYERS));
        } else {
            continue;
        }

        simulate_hits(&mut players, id, target, previous, now, &mut log);
        region_owners.retain(|region, owner| players.get(owner)
            .map_or(false, |peer| peer.shares_world && sector(peer.position) == *region));
        for (peer_id, peer) in &players {
            if peer.shares_world {
                region_owners.entry(sector(peer.position)).or_insert_with(|| peer_id.clone());
            }
        }
        let owner = region_owners.get(&region).map_or("-", String::as_str);
        snapshot.clear();
        if modern_combat {
            let local = &players[id].result;
            writeln!(&mut snapshot, "{challenge} {owner} {} {} {}", local.acknowledged,
                local.damage, local.hit_kind).expect("writing to String");
        } else if modern {
            writeln!(&mut snapshot, "{challenge} {owner}").expect("writing to String");
        } else {
            writeln!(&mut snapshot, "{challenge}").expect("writing to String");
        }
        for (peer_id, peer) in &players {
            if peer_id == id {
                continue;
            }
            let state = if !modern && peer.modern {
                peer.state.split_once(" ~").map_or(peer.state.as_str(), |(old, _)| old)
            } else {
                &peer.state
            };
            write!(&mut snapshot, "{peer_id} ").expect("writing to String");
            if peer_id == owner {
                snapshot.push_str(state);
            } else {
                for (index, field) in state.split_ascii_whitespace().enumerate() {
                    if index != 0 {
                        snapshot.push(' ');
                    }
                    snapshot.push_str(if index == 8 { "-" } else { field });
                }
            }
            if modern_combat {
                if let Some(input) = peer.combat {
                    let ragdoll = input.ragdoll || peer.result.hit_until.map_or(false, |time| time > now);
                    let flags = u8::from(input.enabled) | (u8::from(ragdoll) << 1) |
                        (u8::from(input.dead) << 2);
                    write!(&mut snapshot, " !{flags}").expect("writing to String");
                }
            }
            snapshot.push('\n');
        }
        reply(&socket, &signer, snapshot.as_bytes(), sender, &mut outgoing, &mut log);
    }
}

fn main() -> Result<(), Box<dyn Error>> {
    let bind: Ipv4Addr = env::var("LF_BIND").unwrap_or_else(|_| "127.0.0.1".into()).parse()?;
    let port: u16 = env::var("LF_PORT").unwrap_or_else(|_| "24840".into()).parse()?;
    if port == 0 {
        return Err("LF_PORT must be in 1..65535".into());
    }
    let key_path = env::var_os("LF_KEY_FILE").ok_or("Set LF_KEY_FILE to the session key file")?;
    let key = fs::read_to_string(key_path)?;
    let key = key.trim_end_matches(&['\r', '\n'][..]);
    if key.chars().count() < 24 || key.contains(&['\r', '\n'][..]) {
        return Err("The session key file must contain one line of at least 24 characters".into());
    }
    let signer = PacketMac::new_from_slice(key.as_bytes()).map_err(|_| "Invalid signing key")?;
    let socket = UdpSocket::bind((bind, port))?;
    let mut log = env::var_os("LF_LOG_FILE")
        .map(|path| fs::OpenOptions::new().create(true).append(true).open(path))
        .transpose()?;
    event(&mut log, &format!("Liberty Friends listening on {bind}:{port}/UDP"));
    serve(socket, signer, log)?;
    Ok(())
}
