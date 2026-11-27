use crate::{distance_squared, Player, Position};
use std::collections::HashMap;
use std::time::{Duration, Instant};

const ALL_PINS: u16 = (1 << 10) - 1;
const FRAMES: u8 = 3;

struct Roll {
    started: Instant,
    duration: u64,
    aim: i16,
    power: u8,
    remaining: u16,
}

struct Match {
    players: [String; 2],
    episode: u8,
    anchor: [i32; 3],
    heading: u16,
    scores: [u8; 2],
    turn: usize,
    frame: u8,
    shots: u8,
    standing: u16,
    roll: Option<Roll>,
    next_turn: Option<Instant>,
    ended: Option<Instant>,
    created: Instant,
}

impl Match {
    fn contains(&self, id: &str) -> bool {
        self.players.iter().any(|name| name == id)
    }

    fn position(&self) -> Position {
        self.anchor.map(|axis| f64::from(axis) / 100.0)
    }

    fn advance(&mut self, now: Instant) {
        if let Some(roll) = &self.roll {
            if now.saturating_duration_since(roll.started).as_millis() >= u128::from(roll.duration) {
                self.scores[self.turn] += (self.standing ^ roll.remaining).count_ones() as u8;
                self.standing = roll.remaining;
                self.roll = None;
                self.shots += 1;
                self.next_turn = Some(now + Duration::from_millis(1150));
            }
        }
        if self.next_turn.map_or(false, |at| now >= at) {
            self.next_turn = None;
            if self.shots == 2 || self.standing == 0 {
                self.shots = 0;
                self.standing = ALL_PINS;
                self.turn ^= 1;
                if self.turn == 0 { self.frame += 1; }
                if self.frame > FRAMES { self.ended = Some(now); }
            }
        }
    }

    fn row(&self, now: Instant) -> String {
        let (phase, aim, power, elapsed, duration) = if let Some(roll) = &self.roll {
            (2, roll.aim, roll.power, now.saturating_duration_since(roll.started).as_millis(), roll.duration)
        } else {
            let phase = if self.ended.is_some() { 4 } else if self.next_turn.is_some() { 3 }
                else if self.players[1].is_empty() { 0 } else { 1 };
            (phase, 0, 0, 0, 0)
        };
        let [x, y, z] = self.anchor;
        let second = if self.players[1].is_empty() { "-" } else { self.players[1].as_str() };
        format!("{} 0 B,{x},{y},{z},{},{phase},{second},{},{},{},{},{},{},{aim},{power},{elapsed},{duration}\n",
            self.players[0], self.heading, self.turn, self.frame.min(FRAMES), self.shots,
            self.scores[0], self.scores[1], self.standing)
    }
}

#[derive(Default)]
pub struct Bowling {
    matches: Vec<Match>,
    commands: HashMap<String, u64>,
}

impl Bowling {
    pub fn remove(&mut self, id: &str) {
        self.matches.retain(|game| !game.contains(id));
        self.commands.remove(id);
    }

    pub fn tick(&mut self, players: &HashMap<String, Player>, now: Instant) {
        self.matches.retain(|game| {
            let active = game.players.iter().filter(|name| !name.is_empty()).all(|name| players.get(name)
                .map_or(false, |p| !p.seated && distance_squared(p.position, game.position()) < 23.0_f64.powi(2)));
            active && (game.players[1] != "" || now.saturating_duration_since(game.created) < Duration::from_secs(90)) &&
                game.ended.map_or(true, |ended| now.saturating_duration_since(ended) < Duration::from_secs(20))
        });
        for game in &mut self.matches { game.advance(now); }
    }

    pub fn handle(&mut self, id: &str, command: &str, sequence: u64, player: &Player, episode: u8, now: Instant) -> Option<()> {
        if sequence.abs_diff(player.sample) > 2300 || self.commands.get(id).map_or(false, |&seen| sequence <= seen) {
            return None;
        }
        let mut fields = command.split(',');
        match fields.next()? {
            "join" => {
                let mut anchor = [0i32; 3];
                for axis in &mut anchor {
                    *axis = fields.next()?.parse().ok()?;
                    if !(-2_000_000..=2_000_000).contains(axis) { return None; }
                }
                let heading: u16 = fields.next()?.parse().ok()?;
                if fields.next().is_some() || heading >= 360 || player.seated ||
                    distance_squared(player.position, anchor.map(|axis| f64::from(axis) / 100.0)) > 4.0 { return None; }
                if !self.matches.iter().any(|game| game.contains(id)) {
                    let lobby = self.matches.iter_mut().find(|game| game.episode == episode && game.players[1].is_empty() &&
                        distance_squared(player.position, game.position()) < 64.0);
                    if let Some(game) = lobby {
                        game.players[1] = id.to_owned();
                    } else if self.matches.len() < 4 {
                        self.matches.push(Match { players: [id.to_owned(), String::new()], episode, anchor, heading,
                            turn: 0, frame: 1, shots: 0, standing: ALL_PINS, scores: [0; 2], roll: None, next_turn: None,
                            ended: None, created: now });
                    }
                }
            }
            "leave" => {
                if fields.next().is_some() { return None; }
                self.matches.retain(|game| !game.contains(id));
            }
            "throw" => {
                let aim: i16 = fields.next()?.parse().ok()?;
                let power: u8 = fields.next()?.parse().ok()?;
                if fields.next().is_some() || !(-850..=850).contains(&aim) || !(1..=100).contains(&power) { return None; }
                let game = self.matches.iter_mut().find(|game| game.episode == episode && game.contains(id))?;
                if game.players[1].is_empty() || game.players[game.turn] != id || game.ended.is_some() ||
                    game.roll.is_some() || game.next_turn.is_some() || player.seated ||
                    distance_squared(player.position, game.position()) > 25.0 { return None; }
                game.roll = Some(Roll { started: now, duration: 3000 - u64::from(power) * 14, aim, power,
                    remaining: hit_pins(game.standing, aim, power) });
            }
            _ => return None,
        }
        self.commands.insert(id.to_owned(), sequence);
        Some(())
    }

    pub fn snapshot(&self, id: &str, episode: u8, now: Instant) -> Option<String> {
        self.matches.iter().find(|game| game.episode == episode && game.contains(id)).map(|game| game.row(now))
    }
}

fn pin_offset(index: usize) -> (i32, i32) {
    let mut row = 0;
    while index >= ((row + 1) * (row + 2) / 2) as usize { row += 1; }
    let column = index as i32 - row * (row + 1) / 2;
    ((column * 2 - row) * 145, row * 260)
}

fn hit_pins(standing: u16, aim: i16, power: u8) -> u16 {
    if power < 20 || aim.abs() > 515 { return standing; }
    let mut hits = 0u16;
    let mut carry = 2 + u32::from(power) / 12;
    for i in 0..10 {
        let mask = 1 << i;
        if standing & mask == 0 || carry == 0 { continue; }
        let (x, y) = pin_offset(i);
        let direct = (x - i32::from(aim)).abs() < 170;
        let deflected = (0..i).any(|j| {
            let (other_x, other_y) = pin_offset(j);
            hits & (1 << j) != 0 && (y - other_y) == 260 && (other_x - x).abs() <= 145
        });
        if direct || (power >= 55 && deflected) {
            hits |= mask;
            carry -= 1;
        }
    }
    standing & !hits
}

#[cfg(test)]
mod tests {
    use super::{hit_pins, ALL_PINS};

    #[test]
    fn gutters_and_soft_rolls_miss_and_knocked_pins_stay_down() {
        assert_eq!(hit_pins(ALL_PINS, 650, 100), ALL_PINS);
        assert_eq!(hit_pins(ALL_PINS, 0, 10), ALL_PINS);
        let after = hit_pins(ALL_PINS, 0, 100);
        assert!(after.count_ones() < 5);
        let remaining = hit_pins(after, 435, 80);
        assert_eq!(remaining & !after, 0);
    }
}
