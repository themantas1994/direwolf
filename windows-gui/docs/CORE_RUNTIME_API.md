# Core runtime API (DireWolfGui.Core)

Process control, console parsing, AGW/KISS clients, APRS and the station façade.
Platform neutral (net10.0); Windows-only calls are guarded with `OperatingSystem.IsWindows()`.
No UI types, no NuGet packages. Events are raised on background threads: marshal to the UI thread
or, preferably, **poll** the sequenced buffers every ~250 ms with `GetSince(lastSeq)`.

## Station (`DireWolfGui.Core.Station`) — what the GUI uses
| Type | Purpose |
|---|---|
| `StationSession` | Owns controller, parser, `Log`, `Alerts`, `Packets`, `Stations`, `Messages`, `Agw`. `StartAsync(options)`, `StopAsync()`, `AddGuiLog()`, `Uptime`, `State`, `DireWolfVersion`, `AgwPort` (learned from output), `AgwMonitorReady`, `KissPorts`, `ChannelActivity`, `LastAudioLevels`, `LastAudioStatistics`, `SampleResources()`. Never transmits on its own. |
| `StationSessionOptions` | `Launch`, `ConnectAgwMonitor` (default true), `AgwHost`, `AgwPort` (null = learn), `CsvLogDirectory` (default `Launch.LogDirectory`), `CsvLoadExisting`, `MyCall`, `AdditionalCalls`, `SessionLogFile`. |

## Process (`DireWolfGui.Core.Process`)
| Type | Purpose |
|---|---|
| `DireWolfLaunchOptions` | Exe, config, working dir (default: exe dir), `-a`, `-l`/`-L`, extra args. `BuildArgumentList()` always starts with `-t 0 -c <conf>`; refuses `-x`. `StandardInputAudio` is TEST/DIAGNOSTIC only (adds `-`). |
| `DireWolfState` | Stopped, Starting, Running, Stopping, Failed. |
| `DireWolfProcessController` | `StartAsync`/`StopAsync`/`RestartAsync`; events `StateChanged`, `OutputLine` (raw line, not redacted), `Exited` (expected flag + `DireWolfDiagnosis`); refuses duplicate starts (same controller or same config file); `SampleResources()` (CPU %, working set); `GetRecentLines()`; test-only `WriteStandardInputAsync`/`CloseStandardInput`. |
| `StopResult` / `StopOutcome` | NotRunning, Graceful, Killed; `PttMayBeKeyed` when it had to be killed. |
| `DireWolfStartException` | Start impossible (missing exe, bad options, duplicate, OS error) — friendly message. |
| `GracefulStopper` | Windows: FreeConsole/AttachConsole/ignore Ctrl+C/GenerateConsoleCtrlEvent(CTRL_C); POSIX: SIGINT. |
| `DireWolfDiagnostics`, `DireWolfDiagnosis` | Explains an unexpected exit from the last lines (config file, audio device, port in use, stdin EOF, PTT, `Line N:` errors, crash codes). |
| `DireWolfProbe` | `ProbeAsync(exe)` runs `-t 0 -u` → `DireWolfVersionInfo` (version, dev flag, features); `ParseBanner`; `CandidateExecutablePaths`/`FindExecutable`; `FindRunningInstances()` (incl. external ones). |
| `ProcessResourceSample` | CPU percent, working set, thread count. |

## Logging (`DireWolfGui.Core.Logging`)
| Type | Purpose |
|---|---|
| `ISequenced`, `SequencedBuffer<T>` | Thread-safe ring buffer; `Add` assigns sequence 1,2,3…; `GetSince(afterSeq, max)`, `GetLatest`, `Snapshot`, `Clear`, `LastSequence`, `FirstSequence`, `Added` event. |
| `LogEntry`, `LogSeverity` | Sequence, time, severity (Debug/Info/Packet/Transmit/Warning/Error), text, category. |
| `LogClassifier` | Severity + category ("agw", "kiss", "igate", "wapr", "audio", "config", …) for a console line. |
| `SecretRedactor` | Masks `IGLOGIN call <passcode>`, `pass <n>`, `passcode=`/`password=` values. Session redacts every line before storing. |
| `RotatingLogFileWriter` | Size-capped log file with numbered rotation (redacts again). |

## Packets (`DireWolfGui.Core.Packets`)
| Type | Purpose |
|---|---|
| `PacketRecord` | Time, channel, label, `Direction`, `Origin` (Radio/Local/FromAprsIs/ToAprsIs/IgateToRadio/Gateway/NetworkTnc/Dtmf/Ais/Unknown), source/destination/path, info (escapes undone), frame description, heard station, audio level (+text), FEC, WAPR flag/details, `DecodedLines` (Dire Wolf's decode, grows), `Aprs`, `RawFrame` (from AGW), `RawText`, `SourceKind`. |
| `ConsoleOutputParser` | Stateful `ProcessLine(line, time)` → `ConsoleLineKind`; events `PacketParsed`, `AudioLevel`, `AudioStatistics`, `Notice` (`ConsoleNotice`: Info, Error, Warning, WaprLink, WaprGate, IGate, ServerListening(port, protocol), ClientConnected/Disconnected, AudioDevice, Startup, Shutdown (QRT), AudioInputEnded, ConfigProblem); `Version`; `Unescape()`. Not thread-safe: serialise calls. |
| `Ax25Frame`, `Ax25Address`, `Ax25FrameType` | Decode/encode AX.25 (no FCS), Dire Wolf style control description (`I cmd, n(s)=…`, `SABM cmd, p=1`, …), `ToMonitorString()`, `CreateUi()`. Modulo 8 only. |
| `PacketStore`, `ChannelActivity` | Bounded packet buffer + per-channel rx/tx counters and last audio level. |

## Net (`DireWolfGui.Core.Net`)
| Type | Purpose |
|---|---|
| `AgwFrame`, `AgwPortInfo` | 36-byte header codec (oversized lengths rejected), 'G' reply parsing. |
| `AgwClient` | Async TCP AGW client: `FrameReceived`, `Disconnected`; `RequestVersionAsync` (R), `RequestPortInfoAsync` (G), `RequestPortCapabilitiesAsync` (g), `RegisterCallsignAsync` (X), `RequestOutstandingFramesAsync` (y/Y), `EnableMonitoringAsync` (m, once), `EnableRawFramesAsync` (k, once), `ConnectStation[Via/WithPid]Async` (C/v/c), `SendConnectedDataAsync` (D), `DisconnectStationAsync` (d), `SendUnproto[Via]Async` (M/V), `SendRawFrameAsync` (K), `RequestHeardAsync` (H). |
| `AgwTerminalSession`, `TerminalState` | Connected-mode terminal: register, connect (via), `SendLineAsync` (adds CR), `DataReceived`, `Notification` ("*** CONNECTED With Station …"). |
| `KissCodec`, `KissFrame`, `KissTcpClient` | KISS framing with partial-read decoder; KISS TCP client. |
| `IPacketSender`, `AgwPacketSender` | Transmit a UI frame ('V' with path, 'M' without); only on explicit calls. |
| `ConnectionTester`, `ConnectionTestResult` | TCP / AGW (version + ports) / KISS tests with friendly refused/timeout/host-not-found messages; `Describe(ex)`. |

## Aprs (`DireWolfGui.Core.Aprs`)
| Type | Purpose |
|---|---|
| `AprsParser`, `AprsInfo`, `AprsPacketType` | Position (plain/compressed, ambiguity), with timestamp, Mic-E (fully decoded or `Error`, never guessed), object/item, message/ack/rej/reply-ack, bulletin, status, weather, telemetry, query, third party. |
| `DireWolfCsvLog`, `DireWolfLogRecord` | Parse Dire Wolf's CSV log (quoting per src/log.c; speed kn, altitude m; chan 999 = own beacon). |
| `DireWolfCsvLogTailer` | Follows daily `YYYY-MM-DD.log` (UTC) across midnight, or one `-L` file; truncation, partial lines, start-at-end or load-existing; `Poll()` or `Start()` timer. |
| `StationTracker`, `TrackedStation`, `TrackPoint`, `PositionSources` | Bounded stations + track points from real data only, with position source ("packet"/"Dire Wolf log") and time; `Version` for cheap polling. |
| `GeoMath` | Distance (km), bearing, compass point. |
| `AprsMessageService`, `AprsMessage`, `AprsMessageState` | Compose `:ADDRESSEE:text{n}`, retries (default 3 tries, 30 s then doubling), ack/rej/reply-ack, inbound to my call(s) (exact SSID, `-0` = none), duplicate suppression, `AutoAcknowledge` (default **false**), `Cancel`, `TimeProvider` based. |
| `Exporters` | Packets CSV, stations CSV, GPX tracks (only real track points), CSV-injection safe. |

## Known behaviours worth knowing
- Dire Wolf opens KISS TCP 8001 by default; `KISSPORT 0` removes it.
- Dire Wolf's AGW reader only starts processing a new client's commands up to ~1 s after accept; the
  session does an 'R' round trip before 'm'/'k' and sets `AgwMonitorReady`.
- Integration tests: real direwolf via `DIREWOLF_EXE` or `<repo>/build/src/direwolf`, audio from
  `gen_packets` fed on stdin, one non-parallel xUnit collection; skipped with a reason if not built.
