use crate::{authenticate, PacketMac, PACKET_LIMIT, SIGNATURE_LEN};
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::io;
use std::net::{SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, TrySendError};
use std::thread;
use std::time::Duration;

const QUEUE_PER_WORKER: usize = 64;
const EVENT_QUEUE: usize = 256;

struct Datagram {
    bytes: [u8; PACKET_LIMIT],
    size: usize,
    source: SocketAddr,
}

pub struct Packet {
    datagram: Datagram,
    authenticated: bool,
}

impl Packet {
    pub fn source(&self) -> SocketAddr { self.datagram.source }

    pub fn size(&self) -> usize { self.datagram.size }

    pub fn payload(&self) -> Option<&str> {
        self.authenticated.then(|| {
            std::str::from_utf8(&self.datagram.bytes[SIGNATURE_LEN..self.datagram.size])
                .expect("worker validated the UTF-8 payload")
        })
    }
}

pub fn run(
    socket: &UdpSocket,
    signer: &PacketMac,
    worker_count: usize,
    server: impl FnOnce(Receiver<io::Result<Packet>>, &AtomicU64) -> io::Result<()>,
) -> io::Result<()> {
    let receive_socket = socket.try_clone()?;
    receive_socket.set_read_timeout(Some(Duration::from_millis(250)))?;
    let stop = AtomicBool::new(false);
    let dropped = AtomicU64::new(0);
    thread::scope(|scope| {
        let (event_tx, event_rx) = mpsc::sync_channel(EVENT_QUEUE);
        let mut lanes = Vec::with_capacity(worker_count);
        for _ in 0..worker_count {
            let (packet_tx, packet_rx) = mpsc::sync_channel::<Datagram>(QUEUE_PER_WORKER);
            lanes.push(packet_tx);
            let tx = event_tx.clone();
            scope.spawn(move || {
                while let Ok(datagram) = packet_rx.recv() {
                    let authenticated = datagram.bytes.get(..datagram.size)
                        .and_then(|bytes| authenticate(bytes, signer)).is_some();
                    if tx.send(Ok(Packet { datagram, authenticated })).is_err() { break; }
                }
            });
        }
        let stopped = &stop;
        let queue_drops = &dropped;
        scope.spawn(move || {
            let mut receive = [0; 65_536];
            while !stopped.load(Ordering::Relaxed) {
                match receive_socket.recv_from(&mut receive) {
                    Ok((size, source)) => {
                        // A sender always uses one worker, so its packets reach the world in arrival order.
                        let mut hash = DefaultHasher::new();
                        source.hash(&mut hash);
                        let lane = hash.finish() as usize % lanes.len();
                        let mut bytes = [0; PACKET_LIMIT];
                        if size <= PACKET_LIMIT { bytes[..size].copy_from_slice(&receive[..size]); }
                        match lanes[lane].try_send(Datagram { bytes, size, source }) {
                            Ok(()) => {}
                            Err(TrySendError::Full(_)) => { queue_drops.fetch_add(1, Ordering::Relaxed); }
                            Err(TrySendError::Disconnected(_)) => break,
                        }
                    }
                    Err(error) if matches!(error.kind(), io::ErrorKind::Interrupted |
                        io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut | io::ErrorKind::ConnectionReset) => continue,
                    Err(error) => { let _ = event_tx.send(Err(error)); break; }
                }
            }
        });
        let result = server(event_rx, &dropped);
        stop.store(true, Ordering::Relaxed);
        result
    })
}
