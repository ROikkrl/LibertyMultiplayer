use crate::{world::{priority, World}, Player, MAX_PLAYERS, VERSION};
use std::collections::{HashMap, VecDeque};
use std::fmt::Write as _;
use std::fs;
use std::io::{self, IsTerminal, Write};
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub struct EventLog {
    file: fs::File,
    recent: VecDeque<String>,
    failed: bool,
}

impl EventLog {
    pub fn new(file: fs::File) -> Self {
        Self { file, recent: VecDeque::with_capacity(5), failed: false }
    }

    pub fn record(&mut self, message: String) {
        let timestamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs();
        if let Err(error) = writeln!(self.file, "{timestamp} {message}") {
            if !self.failed { eprintln!("Could not write server log: {error}"); }
            self.failed = true;
        } else {
            self.failed = false;
        }
        if self.recent.len() == 5 { self.recent.pop_front(); }
        self.recent.push_back(message);
    }
}

#[derive(Default)]
pub struct Counters {
    pub incoming: u64,
    pub outgoing: u64,
    pub bytes_in: u64,
    pub bytes_out: u64,
    pub invalid: u64,
    pub stale: u64,
    pub updates: u64,
    pub claims_denied: u64,
    pub version_mismatches: u64,
}

pub struct Status {
    started: Instant,
    cpu: Option<u64>,
    path: Option<PathBuf>,
    failed: bool,
    screen: bool,
    address: SocketAddr,
    worker_count: usize,
    pub counters: Counters,
}

impl Status {
    pub fn new(address: SocketAddr, worker_count: usize) -> Self {
        let screen = match std::env::var("LF_SCREEN").as_deref() {
            Ok("1") => true,
            Ok("0") => false,
            _ => io::stdout().is_terminal() &&
                std::env::var("TERM").as_deref() != Ok("dumb"),
        };
        if !screen { println!("LibertyMultiplayer {VERSION} listening on {address}/UDP"); }
        Self {
            started: Instant::now(), cpu: process_runtime_ns(),
            path: std::env::var_os("LF_STATUS_FILE").filter(|p| !p.is_empty()).map(PathBuf::from),
            failed: false, screen, address, worker_count, counters: Counters::default(),
        }
    }

    pub fn report(&mut self, now: Instant, players: &HashMap<String, Player>, world: &mut World,
                  log: &mut EventLog, queue_drops: &AtomicU64) {
        for line in world.events() {
            log.record(line);
        }
        let elapsed = now.duration_since(self.started);
        if elapsed < Duration::from_secs(1) { return; }
        world.reconcile(players, now);
        for line in world.events() {
            log.record(line);
        }
        let counts = std::mem::take(&mut self.counters);
        let queue_drops = queue_drops.swap(0, Ordering::Relaxed);
        let seconds = elapsed.as_secs_f64();
        self.started = now;
        let cpu = process_runtime_ns();
        let percent = cpu.zip(self.cpu).map(|(new, old)| new.saturating_sub(old) as f64 / (10_000_000.0 * seconds));
        self.cpu = cpu;
        let rss = memory_kib();
        let load = fs::read_to_string("/proc/loadavg").ok().and_then(|value| value.split_whitespace().next()?.parse::<f64>().ok());
        let rate = |count| (count as f64 / seconds).round() as u64;
        let (hosts, vehicles, sources, transfers) = world.status();
        if counts.invalid + counts.claims_denied + counts.version_mismatches != 0 {
            log.record(format!("udp_rejections window_ms={} invalid={} vehicle_claims={} version_mismatches={}",
                elapsed.as_millis(), counts.invalid, counts.claims_denied, counts.version_mismatches));
        }
        let mut ordered: Vec<_> = players.iter().collect();
        ordered.sort_unstable_by_key(|(id, player)| (world.role(id).1 != "host", player.joined, id.as_str()));
        let rows = ordered.into_iter().map(|(id, player)| {
            let (episode, role) = world.role(id);
            let seconds = player.session_seconds(now);
            (id, player.address.ip(), seconds, player.power, priority(player.power, seconds), episode, role)
        }).collect::<Vec<_>>();
        if self.screen {
            let cpu_label = percent.map_or_else(|| "-".into(), |value| {
                let fill = ((value / 5.0).round() as usize).min(20);
                format!("[{}{}] {value:.1}%", "#".repeat(fill), ".".repeat(20 - fill))
            });
            let memory_label = rss.map_or_else(|| "-".into(), |value| format!("{:.1} MiB", value as f64 / 1024.0));
            let load_label = load.map_or_else(|| "-".into(), |value| format!("{value:.2}"));
            let mut screen = format!("\x1b[H\x1b[J\x1b[1;36m LibertyMultiplayer {VERSION}   {} / UDP\x1b[0m\n\n CPU  {cpu_label}   RSS {memory_label}   Load {load_label}\n",
                self.address);
            writeln!(screen, " UDP  RX {} pkt/s ({:.1} KiB/s)   TX {} pkt/s ({:.1} KiB/s)",
                rate(counts.incoming), rate(counts.bytes_in) as f64 / 1024.0,
                rate(counts.outgoing), rate(counts.bytes_out) as f64 / 1024.0).unwrap();
            writeln!(screen, "      {} updates/s   {} invalid   {} stale   {} denied\n",
                rate(counts.updates), counts.invalid, counts.stale, counts.claims_denied).unwrap();
            writeln!(screen, " Network workers {}   Full queue drops {queue_drops}", self.worker_count).unwrap();
            writeln!(screen, " Players {}/{}   Vehicles {vehicles}   Transfers {transfers}", players.len(), MAX_PLAYERS).unwrap();
            writeln!(screen, " ID                IP               Session   Power  Score   Ep   Role").unwrap();
            for &(id, ip, seconds, power, score, episode, role) in &rows {
                if role == "host" { screen.push_str("\x1b[1;32m"); }
                writeln!(screen, " {id:16}  {:15}  {:02}:{:02}:{:02}   0.{power:02}  {score:6}   {:<2}   {role}\x1b[0m",
                    ip.to_string(), seconds / 3600, seconds / 60 % 60, seconds % 60,
                    episode.map_or_else(|| "-".into(), |e| e.to_string())).unwrap();
            }
            writeln!(screen, "\n Recent events").unwrap();
            for line in &log.recent {
                writeln!(screen, " {:.76}", line).unwrap();
            }
            screen.push_str("\n Ctrl+C exits\n");
            let mut stdout = io::stdout().lock();
            if stdout.write_all(screen.as_bytes()).and_then(|_| stdout.flush()).is_err() {
                self.screen = false;
            }
        }
        if let Some(path) = &self.path {
            let mut temporary = path.as_os_str().to_owned();
            temporary.push(".tmp");
            let optional_float = |value: Option<f64>| value.map_or_else(|| "null".into(), |n| format!("{n:.2}"));
            let members = rows.iter().map(|(id, ip, seconds, power, score, episode, role)| format!(
                "{{\"id\":\"{id}\",\"ip\":\"{ip}\",\"session_seconds\":{seconds},\"power\":{power},\"score\":{score},\"episode\":{},\"role\":\"{role}\"}}",
                episode.map_or_else(|| "null".into(), |e| e.to_string()))).collect::<Vec<_>>().join(",");
            let json = format!(
                "{{\"online\":{},\"capacity\":{},\"hosts\":\"{hosts}\",\"players\":[{members}],\"sources\":{sources},\"vehicles\":{vehicles},\"transfers\":{transfers},\"network_workers\":{},\"queue_drops\":{queue_drops},\"cpu_percent\":{},\"rss_kib\":{},\"system_load_1m\":{},\"rx_packets_s\":{},\"tx_packets_s\":{},\"rx_bytes_s\":{},\"tx_bytes_s\":{},\"accepted_updates_s\":{},\"invalid_packets\":{},\"stale_updates\":{},\"claims_denied\":{},\"version_mismatches\":{}}}\n",
                players.len(), MAX_PLAYERS, self.worker_count, optional_float(percent), rss.map_or_else(|| "null".into(), |n| n.to_string()),
                optional_float(load), rate(counts.incoming), rate(counts.outgoing), rate(counts.bytes_in),
                rate(counts.bytes_out), rate(counts.updates), counts.invalid, counts.stale, counts.claims_denied, counts.version_mismatches);
            let result = fs::write(&temporary, json).and_then(|_| fs::rename(&temporary, path));
            if let Err(error) = &result {
                if !self.failed { log.record(format!("status_write_error path={} error={error}", path.display())); }
            }
            self.failed = result.is_err();
        }
    }
}

#[cfg(target_os = "linux")]
fn process_runtime_ns() -> Option<u64> {
    let mut time = libc::timespec { tv_sec: 0, tv_nsec: 0 };
    // The kernel writes a timespec for the entire process, including its UDP workers.
    let result = unsafe { libc::clock_gettime(libc::CLOCK_PROCESS_CPUTIME_ID, &mut time) };
    (result == 0).then(|| (time.tv_sec as u64) * 1_000_000_000 + time.tv_nsec as u64)
}

#[cfg(not(target_os = "linux"))]
fn process_runtime_ns() -> Option<u64> { None }

fn memory_kib() -> Option<u64> {
    let status = fs::read_to_string("/proc/self/status").ok()?;
    status.lines().find_map(|line| line.strip_prefix("VmRSS:"))?.split_whitespace().next()?.parse().ok()
}
