# IV:MP для GTA IV Complete Edition

Порт основан на [IV:MP @ e9fadfc](https://github.com/Neproify/ivmultiplayer/tree/e9fadfc74a87f0d1d232f507b2e0bbdf22a1ab52). `upstream.patch` меняет исходный клиент IV:MP. `tools/prepare.py` применяет патч к указанной ревизии и добавляет заголовки привязок Complete Edition. Раздел распространяется под GPLv3 вместе с уведомлениями исходного проекта.

## Текущий этап

Профиль привязок проверяет PE-заголовок и 12 участков машинного кода Steam `GTAIV.exe` 1.2.0.59. Для поиска GTA natives применяется x86 `stdcall`. В `CIVPhysical` линейная и угловая скорость передаются через проверенные функции Complete Edition с 16-байтовым выравниванием. Отдельно проверены места вызова диспетчера скриптов и чтения хода подвески.

В Complete Edition клиент инициализирует проверенные привязки и останавливает загрузку мультиплеера до старых хуков. До игровой сборки нужны перенос запуска игровой сессии, пулов, сетевых игроков и транспорта, затем проверка оригинального протокола в двух клиентах. Привязки привязаны к проверенному Steam-билду. Другой исполняемый файл с тем же номером версии также должен пройти проверку.

## Проверка без запуска GTA IV

Используйте Zig 0.15.2 для сборки маленькой Win32-утилиты и теста соглашений вызова.

```powershell
zig c++ -target x86-windows-gnu -std=c++14 -municode ivmp-ce/tools/inspect.cpp -o ivmp-ce-inspect.exe
./ivmp-ce-inspect.exe 'F:/SteamLibrary/steamapps/common/Grand Theft Auto IV/GTAIV/GTAIV.exe'
zig c++ -target x86-windows-gnu -std=c++14 ivmp-ce/tests/abi.cpp -o ivmp-ce-abi.exe
./ivmp-ce-abi.exe
```

Утилита отображает файл в режиме чтения и сверяет сигнатуры функций. Она приняла локальный Steam `GTAIV.exe` 1.2.0.59 и отклонила тестовую копию с изменённым байтом в функции поиска natives. Тест ABI использует заглушки вместо игрового процесса. `COffsets.cpp`, `CIVPhysical.cpp`, `Main.cpp` и `CGame.cpp` после применения патча отдельно скомпилированы в Win32-объекты.

## Подготовка и сборка исходного клиента

```powershell
python ivmp-ce/tools/prepare.py work/ivmp-ce-upstream
./ivmp-ce/build.ps1 -Toolset v141
```

Для сборки решения нужны MSBuild, x86 C++ Build Tools и Windows SDK. Полная сборка проверяется отдельно от утилиты привязок. [Установка MSVC Build Tools](https://learn.microsoft.com/en-us/cpp/overview/acquire-msvc)
