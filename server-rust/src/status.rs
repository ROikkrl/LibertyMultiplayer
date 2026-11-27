use crate::{event, world::World, Player, MAX_PLAYERS};
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::time::{Duration, Instant};

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
    pub counters: Counters,
}

impl Status {
    pub fn new() -> Self {
        Self {
            started: Instant::now(), cpu: process_runtime_ns(),
            path: std::env::var_os("LF_STATUS_FILE").filter(|p| !p.is_empty()).map(PathBuf::from),
            failed: false, counters: Counters::default(),
        }
    }

    pub fn report(&mut self, now: Instant, players: &HashMap<String, Player>, world: &mut World,
                  log: &mut Option<fs::File>) {
        for line in world.events() {
            event(log, &line);
        }
        let elapsed = now.duration_since(self.started);
        if elapsed < Duration::from_secs(1) { return; }
        world.reconcile(players, now);
        for line in world.events() {
            event(log, &line);
        }
        let counts = std::mem::take(&mut self.counters);
        let seconds = elapsed.as_secs_f64();
        self.started = now;
        let cpu = process_runtime_ns();
        let percent = cpu.zip(self.cpu).map(|(new, old)| new.saturating_sub(old) as f64 / (10_000_000.0 * seconds));
        self.cpu = cpu;
        let rss = memory_kib();
        let load = fs::read_to_string("/proc/loadavg").ok().and_then(|value| value.split_whitespace().next()?.parse::<f64>().ok());
        let rate = |count| (count as f64 / seconds).round() as u64;
        let (hosts, vehicles, sources, transfers) = world.status();
        let cpu_label = percent.map_or_else(|| "-".into(), |value| format!("{value:.1}%"));
        let memory_label = rss.map_or_else(|| "-".into(), |value| format!("{:.1}MiB", value as f64 / 1024.0));
        println!("status online={}/{} hosts={} sources={sources} vehicles={vehicles} transfers={transfers} cpu={cpu_label} rss={memory_label} udp_rx={}/s udp_tx={}/s bytes_rx={}/s bytes_tx={}/s updates={}/s invalid={} stale={} claims_denied={} version_mismatches={}",
            players.len(), MAX_PLAYERS, if hosts.is_empty() { "-" } else { &hosts },
            rate(counts.incoming), rate(counts.outgoing), rate(counts.bytes_in), rate(counts.bytes_out),
            rate(counts.updates), counts.invalid, counts.stale, counts.claims_denied, counts.version_mismatches);
        if counts.invalid + counts.claims_denied + counts.version_mismatches != 0 {
            event(log, &format!("udp_rejections window_ms={} invalid={} vehicle_claims={} version_mismatches={}",
                elapsed.as_millis(), counts.invalid, counts.claims_denied, counts.version_mismatches));
        }
        if let Some(path) = &self.path {
            let mut temporary = path.as_os_str().to_owned();
            temporary.push(".tmp");
            let optional_float = |value: Option<f64>| value.map_or_else(|| "null".into(), |n| format!("{n:.2}"));
            let json = format!(
                "{{\"online\":{},\"capacity\":{},\"hosts\":\"{hosts}\",\"sources\":{sources},\"vehicles\":{vehicles},\"transfers\":{transfers},\"cpu_percent\":{},\"rss_kib\":{},\"system_load_1m\":{},\"rx_packets_s\":{},\"tx_packets_s\":{},\"rx_bytes_s\":{},\"tx_bytes_s\":{},\"accepted_updates_s\":{},\"invalid_packets\":{},\"stale_updates\":{},\"claims_denied\":{},\"version_mismatches\":{}}}\n",
                players.len(), MAX_PLAYERS, optional_float(percent), rss.map_or_else(|| "null".into(), |n| n.to_string()),
                optional_float(load), rate(counts.incoming), rate(counts.outgoing), rate(counts.bytes_in),
                rate(counts.bytes_out), rate(counts.updates), counts.invalid, counts.stale, counts.claims_denied, counts.version_mismatches);
            let result = fs::write(&temporary, json).and_then(|_| fs::rename(&temporary, path));
            if let Err(error) = &result {
                if !self.failed { event(log, &format!("status_write_error path={} error={error}", path.display())); }
            }
            self.failed = result.is_err();
        }
    }
}

fn process_runtime_ns() -> Option<u64> {
    fs::read_to_string("/proc/self/schedstat").ok()?.split_whitespace().next()?.parse().ok()
}

fn memory_kib() -> Option<u64> {
    let status = fs::read_to_string("/proc/self/status").ok()?;
    status.lines().find_map(|line| line.strip_prefix("VmRSS:"))?.split_whitespace().next()?.parse().ok()
}
