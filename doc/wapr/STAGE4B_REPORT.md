# WAPR stage 4, part B: link behaviour, collisions and channel load

Part A (`STAGE4A_REPORT.md`) added the gateway, loop prevention and busy detection.
Part B measures how the real receiver behaves when frames collide, simulates a shared
channel under increasing load, and adds acknowledgement / retransmission for addressed
messages, duplicate suppression and an airtime limit.  User documentation: `USAGE.md`.

All results here are measurements of the C receiver on simulated audio, or simulations
built on those measurements.  Nothing has been on the air.

## 1. Changes made

| File | Change |
|---|---|
| `src/wapr_link.c/.h` (new) | Link state machine (pure, time passed in): sequence numbers, ack request for addressed messages, retransmission after 3 frame times + 3 s + random 0-1 frame, at most 3 transmissions, receiver duplicate suppression by (source, seq, content) with re-acknowledgement, token bucket airtime limit (acks exempt); per channel registry, mutex, monotonic clock, sender hook |
| `src/wapr_tx.c` | `:ADDRESSEE:` messages (not bulletins / announcements / acks / rejects) get the addressee as WAPR destination and an ack request; the internal `WAPRAK` packet becomes a type 3 frame; link and airtime limit applied before modulation |
| `src/wapr_rx.c` | received frames go through the link (acks consumed, copies dropped and re-acknowledged, acks queued at high priority); due retransmissions polled in the decoder thread; log lines for each link event; delivered packets always use the `APZWAP` tocall (the WAPR destination is link level) |
| `src/wapr.h`, `wapr_codec.c`, `wapr_frame.py`, golden vectors, `FRAME_FORMAT_V0.md` | payload type 3, link acknowledgement |
| `src/config.c`, `src/audio.h` | `MODEM WAPR profile AIRTIME=percent` |
| `src/direwolf.c` | sender hook = `tq_append` |
| `src/wapr_test.c` | collision mode (`-c`), link unit test |
| `test/wapr/wapr_link_test.py`, `wapr_load.py` (new) | two-station live test; load simulation |
| CMake | `wapr_link.c` in every target with WAPR code; `ax25_pad.c`, `fcs_calc.c` in `waprtest` |

## 2. Collisions with the real receiver (`waprtest -c`, 100 trials per cell)

Wanted frame at SNR_2500 +10 dB, one interfering frame at the given SIR, overlapping by
the given fraction of a frame, random offsets within +-25 Hz.  Wanted frames delivered
(of 100), H150 (F600 is similar, `results/stage4b/capture_*.txt`):

| Overlap \ SIR (dB) | -6 | -3 | 0 | 1 | 2 | 3 | 4 | 6 | 10 |
|---|---|---|---|---|---|---|---|---|---|
| 0.10 | 1 | 28 | 98 | 99 | 99 | 100 | 100 | 100 | 100 |
| 0.25 | 0 | 0 | 87 | 97 | 100 | 100 | 100 | 100 | 100 |
| 0.50 | 0 | 0 | 66 | 96 | 99 | 99 | 99 | 100 | 100 |
| 0.75 | 0 | 0 | 20 | 74 | 95 | 100 | 100 | 100 | 100 |
| 1.00 | 0 | 0 | 14 | 100 | 100 | 100 | 100 | 100 | 100 |

Capture is strong: 2 dB stronger than the interferer is enough at any overlap, and at
0 dB both frames of a pair are often decoded.  **No wrong frame in 9 000 collision
trials** (both profiles).

## 3. Channel load simulation (`wapr_load.py`)

20 stations, Poisson traffic, station to gateway SNR_2500 uniform in -3 ... 27 dB,
reception by the measured capture table, 5 station layouts per point (the same layouts
for every load, access method and hidden fraction), seed 1.  Validation first: without
capture and sensing it reproduces pure ALOHA, S = G e^-2G (peak 0.183 at G 0.5, theory
0.184; `results/stage4b/load_validate_aloha.*`).

Throughput S (frames decoded per frame time) and delivery, H150:

| G offered | 0.1 | 0.3 | 0.5 | 0.7 | 1.0 | 1.5 |
|---|---|---|---|---|---|---|
| ALOHA: S / delivery | 0.09 / 0.91 | 0.23 / 0.76 | 0.32 / 0.65 | 0.38 / 0.55 | 0.43 / 0.43 | 0.45 / 0.30 |
| CSMA, all hear all: S / delivery | 0.10 / 0.99 | 0.29 / 0.97 | 0.47 / 0.94 | 0.63 / 0.90 | 0.80 / 0.81 | 0.85 / 0.59 |
| CSMA, 30 % pairs hidden: S / delivery | 0.10 / 0.95 | 0.26 / 0.85 | 0.38 / 0.77 | 0.48 / 0.68 | 0.57 / 0.57 | 0.62 / 0.41 |
| CSMA, all hear all: 95th percentile latency (frame times) | 1.8 | 2.4 | 3.4 | 5.0 | 10.8 | 350 |

F600 is similar (`load_F600.*`); its delivery is capped at about 0.90 even at low load
because the stations below its threshold (-0.2 dB) never get through.

Findings:

* Capture more than doubles the classic ALOHA peak (0.44 vs 0.18).
* Busy detection is worth having: at G 0.5 delivery 0.94 vs 0.65 (no hidden stations),
  0.77 vs 0.64 with 30 % hidden pairs.
* Saturation is bad for latency: above G about 1, queues grow and the 95th percentile
  latency explodes (hundreds of frame times).  For H150 G = 1 is only one 2.7 s frame
  per 2.7 s from all stations together: an airtime limit and conservative beacon rates
  matter more than the access method.
* Not modelled: retransmissions in the load (they add load), fading, frequency
  offsets between colliding frames beyond +-25 Hz, more than one gateway.

## 4. Acknowledgement and retransmission

| Test | Result |
|---|---|
| Unit test, 400 messages, 30 % loss each way (`waprtest`) | delivered 392 (expected 97.3 %), acknowledged 351 (expected 86.7 %), 0 delivered twice, at most 3 transmissions, 95 copies suppressed |
| Broadcast | never retransmitted |
| Airtime limit 10 %, 300 frames offered in 300 s | 33 sent (expected about 34), acknowledgements still allowed |
| `wapr_link_test.py`: two real direwolf, F600, chosen transmissions lost | **6 / 6**: B delivers each frame once, in order; A resends after the lost ack and after the lost frame (2 resends); both acknowledged; B suppresses the copy and acknowledges again; no give-up; exactly 5 + 3 transmissions on air |

## 5. Regression

| Test | Result |
|---|---|
| `ctest` (Release, `-Werror`) | 31 / 31 |
| `waprtest` under ASan + UBSan | pass |
| `wapr_live.py`, `wapr_gate_test.py` | 0 failures each |
| `interfaces.py` vs `5a58f86` | **119 rows, 0 failed** |
| MinGW-w64 cross build | clean, 0 warnings (after replacing `GetTickCount64`, not available for Dire Wolf's Windows target) |

## 6. Unresolved risks

1. Retransmissions are timer based; on a saturated channel they add load (not in the
   simulation).  The airtime limit is off by default.
2. Station clocks seed the backoff; two stations started in the same second with the
   same call hash would back off identically (unlikely, not handled).
3. The link sequence space (1024) wraps; duplicate suppression only looks back a few
   retransmission times, so wrap is harmless for it.
4. Gateways that relay a message onto WAPR wait for the addressee's link ack; if the
   addressee is not on WAPR they retransmit twice for nothing (bounded).

## 7. Acceptance (part B)

| Criterion | Status |
|---|---|
| Busy detection, randomized backoff, bounded retries, duplicate suppression, airtime limits, acks for selected traffic | implemented and tested |
| Broadcasts treated differently from messages needing confirmation | pass |
| Capacity and collision behaviour under increasing load, reproducible, validated | pass (simulation, validated against ALOHA theory) |
| No legacy regression | pass |

**Stage 4 passes.**

## 8. Next step (stage 5)

Profile selection and validation need inputs this environment does not have:
recordings of real HF / VHF channels (or permission to make them) and authorised
over-the-air tests between two stations.  Without them, the smallest useful next step is
a held-out simulation study of the decisions profile selection would make (receiver SNR
estimate vs true SNR, and the delivery each profile gives there), using the existing
SNR estimate the receiver already reports.
