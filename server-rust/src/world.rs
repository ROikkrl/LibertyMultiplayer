use super::{distance_squared, valid_id, Player};
use std::collections::{HashMap, HashSet};
use std::net::SocketAddr;
use std::time::{Duration, Instant};

const MAX_PAGES: usize = 5;
const PAGE_ROWS: usize = 6;
const SOURCE_RADIUS: f64 = 90.0;
const KEEP_SOURCE_RADIUS: f64 = 105.0;
const CAPTURE_RADIUS: f64 = 150.0;
const VIEW_RADIUS: f64 = 100.0;
const FRAME_TTL: Duration = Duration::from_millis(850);
const PRESENCE_TTL: Duration = Duration::from_millis(1200);

struct Entity {
    kind: u8,
    id: i32,
    model: i32,
    position: [f64; 3],
    row: String,
}

impl Entity {
    fn parse(row: &str) -> Option<Self> {
        if row.len() > 140 {
            return None;
        }
        let mut fields = row.split(',');
        let kind = match fields.next()? {
            "p" => b'p',
            "v" => b'v',
            "d" => b'd',
            _ => return None,
        };
        let id = fields.next()?.parse::<i32>().ok()?;
        let model = fields.next()?.parse::<i32>().ok()?;
        if id == 0 || model == 0 {
            return None;
        }
        let mut position = [0.0; 3];
        for axis in &mut position {
            let value = bounded(fields.next()?, -200_000, 200_000)?;
            *axis = f64::from(value) / 10.0;
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
        }
        if fields.next().is_some() {
            return None;
        }
        Some(Self { kind, id, model, position, row: row.to_owned() })
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
    joined: Instant,
    seen: Instant,
    episode: u8,
    incoming: Option<Incoming>,
    frame: Option<Frame>,
}

#[derive(Default)]
pub struct World {
    publishers: HashMap<String, Publisher>,
    sources: HashMap<String, String>,
}

impl World {
    fn reconcile(&mut self, players: &HashMap<String, Player>, now: Instant) {
        self.publishers.retain(|id, entry| {
            players.contains_key(id) && now.duration_since(entry.seen) < PRESENCE_TTL
        });
        let mut ordered: Vec<_> = self.publishers.iter().collect();
        ordered.sort_unstable_by_key(|(id, entry)| (entry.joined, id.as_str()));
        let mut sources = HashMap::with_capacity(ordered.len());
        for (source, publisher) in ordered.iter().copied() {
            if sources.contains_key(source) {
                continue;
            }
            for (id, member) in ordered.iter().copied() {
                let radius = if self.sources.get(id) == Some(source) { KEEP_SOURCE_RADIUS } else { SOURCE_RADIUS };
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
        self.sources = sources;
    }

    pub fn remove(&mut self, id: &str) {
        self.publishers.remove(id);
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
            "LFWQ" => {
                let challenge = fields.next()?;
                let episode: u8 = fields.next()?.parse().ok()?;
                if !valid_id(challenge) || episode > 2 || fields.next().is_some() || !body.is_empty() {
                    return None;
                }
                let publisher = self.publishers.entry(id.to_owned()).or_insert(Publisher {
                    joined: now, seen: now, episode, incoming: None, frame: None,
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
                    entities.push(Entity::parse(row)?);
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
                    let entities = complete.pages.into_iter().flatten().flatten().filter(|item| {
                        let authorized = if item.kind == b'd' {
                            player.driving && player.vehicle_token == item.id &&
                                player.vehicle_model == item.model &&
                                distance_squared(item.position, player.position) <= 15.0_f64.powi(2)
                        } else {
                            owns_area &&
                                distance_squared(item.position, player.position) <= CAPTURE_RADIUS.powi(2)
                        };
                        authorized && unique.insert((item.kind, item.id))
                    }).collect();
                    publisher.frame = Some(Frame { sequence, at: now, ambient: owns_area, entities });
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
                        origin.driving && item.id == origin.vehicle_token && item.model == origin.vehicle_model
                    } else {
                        publisher_id == source
                    };
                    let distance = distance_squared(item.position, player.position);
                    if eligible && distance <= VIEW_RADIUS.powi(2) {
                        rows.push((item.kind != b'd', distance, publisher_id, frame.sequence, &item.row));
                    }
                }
            }
        }
        rows.sort_unstable_by(|a, b| a.0.cmp(&b.0).then_with(|| a.1.total_cmp(&b.1)));
        let mut lines = Vec::new();
        if let Some(sequence) = ambient_sequence {
            // An empty but complete source frame still represents the shared world.
            lines.push(format!("{source} {sequence} @\n"));
        }
        let capacity = MAX_PAGES * PAGE_ROWS - lines.len();
        lines.extend(rows.into_iter().take(capacity).map(|(_, _, owner, sequence, row)| {
            format!("{owner} {sequence} {row}\n")
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
