# WAPR stage 3 report: integration behind explicit configuration

Task: make the stage 2 modem usable in Dire Wolf only when a channel is configured with
`MODEM WAPR <profile>`, with decoding outside the audio thread, offline support in
`gen_packets` / `atest`, and no change for any configuration without it.
User documentation: `USAGE.md`.

## 1. Changes made

| File | Change |
|---|---|
| `src/audio.h` | `MODEM_WAPR` appended to `enum modem_t` (existing values unchanged); `wapr_profile` field |
| `src/dlq.h` | `fec_type_wapr = 3` appended |
| `src/config.c` | `MODEM WAPR F600 / H150 / R25`; unknown or missing profile is an error; extra options warned and ignored |
| `src/demod.c` | WAPR channel: level meter only, samples (after the transmit mute) go to `wapr_rx_sample`; single level shown |
| `src/multi_modem.c` | calls `wapr_rx_init` (no-op without WAPR channels) |
| `src/hdlc_send.c` | WAPR channel: `layer2_send_frame` sends a WAPR frame or nothing (never AX.25); TXDELAY / TXTAIL become silence |
| `src/gen_tone.c/.h` | `gen_tone_amplitude()` accessor (transmit level) |
| `src/direwolf.c` | display "WAPR"; WAPR frames are **not** passed to IGate, digipeaters, REGEN or APRStt |
| `src/atest.c`, `src/gen_packets.c` | `-W profile` option; atest decodes WAPR synchronously and flushes at end of file |
| `src/wapr_rx.c/.h` (new) | lock-free ring buffer filled by the audio thread; decoder thread per channel; overlapping windows; duplicate suppression; frame to packet |
| `src/wapr_tx.c/.h` (new) | packet to frame with explicit refusal reasons; modulation through `gen_tone_put_sample` |
| `src/wapr_modem.c` | variable `near` renamed (a macro in the Windows headers: the stage 2 commit did not build with MinGW) |
| `src/l2send_test.c` | stubs for the two WAPR transmit hooks |
| `src/CMakeLists.txt`, `test/CMakeLists.txt` | WAPR sources in `direwolf`, `atest`, `gen_packets` (and optional test programs); `textcolor.c` in `waprtest` (needed where the C library has no `strlcpy`); ctests `check-wapr-h150`, `check-wapr-f600` |
| `test/scripts/check-wapr-*`, `test/wapr/golden/wapr_check_msgs.txt` | gen_packets -> atest end to end, 8 packets that fit and 2 that must be refused |
| `test/wapr/wapr_live.py` | real `direwolf` end to end test |

Mapping (both directions in `USAGE.md`): transmit accepts only UI / PID F0, no
digipeater path, at most 32 bytes, not MIC-E; receive delivers `SOURCE>APZWAP:payload`.

## 2. Tests executed

| Test | Result |
|---|---|
| Release build, gcc 13, `-DCMAKE_C_FLAGS=-Werror` | 0 warnings |
| `ctest` | **31 / 31** (28 existing + `waprtest`, `check-wapr-h150`, `check-wapr-f600`) |
| Full MinGW-w64 cross build (all programs, Windows target) | builds, 0 warnings.  The stage 2 commit did **not** (fixed here) |
| `waprtest` under AddressSanitizer + UBSan | pass |
| `test/compat/interop.py`: this build vs the stage 2 commit (`5a58f86`, no Dire Wolf changes) for every legacy modem, rate, FX.25, IL2P: identical transmit audio, cross decoding, independent modulators, noise | **297 checks, 0 failed** |
| `test/compat/interfaces.py` rx, tx, digi, beacon, igate, config: this build vs `5a58f86`, real `direwolf` processes (run as an ordinary user) | **119 rows, 0 failed** (KISS / AGW / pty receive and transmit, transmit audio identical, digipeater, beacons, IGate both directions, config) |
| `wapr_live.py` (H150, 48 kHz, UDP audio at 2x real time) | 7 / 7: KISS client gets all 8 frames once; nothing sent to a fake APRS-IS IGate server; packet with a path refused with a reason; decoder kept up; transmit audio decodes to exactly the accepted packet |
| atest -W in AWGN (gen_packets audio + noise, 48 frames per point) | H150 48 kHz: 46 / 48 at -6.0 dB, 48 / 48 at -5.5 dB; F600 44.1 kHz: 47 / 48 at 0 dB, 48 / 48 at 0.5 dB, in line with stage 2 |
| CPU (atest -W, 120 s of noise, 48 kHz, worst case) | F600 21 %, H150 6.6 %, R25 3.6 % of one core |

## 3. Design notes

* The audio thread writes 16 bit samples into a power-of-two ring and publishes the
  count after a full memory barrier.  It takes no lock and never waits.  The decoder
  thread polls every 50 ms, copies a window, re-checks the count to detect overwrite,
  and decodes.  Windows are two frame lengths long, every half frame, so each frame is
  whole in at least two windows; a frame decoded twice (same bits, start within half a
  frame) is delivered once.
* If the decoder falls behind by more than the ring holds, windows are skipped and
  counted; this was not seen in any run.
* Transmit timing in `xmit.c` is untouched: a WAPR channel's `baud` is its symbol
  rate, and the transmit hooks return symbol counts.

## 4. Unresolved risks

1. **CPU**: F600 at 21 % of a desktop core is too much for small boards; the coarse
   search does a full correlation per hop and could use a sliding DFT.  Measure on a
   Raspberry Pi before recommending F600.
2. **No channel-busy detection** on WAPR channels (no DCD).  Stage 4 item.
3. **mheard** records WAPR stations; IGate message routing consults it.  Not changed
   here; to be covered by the stage 4 policy.
4. A digipeater or beacon configured to send to a WAPR channel with a path gets a
   refusal message on every attempt (safe, noisy).
5. Not tested on real radios, Windows (built, not run) or macOS.
6. Thread safety relies on single-writer ring semantics and `__sync_synchronize`
   (gcc / clang / MinGW); not checked with ThreadSanitizer.

## 5. Acceptance

| Criterion | Status |
|---|---|
| Only with explicit `MODEM WAPR`; no change otherwise | pass (interop 297 / 297, interfaces 119 / 119, ctest) |
| WAPR frames never sent through a legacy channel, never AX.25 on a WAPR channel | pass (by construction; refused packets logged) |
| No decoding in the real-time audio path | pass (ring buffer + decoder thread; atest synchronous by design) |
| Targeted tests and legacy regression tests | pass |
| Unrepresentable packets rejected | pass |

**Stage 3 passes.**

## 6. Smallest next step

Stage 4: explicit gateway policy (which WAPR payload types may go to APRS-IS or to an
AX.25 RF channel, and back), loop prevention and duplicate filtering across the two
networks, sequence numbers and acknowledgements for messages, and channel-busy detection.
