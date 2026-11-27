"""Build a local LibertyMultiplayer radio add-on from user-supplied media."""

import argparse
from bisect import bisect_right
from dataclasses import dataclass
import json
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import wave
import zipfile


U32 = struct.Struct("<I")
RPF_ENTRY = struct.Struct("<4I")
SECTOR = 2048
STATION = "RADIO_VLADIVOSTOK"
PLAYLIST = f"{STATION}_MUSIC".encode("ascii")
PLAYLIST_COUNT = 63
TEMPLATE = "LIBERTYCITY"
TRACKS = ("LF_ESLI_YA_BUDU_TANTSEVAT", "LF_BUD_ILI_NE_BUD")


def joaat(name):
    value = 0
    for byte in name.lower().encode("ascii"):
        value = (value + byte) * 1025 & 0xFFFFFFFF
        value ^= value >> 6
    value = value * 9 & 0xFFFFFFFF
    value ^= value >> 11
    return value * 32769 & 0xFFFFFFFF


@dataclass
class Record:
    name: bytes
    data: bytes
    relocations: tuple


class AudioMetadata:
    def __init__(self, data, version):
        found, data_size = struct.unpack_from("<II", data)
        if found != version:
            raise ValueError("Unexpected audio metadata version")
        self.version = version
        table_start = data_size + 8
        table_end = table_start + 4 + U32.unpack_from(data, table_start)[0]
        self.paths = data[table_start:table_end]
        count = U32.unpack_from(data, table_end)[0]
        cursor = table_end + 8
        offsets = []
        records = []
        for _ in range(count):
            length = data[cursor]
            name = data[cursor + 1:cursor + length + 1]
            cursor += length + 1
            offset, size = struct.unpack_from("<II", data, cursor)
            cursor += 8
            offsets.append(offset)
            records.append(Record(name, data[8 + offset:8 + offset + size], ([], [])))
        for group in range(2):
            count = U32.unpack_from(data, cursor)[0]
            cursor += 4
            for (offset,) in struct.iter_unpack("<I", data[cursor:cursor + 4 * count]):
                offset -= 8
                index = bisect_right(offsets, offset) - 1
                if index < 0:
                    raise ValueError("Invalid audio metadata reference")
                relative = offset - offsets[index]
                if relative + 4 > len(records[index].data):
                    raise ValueError("Invalid audio metadata reference")
                records[index].relocations[group].append(relative)
            cursor += 4 * count
        self.records = {record.name: record for record in records}
        if cursor != len(data) or self.encode() != data:
            raise ValueError("Unsupported audio metadata layout")

    def encode(self):
        binary = bytearray()
        names = bytearray()
        relocations = (bytearray(), bytearray())
        string_size = 0
        for record in self.records.values():
            offset = len(binary) + 1
            binary.extend(b"\0" + record.data[:1] + U32.pack(string_size) + record.data[5:])
            names.extend(bytes((len(record.name),)) + record.name + struct.pack("<II", offset, len(record.data)))
            string_size += len(record.name) + 1
            for table, entries in zip(relocations, record.relocations):
                for relative in entries:
                    table.extend(U32.pack(8 + offset + relative))
        return b"".join((
            struct.pack("<II", self.version, len(binary)), binary, self.paths,
            struct.pack("<II", len(self.records), string_size), names,
            *(U32.pack(len(table) // 4) + table for table in relocations),
        ))


def extend_playlist(metadata):
    playlist = metadata.records[PLAYLIST]
    count = playlist.data[PLAYLIST_COUNT]
    if count != 13 or len(playlist.data) != PLAYLIST_COUNT + 1 + 8 * count:
        raise ValueError("Use the Radio Restoration playlist containing the 13 original tracks")
    entries = b"".join(struct.pack("<II", 0, joaat(f"{STATION}_{track}")) for track in TRACKS)
    playlist.data = (
        playlist.data[:PLAYLIST_COUNT] + bytes((count + len(TRACKS),)) +
        playlist.data[PLAYLIST_COUNT + 1:] + entries
    )


def add_sound_records(metadata, track, duration):
    prefix = f"{STATION}_{TEMPLATE}"
    archive = f"{STATION}/{TEMPLATE}"
    records = [record for name, record in metadata.records.items()
               if name == prefix.encode("ascii") or name.startswith((prefix + "_").encode("ascii"))]
    if len(records) != 7:
        raise ValueError("Cannot find the Vladivostok FM stereo and positional sound records")
    names = [record.name.decode("ascii") for record in records]
    hashes = {joaat(name): joaat(name.replace(TEMPLATE, track)) for name in (
        *names, archive, f"{TEMPLATE}_LEFT", f"{TEMPLATE}_RIGHT",
    )}
    for record, name in zip(records, names):
        data = bytearray(record.data)
        for old, new in hashes.items():
            data = data.replace(U32.pack(old), U32.pack(new))
        if name == prefix:
            if len(data) != 39 or data[0] != 8:
                raise ValueError("Unexpected streaming sound layout")
            U32.pack_into(data, 18, duration)
        new_name = name.replace(TEMPLATE, track).encode("ascii")
        if new_name in metadata.records:
            raise ValueError(f"Sound already exists: {new_name!r}")
        metadata.records[new_name] = Record(new_name, data, record.relocations)


def encode_track(work, ffmpeg, converter, track, source):
    wav = work / f"{track}.wav"
    subprocess.run((ffmpeg, "-hide_banner", "-v", "error", "-i", source,
                    "-map", "0:a:0", "-map_metadata", "-1", "-af", "loudnorm=I=-14:TP=-1.5:LRA=11",
                    "-ar", "32000", "-ac", "2", "-c:a", "pcm_s16le", wav), check=True)
    with wave.open(str(wav), "rb") as audio:
        duration = round(1000 * audio.getnframes() / audio.getframerate())
    if not 0 < duration < 900_000:
        raise ValueError(f"Invalid audio duration: {source.name}")
    oaf = wav.with_suffix(".oaf")
    oaf.write_text(json.dumps({
        "isStream": True, "file": wav.name, "channelsCount": 2, "hasTimestamps": False,
        "channels": [
            {"name": f"{track}_{side}", "compression": "ADPCM", "headroom": 136}
            for side in ("LEFT", "RIGHT")
        ],
    }), encoding="ascii")
    subprocess.run((converter, oaf.name), cwd=work, check=True)
    output = work / track
    if not output.is_file() or output.stat().st_size < SECTOR:
        raise ValueError(f"The audio converter did not produce {track}")
    return output, duration


def extend_archive(original, target, game, tracks):
    from Crypto.Cipher import AES
    from pyrpfiv.crypto import decrypt_toc, extract_aes_key

    key = extract_aes_key(str(game / "GTAIV.exe"))
    shutil.copyfile(original, target)
    with target.open("r+b") as archive:
        header = bytearray(archive.read(SECTOR))
        magic, toc_size, count, _, encryption = struct.unpack_from("<4s4I", header)
        if magic != b"RPF3" or toc_size != SECTOR or not encryption or count + len(tracks) > SECTOR // 16:
            raise ValueError("Unsupported Radio Restoration archive")
        toc = decrypt_toc(archive.read(toc_size), key)
        entries = list(RPF_ENTRY.iter_unpack(toc))[:count]
        directory = next(index for index in range(1, entries[0][1] + 1)
                         if entries[index][0] == joaat(STATION))
        name, unused, position, length = entries[directory]
        first = position & 0x7FFFFFFF
        last = first + length
        new_entries = []
        archive.seek(0, 2)
        for track, source in tracks:
            archive.write(bytes(-archive.tell() % SECTOR))
            offset = archive.tell()
            with source.open("rb") as audio:
                shutil.copyfileobj(audio, archive)
            archive.write(bytes(-archive.tell() % SECTOR))
            size = archive.tell() - offset
            new_entries.append((joaat(track), size, offset, size))
        entries[first:last] = sorted(entries[first:last] + new_entries)
        entries[directory] = (name, unused, position, length + len(tracks))
        for index in range(1, entries[0][1] + 1):
            folder, unused, position, length = entries[index]
            if (position & 0x7FFFFFFF) >= last:
                entries[index] = (folder, unused, position + len(tracks), length)
        table = b"".join(RPF_ENTRY.pack(*entry) for entry in entries).ljust(toc_size, b"\0")
        cipher = AES.new(key, AES.MODE_ECB)
        for _ in range(16):
            table = cipher.encrypt(table)
        U32.pack_into(header, 8, len(entries))
        archive.seek(0)
        archive.write(header)
        archive.write(table)


def build(args):
    source = args.restoration / "update"
    config = Path("pc/audio/config")
    sfx = Path("pc/audio/sfx/RADIO_RESTORATION.rpf")
    game_rel = config / "GAME.DAT16"
    sounds_rel = config / "SOUNDS.DAT15"
    playlist = AudioMetadata((source / game_rel).read_bytes(), 16)
    sounds = AudioMetadata((source / sounds_rel).read_bytes(), 15)
    extend_playlist(playlist)
    with tempfile.TemporaryDirectory(prefix="liberty-radio-build-") as staging:
        work = Path(staging)
        tracks = []
        for track, media in zip(TRACKS, args.music):
            encoded, duration = encode_track(work, args.ffmpeg, args.converter, track, media)
            add_sound_records(sounds, track, duration)
            tracks.append((track, encoded))
        archive_file = work / sfx.name
        extend_archive(source / sfx, archive_file, args.game, tracks)
        with zipfile.ZipFile(args.output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
            archive.writestr("liberty-radio.txt", "LibertyMultiplayer Radio 1\n")
            for file in sorted(source.rglob("*")):
                if not file.is_file():
                    continue
                relative = file.relative_to(source)
                if relative == game_rel:
                    archive.writestr("update/" + relative.as_posix(), playlist.encode())
                elif relative == sounds_rel:
                    archive.writestr("update/" + relative.as_posix(), sounds.encode())
                else:
                    archive.write(archive_file if relative == sfx else file, "update/" + relative.as_posix())
            with zipfile.ZipFile(args.fusionfix) as fusion:
                for member in fusion.infolist():
                    if not member.is_dir() and member.filename.startswith(("update/", "plugins/")):
                        with fusion.open(member) as stream, archive.open("fusionfix/" + member.filename, "w") as out:
                            shutil.copyfileobj(stream, out)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("restoration", "game", "ffmpeg", "converter", "fusionfix", "output"):
        parser.add_argument("--" + option, required=True, type=lambda value: Path(value).resolve())
    parser.add_argument("music", nargs=2, type=lambda value: Path(value).resolve())
    build(parser.parse_args())
