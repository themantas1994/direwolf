# Receive Sensitivity Audit and Optimization

Fork: `themantas1994/direwolf`, branch `claude/wonderful-fermi-zc4sip`.
Starting point: commit `eda1383`, identical to upstream `wb2osz/direwolf` master (1.8.2).
All measurements: x86-64 Linux, gcc 13.3, Release build (`-O3 -ffast-math`), 44.1 kHz
unless stated. "Old" is `eda1383`; "new" is this branch with default settings.

## 1. Summary

| Change | Effect (measured, identical audio for old and new) |
|---|---|
| **Soft decision repair** (`SOFT_FIX`, default 1): when the FCS fails, invert only the bits the demodulator was least sure about | Eb/N0 for 90% frame success, white noise: 1200 bps `A+` 11.42 -> 10.95 dB, `A` 11.87 -> 11.22 dB; 300 bps 0.19 dB, 2400 0.63 dB, 4800 0.55 dB, 9600 0.78 dB better. Better under all 13 impairments tested (up to 1.9 dB with impulse noise for `A+`). No frame decoded by the old code is lost. **No corrupted frame was accepted by a repair at the default level** in any final run. |
| **Circular delay lines** in the AFSK demodulator instead of a `memmove` per filter per sample | **-32% CPU** for `A+` (the default), **-39%** for `A`. Output byte-identical. |
| **Automatic decimation** when the 1200 bps band pass filter would otherwise be truncated (above ~55 kHz) | 96 kHz: 0.53 dB better and far less CPU. |
| **Undefined behavior** fixed (UBSan, fuzzing) | Shift count taken from a received packet (PHG/DFS); signed shifts in the G3RUH scrambler/descrambler and FX.25. |
| **`test/benchmark/rx_sensitivity.py`** | Reproducible, seeded sensitivity benchmark with impairments, false decode counting and CSV output. |

`SOFT_FIX 2` recovers more (1200 `A+` 0.70 dB, `A` 1.03 dB, 9600 1.18 dB) but lets a few
corrupted frames through (section 6), so it is not the default.

WA8LMF TNC Test CD (real off-air recordings), frames decoded, old -> new default:
track 1 `A+` 1015 -> 1018, `A` 1005 -> 1016; track 2 `A+` 1002 -> 1007, `A` 983 -> 999;
no frame lost; every gained frame on tracks 1 - 2 is confirmed by an identical frame
decoded with a good FCS elsewhere on the CD.

## 2. Repository state and baseline

* The fork had no changes relative to upstream 1.8.2 and no uncommitted work.
* Linux build needs `libasound2-dev`. Unit tests need `-DUNITTEST=ON` (off by default
  for Release builds).
* Baseline: 0 compiler warnings, `ctest` 24/24 pass.
* `atest` defaults to demodulator `A` (one slicer); `direwolf` defaults to `A+` (nine
  slicers with different mark/space balance). Both were measured. `FIX_BITS` defaults to 0.

## 3. Method

### 3.1 Synthetic benchmark

`test/benchmark/rx_sensitivity.py` (usage in `test/benchmark/README.md`):

1. Distinct APRS frames with 10 - 120 random payload characters, from a seed.
2. The project's `gen_packets` makes the clean signal (32 flag preamble, gaps between frames).
3. Impairment, then seeded white Gaussian noise at a given Eb/N0.
   Eb = average signal power while transmitting / bit rate; N0 = 2 sigma^2 / fs.
   For 1200 bps, SNR in 3 kHz = Eb/N0 - 4.0 dB.
4. All builds decode byte-identical WAV files (`atest -F0`). A frame is "ok" only if its
   full text matches what was sent; anything else decoded is "bad".
5. Per point: frames sent/ok/bad/repaired/missed, frames gained and lost against the
   reference build on the same audio, CPU time, SHA-256 of binaries and WAV files.
6. Thresholds: Eb/N0 for 50% and 90% frame success by linear interpolation (used below)
   and by a logistic fit with standard error (in the CSV files).

Impairments: tone twist -6, -10, +6 dB; bit rate +-1%; tones +2% and -50 Hz; 100 Hz CTCSS
at -6 dB plus DC; peak level 1% of full scale; 3x overdrive with hard clipping; 20 clicks
per second; noise level swinging +-6 dB at 4 Hz ("flutter"); pure noise.

### 3.2 Real recordings

WA8LMF TNC Test CD (http://wa8lmf.net/TNCtest/), tracks 1 - 4 extracted from the CD
image. Track 1 is 25.8 minutes of off-air Los Angeles APRS with flat audio; track 2 is the
same recording de-emphasized. Upstream tuned the demodulators on this CD, which guards
against overfitting to synthetic noise. There is no ground truth, so decoded frames
were compared as sets and every frame only the new code decoded was checked against
independent evidence.

### 3.3 Other checks

* `-fsanitize=address,undefined` build: full `ctest`, CD track 1, and 300,000 mutated
  AX.25/APRS frames (many packet types) through `decode_aprs`.
* CPU: `gprof` and `callgrind` profiles; CPU time of `atest` on CD track 1, 3 runs each,
  otherwise idle machine.
* Builds: gcc Release and Debug, clang Release, each from a clean directory.

## 4. Diagnosis

1. **The demodulator is close to the limit for bit-by-bit detection.** `A` reaches 50%
   frame success at about 10.5 dB Eb/N0, near the ~11 dB expected for an ideal
   non-coherent FSK detector with these ~790 bit frames; `A+` gains ~0.4 dB by picking
   among nine slicers with the FCS.

2. **Tuning the existing DSP parameters does not help** (`TUNE_*` environment overrides,
   identical audio, `results/diag_*.csv`): PLL inertia changes moved thresholds by at
   most 0.15 dB; RRC low pass width 2.0 or 4.0 symbols instead of 2.8 cost 0.8 and
   1.3 dB; prefilter 0.10 or 0.25 - 0.50 x baud instead of 0.155 cost 0.2 - 0.9 dB;
   roll-off 0.05 or 0.40 made no difference.

3. **Most frames lost near the threshold have one or two bad bits.** Brute force
   `FIX_BITS 1` (try inverting each bit) recovered frames worth 0.65 - 0.8 dB but accepted
   corrupted frames: 38 in a 60,000 frame weak-signal run where the old code accepted 4.
   That is why upstream leaves it off.

4. **The information needed to fix them was being thrown away.** `nudge_pll()` computes a
   0 - 100 confidence for every bit, and the PSK demodulator does too, but `hdlc_rec_bit()`
   ignored it (`not_used_remove`, with a comment that it was meant for this). Every bit a
   successful repair inverted had confidence 27 or less (99th percentile 21).

5. **`demod_9600.c` passed the descrambler state in that confidence argument** (harmless
   only because it was unused).

6. **Above ~55 kHz the 1200 bps prefilter is silently truncated** to `MAX_FILTER_SIZE`
   (833 -> 479 taps at 96 kHz), costing 0.53 dB.

7. **`memmove` of filter delay lines was 14% of instructions** and about a third of CPU
   time (callgrind: FIR loops 33%, `memmove` 14%, `hypotf` 1.5%).

Not pursued: a coherent or sequence (MLSE) detector for the phase-continuous Bell 202
signal could in theory gain up to ~3 dB more, but it is sensitive to the phase distortion
of real FM radio audio and is a much larger, riskier change (section 8).

## 5. Changes and results

### 5.1 Soft decision repair (`SOFT_FIX`)

`hdlc_rec_bit()` now keeps each bit's confidence in the `rrbb` with the raw bit. When the
FCS fails (and `FIX_BITS` did not already cover it), `try_soft_fix()` in `hdlc_rec2.c`:

* level 1 (default): inverts each of the 8 least reliable bits, one at a time;
* level 2: each of the 16 least reliable, then pairs from the 12 least reliable;
* never touches a bit with confidence above 30;
* with several slicers (`A+`), repairs only the copy from the slicer whose space tone gain
  best matches the measured mark/space amplitude ratio (`demod_best_slicer()`); for
  other multi-slicer modems, the middle slicer.

So level 1 does at most 8 FCS checks per frame, against one per bit for each slicer
(hundreds per slicer) for `FIX_BITS 1`. A repaired frame must pass the configured sanity test (APRS
by default) and is reported with the existing retry level (`[SINGLE]` etc.), so it keeps
the existing protections: shown and passed to client applications, but never digipeated
or IGated. Skipped for AIS and with sanity test `NONE`. Config `SOFT_FIX 0|1|2` per
channel; `atest -S n`. `direwolf` and `atest` now show the retry level whenever a frame
was repaired, even with `FIX_BITS 0`.

#### 1200 bps, all impairments (`results/final_1200_all*.csv`)

Eb/N0 (dB) for 90% / 50% frame success, 200 frames per point, Eb/N0 4 - 20 dB in 1 dB
steps, seed 42. "Gain" is old minus new at 90%. In every row the new code lost no frame
the old one decoded, and no repair accepted a corrupted frame.

| Condition | Demod | p90 old | p90 new | gain | p50 old | p50 new | frames old -> new |
|---|---|---|---|---|---|---|---|
| flat | A+ | 11.42 | 10.95 | 0.47 | 10.04 | 9.71 | 2088 -> 2148 |
| flat | A | 11.87 | 11.22 | 0.65 | 10.48 | 9.76 | 1998 -> 2124 |
| twist -6 dB | A+ | 14.11 | 13.74 | 0.37 | 12.59 | 12.42 | 1565 -> 1611 |
| twist -6 dB | A | 14.85 | 14.29 | 0.56 | 13.32 | 12.70 | 1429 -> 1536 |
| twist -10 dB | A+ | 19.92 | 19.71 | 0.21 | 18.62 | 18.43 | 377 -> 420 |
| twist +6 dB | A+ | 12.85 | 12.49 | 0.36 | 11.42 | 10.92 | 1809 -> 1889 |
| bit rate +1% | A+ | 11.60 | 11.37 | 0.23 | 10.28 | 10.04 | 2046 -> 2092 |
| bit rate -1% | A+ | 11.11 | 10.89 | 0.22 | 10.04 | 9.67 | 2095 -> 2159 |
| tones +2% | A+ | 11.73 | 11.22 | 0.51 | 10.38 | 10.10 | 2018 -> 2082 |
| tones -50 Hz | A+ | 11.96 | 11.73 | 0.23 | 10.74 | 10.35 | 1955 -> 2024 |
| CTCSS + DC | A+ | 11.35 | 10.86 | 0.49 | 9.93 | 9.64 | 2103 -> 2167 |
| 1% level | A+ | 11.00 | 10.84 | 0.16 | 9.97 | 9.60 | 2112 -> 2172 |
| clipped | A+ | 12.51 | 11.92 | 0.59 | 11.28 | 10.89 | 1847 -> 1924 |
| impulses | A+ | 13.86 | 11.96 | 1.90 | 10.68 | 10.13 | 1847 -> 2024 |
| impulses | A | 19.92 | 12.64 | (7) | 11.11 | 10.28 | 1621 -> 1961 |
| flutter | A+ | 15.50 | 14.87 | 0.63 | 14.00 | 13.62 | 1303 -> 1388 |
| flutter | A | 16.36 | 14.98 | 1.38 | 14.42 | 13.74 | 1202 -> 1347 |

All other `A` rows are similar to or better than the `A+` rows (CSV). Resolution at 200
frames per point is roughly +-0.15 dB for a single threshold; the gains are consistent
across conditions and confirmed frame by frame (`gained_vs_ref`, `lost_vs_ref`). The old
`A` impulse p90 is at the top of the measured range: single slicer `A` fails at any
signal level when clicks upset its envelope followers, and repair fixes most of those.

Level 2 on the same audio, p90: flat `A+` 10.72, `A` 10.84; impulse `A+` 11.57,
`A` 11.89; corrupted frames accepted: 2 (flutter, `A`).

#### Other modes (AWGN, 200 frames/point, `results/final_mode_*.csv`)

| Mode | p90 old | p90 new | gain | level 2 | p50 old | p50 new | frames old -> new -> level 2 | bad (level 2) |
|---|---|---|---|---|---|---|---|---|
| 300 AFSK | 10.95 | 10.76 | 0.19 | 10.53 | 9.96 | 9.59 | 1296 -> 1374 -> 1415 | 0 |
| 2400 QPSK (`-J`) | 11.47 | 10.84 | 0.63 | 10.71 | 10.24 | 9.81 | 1260 -> 1349 -> 1404 | 1 |
| 4800 8PSK | 16.84 | 16.29 | 0.55 | 16.00 | 15.14 | 14.75 | 1056 -> 1137 -> 1168 | 0 |
| 9600 G3RUH | 10.30 | 9.52 | 0.78 | 9.12 | 8.73 | 8.21 | 1935 -> 2058 -> 2116 | 0 |

FX.25 and IL2P have their own FEC and do not go through this path; their tests are unchanged.

### 5.2 Real recordings (WA8LMF TNC Test CD, `results/cd_tracks.txt`)

| Track | Demod | old | new (level 1) | level 2 | lost |
|---|---|---|---|---|---|
| 1 (flat) | A+ | 1015 | 1018 | 1021 | 0 |
| 1 (flat) | A | 1005 | 1016 | 1022 | 0 |
| 2 (de-emphasized) | A+ | 1002 | 1007 | 1014 | 0 |
| 2 (de-emphasized) | A | 983 | 999 | 1006 | 0 |
| 3 (ideal) | A+ | 100 | 100 | 100 | 0 |
| 4 | A+ | 107 | 108 | 110 | 0 |

Every frame gained on tracks 1 and 2, at both levels, has the same source and content as
a frame decoded with a good FCS (no repair) somewhere on track 1 or 2: stations beacon
repeatedly, digipeaters relay copies, and track 2 is the same recording as track 1. The
track 4 gains are MIC-E positions of a moving station that appear nowhere else, so they
can't be confirmed either way.

For `A+`, repairing only the best matched slicer is what costs real-world frames: on
track 1, repairing the best slicer and its neighbors gave 1023 (level 1), and repairing
every slicer gave 1028; on track 2, 1012 for both. The synthetic runs show why it
was not chosen anyway (section 6).

### 5.3 CPU

CPU seconds (user+sys) for CD track 1 (1547 s of audio), median of 3:

| Demod | old | new, `SOFT_FIX 0` | new, default |
|---|---|---|---|
| A+ | 23.36 | 15.95 (-32%) | 16.02 (-31%) |
| A | 20.25 | 12.36 (-39%) | 12.36 (-39%) |

One hour of noise: `A+` 56.2 -> 39.4 s, `A` 47.2 -> 28.8 s. The delay line change is
bit-exact: frames, timestamps and audio levels were byte-identical to the old code for
`A+`, `A` and `B` on CD tracks 1 and 2, and `-S0` matched the old code frame for frame in
every synthetic run (`results/early_*`). Memory: about 13 KB more per active AFSK
demodulator. Repair work is bounded and only happens for frames that already failed.

### 5.4 Sample rates above 55 kHz

If no decimation factor is configured, `demod.c` now picks the smallest that lets the
1200 bps prefilter fit: 96 kHz -> /2, 192 kHz -> /4; 44.1 and 48 kHz unchanged.
96 kHz, `A+`, repair off, same audio: p90 11.60 -> 11.07 dB (flat), 13.98 -> 13.88 dB
(twist -6 dB); CPU for those runs 101.6 -> 31.3 s (with the delay line change). Because the
filters change, individual frames differ in both directions (flat: 60 gained, 24 lost);
the net effect is clearly positive.

### 5.5 Undefined behavior

| Where | Problem | Found by |
|---|---|---|
| `decode_aprs.c` PHG and DFS | `1 << (pdext[4] - '0')` with any character from a received packet: negative or too large shift | fuzzing |
| `demod_9600.h`, `gen_tone.c` | scrambler/descrambler shifts a signed `int` into the sign bit | `ctest` under UBSan |
| `fx25_rec.c` | `1LL << 63` | `ctest` under UBSan |

After the fixes `ctest`, CD track 1 and 300,000 fuzzed frames are clean under ASan and
UBSan. No memory errors were found.

## 6. False decodes

Every extra FCS check on a frame with more errors than a repair can fix is about a 1 in
65536 chance of accepting it with wrong content. Some structure helps: NRZI turns a raw
bit error pattern R(x) into (1+x)R(x), and the AX.25 FCS generator is (1+x)p(x) with p
primitive of period 32767. So one or two remaining raw bit errors are always detected,
and inverting one wrong bit of a frame with a single error can never produce a valid
frame. Inverting a wrong *pair* can (about 1 in 32767). Everything else rests on the
number of checks and on the sanity test.

All corrupted frames accepted by repair in development had three or more damaged
characters: frames hopeless for a one or two bit repair that matched the FCS by chance.
How the design got to the default (corrupted frames accepted *by repair* /
frame decodes, flat + flutter + impulse noise, Eb/N0 8 - 15 dB, `results/policy_*.csv`):

| Variant | Extra correct frames | Corrupted |
|---|---|---|
| 16 single bits + pairs from 12, every slicer (first version) | +19,274 / 144k | 3 |
| 16 single bits, every slicer | +14,354 / 144k | 2 |
| 8 single bits, every slicer | +12,962 / 144k | 1 |
| 4 single bits, every slicer | +11,088 / 144k | 1 (same frame) |
| 8 single bits, best slicer +-1 | +3,973 / 48k | 1 (same frame) |
| **8 single bits, best slicer only (default)** | +2,762 / 48k and +3,328 / 64k | **0** |
| 16 + pairs from 12, best slicer only (level 2) | +5,317 / 64k | 0 |
| 16 + pairs from 12, best slicer +-1 | +6,040 / 48k | 6 |

Final runs:

| Test | Frames | old ok / bad | new ok / bad | level 2 ok / bad |
|---|---|---|---|---|
| 1200, 13 impairments, A+ and A (5.1) | 88,400 | 0 bad | 0 bad | 2 bad |
| other modes (5.1) | 10,400 | 0 bad | 0 bad | 1 bad |
| 1200 A+, Eb/N0 7 and 8.5 dB, seeds 101 - 105 | 20,000 | 410 / 0 | 722 / 0 | 1124 / 1 |
| pure noise, 1 hour, A+ and A | - | 0 | 0 | 0 |

For comparison, the old code itself sometimes accepts corrupted frames at very weak
signals (4 in the 60,000 frame run at Eb/N0 7 and 8.5 dB, 1 in the 64,000 frame best
slicer run). Those were random data with a good FCS; repaired errors look more plausible
(a changed callsign or characters), which is why the default is conservative. A
corrupted frame from repair is shown and sent to client applications marked with its
retry level, and is never digipeated or IGated. `SOFT_FIX 0` restores the old behavior
exactly.

## 7. Tests

| Command | Result |
|---|---|
| `cmake -DUNITTEST=ON .. && make && ctest`, clean build, gcc Release | 24/24 pass, 0 warnings |
| same, gcc Debug (as in CI) | 24/24 pass, 1 pre-existing warning (`aprs_tt.c`) |
| same, clang Release | 24/24 pass, only pre-existing warnings |
| same, `-fsanitize=address,undefined` | 24/24 pass, no reports |
| `direwolf -c` with `ADEVICE stdin null`, `SOFT_FIX 2` and an invalid `SOFT_FIX 7`, noisy audio on stdin | decodes, repaired frames marked; invalid value reported with its line number and replaced by the default |

The modem test scripts compare decode counts with fixed windows (`-L`, `-G`). Their
existing lines now pass `-S0`, so the original windows still apply unchanged and still
pass: the demodulators decode exactly as before with repair off. New lines check the
repair counts at levels 1 and 2, and a 96 kHz case covers the automatic decimation.
Windows (MinGW) and macOS were not built here; the changes are plain C99 with no new
dependencies.

Reproduce the main comparison (old build in `../old`):

    python3 test/benchmark/rx_sensitivity.py \
        --atest old=../old/build/src/atest --atest new=build/src/atest \
        --gen-packets build/src/gen_packets --profiles A+,A \
        --conditions flat,deemph6,deemph10,preemph6,fast1pct,slow1pct,tone+2pct,tone-50hz,ctcss,lowlevel,clipped,impulse,flutter \
        --ebn0 4:20:1 --frames 200 --seed 42 --noise-only 3600 --out final_1200_all.csv

## 8. Limitations and open items

* **The default is deliberately conservative.** Measured real-world gain for `A+` is small
  (+3 and +5 frames on tracks 1 and 2); also repairing the best slicer's two neighbors
  (a one line change to the slicer test in `try_soft_fix`) roughly triples it but let one
  corrupted frame through in 48,000 synthetic frames. A per-frame choice among the
  slicers' failed copies (deferring repair until all slicers have finished a frame) would
  probably get most of that gain with best-slicer exposure; not implemented.
* **Hardware.** All timing is from one x86-64 machine. Raspberry Pi / ARM and Windows /
  macOS were not run. The changes are portable C; the delay line change should help ARM
  at least as much, but that is not measured.
* **Real-world false decodes** can't be measured on the CD (no ground truth); the
  synthetic rates above are the evidence. Actual rates depend on how often weak frames
  fail on a channel.
* **Sanity test `AX25`** (connected mode users with `FIX_BITS n AX25`) only checks
  addresses, so repaired non-APRS frames have less protection; not measured because
  `atest` always uses the APRS test. With the default APRS test, connected mode I frames
  are not repaired at all (no change from before for them).
* **Coherent / MLSE detection** of the phase-continuous AFSK signal is the largest
  remaining theoretical gain (up to ~3 dB) but needs careful work on real, phase-
  distorted radio audio.
* `gen_packets -n` regression files use uniform noise and short frames; they detect
  changes but are not a sensitivity measure. The benchmark needs numpy and is not run by
  `ctest`.
* `tnctest.c` has two `-Wformat-truncation` warnings in an `-O1` build (test tool, not
  touched).

## 9. Files changed

| File | Change |
|---|---|
| `src/hdlc_rec2.c` | `try_soft_fix()` |
| `src/rrbb.h`, `src/hdlc_rec.c`, `src/hdlc_rec.h` | keep per-bit confidence |
| `src/demod.c`, `src/demod.h`, `src/demod_afsk.c`, `src/demod_afsk.h` | `demod_best_slicer()`; automatic decimation |
| `src/demod_9600.c` | pass a real confidence instead of the descrambler state |
| `src/audio.h`, `src/config.c` | `soft_fix` setting, `SOFT_FIX` keyword |
| `src/atest.c`, `src/direwolf.c` | `-S` option; show retry level for repaired frames |
| `src/demod_afsk.c`, `src/fsk_demod_state.h` | circular delay lines |
| `src/decode_aprs.c`, `src/demod_9600.h`, `src/gen_tone.c`, `src/fx25_rec.c` | undefined behavior |
| `test/scripts/check-*` | `-S0` on existing lines; new repair and 96 kHz lines |
| `test/benchmark/` | benchmark and README |
| `CHANGES.md`, this report, `results/` | documentation and raw results |
