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

Core (src/DireWolfGui.Core):
- [ ] Process: launch/stop (Ctrl+C via AttachConsole on Windows, SIGINT elsewhere), probe, resources
- [ ] Console parser: packets rx/tx/igate, audio level, audio stats, notices, WAPR
- [ ] AGW client + terminal session, KISS TCP client, connection tests
- [ ] APRS parser, Dire Wolf CSV log tailer, station tracker, messages (ack/retry), exports
- [ ] Config document (lossless), tokenizer, catalog (checked against src/config.c), validator
- [ ] check-config runner/parser, services/transmit analysis, backups, profiles, diff, templates
- [ ] WAPR support, settings store, external app profiles

GUI (src/DireWolfGui): shell/themes/navigation, dashboard, logs, monitor, APRS+map,
messages, terminal, config editor, wizard, gateway/digi, WAPR, integrations, about.

Release: Windows CI workflow, publish/ZIP script, USER_GUIDE, known limitations.
