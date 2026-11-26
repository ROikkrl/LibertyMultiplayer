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

struct Player {
    state: String,
    seen: Instant,
    modern: bool,
    shares_world: bool,
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

fn parse_update(payload: &str) -> Option<(&str, &str, String, bool, bool)> {
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
    if let Some(meta) = metadata {
        if state.split_ascii_whitespace().count() < 9 {
            return None;
        }
        let mut parts = meta.split(',');
        for _ in 0..2 {
            let value = parts.next()?;
            if value.is_empty() || value.len() > 12 || !value.bytes().all(|byte| byte.is_ascii_digit()) {
                return None;
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
    if fields.next().is_some() || state.len() > 360 {
        return None;
    }
    Some((player, challenge, state, metadata.is_some(), shares_world))
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
    let mut world_owner: Option<String> = None;
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
        let (id, challenge, state, modern, shares_world) = match parse_update(payload) {
            Some(update) => update,
            None => continue,
        };

        let now = Instant::now();
        let update = Player { state, seen: now, modern, shares_world };
        if let Some(player) = players.get_mut(id) {
            *player = update;
        } else if players.len() < MAX_PLAYERS {
            players.insert(id.to_owned(), update);
            event(&mut log, &format!("connect player={id} online={}/{}", players.len(), MAX_PLAYERS));
        } else {
            continue;
        }

        if !world_owner.as_deref().and_then(|owner| players.get(owner))
            .map_or(false, |owner| owner.shares_world) {
            let next = players.iter().filter(|(_, peer)| peer.shares_world)
                .map(|(id, _)| id).min().cloned();
            if world_owner != next {
                event(&mut log, &format!("world_owner={}", next.as_deref().unwrap_or("-")));
                world_owner = next;
            }
        }
        snapshot.clear();
        if modern {
            writeln!(&mut snapshot, "{challenge} {}", world_owner.as_deref().unwrap_or("-"))
                .expect("writing to String");
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
            writeln!(&mut snapshot, "{peer_id} {state}").expect("writing to String");
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
