import argparse
import hmac
import os
import re
import secrets
import socket
import time
from pathlib import Path
from release_updates import VERSION

MAX_PLAYERS = 4
TIMEOUT = 3.0
INTERVAL = 1 / 30
PACKET_LIMIT = 1200
PLAYER_ID = re.compile(rb"[0-9a-f]{16}")
RIDE_ID = re.compile(rb"(?:-|[0-9a-f]{16}:-?[0-9]{1,10})")
WORLD_ENTITY = re.compile(rb"(?:p(?:,-?[0-9]{1,11}){6}|v(?:,-?[0-9]{1,11}){6}(?:,-?[0-9]{1,4}){0,4})")
STATE_META = re.compile(rb"~([0-9]{1,12}),([0-9]{1,12}),([01]),([0-3]),(-1|[0-9]{1,3}),(-|[A-Za-z0-9_]{1,32})")
COMBAT_META = re.compile(rb"!([0-7]),([0-9]{1,19}),([0-2]),(-|[0-9a-f]{16})")
WORLD_PAGE_ROWS = 5
WORLD_MAX_PAGES = 6
VEHICLE_TAKE = re.compile(rb"t,([0-9a-f]{16}),(-?[0-9]{1,10}),(-?[0-9]{1,10}),([01])")
WIRE_VERSION = VERSION.encode("ascii")
VERSION_TOKEN = re.compile(rb"[0-9]+\.[0-9]+\.[0-9]+")


class VersionMismatch(ValueError):
    def __init__(self, received, version=VERSION):
        super().__init__(f"Версии не совпадают. Версия клиента: {version}. Версия сервера: {received}")


def suggested_power():
    return min(99, max(1, (os.cpu_count() or 1) * 4))


def parse_power(value):
    value = value.strip().replace(",", ".")
    if not re.fullmatch(r"0\.[0-9]{1,2}", value):
        raise ValueError("Допустимый индекс мощности от 0.01 до 0.99.")
    power = int(value[2:].ljust(2, "0"))
    if not 1 <= power <= 99:
        raise ValueError("Допустимый индекс мощности от 0.01 до 0.99.")
    return power


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


def validate_reply(response, challenge, version=VERSION):
    fields = response.split()
    if len(fields) == 3 and fields[1] == challenge and VERSION_TOKEN.fullmatch(fields[2]):
        if fields[0] == b"LF-VERSION" or fields[0] == b"LF-PONG" and fields[2] != version.encode("ascii"):
            raise VersionMismatch(fields[2].decode("ascii"), version)
        if fields[0] == b"LF-PONG":
            return
    raise ValueError("Unexpected server response")


def probe_connection(sock, key, version=VERSION):
    challenge = secrets.token_hex(8).encode("ascii")
    sock.settimeout(1)
    started = time.monotonic()
    sock.send(seal(key, b" ".join((b"LF-PING", challenge, version.encode("ascii")))))
    try:
        response = open_packet(key, sock.recv(PACKET_LIMIT + 1))
    except socket.timeout:
        # Pre-version servers respond to a two-field probe without reporting their version.
        sock.send(seal(key, b"LF-PING " + challenge))
        response = open_packet(key, sock.recv(PACKET_LIMIT + 1))
        if response == b"LF-PONG " + challenge:
            raise VersionMismatch("не сообщается устаревшим сервером", version)
    validate_reply(response, challenge, version)
    return round((time.monotonic() - started) * 1000)


def check_server(host, port, key, version=VERSION):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.connect((host, port))
        return probe_connection(sock, key, version)


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
                if 2 <= len(parts) <= 3 and parts[0] == b"LF-PING" and PLAYER_ID.fullmatch(parts[1]):
                    kind = b"LF-PONG" if parts[2:] == [WIRE_VERSION] else b"LF-VERSION"
                    sock.sendto(seal(args.key, b" ".join((kind, parts[1], WIRE_VERSION))), address)
                    continue
                if len(parts) == 2 and parts[0] == b"LF-LEAVE":
                    peer = players.get(parts[1])
                    if peer is not None and peer[2] == address:
                        del players[parts[1]]
                        print(f"Disconnected {parts[1].decode()} ({len(players)}/{MAX_PLAYERS})", flush=True)
                    continue
                version, player_id, challenge, *fields = parts
                if version != b"LF1" or not PLAYER_ID.fullmatch(player_id):
                    continue
                if not PLAYER_ID.fullmatch(challenge):
                    continue
                if not fields or fields[0] != WIRE_VERSION:
                    sock.sendto(seal(args.key, b" ".join((b"LF-VERSION", challenge, WIRE_VERSION))), address)
                    continue
                if len(fields) < 2 or not fields[1].isdigit() or not 1 <= int(fields[1]) <= 99:
                    continue
                fields = fields[2:]
                position = coordinates(fields).split(b" !", 1)[0]
            except ValueError:
                continue
            now = time.monotonic()
            if player_id not in players and len(players) >= MAX_PLAYERS:
                continue
            if player_id not in players:
                print(f"Connected {player_id.decode()} ip={address[0]} ({len(players)+1}/{MAX_PLAYERS})", flush=True)
            players[player_id] = (now, position, address)
            metadata = position.rpartition(b" ~")[2] if b" ~" in position else None
            candidates = [peer for peer, (_, state, _) in players.items()
                          if b" ~" in state and state.rpartition(b" ~")[2].split(b",")[2] == b"1"]
            if world_owner not in candidates:
                world_owner = min(candidates, default=None)
            rows = [challenge + b" " + (world_owner or b"-") if metadata is not None else challenge]
            rows.extend(peer + b" " + (data[1] if metadata is not None else data[1].split(b" ~", 1)[0])
                        for peer, data in players.items() if peer != player_id)
            sock.sendto(seal(args.key, b"\n".join(rows) + b"\n"), address)


class WorldExchange:
    def __init__(self, directory, player_id, key):
        self.outgoing = directory / "world-local.txt"
        self.incoming = directory / "world-remote.txt"
        self.temporary = directory / ("world-" + player_id.decode("ascii") + ".tmp")
        self.player_id = player_id
        self.key = key
        self.next_poll = 0.0
        self.last_upload = None
        self.requests = {}
        self.received_at = 0

    def pause(self):
        self.requests.clear()
        self.last_upload = None

    def poll(self, sock, sample):
        now = time.monotonic()
        if now < self.next_poll:
            return
        self.next_poll = now + 0.1
        self.requests = {token: state for token, state in self.requests.items()
                         if now - state[0] < 0.5}
        try:
            frame = self.outgoing.read_bytes()
        except OSError:
            return
        lines = frame.splitlines()
        if not lines or not frame.endswith(b"\n") or len(lines) > 1 + WORLD_PAGE_ROWS * WORLD_MAX_PAGES:
            return
        header = lines[0].split()
        if len(header) != 3 or header[0] != self.player_id or not header[1].isdigit():
            return
        _, sequence, episode = header
        if episode not in (b"0", b"1", b"2") or abs(int(sequence) - int(sample)) > 1000:
            return
        if frame != self.last_upload:
            rows = lines[1:]
            if rows and rows[0].startswith(b"t,"):
                take = VEHICLE_TAKE.fullmatch(rows.pop(0))
                if take:
                    sock.send(seal(self.key, b" ".join((b"LFWT", self.player_id, *take.groups()))))
            total = max(1, (len(rows) + WORLD_PAGE_ROWS - 1) // WORLD_PAGE_ROWS)
            for page in range(total):
                prefix = b" ".join((b"LFWU", self.player_id, sequence, episode,
                                    str(page).encode("ascii"), str(total).encode("ascii")))
                selected = rows[page * WORLD_PAGE_ROWS:(page + 1) * WORLD_PAGE_ROWS]
                payload = prefix + b"\n" + b"\n".join(selected)
                if len(payload) + 32 <= PACKET_LIMIT:
                    sock.send(seal(self.key, payload))
            self.last_upload = frame
        token = secrets.token_hex(8).encode("ascii")
        self.requests[token] = (now, time.monotonic_ns(), sample, episode, {})
        sock.send(seal(self.key, b" ".join((b"LFWQ", self.player_id, token, episode))))

    def receive(self, response):
        header, _, rows = response.partition(b"\n")
        fields = header.split()
        if len(fields) != 5 or fields[1] not in self.requests or not PLAYER_ID.fullmatch(fields[2]):
            return
        _, token, owner, index, total = fields
        if not index.isdigit() or not total.isdigit() or not response.endswith(b"\n"):
            return
        index, total = int(index), int(total)
        if not 0 <= index < total <= WORLD_MAX_PAGES or len(rows.splitlines()) > WORLD_PAGE_ROWS:
            return
        _, timestamp, sample, episode, pages = self.requests[token]
        if timestamp <= self.received_at:
            del self.requests[token]
            return
        if pages and next(iter(pages.values()))[:2] != (owner, total):
            return
        pages[index] = (owner, total, rows)
        if len(pages) != total:
            return
        header = b" ".join((self.player_id, str(timestamp).encode("ascii"), sample, episode, owner))
        self.temporary.write_bytes(header + b"\n" + b"".join(pages[page][2] for page in range(total)))
        self.temporary.replace(self.incoming)
        self.received_at = timestamp
        del self.requests[token]


def join(args, stop=None, report=print):
    directory = args.mod_dir.resolve()
    if not any((directory / marker).is_file() for marker in ("bridge.txt", "manifest.lua")):
        raise SystemExit(f"Mod not found in {directory}")
    local_file = directory / "position.txt"
    peer_file = directory / "peers.txt"
    temporary_file = directory / "peers.tmp"
    bridge_file = directory / "bridge-live.txt"
    player_id = secrets.token_hex(8).encode("ascii")
    world = WorldExchange(directory, player_id, args.key)
    (directory / "player.txt").write_bytes(player_id)
    bridge_temporary = directory / ("bridge-" + player_id.decode("ascii") + ".tmp")
    next_heartbeat = 0.0
    try:
        last_position = local_file.read_bytes()
    except OSError:
        last_position = None
    last_change = 0.0
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.connect((args.host, args.port))
        probe_connection(sock, args.key)
        power = str(getattr(args, "power", suggested_power())).encode("ascii")
        server_address = sock.getpeername()[0].encode("ascii")
        pause_marker = b"PAUSED " + player_id + b"\n"
        leave_packet = seal(args.key, b"LF-LEAVE " + player_id)
        status = None
        while stop is None or not stop.is_set():
            started = time.monotonic()
            next_status = "Waiting for GTA IV"
            if started >= next_heartbeat:
                try:
                    bridge_temporary.write_bytes(player_id)
                    bridge_temporary.replace(bridge_file)
                    next_heartbeat = started + 1
                except OSError:
                    # The game may have the file open briefly.
                    next_heartbeat = started + INTERVAL
            try:
                position = local_file.read_bytes()
                if position != last_position:
                    last_position, last_change = position, started
                if position == pause_marker:
                    next_status = "Paused in GTA IV"
                    if status != next_status:
                        world.pause()
                        sock.send(leave_packet)
                elif position.endswith(b"\n") and started - last_change < TIMEOUT:
                    tick, *fields = position.split()
                    sample = str(int(tick)).encode("ascii")
                    challenge = secrets.token_hex(8).encode("ascii")
                    payload = b" ".join((b"LF1", player_id, challenge, WIRE_VERSION, power, coordinates(fields)))
                    next_status = "Waiting for the server"
                    sock.send(seal(args.key, payload))
                    deadline = started + 0.15
                    while time.monotonic() < deadline:
                        sock.settimeout(max(0.001, deadline - time.monotonic()))
                        response = open_packet(args.key, sock.recv(65535))
                        if response.startswith(b"LF-VERSION "):
                            validate_reply(response, challenge)
                        if response.startswith(b"LFW1 "):
                            try:
                                world.receive(response)
                            except OSError:
                                pass
                            continue
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
                            source = b":".join((bridge_header, sample, server_address, player_id))
                            bridge_header = source + b" " + b" ".join(fields[1:])
                        temporary_file.write_bytes(bridge_header + b"\n" + rows)
                        temporary_file.replace(peer_file)
                        next_status = f"Connected — other players: {len(rows.splitlines())}"
                        try:
                            world.poll(sock, sample)
                        except OSError:
                            pass
                        break
            except VersionMismatch:
                raise
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
    client.add_argument("--power-index", dest="power", type=parse_power, default=suggested_power())
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
    except VersionMismatch as error:
        parser.exit(1, str(error) + "\n")
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    cli()
