# DireWolfGui.Core: configuration, WAPR, settings and integrations API

Platform neutral (net10.0, no NuGet packages). Tests: `dotnet test windows-gui/tests/DireWolfGui.Core.Tests`
(set `DIREWOLF_EXE` to a direwolf with `--check-config`; integration tests are skipped otherwise).

## DireWolfGui.Core.Config

| Type | Purpose |
|---|---|
| `ConfigTokenizer` | `Split`/`Tokenize`/`RestOfLine` exactly like `split()` in src/config.c (quotes, doubled quotes, tabs); `Quote` for writing values. |
| `ConfigDocument` | Lossless file model: every byte kept (comments, order, unknown lines, CRLF/LF/CR per line, final newline, BOM, Latin-1 fallback). |
| `ConfigLine` | One line: `Kind` (Blank/Comment/Directive), `Keyword`, `Directive` (alias resolved), `Arguments`, `AllTokens`, `TrailingComment`, `Channel`, `AudioDevice`, `LineNumber`. |
| `ConfigDocument` edits | `SetDirective(keyword, args, context)` replaces in place or inserts in the right section; `DisableDirective`/`EnableDirective`; `InsertLine`, `ReplaceLine`, `RemoveLine`; `IsDirty`; `ToText`/`ToBytes`; `Save(path, backups)` atomic. |
| `AtomicFile` | Temp file + `File.Replace`/`Move`. |
| `DirectiveCatalog` / `DirectiveInfo` | Every keyword `config_init()` accepts (test-checked against src/config.c) plus `TCPBIND`: scope, category, summary, syntax, example, transmit/forward/experimental flags. |
| `DirectiveScope`, `DirectiveCategory` | Global / Channel / AudioDevice; grouping for the editor. |
| `ConfigFacts` | What a document sets up (devices, channels, MYCALL, modems, ports, TCPBIND, digipeat, IGate, beacons, WAPR gates) with the parser's ordering rules. |
| `ConfigValidator` | GUI-side checks with line numbers (`ConfigDiagnostic`: severity, line, code, message). |
| `ConfigChecker` | Runs `direwolf -t 0 --check-config -c file`, parses diagnostics + summary, detects builds without the option, redacts passcodes. |
| `CheckConfigResult` / `CheckConfigStatus` | Ok / Diagnostics / Unsupported / Failed, diagnostics, notes, summary, redacted raw output. |
| `CheckConfigSummary` (+ `CheckChannel`, `CheckAudioDevice`, `CheckKissPort`, `CheckIGate`, `CheckBeacon`, `CheckWaprGate`, ...) | Typed `check-config:` lines incl. `Features`, `BindLocalOnly`. |
| `StationServicesAnalyzer` / `StationServicesReport` / `StationService` | Plain-language list of what transmits or forwards, plus warnings (loops, IGate, beacon rate, WAPR duty, network exposure). |
| `ConfigBackupManager` / `ConfigBackup` | Timestamped backups, list, restore (backs up current first), prune. |
| `ProfileManager` / `ConfigProfile` | Named configs: list, save, import, export, duplicate, rename, delete; `SanitizeName`. |
| `TextDiff` / `DiffLine` | Myers line diff and unified text for the save preview. |
| `ConfigTemplates` / `NewStationOptions` / `AudioDeviceOption` / `ChannelOption` / `PttMethod` | Safe, commented starter config for the wizard. |

### Diagnostic codes (ConfigValidator)

`unknown-directive`, `inline-comment`, `overridden`, `adevice-range`, `adevice-missing`, `adevice-twice`,
`achannels`, `arate`, `channel-range`, `channel-no-device`, `channel-not-stereo`, `mycall-missing`,
`mycall-placeholder`, `mycall-invalid`, `mycall-none`, `modem-missing`, `modem-speed`, `modem-nonstandard`,
`wapr-profile`, `wapr-option`, `wapr-experimental` (info), `wapr-unsupported`, `fec-on-wapr`,
`waprgate-from-is`, `waprgate-syntax`, `waprgate-types`, `waprgate-channels`, `waprgate-no-wapr`,
`waprgate-no-igate`, `waprgate-forwards` (info), `range`, `range-unusual`, `fulldup`, `port-range`,
`port-duplicate`, `tcpbind`, `network-exposed` (warning: reachable from other computers),
`network-local` (info), `iglogin`, `iglogin-no-server`, `igserver-no-login`, `igtxvia-no-server`,
`igtxvia-channel`, `digi-channel`, `digi-mycall`, `digi-no-beacon`, `digi-wapr`, `beacon-option`,
`beacon-channel`, `beacon-igate`, `beacon-obsolete`, `satgate`.
From the real parser: `direwolf`, `direwolf-environment` (symbols-new.txt missing), `direwolf-count`
(info: Dire Wolf 1.8.2 counts every valid TXDELAY line as a diagnostic without printing one).

### Editing rules

- `SetDirective` context = channel for channel-scoped directives, device for ARATE/ACHANNELS/ADEVICE.
- Existing line: replaced in place, indentation and inline `# ...` text kept (Dire Wolf reads that as parameters;
  the validator warns). New line: after the last channel setting of that `CHANNEL` section (a `CHANNEL n` header
  is added when missing), after the device's ADEVICE/ACHANNELS lines, next to the same category for globals,
  otherwise before the first `CHANNEL` line and the comment block above it.
- Args are written verbatim; use `ConfigTokenizer.Quote` for values with spaces.
- `DisableDirective` prefixes `#[disabled by Dire Wolf Station] `; nothing is deleted silently.

## DireWolfGui.Core.Wapr

| Type | Purpose |
|---|---|
| `WaprSupport` | Profiles F600/H150/R25 (doc/wapr/USAGE.md), `IsSupported(summary)` from the `wapr` feature, `ExperimentalNotice`, `NotSupportedExplanation`, gate types, `ParseGateTypes`, `TryParseAirtime`, `EstimateDuty`, `FramesPerAirtimeWindow`, `FitsPayload` (32 bytes). |
| `WaprProfile` | Name, use, band, symbol rate, tones, tone range, seconds per 32-byte frame. |
| `WaprDutyEstimate` / `DutyLevel` | Share of time on air: Ok < 10 %, Elevated, High ≥ 25 %, Excessive ≥ 50 %. |

## DireWolfGui.Core.Settings

| Type | Purpose |
|---|---|
| `AppSettings` | GUI settings POCO (exe/config/working dir, theme, audio stats interval, CSV logs, retention, AGW host/port, messaging, map tiles, home position, window, panels, first run, external apps). No passcode field. |
| `SettingsStore` | JSON at %APPDATA%\DireWolfStation\settings.json (path overridable), atomic save, corrupt file kept as `.corrupt-<time>`, unknown properties dropped. |
| `AppTheme`, `WindowPlacement`, `SettingsLoadResult` | Supporting types. |

## DireWolfGui.Core.Integrations

| Type | Purpose |
|---|---|
| `ExternalAppProfile` / `TncProtocol` | A configured application: exe, argument list, working dir, protocol (AGWPE, KISS TCP, serial KISS), host, port. |
| `ExternalAppCatalog` / `ExternalAppTemplate` | APRSISCE/32, YAAC, Xastir, UI-View32, BPQ32, Winlink Express, Outpost, PinPoint APRS, SARTrack, Packet Commander, generic AGWPE, generic KISS TCP, with conservative setup steps. |
| `ExternalAppLauncher` / `LaunchResult` | Start a program with `ArgumentList`, open a folder; errors returned, not thrown. |
