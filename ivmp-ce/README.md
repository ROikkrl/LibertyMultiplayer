# IV:MP для GTA IV Complete Edition

Порт основан на [IV:MP @ e9fadfc](https://github.com/Neproify/ivmultiplayer/tree/e9fadfc74a87f0d1d232f507b2e0bbdf22a1ab52). `upstream.patch` меняет исходный клиент IV:MP. `tools/prepare.py` применяет патч к указанной ревизии и добавляет заголовки привязок Complete Edition. Раздел распространяется под GPLv3 вместе с уведомлениями исходного проекта.

## Текущий этап

Профиль привязок проверяет PE-заголовок и 15 участков машинного кода Steam `GTAIV.exe` 1.2.0.59. Для поиска GTA natives применяется x86 `stdcall`. В `CIVPhysical` линейная и угловая скорость передаются через функции Complete Edition с 16-байтовым выравниванием. Для пулов персонажей и транспорта клиент проверяет адреса, которые использует сама игра. На Complete Edition обёртки `CPools` получают указатели на эти пулы без патча рендера чекпоинтов.

Клиент Complete Edition после загрузки сохранения подключается по оригинальному протоколу IV:MP и отправляет координаты, направление и скорость игрока пешком. Это проверено со Steam-клиентом 1.2.0.59 и вторым тестовым клиентом.

Экспериментальное отображение удалённых игроков теперь использует дополнительные слоты `CPlayerPed`. Модель Нико требует слота игрового персонажа. Освободившийся слот скрывается и используется снова в той же сессии. Проверка нового пути в GTA IV ещё требуется, поэтому `render_peers` выключен по умолчанию. Синхронизация анимаций и транспорта ещё в разработке. [Исследование игровых слотов IV:Coop](https://github.com/DanOSG/iv-coop/blob/main/docs/research-native-netcode.md#7b-extra-player-peds-the-host-appears-as-niko)

## Настройка тестовой сессии

В тестовом запуске ASI-загрузчик загружает собранный `Client.Core.dll` под именем `IVMP.CE.asi`. Положите `ivmp-ce.ini`, `bass.dll` и собранную `Network.Core.dll` рядом с `GTAIV.exe`. После загрузки сохранения в основной кампании F9 подключает и отключает клиент. Для автоматического подключения укажите `auto_connect=1`.

```ini
[session]
host=127.0.0.1
port=9999
nickname=Niko
password=
auto_connect=0
render_peers=0
```

Тестовый сервер IV:MP должен запускаться без записей `clientscript` и `clientresource` в `settings.xml`. Используйте разные ники для каждого клиента. Состояние подключения выводится через `OutputDebugString`.

## Проверка без запуска GTA IV

Используйте Zig 0.15.2 для сборки маленькой Win32-утилиты и теста соглашений вызова.

```powershell
zig c++ -target x86-windows-gnu -std=c++14 -municode ivmp-ce/tools/inspect.cpp -o ivmp-ce-inspect.exe
./ivmp-ce-inspect.exe 'F:/SteamLibrary/steamapps/common/Grand Theft Auto IV/GTAIV/GTAIV.exe'
zig c++ -target x86-windows-gnu -std=c++14 ivmp-ce/tests/abi.cpp -o ivmp-ce-abi.exe
./ivmp-ce-abi.exe
zig c++ -target x86-windows-gnu -std=c++14 ivmp-ce/tests/frame.cpp -o ivmp-ce-frame.exe
./ivmp-ce-frame.exe
```

Утилита сверяет сигнатуры в исполняемом файле, открытом в режиме чтения. Тест ABI проверяет соглашения вызова с заглушками. Тест наблюдателя кадра проверяет отказ при изменённом адресе вызова или уже запущенном планировщике, сохранение результата штатного вызова и восстановление оригинальной инструкции.

`tools/game-probe.cpp` собирается в диагностический ASI-модуль. Он пишет в `%TEMP%\IVMP.CE-Probe.log` и пытается прочитать позицию локального персонажа и дескриптор его машины после штатного диспетчера скриптов. В Steam 1.2.0.59 после загрузки сохранения подтверждены вызов наблюдателя, чтение позиции и поиск Нико по дескриптору с обратной проверкой. Чтение дескриптора машины в игре ещё предстоит проверить.

## Подготовка и сборка исходного клиента

```powershell
python ivmp-ce/tools/prepare.py work/ivmp-ce-upstream
./ivmp-ce/build.ps1 -Toolset v143
```

Для сборки решения нужны MSBuild, x86 C++ Build Tools и Windows SDK. Решение собрано в конфигурации Release Win32 с MSVC v143 и Windows SDK 10.0.26100.0. Сервер запустил штатный сценарий, ответил на локальные UDP-запросы и подтвердил вход двух тестовых клиентов сообщениями `JoinedGame`. [Установка MSVC Build Tools](https://learn.microsoft.com/en-us/cpp/overview/acquire-msvc)

## Проверка входа и синхронизации двух клиентов

Соберите тест из консоли разработчика MSVC x86. Запустите локальный сервер с двумя свободными слотами и без пароля. Тест подключается к `127.0.0.1`, использует `Network.Core.dll` из текущей папки, проверяет пересылку позиции и удаление игрока из состояния второго клиента после отключения.

```powershell
$upstream = (Resolve-Path 'work/ivmp-ce-upstream').Path
cl /nologo /EHsc /MD /std:c++14 /DWIN32 /DWIN32_LEAN_AND_MEAN /D_CRT_SECURE_NO_WARNINGS "/I$upstream/Shared" ivmp-ce/tests/peers.cpp "$upstream/Shared/CString.cpp" "$upstream/Shared/Network/CBitStream.cpp" "$upstream/Shared/Game/CControlState.cpp" "/Fe:$upstream/Binary/ivmp-ce-peers.exe" /link ws2_32.lib
Push-Location "$upstream/Binary"
try { ./ivmp-ce-peers.exe 9999 } finally { Pop-Location }
```
