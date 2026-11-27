use super::{distance_squared, valid_id, Player};
use std::collections::{HashMap, HashSet, VecDeque};
use std::net::SocketAddr;
use std::time::{Duration, Instant};

const MAX_PAGES: usize = 12;
const PAGE_ROWS: usize = 5;
const SOURCE_RADIUS: f64 = 90.0;
const KEEP_SOURCE_RADIUS: f64 = 105.0;
const CAPTURE_RADIUS: f64 = 150.0;
const VIEW_RADIUS: f64 = 118.0;
const TRAFFIC_CELL: f64 = 64.0;
const FRAME_TTL: Duration = Duration::from_millis(1200);
const PRESENCE_TTL: Duration = Duration::from_millis(1800);
const VEHICLE_TTL: Duration = Duration::from_secs(3);
const OCCUPIED_VEHICLE_TTL: Duration = Duration::from_secs(20);
const SIMULATION_RADIUS: f64 = 115.0;
const MAX_TRAFFIC: usize = 384;
const MAX_DELEGATED: usize = 20;
const MAX_PRIORITY_SECONDS: u64 = 3600;
const WORLD_REPLY_TTL: Duration = Duration::from_secs(2);
const WORLD_REPLY_LIMIT: usize = 20;
const INCOMING_LIMIT: usize = 6;

pub fn priority(power: u8, seconds: u64) -> u64 {
    u64::from(power) * seconds.min(MAX_PRIORITY_SECONDS)
}

struct Entity {
    kind: u8,
    id: i32,
    model: i32,
    position: [f64; 3],
    row: String,
    seen: Instant,
    sequence: u64,
    origin: Option<String>,
}

impl Entity {
    fn parse(row: &str, seen: Instant, sequence: u64) -> Option<Self> {
        if row.len() > 181 {
            return None;
        }
        let mut fields = row.split(',');
        let kind = match fields.next()? {
            "p" => b'p',
            "v" => b'v',
            "m" => b'm',
            "d" => b'd',
            _ => return None,
        };
        let id = fields.next()?.parse::<i32>().ok()?;
        let model = fields.next()?.parse::<i32>().ok()?;
        if id == 0 || model == 0 {
            return None;
        }
        let mut position = [0.0; 3];
        let scale = 100;
        for axis in &mut position {
            let value = bounded(fields.next()?, -20_000 * scale, 20_000 * scale)?;
            *axis = f64::from(value) / f64::from(scale);
        }
        bounded(fields.next()?, 0, 359)?;
        if kind == b'p' {
            bounded(fields.next()?, 0, 4)?;
            for _ in 0..3 {
                bounded(fields.next()?, -3000, 3000)?;
            }
        } else {
            let mut norm = 0;
            for _ in 0..4 {
                let value = bounded(fields.next()?, -1000, 1000)?;
                norm += value * value;
            }
            if !(500_000..=1_500_000).contains(&norm) {
                return None;
            }
            fields.next()?.parse::<u32>().ok()?;
            bounded(fields.next()?, 0, 511)?;
            fields.next()?.parse::<u32>().ok()?;
            bounded(fields.next()?, -1, 255)?;
            fields.next()?.parse::<i32>().ok()?;
            bounded(fields.next()?, -4000, 1000)?;
            bounded(fields.next()?, 0, 1000)?;
            bounded(fields.next()?, -4000, 1000)?;
            bounded(fields.next()?, -1, 15)?;
            bounded(fields.next()?, -15_000, 15_000)?;
            bounded(fields.next()?, -15_000, 15_000)?;
            bounded(fields.next()?, -6_000, 6_000)?;
            bounded(fields.next()?, -1_000, 1_000)?;
            bounded(fields.next()?, 0, 60_000)?;
        }
        let (origin, row) = if kind == b'm' {
            let (rest, source) = row[1..].rsplit_once(',')?;
            if fields.next()? != source || !valid_id(source) {
                return None;
            }
            (Some(source.to_owned()), format!("v{rest}"))
        } else {
            (None, row.to_owned())
        };
        if fields.next().is_some() {
            return None;
        }
        Some(Self { kind, id, model, position, row, seen, sequence, origin })
    }
}

fn bounded(raw: &str, min: i32, max: i32) -> Option<i32> {
    let value = raw.parse().ok()?;
    (min..=max).contains(&value).then_some(value)
}

fn traffic_cell_center(position: [f64; 3]) -> [f64; 3] {
    let center = |axis: f64| (axis.div_euclid(TRAFFIC_CELL) + 0.5) * TRAFFIC_CELL;
    [center(position[0]), center(position[1]), position[2]]
}

struct Frame {
    sequence: u64,
    at: Instant,
    ambient: bool,
    entities: Vec<Entity>,
}

struct Incoming {
    sequence: u64,
    pages: Vec<Option<Vec<Entity>>>,
    repair_sent: bool,
}

struct ReplyPages {
    challenge: String,
    sent: Instant,
    pages: Vec<String>,
    repaired: bool,
}

struct Publisher {
    seen: Instant,
    episode: u8,
    incoming: Vec<Incoming>,
    replies: VecDeque<ReplyPages>,
    frame: Option<Frame>,
}

type TrafficKey = (String, u8, i32, i32);

pub fn traffic_key(value: &str) -> Option<(&str, u8, i32, i32)> {
    if value.len() > 42 {
        return None;
    }
    let mut fields = value.split(':');
    let origin = fields.next()?;
    if !valid_id(origin) { return None; }
    let mode = match fields.next()? { "v" => b'v', "c" => b'c', _ => return None };
    let handle: i32 = fields.next()?.parse().ok()?;
    let model: i32 = fields.next()?.parse().ok()?;
    (handle != 0 && model != 0 && fields.next().is_none()).then_some((origin, mode, handle, model))
}

struct Transfer {
    driver: String,
    simulator: String,
    position: [f64; 3],
    until: Instant,
    vehicle_token: Option<i32>,
    episode: u8,
}

impl Transfer {
    fn committed(&self) -> bool {
        self.vehicle_token.is_some()
    }
}

struct Traffic {
    state: Entity,
    simulator: String,
    episode: u8,
    delegated: bool,
    assigned_at: Instant,
}

#[derive(Default)]
pub struct World {
    publishers: HashMap<String, Publisher>,
    sources: HashMap<String, String>,
    transfers: HashMap<TrafficKey, Transfer>,
    traffic: HashMap<TrafficKey, Traffic>,
    traffic_epoch: Option<Instant>,
    hosts: HashMap<u8, String>,
    events: Vec<String>,
    bowling: crate::bowling::Bowling,
}

impl World {
    pub fn reconcile(&mut self, players: &HashMap<String, Player>, now: Instant) {
        self.bowling.tick(players, now);
        self.publishers.retain(|id, entry| {
            players.contains_key(id) && now.duration_since(entry.seen) < PRESENCE_TTL
        });
        self.transfers.retain(|key, transfer| {
            if let Some(token) = transfer.vehicle_token {
                if players.get(&transfer.driver).and_then(|driver| driver.owned_vehicle) == Some((token, key.3)) {
                    transfer.until = now + OCCUPIED_VEHICLE_TTL;
                }
            }
            now < transfer.until && (transfer.committed() || players.contains_key(&transfer.driver))
        });
        let mut ordered: Vec<_> = self.publishers.iter().collect();
        ordered.sort_unstable_by_key(|(id, _)| {
            let player = &players[*id];
            (std::cmp::Reverse(priority(player.power, player.session_seconds(now))),
                player.joined, id.as_str())
        });
        let mut sources = HashMap::with_capacity(ordered.len());
        let old_hosts = std::mem::take(&mut self.hosts);
        for (source, publisher) in ordered.iter().copied() {
            self.hosts.entry(publisher.episode).or_insert_with(|| source.clone());
            if sources.contains_key(source) {
                continue;
            }
            for (id, member) in ordered.iter().copied() {
                let nearby_before = self.sources.get(id) == Some(source) || self.sources.get(source) == Some(id);
                let radius = if nearby_before { KEEP_SOURCE_RADIUS } else { SOURCE_RADIUS };
                if member.episode == publisher.episode &&
                    distance_squared(players[source].position, players[id].position) <= radius.powi(2)
                {
                    sources.entry(id.clone()).or_insert_with(|| source.clone());
                }
            }
        }
        for episode in 0..=2 {
            if old_hosts.get(&episode) != self.hosts.get(&episode) {
                let before = old_hosts.get(&episode).map_or("-", String::as_str);
                if let Some(id) = self.hosts.get(&episode) {
                    let player = &players[id];
                    let seconds = player.session_seconds(now);
                    let score = priority(player.power, seconds);
                    self.events.push(format!(
                        "host_change episode={episode} previous={before} player={id} ip={} power={} session_seconds={seconds} priority={score}",
                        player.address.ip(), player.power));
                } else {
                    self.events.push(format!("host_change episode={episode} previous={before} player=-"));
                }
            }
        }
        // Pedestrian frames follow the area's crowd source. Vehicles keep their simulator.
        for (id, publisher) in &mut self.publishers {
            let was_source = self.sources.get(id) == Some(id);
            let is_source = sources.get(id) == Some(id);
            if was_source != is_source {
                if let Some(frame) = &mut publisher.frame {
                    frame.ambient = false;
                    frame.entities.retain(|item| item.kind == b'd');
                }
            }
        }
        self.reconcile_traffic(players, now);
        self.sources = sources;
    }

    fn reconcile_traffic(&mut self, players: &HashMap<String, Player>, now: Instant) {
        let publishers = &self.publishers;
        let nearby = |vehicle: &Traffic, id: &str, radius: f64| {
            publishers.get(id).map_or(false, |peer| peer.episode == vehicle.episode &&
                distance_squared(players[id].position, vehicle.state.position) <= radius.powi(2))
        };
        self.traffic.retain(|_, vehicle| {
            let occupied = publishers.keys().any(|id| nearby(vehicle, id, SIMULATION_RADIUS));
            now.duration_since(vehicle.state.seen) <
                if occupied { OCCUPIED_VEHICLE_TTL } else { VEHICLE_TTL }
        });

        let can_keep = |vehicle: &Traffic| {
            nearby(vehicle, &vehicle.simulator, SIMULATION_RADIUS) &&
                (now.duration_since(vehicle.state.seen) < FRAME_TTL ||
                    vehicle.delegated && now.duration_since(vehicle.assigned_at) < FRAME_TTL)
        };
        let mut delegated = HashMap::<String, usize>::new();
        for vehicle in self.traffic.values().filter(|vehicle| vehicle.delegated && can_keep(vehicle)) {
            *delegated.entry(vehicle.simulator.clone()).or_default() += 1;
        }
        let mut waiting: Vec<_> = self.traffic.iter_mut().filter(|(_, vehicle)| !can_keep(vehicle)).collect();
        let approach = |vehicle: &Traffic| {
            publishers.iter().filter(|(_, publisher)| publisher.episode == vehicle.episode)
                .map(|(id, _)| distance_squared(players[id].position, vehicle.state.position))
                .fold(f64::INFINITY, f64::min)
        };
        waiting.sort_unstable_by(|a, b| approach(a.1).total_cmp(&approach(b.1)).then_with(|| a.0.cmp(b.0)));
        for (_, vehicle) in waiting {
            let available = publishers.keys().filter(|id| nearby(vehicle, id, SIMULATION_RADIUS) &&
                delegated.get(*id).copied().unwrap_or(0) < MAX_DELEGATED);
            let best = available.min_by(|a, b| {
                let distance = |id: &String| distance_squared(players[id].position, vehicle.state.position);
                distance(a).total_cmp(&distance(b)).then_with(|| a.cmp(b))
            });
            if let Some(id) = best {
                vehicle.simulator = id.clone();
                vehicle.delegated = true;
                vehicle.assigned_at = now;
                *delegated.entry(id.clone()).or_default() += 1;
            }
        }
    }

    pub fn remove(&mut self, id: &str) {
        self.bowling.remove(id);
        self.publishers.remove(id);
        self.transfers.retain(|_, transfer| transfer.committed() || transfer.driver != id);
    }

    pub fn events(&mut self) -> Vec<String> {
        std::mem::take(&mut self.events)
    }

    pub fn status(&self) -> (String, usize, usize, usize) {
        let hosts = (0..=2).filter_map(|episode|
            self.hosts.get(&episode).map(|id| format!("{episode}:{id}"))
        ).collect::<Vec<_>>().join(",");
        (hosts, self.traffic.len(), self.sources.len(), self.transfers.len())
    }

    pub fn role(&self, id: &str) -> (Option<u8>, &'static str) {
        let Some(publisher) = self.publishers.get(id) else { return (None, "connecting"); };
        let role = if self.hosts.get(&publisher.episode).map(String::as_str) == Some(id) {
            "host"
        } else if self.sources.get(id).map(String::as_str) == Some(id) {
            "area source"
        } else {
            "player"
        };
        (Some(publisher.episode), role)
    }

    fn observe_traffic(&mut self, id: &str, episode: u8, mut state: Entity,
                       players: &HashMap<String, Player>) {
        let origin = state.origin.take().unwrap_or_else(|| id.to_owned());
        let key = (origin, b'v', state.id, state.model);
        if let Some(transfer) = self.transfers.get_mut(&key) {
            if transfer.committed() {
                // Keep a tombstone while the original simulator is still publishing the old car.
                if id == key.0 || id == transfer.simulator {
                    transfer.until = state.seen + Duration::from_secs(20);
                }
                return;
            }
        }
        let delegated = state.kind == b'm';
        if let Some(vehicle) = self.traffic.get(&key) {
            if vehicle.simulator != id || vehicle.episode != episode || vehicle.delegated != delegated {
                return;
            }
        } else {
            if delegated || self.traffic.len() >= MAX_TRAFFIC {
                return;
            }
            // Choose a donor when the vehicle first appears, then keep its identity
            // and simulator as it crosses the boundaries of subsequent cells.
            let center = traffic_cell_center(state.position);
            let donor = self.publishers.iter().filter_map(|(candidate, publisher)| {
                let player = players.get(candidate)?;
                if publisher.episode != episode ||
                    distance_squared(state.position, player.position) > CAPTURE_RADIUS.powi(2) {
                    return None;
                }
                Some((candidate, distance_squared(center, player.position), player.joined))
            }).min_by(|a, b| a.1.total_cmp(&b.1).then_with(|| a.2.cmp(&b.2)).then_with(|| a.0.cmp(b.0)));
            if donor.map(|(candidate, _, _)| candidate.as_str()) != Some(id) ||
                self.traffic.values().any(|vehicle| vehicle.episode == episode &&
                    vehicle.state.model == state.model && vehicle.simulator != id &&
                    distance_squared(vehicle.state.position, state.position) < 4.0) {
                return;
            }
        }
        let epoch = self.traffic_epoch.get_or_insert(state.seen);
        let at = 1 + state.seen.duration_since(*epoch).as_millis() as u64;
        state.sequence = self.traffic.get(&key).map_or(at, |vehicle| at.max(vehicle.state.sequence + 1));
        let assigned_at = self.traffic.get(&key).map_or(state.seen, |vehicle| vehicle.assigned_at);
        self.traffic.insert(key, Traffic { state, simulator: id.to_owned(), episode, delegated, assigned_at });
    }

    fn claim_target(&self, key: &TrafficKey, driver: &str, players: &HashMap<String, Player>, now: Instant)
        -> Option<([f64; 3], String, u8)> {
        let (origin, mode, handle, model) = key;
        let episode = self.publishers.get(driver)?.episode;
        if *mode == b'v' {
            let vehicle = self.traffic.get(key)?;
            return (episode == vehicle.episode && now.duration_since(vehicle.state.seen) < OCCUPIED_VEHICLE_TTL)
                .then(|| (vehicle.state.position, vehicle.simulator.clone(), episode));
        }
        let owner = players.get(origin)?;
        let publisher = self.publishers.get(origin)?;
        if owner.driving || owner.owned_vehicle != Some((*handle, *model)) || publisher.episode != episode {
            return None;
        }
        let item = publisher.frame.as_ref()?.entities.iter().find(|item|
            item.kind == b'd' && item.id == *handle && item.model == *model &&
            now.duration_since(item.seen) < FRAME_TTL)?;
        Some((item.position, origin.clone(), episode))
    }

    pub fn commit_driving(&mut self, driver: &str, claim: &str, player: &Player,
                          players: &HashMap<String, Player>, now: Instant) -> bool {
        let (origin, mode, handle, model) = match traffic_key(claim) {
            Some(key) => key,
            None => return false,
        };
        if !player.driving || player.vehicle_model != model {
            return false;
        }
        if player.vehicle_origin.as_deref() == Some(claim) {
            return true;
        }
        let key = (origin.to_owned(), mode, handle, model);
        let (simulator, episode) = if let Some(transfer) = self.transfers.get(&key).filter(|transfer| now < transfer.until) {
            if transfer.driver != driver || (transfer.committed() ||
                self.publishers.get(driver).map(|peer| peer.episode) != Some(transfer.episode) ||
                (mode == b'c' && self.claim_target(&key, driver, players, now).is_none()) ||
                distance_squared(transfer.position, player.position) > 22.0_f64.powi(2)) {
                return false;
            }
            (transfer.simulator.clone(), transfer.episode)
        } else if let Some((position, simulator, episode)) = self.claim_target(&key, driver, players, now) {
            if distance_squared(position, player.position) > 11.0_f64.powi(2) {
                return false;
            }
            (simulator, episode)
        } else if origin == driver && mode == b'v' {
            let Some(publisher) = self.publishers.get(driver) else { return false; };
            (driver.to_owned(), publisher.episode)
        } else {
            return false;
        };
        self.traffic.remove(&key);
        self.events.push(format!("vehicle_committed player={driver} source={origin} type={} handle={handle} model={model} simulator={simulator}", char::from(mode)));
        self.transfers.insert(key, Transfer { driver: driver.to_owned(), simulator, episode,
            position: player.position, until: now + OCCUPIED_VEHICLE_TTL, vehicle_token: Some(player.vehicle_token) });
        true
    }

    pub fn revoked_vehicle(&self, id: &str, handle: i32, model: i32) -> bool {
        self.transfers.get(&(id.to_owned(), b'c', handle, model)).map_or(false, Transfer::committed)
    }

    fn transfer(&mut self, driver: &str, source: &str, mode: u8, handle: i32, model: i32,
                players: &HashMap<String, Player>, now: Instant) {
        let key = (source.to_owned(), mode, handle, model);
        let player = &players[driver];
        if self.transfers.contains_key(&key) || player.seated ||
            self.transfers.values().any(|item| item.driver == driver && !item.committed()) {
            return;
        }
        if let Some((position, simulator, episode)) = self.claim_target(&key, driver, players, now).filter(
            |(position, _, _)| distance_squared(*position, player.position) <= 11.0_f64.powi(2)) {
            self.events.push(format!("vehicle_reserved player={driver} source={source} type={} handle={handle} model={model}", char::from(mode)));
            self.transfers.insert(key, Transfer {
                driver: driver.to_owned(), position, simulator, episode,
                until: now + Duration::from_secs(3), vehicle_token: None,
            });
        }
    }

    pub fn handle(&mut self, payload: &str, sender: SocketAddr, players: &HashMap<String, Player>,
                  now: Instant) -> Option<Vec<String>> {
        let (header, body) = payload.split_once('\n').unwrap_or((payload, ""));
        let mut fields = header.split_ascii_whitespace();
        let action = fields.next()?;
        let id = fields.next()?;
        let player = players.get(id)?;
        if player.address != sender {
            return None;
        }
        match action {
            "LFWB" => {
                let sequence: u64 = fields.next()?.parse().ok()?;
                let command = fields.next()?;
                if fields.next().is_some() || !body.is_empty() { return None; }
                let publisher = self.publishers.get(id)?;
                self.bowling.handle(id, command, sequence, player, publisher.episode, now)?;
                None
            }
            "LFWR" => {
                let challenge = fields.next()?;
                let mask: u16 = fields.next()?.parse().ok()?;
                if fields.next().is_some() || !body.is_empty() || !valid_id(challenge) {
                    return None;
                }
                let publisher = self.publishers.get_mut(id)?;
                let reply = publisher.replies.iter_mut().find(|reply| reply.challenge == challenge &&
                    now.duration_since(reply.sent) < WORLD_REPLY_TTL && !reply.repaired)?;
                if mask == 0 || usize::from(mask) >= (1 << reply.pages.len()) {
                    return None;
                }
                reply.repaired = true;
                Some(reply.pages.iter().enumerate().filter(|(page, _)| mask & (1 << page) != 0)
                    .map(|(_, page)| page.clone()).collect())
            }
            "LFWT" => {
                let source = fields.next()?;
                let mode = match fields.next()? { "v" => b'v', "c" => b'c', _ => return None };
                let handle = fields.next()?.parse().ok()?;
                let model = fields.next()?.parse().ok()?;
                if fields.next().is_some() || !body.is_empty() || !valid_id(source) {
                    return None;
                }
                self.reconcile(players, now);
                self.transfer(id, source, mode, handle, model, players, now);
                None
            }
            "LFWQ" => {
                let challenge = fields.next()?;
                let episode: u8 = fields.next()?.parse().ok()?;
                if !valid_id(challenge) || episode > 2 || fields.next().is_some() || !body.is_empty() {
                    return None;
                }
                let publisher = self.publishers.entry(id.to_owned()).or_insert(Publisher {
                    seen: now, episode, incoming: Vec::new(), replies: VecDeque::new(), frame: None,
                });
                if publisher.episode != episode {
                    publisher.episode = episode;
                    publisher.incoming.clear();
                    publisher.replies.clear();
                    publisher.frame = None;
                }
                publisher.seen = now;
                self.reconcile(players, now);
                let pages = self.snapshot(id, challenge, player, players, now);
                let publisher = self.publishers.get_mut(id)?;
                publisher.replies.retain(|reply| now.duration_since(reply.sent) < WORLD_REPLY_TTL);
                if let Some(saved) = publisher.replies.iter().find(|reply| reply.challenge == challenge) {
                    return Some(saved.pages.clone());
                }
                if publisher.replies.len() >= WORLD_REPLY_LIMIT {
                    publisher.replies.pop_front();
                }
                publisher.replies.push_back(ReplyPages {
                    challenge: challenge.to_owned(), sent: now, pages: pages.clone(), repaired: false,
                });
                let mut outgoing = pages;
                if let Some(incoming) = publisher.incoming.iter_mut().max_by_key(|item| item.sequence) {
                    if !incoming.repair_sent {
                        let missing = incoming.pages.iter().enumerate().filter(|(_, page)| page.is_none())
                            .fold(0, |mask, (index, _)| mask | (1 << index));
                        if missing != 0 {
                            incoming.repair_sent = true;
                            outgoing.push(format!("LFWN {challenge} {} {missing}", incoming.sequence));
                        }
                    }
                }
                Some(outgoing)
            }
            "LFWU" => {
                let sequence: u64 = fields.next()?.parse().ok()?;
                let episode: u8 = fields.next()?.parse().ok()?;
                let index: usize = fields.next()?.parse().ok()?;
                let total: usize = fields.next()?.parse().ok()?;
                if fields.next().is_some() || total == 0 || total > MAX_PAGES || index >= total ||
                    sequence.abs_diff(player.sample) > 1500 {
                    return None;
                }
                let owns_area = self.sources.get(id).map_or(false, |source| source == id);
                let publisher = self.publishers.get_mut(id)?;
                if episode != publisher.episode ||
                    publisher.frame.as_ref().map_or(false, |frame| sequence <= frame.sequence) {
                    return None;
                }
                let mut entities = Vec::new();
                for row in body.lines() {
                    if entities.len() == PAGE_ROWS {
                        return None;
                    }
                    entities.push(Entity::parse(row, now, sequence)?);
                }
                let index_of_frame = match publisher.incoming.iter().position(|item| item.sequence == sequence) {
                    Some(index) => index,
                    None => {
                        if publisher.incoming.len() >= INCOMING_LIMIT {
                            let (oldest, pending) = publisher.incoming.iter().enumerate().min_by_key(|(_, item)| item.sequence)?;
                            if sequence < pending.sequence {
                                return None;
                            }
                            publisher.incoming.swap_remove(oldest);
                        }
                        publisher.incoming.push(Incoming { sequence, pages: (0..total).map(|_| None).collect(), repair_sent: false });
                        publisher.incoming.len() - 1
                    }
                };
                let pages = &mut publisher.incoming[index_of_frame];
                if total != pages.pages.len() || pages.pages[index].is_some() {
                    return None;
                }
                pages.pages[index] = Some(entities);
                if pages.pages.iter().all(Option::is_some) {
                    let complete = publisher.incoming.swap_remove(index_of_frame);
                    publisher.incoming.retain(|item| item.sequence > sequence);
                    let mut unique = HashSet::new();
                    let (traffic, entities) = complete.pages.into_iter().flatten().flatten().filter(|item| {
                        let distance = distance_squared(item.position, player.position);
                        let authorized = match item.kind {
                            b'd' => {
                                let radius: f64 = if player.driving { 15.0 } else { 100.0 };
                                player.owned_vehicle == Some((item.id, item.model)) && distance <= radius.powi(2)
                            }
                            b'm' => distance <= SIMULATION_RADIUS.powi(2),
                            b'v' => distance <= CAPTURE_RADIUS.powi(2),
                            _ => owns_area && distance <= CAPTURE_RADIUS.powi(2),
                        };
                        authorized && unique.insert((item.kind, item.origin.clone(), item.id))
                    }).partition::<Vec<_>, _>(|item| matches!(item.kind, b'v' | b'm'));
                    publisher.frame = Some(Frame { sequence, at: now, ambient: owns_area, entities });
                    for state in traffic {
                        self.observe_traffic(id, episode, state, players);
                    }
                }
                None
            }
            _ => None,
        }
    }

    fn snapshot(&self, id: &str, challenge: &str, player: &Player,
                players: &HashMap<String, Player>, now: Instant) -> Vec<String> {
        let source = self.sources.get(id).map_or(id, String::as_str);
        let episode = self.publishers[id].episode;
        let mut rows = Vec::new();
        let mut ambient_sequence = None;
        for (publisher_id, publisher) in &self.publishers {
            if publisher_id == id || publisher.episode != episode {
                continue;
            }
            if let Some(frame) = publisher.frame.as_ref().filter(|f| now.duration_since(f.at) < FRAME_TTL) {
                if publisher_id == source && frame.ambient {
                    ambient_sequence = Some(frame.sequence);
                }
                let origin = &players[publisher_id];
                for item in &frame.entities {
                    let eligible = if item.kind == b'd' {
                        origin.owned_vehicle == Some((item.id, item.model)) &&
                            !self.revoked_vehicle(publisher_id, item.id, item.model)
                    } else {
                        publisher_id == source
                    };
                    let distance = distance_squared(item.position, player.position);
                    if eligible && distance <= VIEW_RADIUS.powi(2) {
                        let priority = if item.kind == b'd' { 0 } else { 3 };
                        let previous = if item.kind == b'd' { origin.vehicle_origin.as_deref() } else { None };
                        rows.push((priority, distance, publisher_id, item.sequence, item.row.as_str(), previous));
                    }
                }
            }
        }
        let mut lines = Vec::new();
        if let Some(host) = self.hosts.get(&episode) {
            lines.push(format!("{host} 0 #\n"));
        }
        if let Some(bowling) = self.bowling.snapshot(id, episode, now) { lines.push(bowling); }
        let mut local: Vec<_> = self.traffic.iter().filter(|((origin, _, _, _), vehicle)| {
            origin == id && vehicle.episode == episode && !vehicle.delegated &&
                vehicle.simulator == id && now.duration_since(vehicle.state.seen) < FRAME_TTL
        }).collect();
        local.sort_unstable_by(|a, b| b.1.state.sequence.cmp(&a.1.state.sequence).then_with(|| a.0.cmp(b.0)));
        let handles = local.iter().take(32).map(|((_, _, handle, _), _)| format!("{handle:x}"))
            .collect::<Vec<_>>().join(",");
        lines.push(format!("{id} 0 +{handles}\n"));
        let mut notices: Vec<_> = self.transfers.iter().filter_map(|(key, transfer)| {
            if transfer.episode != episode { return None; }
            let direct = transfer.driver == id || key.0 == id || transfer.simulator == id;
            let nearby = distance_squared(transfer.position, player.position) <= VIEW_RADIUS.powi(2);
            (transfer.driver == id || transfer.committed() && (direct || nearby)).then_some((!direct, key, transfer))
        }).collect();
        notices.sort_unstable_by_key(|&(indirect, key, transfer)| (indirect, transfer.driver != id, key));
        for (_, (origin, mode, handle, model), transfer) in notices.into_iter().take(12) {
                let command = match (transfer.committed(), *mode, transfer.driver == id) {
                    (false, b'c', _) => "takec", (true, b'c', true) => "ownc", (true, b'c', false) => "dropc",
                    (false, _, _) => "take", (true, _, true) => "own", (true, _, false) => "drop",
                };
                lines.push(format!("{origin} 0 {command},{handle},{model}\n"));
        }
        for ((origin, _, handle, model), vehicle) in &self.traffic {
            if vehicle.episode != episode {
                continue;
            }
            let distance = distance_squared(vehicle.state.position, player.position);
            if vehicle.delegated && vehicle.state.kind == b'm' && vehicle.state.seen >= vehicle.assigned_at &&
                origin == id && vehicle.simulator != id {
                if distance <= SIMULATION_RADIUS.powi(2) {
                    lines.push(format!("{origin} 0 hide,{handle},{model}\n"));
                }
            }
            let simulation = vehicle.delegated && vehicle.simulator == id && distance <= SIMULATION_RADIUS.powi(2);
            if simulation || (vehicle.simulator != id && distance <= VIEW_RADIUS.powi(2)) {
                let priority = if simulation { 1 } else { 2 };
                rows.push((priority, distance, origin, vehicle.state.sequence, vehicle.state.row.as_str(), None));
            }
        }
        if let Some(sequence) = ambient_sequence {
            // An empty but complete source frame still represents the shared world.
            lines.push(format!("{source} {sequence} @\n"));
        }
        lines.truncate(MAX_PAGES * PAGE_ROWS);
        let capacity = MAX_PAGES * PAGE_ROWS - lines.len();
        rows.sort_unstable_by(|a, b| a.0.cmp(&b.0).then_with(|| a.1.total_cmp(&b.1))
            .then_with(|| a.2.cmp(b.2)).then_with(|| a.3.cmp(&b.3)));
        lines.extend(rows.into_iter().take(capacity).map(|(priority, _, owner, sequence, row, previous)| {
            if priority == 1 {
                format!("{owner} {sequence} m{}\n", &row[1..])
            } else if let Some(previous) = previous {
                format!("{owner} {sequence} {row},{previous}\n")
            } else {
                format!("{owner} {sequence} {row}\n")
            }
        }));
        if lines.is_empty() {
            return vec![format!("LFW1 {challenge} {source} 0 1\n")];
        }
        let total = (lines.len() + PAGE_ROWS - 1) / PAGE_ROWS;
        lines.chunks(PAGE_ROWS).enumerate().map(|(page, rows)| {
            let mut result = format!("LFW1 {challenge} {source} {page} {total}\n");
            for row in rows {
                result.push_str(row);
            }
            result
        }).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::{priority, Duration, Entity, Instant, World};
    use crate::{parse_update, VERSION};
    use std::collections::HashMap;

    #[test]
    fn hosting_priority_caps_session_time_not_power() {
        assert_eq!(priority(50, 5), 250);
        assert!(priority(95, 20) > priority(40, 40));
        assert!(priority(15, 40) > priority(99, 5));
        assert_eq!(priority(99, 7_200), 99 * 3_600);
        assert_eq!(priority(1, 7_200), 3_600);
    }

    #[test]
    fn nearby_non_host_can_take_traffic_once() {
        let now = Instant::now();
        let host = "aaaaaaaaaaaaaaaa";
        let driver = "bbbbbbbbbbbbbbbb";
        let token = "ffffffffffffffff";
        let address = "127.0.0.1:4567".parse().unwrap();
        let initial = |id, power| format!(
            "LF1 {id} {token} {VERSION} {power} - 0 0 1 90 25 0 0 - - ~10000,10000,1,0,-1,-,0,0,1,100,-500,2 !1,0,0,-");
        let players = HashMap::from([
            (host.to_owned(), parse_update(&initial(host, 99), now, address).unwrap().player),
            (driver.to_owned(), parse_update(&initial(driver, 40), now, address).unwrap().player),
        ]);
        let mut world = World::default();
        for id in [host, driver] {
            world.handle(&format!("LFWQ {id} {token} 0"), address, &players, now);
        }
        let row = "v,88,-773,0,0,100,90,0,0,0,1000,0,231,3,-1,0,900,900,900,0,10,-40,1,-999,0";
        assert!(Entity::parse(row, now, 10000).is_some());
        world.handle(&format!("LFWU {host} 10000 0 0 1\n{row}"), address, &players, now);
        assert_eq!(world.traffic.len(), 1);
        world.handle(&format!("LFWT {driver} {host} v 88 -773"), address, &players, now);
        let mut moving = parse_update(&initial(driver, 40), now, address).unwrap().player;
        moving.driving = true;
        moving.vehicle_model = -773;
        let claim = format!("{host}:v:88:-773");
        assert!(!world.commit_driving(host, &claim, &moving, &players, now));
        assert!(world.commit_driving(driver, &claim, &moving, &players, now));
        assert!(world.traffic.is_empty());
        assert!(!world.commit_driving(host, &claim, &moving, &players, now));
    }

    #[test]
    fn road_authority_is_local_and_survives_cell_crossing() {
        let now = Instant::now();
        let host = "aaaaaaaaaaaaaaaa";
        let peer = "bbbbbbbbbbbbbbbb";
        let token = "ffffffffffffffff";
        let address = "127.0.0.1:4567".parse().unwrap();
        let player = |id, x, power, joined| {
            let message = format!("LF1 {id} {token} {VERSION} {power} - {x} 32 1 90 25 0 0 - - ~10000,10000,1,0,-1,-,0,0,1,0,0,0 !1,0,0,-");
            parse_update(&message, joined, address).unwrap().player
        };
        let mut players = HashMap::from([
            (host.to_owned(), player(host, 32, 99, now - Duration::from_secs(90))),
            (peer.to_owned(), player(peer, 95, 40, now - Duration::from_secs(30))),
        ]);
        let mut world = World::default();
        for id in [host, peer] {
            world.handle(&format!("LFWQ {id} {token} 0"), address, &players, now);
        }
        assert_eq!(world.sources[peer], host);
        let row = |kind, handle, x| {
            format!("{kind},{handle},-773,{x},3200,100,90,0,0,0,1000,0,231,3,-1,0,900,900,900,0,10,-40,1,-999,0")
        };
        for handle in 1..=20 {
            let state = Entity::parse(&row("v", handle, 6400 + handle * 130), now, 10000).unwrap();
            world.observe_traffic(peer, 0, state, &players);
        }
        assert_eq!(world.traffic.len(), 20);
        let duplicate = Entity::parse(&row("v", 89, 9000), now, 10000).unwrap();
        world.observe_traffic(host, 0, duplicate, &players);
        assert_eq!(world.traffic.len(), 20);
        let target = (peer.to_owned(), b'v', 1, -773);
        let crosses = Entity::parse(&row("v", 1, 6000), now + Duration::from_millis(100), 10100).unwrap();
        world.observe_traffic(peer, 0, crosses, &players);
        assert_eq!(world.traffic[&target].state.position[0], 60.0);

        players.get_mut(peer).unwrap().position = [260.0, 32.0, 1.0];
        let handoff = now + Duration::from_millis(200);
        world.reconcile(&players, handoff);
        assert!(world.traffic[&target].delegated);
        assert_eq!(world.traffic[&target].simulator, host);
        let state = Entity::parse(&format!("{},{peer}", row("m", 1, 5700)), handoff, 10200).unwrap();
        world.observe_traffic(host, 0, state, &players);
        assert_eq!(world.traffic[&target].state.position[0], 57.0);
        assert_eq!(world.traffic.len(), 20);
    }

    #[test]
    fn parked_snapshot_carries_transfer_identity_after_the_notice_timeout() {
        let now = Instant::now();
        let old = "aaaaaaaaaaaaaaaa";
        let owner = "bbbbbbbbbbbbbbbb";
        let token = "ffffffffffffffff";
        let address = "127.0.0.1:4567".parse().unwrap();
        let make_player = |id| {
            let update = format!("LF1 {id} {token} {VERSION} 50 - 0 0 1 90 25 0 0 - - ~10000,10000,1,0,-1,-,0,0,1,0,0,0 !1,0,0,-");
            parse_update(&update, now, address).unwrap().player
        };
        let mut players = HashMap::from([(old.to_owned(), make_player(old)), (owner.to_owned(), make_player(owner))]);
        let mut world = World::default();
        for id in [old, owner] { world.handle(&format!("LFWQ {id} {token} 0"), address, &players, now); }
        let row = "v,88,-773,0,0,100,90,0,0,0,1000,0,1,0,-1,0,900,900,900,0,0,0,0,0,0";
        world.handle(&format!("LFWU {old} 10000 0 0 1\n{row}"), address, &players, now);
        let mut parked = make_player(owner);
        parked.driving = true;
        parked.vehicle_model = -773;
        parked.vehicle_token = 3;
        let claim = format!("{old}:v:88:-773");
        assert!(world.commit_driving(owner, &claim, &parked, &players, now));
        parked.driving = false;
        parked.owned_vehicle = Some((3, -773));
        parked.vehicle_origin = Some(claim.clone());
        players.insert(owner.to_owned(), parked);

        let later = now + Duration::from_secs(25);
        for id in [old, owner] { world.handle(&format!("LFWQ {id} {token} 0"), address, &players, later); }
        let parked_row = row.replacen("v,88,", "d,3,", 1);
        world.handle(&format!("LFWU {owner} 10000 0 0 1\n{parked_row}"), address, &players, later);
        world.handle(&format!("LFWU {old} 10100 0 0 1\n{row}"), address, &players, later);
        let observer = world.snapshot(old, token, &players[old], &players, later).join("");
        assert!(observer.contains(&format!("{parked_row},{claim}\n")), "{observer}");
        assert!(observer.contains("drop,88,-773"), "{observer}");
        assert!(!observer.contains(" v,88,-773"), "{observer}");
    }

    #[test]
    fn host_priority_restarts_on_new_session() {
        let now = Instant::now();
        let first = "aaaaaaaaaaaaaaaa";
        let second = "bbbbbbbbbbbbbbbb";
        let token = "ffffffffffffffff";
        let address = "127.0.0.1:4567".parse().unwrap();
        let connect = |id, power, at| {
            let message = format!("LF1 {id} {token} {VERSION} {power} - 0 0 1");
            parse_update(&message, at, address).unwrap().player
        };
        let mut players = HashMap::from([
            (first.to_owned(), connect(first, 99, now - Duration::from_secs(20))),
            (second.to_owned(), connect(second, 40, now - Duration::from_secs(10))),
        ]);
        let mut world = World::default();
        for id in [first, second] {
            world.handle(&format!("LFWQ {id} {token} 0"), address, &players, now);
        }
        assert_eq!(world.role(first).1, "host");
        players.remove(first);
        world.remove(first);
        players.insert(first.to_owned(), connect(first, 99, now));
        world.handle(&format!("LFWQ {first} {token} 0"), address, &players, now);
        assert_eq!(players[first].session_seconds(now), 0);
        assert_eq!(world.role(second).1, "host");
    }
}
