import argparse
import queue
import threading
import tkinter as tk
import webbrowser
from pathlib import Path
from tkinter import filedialog, ttk

from installer import install
from liberty_friends import check_server, join
from release_updates import VERSION, latest_update, repository


class Launcher:
    def __init__(self, root):
        self.root = root
        self.worker = None
        self.stop = threading.Event()
        self.messages = queue.Queue()
        self.install_results = queue.Queue()
        self.check_results = queue.Queue()
        self.installing = False
        self.repo = repository()
        self.update_results = queue.Queue()
        self.folder = tk.StringVar()
        self.host = tk.StringVar(value="127.0.0.1")
        self.port = tk.StringVar(value="24840")
        self.key = tk.StringVar()
        self.status = tk.StringVar(value="Закрой GTA IV и выбери папку игры для установки.")
        root.title(f"LibertyMultiplayer {VERSION}")
        panel = ttk.Frame(root, padding=18)
        panel.grid(sticky="nsew")
        root.columnconfigure(0, weight=1)
        panel.columnconfigure(1, weight=1)
        for row, (label, value) in enumerate((
            ("Папка GTA IV", self.folder), ("Сервер", self.host),
            ("UDP-порт", self.port), ("Ключ сессии", self.key),
        )):
            ttk.Label(panel, text=label).grid(row=row, column=0, sticky="w", padx=(0, 12), pady=5)
            entry = ttk.Entry(panel, textvariable=value, width=40, show="*" if row == 3 else "")
            entry.grid(row=row, column=1, sticky="ew", pady=5)
            entry.bind("<Control-KeyPress>", self.paste_shortcut)
            if row == 3:
                ttk.Button(panel, text="Вставить ключ", command=lambda: self.paste(entry)).grid(
                    row=row, column=2, padx=(8, 0)
                )
        self.install_button = ttk.Button(panel, text="Выбрать и установить", command=self.browse)
        self.install_button.grid(row=0, column=2, padx=(8, 0))
        self.button = ttk.Button(panel, text="Подключиться", command=self.toggle)
        self.button.grid(row=4, column=1, sticky="w", pady=(10, 4))
        self.check_button = ttk.Button(panel, text="Проверить сервер", command=self.probe)
        self.check_button.grid(row=4, column=2, padx=(8, 0))
        self.check_status = tk.StringVar()
        ttk.Label(panel, textvariable=self.check_status, wraplength=440).grid(
            row=6, column=0, columnspan=3, sticky="w"
        )
        ttk.Label(panel, textvariable=self.status, wraplength=440).grid(
            row=5, column=0, columnspan=3, sticky="w", pady=(5, 0)
        )
        if self.repo:
            self.update = tk.StringVar(value="Проверка обновлений…")
            ttk.Label(panel, textvariable=self.update).grid(row=7, column=0, columnspan=2, sticky="w")
            ttk.Button(panel, text="GitHub Releases", command=self.open_releases).grid(row=7, column=2)
            threading.Thread(target=self.check_updates, daemon=True).start()
        root.protocol("WM_DELETE_WINDOW", self.close)
        root.after(100, self.refresh)

    def paste(self, entry):
        try:
            value = self.root.clipboard_get().rstrip("\r\n")
        except tk.TclError:
            return
        if entry.selection_present():
            entry.delete("sel.first", "sel.last")
        entry.insert("insert", value)

    def paste_shortcut(self, event):
        if event.keycode == 86 or event.keysym.lower() in ("v", "cyrillic_em"):
            self.paste(event.widget)
            return "break"

    def server_settings(self):
        host, key = self.host.get().strip(), self.key.get()
        port = int(self.port.get())
        if not host or not 1 <= port <= 65535 or len(key) < 24:
            raise ValueError
        return host, port, key.encode("utf-8")

    def probe(self):
        try:
            settings = self.server_settings()
        except ValueError:
            self.check_status.set("Проверь адрес, порт и ключ от 24 символов.")
            return
        self.check_button.config(state="disabled")
        self.check_status.set("Проверяю UDP-сервер…")
        threading.Thread(target=self.run_probe, args=settings, daemon=True).start()

    def run_probe(self, host, port, key):
        try:
            latency = check_server(host, port, key)
        except (OSError, ValueError):
            self.check_results.put("Нет подтверждённого ответа. Проверь адрес, UDP-порт, ключ и версию сервера.")
        else:
            self.check_results.put(f"Сервер доступен. Ключ принят. Задержка: {latency} мс.")

    def browse(self):
        selected = filedialog.askdirectory()
        if selected:
            self.installing = True
            self.install_button.config(state="disabled")
            self.button.config(state="disabled")
            self.status.set("Устанавливаю мод и зависимости…")
            threading.Thread(target=self.install_game, args=(Path(selected),), daemon=True).start()

    def install_game(self, selected):
        try:
            result = install(selected, Path(__file__).resolve().parent / "install_bundle")
        except (ValueError, OSError, RuntimeError) as error:
            self.install_results.put((None, str(error)))
        else:
            game, backup = result
            message = "Готово. Запусти GTA IV, загрузи сохранение и нажми F9."
            if backup:
                message += f" Резервная копия: {backup.name}"
            self.install_results.put((game, message))

    def toggle(self):
        if self.worker is not None:
            self.stop.set()
            self.button.config(state="disabled")
            return
        game = Path(self.folder.get().strip())
        try:
            host, port, key = self.server_settings()
        except ValueError:
            self.status.set("Укажи адрес, порт от 1 до 65535 и ключ длиной от 24 символов.")
            return
        if not (game / "GTAIV.exe").is_file():
            self.status.set("В выбранной папке должен быть GTAIV.exe.")
            return
        args = argparse.Namespace(host=host, port=port, key=key,
                                  mod_dir=game / "mods" / "LibertyFriends")
        self.stop.clear()
        self.install_button.config(state="disabled")
        self.button.config(text="Отключиться")
        self.status.set("Подключение…")
        self.worker = threading.Thread(target=self.connect, args=(args,), daemon=True)
        self.worker.start()

    def connect(self, args):
        try:
            join(args, stop=self.stop, report=lambda message: self.messages.put((message, False)))
        except (OSError, SystemExit) as error:
            self.messages.put((str(error), True))
        else:
            self.messages.put(("Отключено", True))

    def refresh(self):
        if not self.check_results.empty():
            self.check_status.set(self.check_results.get_nowait())
            self.check_button.config(state="normal")
        if not self.update_results.empty():
            self.update.set(self.update_results.get_nowait())
        if not self.install_results.empty():
            game, message = self.install_results.get_nowait()
            if game is not None:
                self.folder.set(str(game))
            self.status.set(message)
            self.installing = False
            self.install_button.config(state="normal")
            self.button.config(state="normal")
        while not self.messages.empty():
            message, finished = self.messages.get_nowait()
            if message.startswith("Connected"):
                message = "Подключено. Других игроков: " + message.rsplit(" ", 1)[-1]
            elif message.startswith("Waiting for GTA IV"):
                message = "Загрузи сейв в GTA IV и нажми F9."
            elif message == "Waiting for the server":
                message = "Ожидание ответа от сервера."
            self.status.set(message)
            if finished:
                self.worker = None
                self.button.config(text="Подключиться", state="normal")
                self.install_button.config(state="normal")
        self.root.after(100, self.refresh)

    def check_updates(self):
        try:
            tag = latest_update(self.repo)
        except (OSError, ValueError):
            self.update_results.put("GitHub недоступен, проверка обновлений не выполнена.")
        else:
            self.update_results.put(f"Доступна новая версия {tag}." if tag else "Обновлений нет.")

    def open_releases(self):
        webbrowser.open(f"https://github.com/{self.repo}/releases")

    def close(self):
        if self.installing:
            return
        self.stop.set()
        self.root.destroy()


if __name__ == "__main__":
    window = tk.Tk()
    Launcher(window)
    window.mainloop()
