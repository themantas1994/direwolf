# Implementation checklist (kept short; resume from here)

Environment facts (Linux dev container):
- .NET SDK 10 at ~/.dotnet (`export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1`).
  WPF project builds on Linux via EnableWindowsTargeting; it can only *run* on Windows.
- Dire Wolf Linux build: `mkdir build && cd build && cmake .. -DUNITTEST=1 && make -j8`; `ctest`.
- Hardware-free real Dire Wolf: config `ADEVICE stdin null`, pipe raw S16LE audio
  (WAV minus 44 byte header, from `gen_packets`) to `direwolf -t 0 -c x.conf -`.
  A default KISS TCP port 8001 opens in addition to any KISSPORT line.

Backend (C) changes, all opt-in:
- [x] `--check-config` (real parser summary, exit 1 on diagnostics) + ctest `check-config`
- [x] Windows: stdout unbuffered when redirected (live log in GUI)
- [x] `TCPBIND LOCAL|ANY` (opt-in loopback-only servers)
- [x] --check-config counts printed messages only (valid TXDELAY no longer counted)

Core (src/DireWolfGui.Core):
- [x] Process: launch/stop (Ctrl+C via AttachConsole on Windows, SIGINT elsewhere), probe, resources
- [x] Console parser: packets rx/tx/igate, audio level, audio stats, notices, WAPR
- [x] AGW client + terminal session, KISS TCP client, connection tests
- [x] APRS parser, Dire Wolf CSV log tailer, station tracker, messages (ack/retry), exports
- [x] Config document (lossless), tokenizer, catalog (checked against src/config.c), validator
- [x] check-config runner/parser, services/transmit analysis, backups, profiles, diff, templates
- [x] WAPR support, settings store, external app profiles

GUI (src/DireWolfGui):
- [x] shell, themes, navigation, MainViewModel (IShell), smoke-test mode
- [x] Dashboard, Log, Packet monitor, APRS map & stations, Messages, Terminal
- [x] Configuration, Setup wizard (receive-only test), Gateway & digipeater, WAPR,
      External programs, Settings/About

Release:
- [x] Windows CI: MinGW direwolf + ctest, Core tests vs direwolf.exe, package ZIP, smoke test
- [x] scripts/package.ps1, README, docs/USER_GUIDE.md (incl. known limitations)
- [ ] Interactive Windows validation with real audio/radio (operator)

Core API docs: docs/CORE_RUNTIME_API.md, docs/CORE_CONFIG_API.md.  Inside DireWolfGui.Core.* use
`System.Diagnostics.Process` fully qualified (the Process folder namespace shadows it).
