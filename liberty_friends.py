import argparse
import hmac
import re
import secrets
import socket
import time
from pathlib import Path

MAX_PLAYERS = 4
TIMEOUT = 3.0
INTERVAL = 1 / 30
PACKET_LIMIT = 1200
PLAYER_ID = re.compile(rb"[0-9a-f]{16}")
RIDE_ID = re.compile(rb"(?:-|[0-9a-f]{16}:-?[0-9]{1,10})")
WORLD_ENTITY = re.compile(rb"(?:p(?:,-?[0-9]{1,11}){6}|v(?:,-?[0-9]{1,11}){6}(?:,-?[0-9]{1,4}){0,4})")
STATE_META = re.compile(rb"~([0-9]{1,12}),([0-9]{1,12}),([01]),([0-3]),(-1|[0-9]{1,3}),(-|[A-Za-z0-9_]{1,32})")
COMBAT_META = re.compile(rb"!([0-7]),([0-9]{1,19}),([0-2]),(-|[0-9a-f]{16})")


def coordinates(fields):
    combat = None
    if fields and fields[-1].startswith(b"!"):
        combat = fields[-1]
        match = COMBAT_META.fullmatch(combat)
        if (not match or (match[3] == b"0") != (match[4] == b"-") or
                (match[3] != b"0" and int(match[2]) == 0)):
            raise ValueError("invalid combat state")
        fields = fields[:-1]
    metadata = b""
    metadata_field = None
    if fields and fields[-1].startswith(b"~"):
        metadata_field = fields[-1]
        match = STATE_META.fullmatch(fields[-1])
        if not match or not -1 <= int(match[5]) <= 255:
            raise ValueError("invalid state metadata")
        metadata = b" " + fields[-1]
        fields = fields[:-1]
        if len(fields) < 9:
            raise ValueError("metadata requires entity state")
    if combat is not None and not metadata:
        raise ValueError("combat requires entity metadata")
    if len(fields) not in (3, 8, 9, 10, 11):
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
        if len(world) > 250 or (world != b"-" and (len(entities) > 4 or not all(
                WORLD_ENTITY.fullmatch(item) and item.count(b",") in (6, 10) for item in entities))):
            raise ValueError("invalid world state")
        state += b" " + world
    if len(fields) >= 10:
        physics = fields[9]
        values = [float(value) for value in physics.split(b",")]
        if (car == 0 or len(physics) > 80 or len(values) not in (7, 8) or
                not 0.5 <= sum(value * value for value in values[:4]) <= 1.5 or
                not -4000 <= values[4] <= 1000 or
                not 0 <= values[5] <= 1000 or not -4000 <= values[6] <= 1000):
            raise ValueError("invalid vehicle physics")
        if len(values) == 8 and not 0 <= int(physics.split(b",")[-1]) <= 15:
            raise ValueError("invalid tire mask")
        state += b" " + physics
    if len(fields) == 11:
        station = int(fields[10])
        if not -1 <= station <= 19:
            raise ValueError("invalid radio station")
        state += f" {station}".encode("ascii")
    if len(state) + len(metadata) > 332:
        if len(fields) >= 9 and fields[8] != b"-":
            fields = list(fields)
            fields[8] = fields[8].rpartition(b";")[0] or b"-"
            if metadata_field is not None:
                fields.append(metadata_field)
            if combat is not None:
                fields.append(combat)
            return coordinates(fields)
        raise ValueError("entity state is too large")
    return state + metadata + (b" " + combat if combat is not None else b"")


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
    world_owner = None
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
                position = coordinates(fields).split(b" !", 1)[0]
            except ValueError:
                continue
            now = time.monotonic()
            if player_id not in players and len(players) >= MAX_PLAYERS:
                continue
            if player_id not in players:
                print(f"Connected {player_id.decode()} ({len(players)+1}/{MAX_PLAYERS})", flush=True)
            players[player_id] = (now, position)
            metadata = position.rpartition(b" ~")[2] if b" ~" in position else None
            candidates = [peer for peer, (_, state) in players.items()
                          if b" ~" in state and state.rpartition(b" ~")[2].split(b",")[2] == b"1"]
            if world_owner not in candidates:
                world_owner = min(candidates, default=None)
            rows = [challenge + b" " + (world_owner or b"-") if metadata is not None else challenge]
            rows.extend(peer + b" " + (data[1] if metadata is not None else data[1].split(b" ~", 1)[0])
                        for peer, data in players.items() if peer != player_id)
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
                    fields = header.split()
                    if not fields or fields[0] != challenge or len(fields) not in (1, 2, 5):
                        continue
                    if len(fields) >= 2 and fields[1] != b"-" and not PLAYER_ID.fullmatch(fields[1]):
                        continue
                    if len(fields) == 5 and (
                        not fields[2].isdigit() or not fields[3].isdigit() or
                        fields[4] not in (b"0", b"1", b"2", b"3")
                    ):
                        continue
                    bridge_header = str(time.monotonic_ns()).encode()
                    if len(fields) > 1:
                        bridge_header += b" " + b" ".join(fields[1:])
                    temporary_file.write_bytes(bridge_header + b"\n" + rows)
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
