use super::{distance_squared, valid_id, Player};
use std::collections::{HashMap, HashSet};
use std::net::SocketAddr;
use std::time::{Duration, Instant};

const MAX_PAGES: usize = 6;
const PAGE_ROWS: usize = 5;
const SOURCE_RADIUS: f64 = 90.0;
const KEEP_SOURCE_RADIUS: f64 = 105.0;
const CAPTURE_RADIUS: f64 = 150.0;
const VIEW_RADIUS: f64 = 100.0;
const FRAME_TTL: Duration = Duration::from_millis(1200);
const PRESENCE_TTL: Duration = Duration::from_millis(1800);
const VEHICLE_TTL: Duration = Duration::from_secs(3);
const OCCUPIED_VEHICLE_TTL: Duration = Duration::from_secs(20);
const SIMULATION_RADIUS: f64 = 115.0;
const MAX_TRAFFIC: usize = 384;
const MAX_DELEGATED: usize = 8;
const MAX_PRIORITY_SECONDS: u64 = 3600;

fn priority(power: u8, elapsed: Duration) -> u64 {
    u64::from(power) * elapsed.as_secs().min(MAX_PRIORITY_SECONDS)
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
        if row.len() > 176 {
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
        let scale = if kind == b'p' { 10 } else { 100 };
        for axis in &mut position {
            let value = bounded(fields.next()?, -20_000 * scale, 20_000 * scale)?;
            *axis = f64::from(value) / f64::from(scale);
        }
        bounded(fields.next()?, 0, 359)?;
        if kind == b'p' {
            bounded(fields.next()?, 0, 4)?;
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
            bounded(fields.next()?, 0, 15)?;
            fields.next()?.parse::<u32>().ok()?;
            bounded(fields.next()?, -1, 255)?;
            fields.next()?.parse::<i32>().ok()?;
            bounded(fields.next()?, -4000, 1000)?;
            bounded(fields.next()?, 0, 1000)?;
            bounded(fields.next()?, -4000, 1000)?;
            bounded(fields.next()?, -1, 15)?;
            bounded(fields.next()?, -15_000, 15_000)?;
            bounded(fields.next()?, -6_000, 6_000)?;
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

struct Frame {
    sequence: u64,
    at: Instant,
    ambient: bool,
    entities: Vec<Entity>,
}

struct Incoming {
    sequence: u64,
    pages: Vec<Option<Vec<Entity>>>,
}

struct Publisher {
    seen: Instant,
    episode: u8,
    incoming: Option<Incoming>,
    frame: Option<Frame>,
}

type TrafficKey = (String, i32, i32);

struct Transfer {
    driver: String,
    simulator: String,
    position: [f64; 3],
    until: Instant,
    committed: bool,
}

struct Traffic {
    state: Entity,
    simulator: String,
    episode: u8,
    delegated: bool,
}

#[derive(Default)]
pub struct World {
    publishers: HashMap<String, Publisher>,
    sources: HashMap<String, String>,
    transfers: HashMap<TrafficKey, Transfer>,
    traffic: HashMap<TrafficKey, Traffic>,
    traffic_revision: u64,
    hosts: HashMap<u8, String>,
}

impl World {
    fn reconcile(&mut self, players: &HashMap<String, Player>, now: Instant) {
        self.publishers.retain(|id, entry| {
            players.contains_key(id) && now.duration_since(entry.seen) < PRESENCE_TTL
        });
        self.transfers.retain(|_, transfer| {
            now < transfer.until && (transfer.committed || players.contains_key(&transfer.driver))
        });
        let mut ordered: Vec<_> = self.publishers.iter().collect();
        ordered.sort_unstable_by_key(|(id, _)| {
            let player = &players[*id];
            (std::cmp::Reverse(priority(player.power, now.duration_since(player.joined))),
                player.joined, id.as_str())
        });
        let mut sources = HashMap::with_capacity(ordered.len());
        self.hosts.clear();
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
        // Atmospheric traffic is valid only while this game instance owns the area.
        for (id, publisher) in &mut self.publishers {
            let was_source = self.sources.get(id) == Some(id);
            let is_source = sources.get(id) == Some(id);
            if was_source != is_source {
                publisher.incoming = None;
                if let Some(frame) = &mut publisher.frame {
                    frame.ambient = false;
                    frame.entities.retain(|item| item.kind == b'd');
                }
            }
        }
        self.reconcile_traffic(players, &sources, now);
        self.sources = sources;
    }

    fn reconcile_traffic(&mut self, players: &HashMap<String, Player>, sources: &HashMap<String, String>,
                         now: Instant) {
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
                now.duration_since(vehicle.state.seen) < FRAME_TTL &&
                (vehicle.delegated || sources.get(&vehicle.simulator) == Some(&vehicle.simulator))
        };
        let mut delegated = HashMap::<String, usize>::new();
        for vehicle in self.traffic.values().filter(|vehicle| vehicle.delegated && can_keep(vehicle)) {
            *delegated.entry(vehicle.simulator.clone()).or_default() += 1;
        }
        for vehicle in self.traffic.values_mut().filter(|vehicle| !can_keep(vehicle)) {
            let available = publishers.keys().filter(|id| nearby(vehicle, id, SIMULATION_RADIUS) &&
                delegated.get(*id).copied().unwrap_or(0) < MAX_DELEGATED);
            let best = available.min_by(|a, b| {
                let rank = |id: &String| {
                    (sources.get(id) != Some(id), std::cmp::Reverse(priority(players[id].power,
                        now.duration_since(players[id].joined))), players[id].joined)
                };
                rank(a).cmp(&rank(b)).then_with(|| a.cmp(b))
            });
            if let Some(id) = best {
                vehicle.simulator = id.clone();
                vehicle.delegated = true;
                *delegated.entry(id.clone()).or_default() += 1;
            }
        }
    }

    pub fn remove(&mut self, id: &str) {
        self.publishers.remove(id);
    }

    fn observe_traffic(&mut self, id: &str, episode: u8, mut state: Entity) {
        let origin = state.origin.take().unwrap_or_else(|| id.to_owned());
        let key = (origin, state.id, state.model);
        if self.transfers.get(&key).map_or(false, |transfer| transfer.committed) {
            return;
        }
        let delegated = state.kind == b'm';
        if let Some(vehicle) = self.traffic.get(&key) {
            if vehicle.simulator != id || vehicle.episode != episode || vehicle.delegated != delegated {
                return;
            }
        } else if delegated || self.traffic.len() >= MAX_TRAFFIC {
            return;
        }
        self.traffic_revision += 1;
        state.sequence = self.traffic_revision;
        self.traffic.insert(key, Traffic { state, simulator: id.to_owned(), episode, delegated });
    }

    fn transfer(&mut self, driver: &str, source: &str, handle: i32, model: i32, commit: bool,
                players: &HashMap<String, Player>, now: Instant) {
        let key = (source.to_owned(), handle, model);
        let player = &players[driver];
        if let Some(transfer) = self.transfers.get_mut(&key) {
            if commit && transfer.driver == driver && player.driving && player.vehicle_model == model &&
                !transfer.committed && distance_squared(transfer.position, player.position) <= 22.0_f64.powi(2) {
                transfer.committed = true;
                transfer.until = now + Duration::from_secs(12);
                self.traffic.remove(&key);
            }
            return;
        }
        if commit || player.seated ||
            self.transfers.values().any(|item| item.driver == driver && !item.committed) {
            return;
        }
        let nearby = self.traffic.get(&key).filter(|vehicle|
            (vehicle.delegated || vehicle.simulator != driver) &&
            self.publishers.get(driver).map_or(false, |publisher| publisher.episode == vehicle.episode) &&
            now.duration_since(vehicle.state.seen) < VEHICLE_TTL &&
            distance_squared(vehicle.state.position, player.position) <= 9.0_f64.powi(2));
        if let Some(vehicle) = nearby {
            self.transfers.insert(key, Transfer {
                driver: driver.to_owned(), position: vehicle.state.position, simulator: vehicle.simulator.clone(),
                until: now + Duration::from_secs(3), committed: false,
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
            "LFWT" => {
                let source = fields.next()?;
                let handle = fields.next()?.parse().ok()?;
                let model = fields.next()?.parse().ok()?;
                let commit = match fields.next()? { "0" => false, "1" => true, _ => return None };
                if fields.next().is_some() || !body.is_empty() || !valid_id(source) {
                    return None;
                }
                self.reconcile(players, now);
                self.transfer(id, source, handle, model, commit, players, now);
                None
            }
            "LFWQ" => {
                let challenge = fields.next()?;
                let episode: u8 = fields.next()?.parse().ok()?;
                if !valid_id(challenge) || episode > 2 || fields.next().is_some() || !body.is_empty() {
                    return None;
                }
                let publisher = self.publishers.entry(id.to_owned()).or_insert(Publisher {
                    seen: now, episode, incoming: None, frame: None,
                });
                if publisher.episode != episode {
                    publisher.episode = episode;
                    publisher.incoming = None;
                    publisher.frame = None;
                }
                publisher.seen = now;
                self.reconcile(players, now);
                Some(self.snapshot(id, challenge, player, players, now))
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
                self.reconcile(players, now);
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
                let pages = publisher.incoming.get_or_insert_with(|| Incoming {
                    sequence, pages: (0..total).map(|_| None).collect(),
                });
                if sequence < pages.sequence {
                    return None;
                }
                if sequence > pages.sequence {
                    *pages = Incoming { sequence, pages: (0..total).map(|_| None).collect() };
                }
                if total != pages.pages.len() || pages.pages[index].is_some() {
                    return None;
                }
                pages.pages[index] = Some(entities);
                if pages.pages.iter().all(Option::is_some) {
                    let complete = publisher.incoming.take()?;
                    let mut unique = HashSet::new();
                    let (traffic, entities) = complete.pages.into_iter().flatten().flatten().filter(|item| {
                        let distance = distance_squared(item.position, player.position);
                        let authorized = match item.kind {
                            b'd' => {
                                let radius: f64 = if player.driving { 15.0 } else { 100.0 };
                                player.owned_vehicle == Some((item.id, item.model)) && distance <= radius.powi(2)
                            }
                            b'm' => distance <= SIMULATION_RADIUS.powi(2),
                            _ => owns_area && distance <= CAPTURE_RADIUS.powi(2),
                        };
                        authorized && unique.insert((item.kind, item.origin.clone(), item.id))
                    }).partition::<Vec<_>, _>(|item| matches!(item.kind, b'v' | b'm'));
                    publisher.frame = Some(Frame { sequence, at: now, ambient: owns_area, entities });
                    for state in traffic {
                        self.observe_traffic(id, episode, state);
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
                        origin.owned_vehicle == Some((item.id, item.model))
                    } else {
                        publisher_id == source
                    };
                    let distance = distance_squared(item.position, player.position);
                    if eligible && distance <= VIEW_RADIUS.powi(2) {
                        let priority = if item.kind == b'd' { 0 } else { 3 };
                        rows.push((priority, distance, publisher_id, item.sequence, item.row.as_str()));
                    }
                }
            }
        }
        let mut lines = Vec::new();
        if let Some(host) = self.hosts.get(&episode) {
            lines.push(format!("{host} 0 #\n"));
        }
        for ((origin, handle, model), transfer) in &self.transfers {
            let nearby = distance_squared(transfer.position, player.position) <= VIEW_RADIUS.powi(2);
            if transfer.driver == id || transfer.committed && (origin == id || transfer.simulator == id || nearby) {
                let command = if transfer.committed { "drop" } else { "take" };
                lines.push(format!("{origin} 0 {command},{handle},{model}\n"));
            }
        }
        for ((origin, handle, model), vehicle) in &self.traffic {
            if vehicle.episode != episode {
                continue;
            }
            let distance = distance_squared(vehicle.state.position, player.position);
            if vehicle.delegated && origin == id && vehicle.simulator != id {
                if distance <= SIMULATION_RADIUS.powi(2) {
                    lines.push(format!("{origin} 0 hide,{handle},{model}\n"));
                }
            }
            let simulation = vehicle.delegated && vehicle.simulator == id && distance <= SIMULATION_RADIUS.powi(2);
            if simulation || (vehicle.simulator != id && distance <= VIEW_RADIUS.powi(2)) {
                let priority = if simulation { 1 } else { 2 };
                rows.push((priority, distance, origin, vehicle.state.sequence, vehicle.state.row.as_str()));
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
        lines.extend(rows.into_iter().take(capacity).map(|(priority, _, owner, sequence, row)| {
            if priority == 1 {
                format!("{owner} {sequence} m{}\n", &row[1..])
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
    use super::{priority, Duration};

    #[test]
    fn hosting_priority_caps_session_time_not_power() {
        assert_eq!(priority(50, Duration::from_millis(5_900)), 250);
        assert!(priority(95, Duration::from_secs(20)) > priority(40, Duration::from_secs(40)));
        assert!(priority(15, Duration::from_secs(40)) > priority(99, Duration::from_secs(5)));
        assert_eq!(priority(99, Duration::from_secs(7_200)), 99 * 3_600);
        assert_eq!(priority(1, Duration::from_secs(7_200)), 3_600);
    }
}
