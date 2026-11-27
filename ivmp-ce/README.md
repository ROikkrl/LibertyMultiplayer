# IV:MP для GTA IV Complete Edition

Порт основан на [IV:MP @ e9fadfc](https://github.com/Neproify/ivmultiplayer/tree/e9fadfc74a87f0d1d232f507b2e0bbdf22a1ab52). `upstream.patch` меняет исходный клиент IV:MP. `tools/prepare.py` применяет патч к указанной ревизии и добавляет заголовки привязок Complete Edition. Раздел распространяется под GPLv3 вместе с уведомлениями исходного проекта.

## Текущий этап

Профиль привязок проверяет PE-заголовок и 15 участков машинного кода Steam `GTAIV.exe` 1.2.0.59. Для поиска GTA natives применяется x86 `stdcall`. В `CIVPhysical` линейная и угловая скорость передаются через функции Complete Edition с 16-байтовым выравниванием. Для пулов персонажей и транспорта клиент проверяет адреса, которые использует сама игра. На Complete Edition обёртки `CPools` получают указатели на эти пулы без патча рендера чекпоинтов.

На Complete Edition загрузка мультиплеера останавливается после проверки привязок. Ещё требуется перенос запуска игровой сессии и безопасного создания сетевых сущностей, затем проверка оригинального протокола в двух клиентах. Сборка ориентирована на проверенный Steam-билд.

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

`tools/game-probe.cpp` собирается в диагностический ASI-модуль. Он пишет в `%TEMP%\IVMP.CE-Probe.log` и пытается прочитать позицию локального персонажа и дескриптор его машины после штатного диспетчера скриптов. В Steam 1.2.0.59 подтверждена установка наблюдателя. Получение игрового кадра и чтение сущностей требуют отдельной проверки в запущенной сессии.

## Подготовка и сборка исходного клиента

```powershell
python ivmp-ce/tools/prepare.py work/ivmp-ce-upstream
./ivmp-ce/build.ps1 -Toolset v143
```

Для сборки решения нужны MSBuild, x86 C++ Build Tools и Windows SDK. С MSVC v143 проверены компиляция исходного клиента и сборка оригинального Windows-сервера с сетевой библиотекой. Сервер проверен на запуске штатного сценария и ответах на локальные UDP-запросы. [Установка MSVC Build Tools](https://learn.microsoft.com/en-us/cpp/overview/acquire-msvc)
