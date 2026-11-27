"""Mount the Niko NPC shipped with the user's Complete Edition."""

import re
import struct
from pathlib import Path

from Crypto.Cipher import AES


HEADER = struct.Struct("<IIIIHH")
ENTRY = struct.Struct("<IIIHH")
MAGIC = 0xA94E2A52
SECTOR = 2048
EXE_KEY_OFFSET = 0xC5B73C
MODELS = {b"ig_niko." + ext: b"lf_niko." + ext for ext in (b"wdd", b"wft", b"wtd")}
ARCHIVE = "pc/models/cdimages/liberty_niko.img"
ARCHIVE_ENTRY = b"platformimg:/models/cdimages/liberty_niko\t1"


def transform(data, cipher, encrypt=False):
    end = len(data) & ~15
    blocks = data[:end]
    operation = cipher.encrypt if encrypt else cipher.decrypt
    for _ in range(16):
        blocks = operation(blocks)
    return blocks + data[end:]


def read_niko(game, cipher):
    archive = game / "TBoGT/pc/models/cdimages/componentpeds.img"
    archive_size = archive.stat().st_size
    with archive.open("rb") as stream:
        raw = stream.read(HEADER.size)
        encrypted = raw[:4] != struct.pack("<I", MAGIC)
        header = transform(raw, cipher) if encrypted else raw
        magic, version, count, size, stride, flags = HEADER.unpack(header)
        if magic != MAGIC or version != 3 or stride != ENTRY.size or not count <= 20_000 or not count * stride <= size <= 2_000_000:
            raise ValueError("Не удалось прочитать архив модели Нико в The Ballad of Gay Tony.")
        toc = stream.read(size)
        if len(toc) != size:
            raise ValueError("Архив модели Нико неполный.")
        if encrypted:
            toc = transform(toc, cipher)
        names = toc[count * stride:].split(b"\0")
        if len(names) <= count:
            raise ValueError("В архиве модели Нико повреждён список файлов.")
        entries = {}
        for i, name in enumerate(names[:count]):
            model_name = MODELS.get(name.lower())
            if model_name is None:
                continue
            resource, kind, offset, blocks, padding = ENTRY.unpack_from(toc, i * stride)
            offset *= SECTOR
            length = blocks * SECTOR - (padding & (SECTOR - 1))
            if not resource & 0xC0000000 or offset < HEADER.size + size or not 12 < length <= 8_000_000 or offset + length > archive_size:
                raise ValueError("Повреждён файл модели Нико.")
            stream.seek(offset)
            data = stream.read(length)
            if data[:4] != b"RSC\x05" or len(data) != length:
                raise ValueError("В архиве Нико отсутствуют данные модели.")
            entries[model_name] = resource, kind, blocks, padding, data
    if set(entries) != set(MODELS.values()):
        raise ValueError("Для модели Нико нужны оригинальные файлы The Ballad of Gay Tony.")
    return flags, entries


def write_archive(output, cipher, flags, entries):
    names = b"".join(name + b"\0" for name in sorted(entries))
    toc_size = len(entries) * ENTRY.size + len(names)
    data_sector = (HEADER.size + toc_size + SECTOR - 1) // SECTOR
    sector = data_sector
    directory = bytearray()
    for name in sorted(entries):
        resource, kind, blocks, padding, _ = entries[name]
        directory.extend(ENTRY.pack(resource, kind, sector, blocks, padding))
        sector += blocks
    with output.open("wb") as stream:
        stream.write(transform(HEADER.pack(MAGIC, 3, len(entries), toc_size, ENTRY.size, flags), cipher, True))
        stream.write(transform(bytes(directory) + names, cipher, True))
        stream.write(bytes(data_sector * SECTOR - stream.tell()))
        for name in sorted(entries):
            _, _, blocks, _, data = entries[name]
            stream.write(data)
            stream.write(bytes(blocks * SECTOR - len(data)))


def add_definition(source, definition):
    if any(re.match(rb"\s*lf_niko\s*,", line, re.I) for line in source.splitlines()):
        return source
    start = re.search(rb"(?im)^[ \t]*peds[ \t]*\r?$", source)
    end = re.search(rb"(?im)^[ \t]*end[ \t]*\r?$", source[start.end():]) if start else None
    if end is None:
        raise ValueError("В peds.ide отсутствует секция моделей персонажей.")
    index = start.end() + end.start()
    return source[:index] + definition + newline(source) + source[index:]


def newline(data):
    return b"\r\n" if b"\r\n" in data else b"\n"


def stage_niko(game, staging, staged):
    with (game / "GTAIV.exe").open("rb") as executable:
        executable.seek(EXE_KEY_OFFSET)
        cipher = AES.new(executable.read(32), AES.MODE_ECB)
    flags, entries = read_niko(game, cipher)
    definition_file = (game / "TBoGT/common/data/peds.ide").read_bytes()
    model = next((line for line in definition_file.splitlines() if re.match(rb"\s*ig_niko\s*,", line, re.I)), None)
    if model is None:
        raise ValueError("В данных The Ballad of Gay Tony отсутствует модель Нико.")
    definition = re.sub(rb"\big_niko\b", b"lf_niko", model, count=1, flags=re.I)
    definition = definition.replace(b"move_gng@generic_c", b"move_player").replace(b"NIKO_E2_PVG", b"VOICE_PLY_CR")
    archive = staging / ARCHIVE
    archive.parent.mkdir(parents=True, exist_ok=True)
    write_archive(archive, cipher, flags, entries)
    modified = {ARCHIVE: archive}
    for name in ("common/data/peds.ide", "common/data/images.txt", "update/common/data/peds.ide", "update/common/data/images.txt"):
        original = staged.get(name, game / name)
        if name.startswith("update/") and not original.is_file():
            continue
        source = original.read_bytes()
        if name.endswith("peds.ide"):
            result = add_definition(source, definition)
        elif any(line.split(b"#", 1)[0].strip().lower().split()[:1] == [ARCHIVE_ENTRY.split()[0]]
                 for line in source.splitlines()):
            result = source
        else:
            eol = newline(source)
            componentpeds = re.search(rb"(?im)^platformimg:/models/cdimages/componentpeds[ \t]+[^\r\n]*(?:\r?\n|$)", source)
            if componentpeds:
                index = componentpeds.end()
                prefix = source[:index]
                result = prefix + (b"" if prefix.endswith(b"\n") else eol) + ARCHIVE_ENTRY + eol + source[index:]
            else:
                result = source.rstrip(b"\r\n") + eol + ARCHIVE_ENTRY + eol
        destination = staging / Path(name)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(result)
        modified[name] = destination
    return modified
