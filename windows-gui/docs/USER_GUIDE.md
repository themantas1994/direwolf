# Dire Wolf Station — user guide

Dire Wolf Station is a Windows window around [Dire Wolf](https://github.com/wb2osz/direwolf),
the software packet modem and TNC.  Dire Wolf does the radio work (modems, AX.25, APRS,
digipeater, IGate, WAPR); this application starts and stops it, edits its configuration,
shows what it receives and transmits, and gives you APRS, messaging and a packet terminal
on top of Dire Wolf's own AGWPE interface.

> **You are the licensed operator.**  Nothing in this application transmits just because
> it started.  Features that transmit or forward traffic (beacons, digipeating, IGate,
> WAPR gateways, APRS messages and their acknowledgements, the packet terminal) only work
> after you turn them on, and you are responsible for frequencies, power, duty cycle and
> content complying with the rules that apply to you.

## 1. Install and first start

1. Unzip `DireWolfStation-<version>-win-x64.zip` anywhere (e.g. `C:\DireWolfStation`) and run
   `DireWolfStation.exe`.  Nothing is installed; no administrator rights are needed.
2. You need `direwolf.exe`.  The ZIP built with Dire Wolf includes it in the `direwolf`
   folder; otherwise use an existing installation (e.g. `C:\direwolf`) or build it (see the
   main README).  This fork's build adds configuration checking (`--check-config`),
   loopback-only ports (`TCPBIND LOCAL`) and live logs; older builds work with fewer
   checks.
3. The **Setup wizard** opens on first start.  It walks through:
   * finding `direwolf.exe` (its version is shown),
   * using an existing configuration file or creating a new one,
   * choosing the audio input and output (the same device list Dire Wolf for Windows
     sees), with a live input level meter,
   * channels, modem speed per channel (and WAPR, experimental, only if you tick it),
   * push-to-talk: none/VOX, a serial port's RTS or DTR line, or a CM108-type USB sound
     adapter's GPIO,
   * your callsign, and the network ports for other programs (by default only programs on
     this computer can connect: `TCPBIND LOCAL`),
   * a check of the new configuration with Dire Wolf's own parser, then saving it,
   * a **receive-only test**: Dire Wolf runs with your audio settings but without beacons,
     digipeating, IGate or client ports, so nothing can be transmitted.  Watch for received
     packets and audio levels, then stop the test.

Settings are kept in `%APPDATA%\DireWolfStation`; logs, map tiles and crash reports in
`%LOCALAPPDATA%\DireWolfStation`.

## 2. Everyday operation

The header shows Dire Wolf's state, uptime and buttons:

| Action | Button | Key |
|---|---|---|
| Start Dire Wolf | Start | F5 |
| Stop it cleanly | Stop | Shift+F5 |
| Restart (e.g. after changing the configuration) | Restart | Ctrl+Shift+F5 |
| Go to a page | navigation on the left | Ctrl+1 … Ctrl+9 |

* Before starting, the configuration is checked with Dire Wolf's own parser; problems are
  shown and you can still start.  A warning appears if another Dire Wolf is already running.
* **Stop** sends Dire Wolf the same Ctrl+C a console window would, so it releases the
  transmitter (PTT) and closes cleanly.  Only if it does not stop within a few seconds is it
  terminated, and you are told to check that the radio is not left transmitting.
* Closing the window stops Dire Wolf cleanly first.
* Pills on the right of the header: **Restart needed** (the configuration changed while
  Dire Wolf was running), **Transmits automatically / No automatic TX** (whether the
  configuration has beacons, digipeating, IGate transmit or WAPR gateways; hover for the
  list), and the packet **Monitor** source.

### Dashboard (Ctrl+1)
Process state and version, the result of the configuration check, what the station does on
its own (and why), client ports and whether other computers can reach them, audio devices
and levels, every radio channel with its modem, callsign, PTT, packet counts, last heard
time and audio level, recent packets and alerts.  Data is labelled when it is not live
(e.g. "not running", "nothing heard yet", age of the last reading).

### Packet monitor (Ctrl+2)
Every frame Dire Wolf receives or sends, as it happens.  Each row says where it came from:
received on radio, transmitted by this station, from/to APRS-IS, IGate to radio, relayed by
a WAPR gateway, network TNC, DTMF, AIS.  Search and filter by channel, origin, APRS type,
callsign or WAPR; pause (new packets are counted while paused); auto-scroll.  The detail
panel shows the decoded fields, Dire Wolf's own decoding, the APRS interpretation, the
console line and, when this application is connected to Dire Wolf's AGW port, the raw AX.25
bytes with a decode.  Copy rows or callsigns; export to CSV or text.  The number of packets
kept is set in Settings.

### APRS map & stations (Ctrl+3)
Stations and objects heard, with last heard time, distance and bearing from your home
position, symbol, channel and origin, comment and status.  Positions come only from real
reports: decoded by Dire Wolf itself (its CSV log, enabled in Settings) or parsed from the
packet; their source and time are shown, and stations not heard for an hour are drawn as
stale.  Tracks, labels and fit-to-stations on the map; export stations to CSV and tracks to
GPX.  The map works offline as a latitude/longitude grid; OpenStreetMap tiles can be
turned on in Settings (they are cached and attributed).

### APRS messages (Ctrl+4)
Conversations by callsign.  Messages you send get a message number and are retransmitted
until the other station acknowledges them (or you cancel, or retries run out); the state is
shown as Sent (with tries), Acknowledged, Rejected or Gave up — only a real received ack
marks a message acknowledged.  Messages to your callsign are listed, and acknowledged
automatically if you allow it.  **Sending, retries and automatic acks transmit**, so
messaging must first be enabled in Settings, and the first send asks for confirmation.
Retries are done by this application; Dire Wolf itself does not retry APRS messages (on a
WAPR channel the WAPR link layer additionally acknowledges and repeats on its own).

### Packet terminal (Ctrl+5)
Connected-mode AX.25 (keyboard-to-keyboard, BBS and node sessions) through Dire Wolf's
AGWPE interface.  Press **Connect to TNC**, enter your callsign, the station to call and
optional digipeaters, choose the radio port, and **Connect**.  Typed lines are sent with
Enter; Up/Down recall earlier lines; the transcript can be saved.  The panel lists the
ports and capabilities Dire Wolf reports.  KISS alone does not provide connected-mode
control here; Dire Wolf's AGWPORT must be enabled.

### Log (Ctrl+6)
Everything Dire Wolf prints, live, with severity filters, search, pause, follow, copy and
export.  APRS-IS passcodes are masked.  A copy is written to rotating files (Open log
folder).

## 3. Configuration

The **Configuration** page edits Dire Wolf's own configuration file:

* **Guided** forms for station identity, audio devices, channels (modem, PTT, timing such as
  TXDELAY, FX.25/IL2P), network ports, logging and GPS, with the explanation of each setting.
* **All directives**: every line of the file with its meaning and any problem found; lines
  can be disabled and enabled.
* **Raw text** for anything else.
* **Diagnostics**: the GUI's checks plus **Check with Dire Wolf**, which runs Dire Wolf's
  real parser on the file without opening audio, PTT or the network.
* **Backups** and **Profiles** (named configurations you can switch between, import,
  export and duplicate).

Saving never rewrites the whole file from a template: only the lines you changed are
touched; comments, order and settings the GUI does not know are kept.  Every save shows the
differences first, makes a timestamped backup and writes the file atomically.  If Dire Wolf
is running, the header then shows **Restart needed**.

### Gateway & digipeater
What the configuration makes the station transmit or forward, in plain language, with
warnings (e.g. RF-to-Internet IGating, Internet-to-RF transmission, short beacon intervals,
possible loops).  Editors for digipeater rules (with presets explained), IGate server,
login, filters and transmit limits, beacons and filters.  Turning any of these on asks for
confirmation.  Recent forwarding decisions that Dire Wolf logs (IGate and WAPR gate lines)
are listed as they happen.  The APRS-IS passcode is written only to the configuration
file; it is never stored in this application's settings or shown in logs.

### WAPR (experimental)
WAPR is an experimental weak-signal mode in this Dire Wolf fork (see
`doc/wapr/USAGE.md`).  It is **not compatible with AX.25 / APRS radios**; a WAPR channel only
talks to other WAPR stations.  Its performance figures come from simulations; real-radio
and on-air behaviour has not been verified, and this application does not claim otherwise.
The page shows whether your Dire Wolf build supports WAPR, the profiles (F600, H150, R25)
with their airtime per 32-byte frame and a duty-cycle calculator, per-channel setup (never
applied to an existing channel without your confirmation), the optional airtime limit,
`WAPRGATE` rules with validation, and WAPR events from the log (acknowledgements, resends,
airtime limit, decoder overload).

## 4. Other programs (External programs page)

Dire Wolf can serve other packet and APRS programs through its AGWPE (default port 8000) and
KISS TCP (default port 8001) interfaces.  The page keeps profiles for programs such as
APRSISCE/32, YAAC, Xastir, UI-View32, BPQ32, Winlink Express, Outpost, PinPoint APRS,
SARTrack and Packet Commander: their location, arguments and which interface they use.  You
can launch them, open their folder, and **test the connection** to Dire Wolf.  The ports
panel shows what to type into the other program.  These programs provide their own
features (BBS, node, Winlink e-mail…); Dire Wolf provides the modem and TNC underneath.

Programs connected to AGWPE or KISS can transmit through your radio.  With `TCPBIND LOCAL`
only programs on this computer can connect; without it any computer on your network can.

## 5. Settings

Theme (Windows, light or dark), locations of `direwolf.exe` and the configuration, Dire
Wolf's working folder, the audio statistics interval (Dire Wolf's `-a` option: periodic
sample rate and level reports for the dashboard), Dire Wolf's CSV packet log (lets the map
use Dire Wolf's own position decoding), how many log lines and packets to keep, the AGW
address used by the monitor, messaging and automatic acknowledgements, map tiles (off by
default; URL, attribution and cache), and your home position for distances.

## 6. Troubleshooting

| Symptom | What to check |
|---|---|
| "Could not open audio device" | Another program may hold the device exclusively; Windows privacy settings may block microphone access (Settings › Privacy › Microphone › allow desktop apps); the device name in ADEVICE must match part of the name in the wizard's list. |
| No packets, audio level 0 | Wrong input device or muted input; check the level meter in the wizard. |
| Audio level above 110 or "too high" | Lower the recording level (Windows sound settings › Recording › device › Levels) or the radio's output. Aim for about 50. |
| Decodes poor with a good level | Turn off Windows audio enhancements and AGC for the device; use 44100 or 48000 Hz; disable "Allow applications to take exclusive control" only if another program conflicts. |
| Transmits but nobody hears | PTT: check the COM port and RTS/DTR choice; VOX needs enough output level; TXDELAY may need to be longer for your radio. |
| Start fails at once | Read the message under Dire Wolf on the Dashboard and the Log: it names the configuration line, device or port at fault. A port "in use" usually means another Dire Wolf is running. |
| Other program cannot connect | External programs › Test connection; check AGWPORT/KISSPORT; with TCPBIND LOCAL other computers cannot connect (by design). |
| Lines in the log arrive late | You are using a Dire Wolf build without this fork's live-log change; use this fork's build. |

Crash reports (if any) are saved in `%LOCALAPPDATA%\DireWolfStation\crash` and contain no
configuration contents or credentials.

## 7. Known limitations

* This application has been built and tested automatically (unit and integration tests
  against a real Dire Wolf on Linux; build, tests and a start-up check of every page on
  Windows).  Use with real sound cards, radios, PTT hardware and over the air on Windows is
  being validated by the operator; please report problems.
* Dire Wolf's AGW and KISS servers listen on all network interfaces unless the
  configuration has `TCPBIND LOCAL` (this fork only).
* Packet monitoring comes from Dire Wolf's console output; with Dire Wolf builds older than
  this fork the output can arrive in bursts on Windows.  Raw frame bytes need the AGW port.
* AX.25 decoding in the monitor handles modulo-8 frames only (modulo-128 frames are shown as
  Dire Wolf prints them).
* WAPR: experimental, simulation-validated only, one 32-byte size class, no automatic
  profile choice (see `doc/wapr/USAGE.md`).
* Map tiles need an Internet connection and are subject to the tile server's usage policy.
