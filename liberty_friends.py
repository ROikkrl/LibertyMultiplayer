import argparse
import hmac
import re
import secrets
import socket
import time
from pathlib import Path

MAX_PLAYERS = 4
TIMEOUT = 3.0
INTERVAL = 0.05
PACKET_LIMIT = 1200
PLAYER_ID = re.compile(rb"[0-9a-f]{16}")
RIDE_ID = re.compile(rb"(?:-|[0-9a-f]{16}:-?[0-9]{1,10})")
WORLD_ENTITY = re.compile(rb"[pv](?:,-?[0-9]{1,11}){6}")


def coordinates(fields):
    if len(fields) not in (3, 8, 9, 10):
        raise ValueError("expected three coordinates")
    values = tuple(map(float, fields[:3]))
    if not all(-20000 <= value <= 20000 for value in values):
        raise ValueError("coordinate outside world bounds")
    position = " ".join(f"{value:.2f}" for value in values)
    if len(fields) == 3:
        return position.encode("ascii")
    heading = float(fields[3])
    ped, car, handle = map(int, fields[4:7])
    ride = fields[7]
    if not 0 <= heading <= 360 or not all(-(2**31) <= n < 2**31 for n in (ped, car, handle)):
        raise ValueError("invalid entity state")
    if not RIDE_ID.fullmatch(ride) or (car != 0 and (handle == 0 or ride != b"-")):
        raise ValueError("invalid vehicle state")
    state = f"{position} {heading:.2f} {ped} {car} {handle} {ride.decode('ascii')}".encode("ascii")
    if len(fields) >= 9:
        world = fields[8]
        entities = world.split(b";")
        if len(world) > 250 or (world != b"-" and
                (len(entities) > 4 or not all(WORLD_ENTITY.fullmatch(item) for item in entities))):
            raise ValueError("invalid world state")
        state += b" " + world
    if len(fields) == 10:
        physics = fields[9]
        values = [float(value) for value in physics.split(b",")]
        if (car == 0 or len(physics) > 80 or len(values) != 7 or
                not 0.5 <= sum(value * value for value in values[:4]) <= 1.5 or
                not -4000 <= values[4] <= 1000 or
                not 0 <= values[5] <= 1000 or not -4000 <= values[6] <= 1000):
            raise ValueError("invalid vehicle physics")
        state += b" " + physics
    if len(state) > 360:
        raise ValueError("entity state is too large")
    return state


def seal(key, payload):
    return hmac.digest(key, payload, "sha256") + payload


def open_packet(key, packet):
    signature, payload = packet[:32], packet[32:]
    if len(packet) <= PACKET_LIMIT and hmac.compare_digest(
        signature, hmac.digest(key, payload, "sha256")
    ):
        return payload
    raise ValueError("invalid packet")


def check_server(host, port, key):
    challenge = secrets.token_hex(8).encode("ascii")
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(2)
        sock.connect((host, port))
        started = time.monotonic()
        sock.send(seal(key, b"LF-PING " + challenge))
        if open_packet(key, sock.recv(PACKET_LIMIT + 1)) != b"LF-PONG " + challenge:
            raise ValueError("unexpected server reply")
    return round((time.monotonic() - started) * 1000)


def serve(args):
    players = {}
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind((args.bind, args.port))
        sock.settimeout(0.5)
        print(f"Listening on {args.bind}:{args.port}/UDP", flush=True)
        while True:
            now = time.monotonic()
            for peer, data in list(players.items()):
                if now - data[0] >= TIMEOUT:
                    del players[peer]
                    print(f"Disconnected {peer.decode()} ({len(players)}/{MAX_PLAYERS})", flush=True)
            try:
                packet, address = sock.recvfrom(65535)
            except socket.timeout:
                continue
            try:
                parts = open_packet(args.key, packet).split()
                if len(parts) == 2 and parts[0] == b"LF-PING" and PLAYER_ID.fullmatch(parts[1]):
                    sock.sendto(seal(args.key, b"LF-PONG " + parts[1]), address)
                    continue
                version, player_id, challenge, *fields = parts
                if version != b"LF1" or not PLAYER_ID.fullmatch(player_id):
                    continue
                if not PLAYER_ID.fullmatch(challenge):
                    continue
                position = coordinates(fields)
            except ValueError:
                continue
            now = time.monotonic()
            if player_id not in players and len(players) >= MAX_PLAYERS:
                continue
            if player_id not in players:
                print(f"Connected {player_id.decode()} ({len(players)+1}/{MAX_PLAYERS})", flush=True)
            players[player_id] = (now, position)
            rows = [challenge]
            rows.extend(peer + b" " + data[1] for peer, data in players.items() if peer != player_id)
            sock.sendto(seal(args.key, b"\n".join(rows) + b"\n"), address)


def join(args, stop=None, report=print):
    directory = args.mod_dir.resolve()
    if not any((directory / marker).is_file() for marker in ("bridge.txt", "manifest.lua")):
        raise SystemExit(f"Mod not found in {directory}")
    local_file = directory / "position.txt"
    peer_file = directory / "peers.txt"
    temporary_file = directory / "peers.tmp"
    player_id = secrets.token_hex(8).encode("ascii")
    (directory / "player.txt").write_bytes(player_id)
    try:
        last_position = local_file.read_bytes()
    except OSError:
        last_position = None
    last_change = 0.0
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.connect((args.host, args.port))
        status = None
        while stop is None or not stop.is_set():
            started = time.monotonic()
            next_status = "Waiting for GTA IV — enable Liberty Friends with F9"
            try:
                position = local_file.read_bytes()
                if position != last_position:
                    last_position, last_change = position, started
                if not position.endswith(b"\n") or started - last_change >= TIMEOUT:
                    raise ValueError("no fresh position")
                tick, *fields = position.split()
                int(tick)
                challenge = secrets.token_hex(8).encode("ascii")
                payload = b" ".join((b"LF1", player_id, challenge, coordinates(fields)))
                next_status = "Waiting for the server"
                sock.send(seal(args.key, payload))
                deadline = started + 0.15
                while time.monotonic() < deadline:
                    sock.settimeout(max(0.001, deadline - time.monotonic()))
                    response = open_packet(args.key, sock.recv(65535))
                    header, _, rows = response.partition(b"\n")
                    if header != challenge:
                        continue
                    temporary_file.write_bytes(str(time.monotonic_ns()).encode() + b"\n" + rows)
                    temporary_file.replace(peer_file)
                    next_status = f"Connected — other players: {len(rows.splitlines())}"
                    break
            except (OSError, ValueError):
                pass
            if next_status != status:
                status = next_status
                report(status)
            delay = max(0, INTERVAL - (time.monotonic() - started))
            if stop is None:
                time.sleep(delay)
            else:
                stop.wait(delay)


def cli():
    parser = argparse.ArgumentParser(description="Liberty Friends network prototype")
    commands = parser.add_subparsers(dest="command", required=True)
    server = commands.add_parser("serve")
    server.add_argument("--bind", default="127.0.0.1")
    client = commands.add_parser("join")
    client.add_argument("--host", default="127.0.0.1")
    client.add_argument("--mod-dir", required=True, type=Path)
    for command in (server, client):
        command.add_argument("--port", default=24840, type=int)
        command.add_argument("--key", required=True)
    args = parser.parse_args()
    if len(args.key) < 24:
        parser.error("--key must contain at least 24 characters")
    if not 1 <= args.port <= 65535:
        parser.error("--port must be in 1..65535")
    args.key = args.key.encode("utf-8")
    try:
        (serve if args.command == "serve" else join)(args)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    cli()
