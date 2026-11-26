import base64
import ctypes
import json
import os
import tempfile
from ctypes import wintypes
from pathlib import Path


class DataBlob(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]


def settings_file():
    return Path(os.environ["LOCALAPPDATA"]) / "LibertyMultiplayer" / "settings.json"


def protect(data, decrypt=False):
    crypt = ctypes.WinDLL("crypt32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    func = crypt.CryptUnprotectData if decrypt else crypt.CryptProtectData
    func.argtypes = [ctypes.POINTER(DataBlob), ctypes.c_void_p, ctypes.c_void_p,
                     ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(DataBlob)]
    func.restype = wintypes.BOOL
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    storage = ctypes.create_string_buffer(data)
    source = DataBlob(len(data), ctypes.cast(storage, ctypes.POINTER(ctypes.c_ubyte)))
    result = DataBlob()
    if not func(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(result)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return ctypes.string_at(result.data, result.size)
    finally:
        kernel.LocalFree(result.data)


def load_settings():
    try:
        data = json.loads(settings_file().read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    if not isinstance(data, dict):
        return {}
    values = {key: data[key] for key in ("folder", "host", "port") if isinstance(data.get(key), str)}
    try:
        secret = data.get("key_dpapi")
        if isinstance(secret, str) and secret:
            values["key"] = protect(base64.b64decode(secret, validate=True), decrypt=True).decode("utf-8")
    except (OSError, ValueError):
        pass
    return values


def save_settings(values):
    path = settings_file()
    path.parent.mkdir(parents=True, exist_ok=True)
    data = {key: values[key] for key in ("folder", "host", "port")}
    data["key_dpapi"] = (base64.b64encode(protect(values["key"].encode("utf-8"))).decode("ascii")
                         if values["key"] else "")
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        try:
            json.dump(data, stream, ensure_ascii=False)
        except Exception:
            stream.close()
            temporary.unlink(missing_ok=True)
            raise
    try:
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)
