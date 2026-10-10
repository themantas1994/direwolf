# WAPR stage 4, part A: gateway, loop prevention, channel busy detection

Stage 4 is split.  Part A (this report): the explicit gateway between WAPR, AX.25 and
APRS-IS, loop prevention, duplicate filtering and channel-busy detection.  Part B:
acknowledgements and retries for messages, airtime limits, and the channel load /
collision simulation.  User documentation: `USAGE.md`.

## 1. Changes made

| File | Change |
|---|---|
| `src/wapr_gate.c/.h` (new) | `WAPRGATE` rules: WAPR to AX.25, AX.25 to WAPR, WAPR to WAPR, WAPR to APRS-IS; type classes; loop checks; 60 s duplicate filter per destination; every refusal logged |
| `src/config.c/.h` | `WAPRGATE from to|IS [types]`, up to 16 rules, none by default |
| `src/direwolf.c` | `wapr_gate_init` at start; every received radio frame offered to `wapr_gate_rec` (returns at once without rules) |
| `src/wapr.h`, `src/wapr_codec.c`, `test/wapr/wapr_frame.py`, golden vectors | payload type 2, "APRS relayed by a gateway"; `WAPR_RELAY_MARK` (`WAPRGW`), `WAPR_BROADCAST_TOCALL` |
| `src/wapr_rx.c`, `src/wapr_tx.c` | type 2 shown as / made from the path `WAPRGW`; channel busy reported through `dcd_change` |
| `src/wapr_modem.c` | `wapr_dcd_*`: in-band energy over a tracked noise floor (biquad band pass, +6 dB on / +3 dB off, skips muted input, re-baselines after two frame lengths) |
| `src/wapr_test.c` | DCD test |
| `test/wapr/wapr_gate_test.py` (new) | end to end gateway test |
| `doc/wapr/USAGE.md`, `FRAME_FORMAT_V0.md` | gateway, busy detection, type 2 |

## 2. Loop prevention design

1. A frame put on WAPR by a gateway is **type 2** and is never gated again, by any
   gateway running this code.
2. A frame put on AX.25 by a gateway is `SOURCE>APZWAP,MYCALL*,NOGATE:info`.
   `NOGATE` keeps every IGate, including this one hearing an echo, from putting it on
   APRS-IS, so WAPR reaches APRS-IS **only** through an explicit `WAPRGATE ... IS` rule;
   digipeaters don't relay it either.  This was added after the first echo test showed
   the existing RF IGate (which has RF>IS duplicate checking off by default, issue 85)
   sending the echoed copy to APRS-IS a second time: a real policy bypass, now closed.
3. AX.25 to WAPR refuses: tocall `APZWAP`, our own call in the path or as the source,
   `TCPIP` / `TCPXX` in the path or third party format, more than 32 bytes, bad FCS.
4. Same source and information part to the same destination: at most once per 60 s.

## 3. Tests executed

| Test | Result |
|---|---|
| `wapr_gate_test.py` (real direwolf; AX.25 1200 channel 0 + WAPR H150 channel 1; 3 rules; 8 AX.25 + 3 WAPR frames; then the gateway's own transmissions fed back to the same process) | **14 / 14**: exactly the allowed frames on each side, `NOGATE` and our call on AX.25, type 2 on WAPR, W1AAA / W1CCC once to APRS-IS and the relayed W1BBB never, all 7 refusal reasons logged, echo: nothing transmitted or sent to APRS-IS again |
| DCD (`waprtest`): H150 at SNR_2500 +10 dB in noise, 48 kHz | busy after 5 ms, clear 42 ms after the end, no false busy in 5 s of noise, no flicker during the frame; at -5 dB (decodable) not detected (documented limitation) |
| `ctest` (Release, `-Werror`) | 31 / 31 |
| `wapr_live.py` | 7 / 7 |
| MinGW-w64 cross build | clean, 0 warnings |
| `interfaces.py` vs `5a58f86` (rx, tx, digi, beacon, igate, config) | **119 rows, 0 failed** |

A finding while building the test: a half-duplex gateway that relays onto a WAPR channel
cannot hear that channel while it transmits (2.7 s for H150).  In the first test run two
WAPR frames arriving during the gateway's own transmission were lost, as they would be on
a real radio.  The busy detector reduces this only for signals audible above the noise.

## 4. Unresolved risks

1. Energy busy detection does not see weak WAPR signals; hidden and weak stations will
   collide.  Part B quantifies this with a load simulation.
2. Type 2 frames from older code (stage 3) are rejected as an unknown type: acceptable
   for an experimental format, recorded in `FRAME_FORMAT_V0.md`.
3. `mheard` still records WAPR stations; IGate message routing (IS to RF) could then try
   to send a message to a WAPR-only station on an AX.25 channel.  Harmless, but not tested.
4. The duplicate window (60 s) is fixed, not configurable.

## 5. Acceptance (part A)

| Criterion | Status |
|---|---|
| Explicit gateway for supported APRS types, configurable RF / internet policy | pass |
| Malformed or unrepresentable packets refused | pass |
| Loop prevention and duplicate filtering, tested | pass (including a same-process echo) |
| Existing APRS / AX.25 behaviour preserved | pass (ctest 31 / 31, interfaces 119 / 119) |

**Part A passes**.

## 6. Next step

Part B: link-level acknowledgement and bounded retries for addressed messages (the `ack`
header flag and sequence number exist since v0), receiver duplicate suppression by
(source, sequence), a per-channel airtime limit, and a channel load / collision simulation
with and without busy detection.
