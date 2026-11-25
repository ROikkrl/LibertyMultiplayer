import csv
import ctypes
import filecmp
import io
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


SUPPORTED_VERSION = (1, 2, 0, 59)


def game_version(executable):
    version = ctypes.WinDLL("version", use_last_error=True)
    version.GetFileVersionInfoSizeW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p]
    version.GetFileVersionInfoW.argtypes = [
        ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p
    ]
    version.VerQueryValueW.argtypes = [
        ctypes.c_void_p, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_void_p),
        ctypes.POINTER(ctypes.c_uint),
    ]
    size = version.GetFileVersionInfoSizeW(str(executable), None)
    data = ctypes.create_string_buffer(size)
    pointer, length = ctypes.c_void_p(), ctypes.c_uint()
    if not size or not version.GetFileVersionInfoW(str(executable), 0, size, data):
        raise ValueError("Не удалось определить версию GTAIV.exe.")
    if not version.VerQueryValueW(data, "\\", ctypes.byref(pointer), ctypes.byref(length)):
        raise ValueError("В GTAIV.exe отсутствуют данные о версии.")
    fields = ctypes.cast(pointer, ctypes.POINTER(ctypes.c_uint32))
    if length.value < 16 or fields[0] != 0xFEEF04BD:
        raise ValueError("Недопустимые данные о версии GTAIV.exe.")
    high, low = fields[2], fields[3]
    return high >> 16, high & 0xFFFF, low >> 16, low & 0xFFFF


def locate_game(selected):
    for directory in (selected, selected / "GTAIV"):
        exe = directory / "GTAIV.exe"
        if exe.is_file():
            found = game_version(exe)
            if found != SUPPORTED_VERSION:
                label = ".".join(map(str, found))
                raise ValueError(f"Нужна GTA IV Complete Edition 1.2.0.59. Найдена версия {label}.")
            return directory.resolve()
    raise ValueError("В выбранной папке или подпапке GTAIV должен быть GTAIV.exe.")


def ensure_game_closed():
    tasklist = Path(os.environ["SystemRoot"]) / "System32" / "tasklist.exe"
    result = subprocess.check_output(
        [str(tasklist), "/FI", "IMAGENAME eq GTAIV.exe", "/FO", "CSV", "/NH"],
        creationflags=subprocess.CREATE_NO_WINDOW,
    ).decode("ascii", errors="replace")
    if any(row and row[0].lower() == "gtaiv.exe" for row in csv.reader(io.StringIO(result))):
        raise ValueError("Закрой GTA IV и повтори установку.")


def ensure_vc_runtime(game, redistributable):
    wow64 = Path(os.environ["SystemRoot"]) / "SysWOW64"
    components = ("msvcp140.dll", "vcruntime140.dll")

    def available():
        return all((wow64 / name).is_file() or (game / name).is_file() for name in components)

    if not available():
        result = subprocess.run([str(redistributable), "/install", "/passive", "/norestart"],
                                check=False)
        if not available():
            raise ValueError(f"Visual C++ x86 не установлен. Код установщика: {result.returncode}.")


def replace_file(source, target):
    handle, name = tempfile.mkstemp(prefix=".liberty-friends-", dir=target.parent)
    os.close(handle)
    temp = Path(name)
    try:
        shutil.copyfile(source, temp)
        os.replace(temp, target)
    finally:
        temp.unlink(missing_ok=True)


def install(selected, bundle):
    game = locate_game(selected)
    ensure_game_closed()
    payload = bundle / "game"
    required = (
        "dinput8.dll", "ScriptHook.dll", "ScriptHookDotNet.asi", "aCompleteEditionHook.asi",
        "d3dx9_43.dll", "scripts/LibertyFriends.net.dll", "mods/LibertyFriends/bridge.txt",
    )
    if not all((payload / name).is_file() for name in required):
        raise ValueError("В этой сборке отсутствуют файлы установки. Открой готовый LibertyFriends.exe.")
    files = list(payload.rglob("*"))
    changes = []
    for source in files:
        if not source.is_file():
            continue
        relative = source.relative_to(payload)
        target = game / relative
        if not target.resolve().is_relative_to(game):
            raise ValueError(f"Путь установки выходит за пределы GTA IV: {relative}")
        if not target.is_file() or not filecmp.cmp(source, target, shallow=False):
            changes.append((source, relative, target))
    # Back up every existing file before making changes to the game.
    backup = None
    originals = [item for item in changes if item[2].exists()]
    if originals:
        backup = Path(tempfile.mkdtemp(prefix="LibertyFriends-backup-", dir=game))
        for _, relative, target in originals:
            saved = backup / relative
            saved.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(target, saved)
    ensure_vc_runtime(game, bundle / "vc_redist.x86.exe")

    applied = []
    try:
        for source, relative, target in changes:
            target.parent.mkdir(parents=True, exist_ok=True)
            replace_file(source, target)
            applied.append((relative, target))
    except OSError as error:
        rollback_errors = []
        for relative, target in reversed(applied):
            saved = backup / relative if backup else None
            try:
                if saved and saved.exists():
                    replace_file(saved, target)
                else:
                    target.unlink()
            except OSError:
                rollback_errors.append(str(target))
        if rollback_errors:
            raise OSError(f"Не удалось восстановить: {', '.join(rollback_errors)}. Копия: {backup}") from error
        raise
    return game, backup
