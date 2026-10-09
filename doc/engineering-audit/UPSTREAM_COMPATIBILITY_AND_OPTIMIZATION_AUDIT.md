# Upstream Review, APRS/AX.25 Compatibility and Second Optimization Round

Fork: `themantas1994/direwolf`, branch `ccr-af7cf828-8zgxz8`, based on `master` at
`301de93` (PR #1 merged).  Compared builds, all from source on the same machine:

| Label | Commit | What |
|---|---|---|
| old | `eda1383` | upstream `wb2osz/direwolf` master, release 1.8.2 |
| PR#1 | `301de93` | this fork after PR #1 |
| new | this branch | this round |

x86-64 Linux, gcc 13.3, Release (`-O3`), 44.1 kHz unless stated.  Raw CSV results are in
`results/round2/`.

## 1. Executive summary

* **Three remotely triggerable memory safety bugs in received packet decoding were
  found by fuzzing and fixed.  All three are also in upstream 1.8.2 and the current
  upstream `dev` branch.**
  * One received APRS packet (`{DA` AIS user data type, sentence missing its last field)
    **crashes Dire Wolf** (NULL passed to `atoi`).  Reproduced end to end: audio on stdin
    -> `direwolf` exits with SIGSEGV for old and PR#1 (run); upstream dev has the same
    code (read, not run).
  * A MIC-E packet whose comment is just `` ` `` or `'` makes the device identification
    read before the start of the comment buffer, and can write out of bounds.
  * An AIS packet whose sentence is empty reads uninitialized stack, possibly past the end
    of the buffer.
* **Forwarding safety gap from PR #1 closed.**  `REGEN` (signal regeneration) retransmitted
  every received frame, including ones repaired by `SOFT_FIX`, which PR #1 turned on by
  default.  Measured end to end: PR#1 retransmitted all 22 frames that were only received
  by repair; new retransmits 0 of them.  Digipeater and IGate gates were verified end to
  end (IGate against a local fake APRS-IS server: 118 clean frames uploaded, 0 of 70
  repaired frames).
* **Soft repair exposure is now bounded per frame, as PR #1 intended.**  PR #1 bounded
  repair to 8 FCS checks per frame for `A+`, but channels with several demodulators (PSK
  modes, `-P AB`, multiple frequencies) repaired every demodulator's copy.  On pure noise
  PR#1 made 79,192 (2400 QPSK) and 96,352 (4800 8PSK) repair FCS checks per 15 minutes;
  new makes 5,112 and 3,064.  For `A+`: 6,512 -> 0; 9600, 300 and `A`: -> 0.  In PR #1's
  own development runs every corrupted frame accepted by repair came from variants making
  more than 8 checks per frame; in this round PR#1 accepted one corrupted frame on 2400
  QPSK (4 copies repaired), new none.
* **Sensitivity:** PR #1's published numbers reproduce exactly (1200 `A+` 90% threshold
  11.42 -> 10.95 dB, `A` 11.87 -> 11.22 dB).  New is identical to PR#1 for single slicer
  channels, equal or slightly better for `A+` (the default), and gives back part of PR #1's
  gain where PR #1 spent the extra checks: 0.12 dB (2400) and 0.33 dB (4800) at 90% for
  PSK, still 0.5 / 0.4 dB better than upstream; for multi-profile `AB` 0.1 dB (flat) up to
  about 40% of PR #1's extra frames (impulse noise).  No frame decoded by upstream is lost
  in any run, and no corrupted frame was accepted *by repair* in any run of the new code.
  (The plain decoder of all three builds, upstream included, accepted the same 2 random
  FCS matches among 90,000 very weak frames; section 6.6.)
* **Upstream fixes adopted** (cherry-picked with `-x`): AGW `M` frame NULL dereference
  (#641), IL2P unit test compared a buffer with itself (#576), thread safe `gmtime_r`
  (#655), false CTCSS warning (#657), leftover debug output (#656).
* **Tests:** new `aistest` and `deviceidtest` ctests (both fail with the fixes reverted),
  modem test lines for the multi-demodulator repair path, an independent AFSK modulator in
  the benchmark, 99% thresholds.  26/26 ctests pass in gcc Release, gcc Debug, clang and
  ASan+UBSan builds.

Not verified: ARM / Raspberry Pi, Windows, macOS, real radios and real channels beyond the
synthetic tests (section 9).

## 2. Repository state and PR #1

* `master` = `301de93`, the merge of PR #1; version 1.8.2 "Development"; no other work.
  The working tree was clean.
* PR #1: merged by the owner, **no review comments, no conversation, and no CI check runs**
  recorded on the PR (GitHub Actions may simply not be enabled on the fork).  There were no
  review concerns to resolve; the review below is my own, and CI results for the Windows /
  macOS / Ubuntu matrix in `.github/workflows/ci.yml` were not available.
* `SOFT_FIX` defaults to 1 (on), `FIX_BITS` to 0.  The `TUNE_*` environment overrides in
  `demod_afsk.c` are upstream code, not from PR #1.

### 2.1 Verification of PR #1's claims

| Claim | Status |
|---|---|
| 1200 `A+` 11.42 -> 10.95 dB, `A` 11.87 -> 11.22 dB (90%) | **Reproduced exactly** (same seed, rebuilt binaries, section 6.1) |
| No frame lost vs upstream, 0 corrupted frames in final runs | Reproduced (0 lost, 0 bad in all rows) |
| Delay line change bit exact, 31 - 39% less CPU | Output identical (`-S0` matches old frame for frame).  **CPU saving smaller here: 15 - 18% of instructions, ~10% of CPU time** (section 6.8) |
| "Repaired frames are never digipeated or IGated" | **Not quite:** `REGEN` retransmitted them (fixed).  APRS digipeater, connected mode digipeater and IGate gates hold (verified end to end). |
| Repair bounded to 8 FCS checks per frame | **Only for single demodulator channels.**  With several demodulators each copy got 8 (fixed). |
| UB fixes | Present and correct |

### 2.2 Other observations on PR #1

* Bit position mapping in `try_soft_fix` is right: it inverts raw (pre-NRZI, pre-destuffing)
  bits, as `FIX_BITS` does, and `try_decode` redoes NRZI, destuffing and descrambling.
* Candidate selection in `multi_modem` scores an unrepaired copy 1000 points per retry level
  above a repaired one, so a repaired copy can never displace a clean one, and duplicates
  are not produced (0 duplicates in every run).
* **KISS/AGW clients receive repaired frames by default.**  This is upstream's existing rule
  for `FIX_BITS` (shown and passed to applications, never digipeated/IGated by Dire Wolf),
  but upstream only applies it when the user enables `FIX_BITS`.  With `SOFT_FIX 1` on by
  default, an external client that digipeats or IGates (APRSIS32, YAAC, Xastir, ...) now
  receives repaired frames without the user having asked.  The KISS protocol can't mark
  them.  I did not change this default; it is a decision for the owner (section 8).

## 3. Upstream review

Upstream `dev` is 76 commits ahead of 1.8.2 (mostly AX.25 v2.2 SREJ, KISS pty, hamlib 5,
DNS-SD).  None touch the demodulators, HDLC decoding or FCS.  Relevant items, with the
actual patch or thread read:

| Ref | Topic | Finding | Decision |
|---|---|---|---|
| [#641](https://github.com/wb2osz/direwolf/pull/641) `9ac67f5` | AGW `M` frame with bad address -> NULL packet used | Real crash from any AGW client | **Adopted** |
| [#576](https://github.com/wb2osz/direwolf/pull/576) `a821a0e` | IL2P scrambler test compared `scramout` with itself | Test tested nothing; passes with fix | **Adopted** |
| [#655](https://github.com/wb2osz/direwolf/pull/655) `cfaa64a` | `gmtime` not thread safe, NULL unchecked in `get_timestamp` | Correct; `localtime_r` already used, Windows build defines `_POSIX_C_SOURCE` | **Adopted** |
| [#657](https://github.com/wb2osz/direwolf/pull/657), [#656](https://github.com/wb2osz/direwolf/pull/656) | False CTCSS warning for `100%`; debug output in general query | Display only | **Adopted** |
| [#651](https://github.com/wb2osz/direwolf/issues/651), [#652](https://github.com/wb2osz/direwolf/pull/652), [#653](https://github.com/wb2osz/direwolf/pull/653) | CVE-2025-34457 (KISS overflow), CVE-2025-34458 (MIC-E abort) | Already in 1.8.2 / the fork | Already present |
| [#376](https://github.com/wb2osz/direwolf/issues/376) | FX.25/IL2P corrected frames not digipeated/IGated | Fixed since 1.7: FEC frames pass the gate | Already present |
| [#439](https://github.com/wb2osz/direwolf/pull/439) | AGC parameters missing for profiles B/D | Already in the fork's `demod_afsk.c` | Obsolete |
| [#426](https://github.com/wb2osz/direwolf/issues/426) | Corrupted TX bitstream with FX.25 | Open; disappeared after a sound card change; no code identified; maintainer couldn't reproduce | Not actionable; `check-fx25` passes |
| [#488](https://github.com/wb2osz/direwolf/issues/488) | WIDE1-1,WIDE2-1 not digipeated | Maintainer: configuration, not a bug (only the first unused address is examined) | No change |
| [#381](https://github.com/wb2osz/direwolf/issues/381) | 300 bps, `@` and `+` mutually exclusive | Configuration message; no decoding defect | No change |
| [#541](https://github.com/wb2osz/direwolf/issues/541) | Digipeating to NCHANNEL | Feature/behavior question on network channels; upstream also extended digipeater medium checks to SERTNC in dev | Not adopted (new feature, not needed for RF compatibility) |
| `078b2e8` (dev) | Warn on `ack1348{4205` (invalid extra message id in ack/rej) | Changes displayed message number for invalid acks only | Not adopted (cosmetic; no forwarding effect) |
| [#572](https://github.com/wb2osz/direwolf/pull/572) | IL2P CRC (open PR) | New on-air extension used by some TNCs | Not adopted (new on-air feature; future item) |
| [#669](https://github.com/wb2osz/direwolf/pull/669) | AGWPE extension with per-frame signal quality | Could let clients see that a frame was repaired | Future item (section 8) |
| [#628](https://github.com/wb2osz/direwolf/pull/628), [#638](https://github.com/wb2osz/direwolf/pull/638), [#665](https://github.com/wb2osz/direwolf/pull/665) | IQ input, LoRa, OFDM (open PRs) | New modems, out of scope | Not adopted |

No upstream issue or PR proposes receive DSP improvements beyond what PR #1 already
measured; upstream has no soft decision repair.

## 4. Defects found in this round

| # | Where | Defect | Reach | Found by | Upstream? |
|---|---|---|---|---|---|
| 1 | `ais.c` `ais_parse` | `atoi(NULL)` when the fill bits field is missing | **Any received APRS packet** with `{DA` user data; crashes `direwolf` | libFuzzer + ASan | 1.8.2 and dev |
| 2 | `deviceid.c` `deviceid_decode_mice` | Suffix compared before the start of the comment when the comment is shorter than prefix + suffix; trimmed length can go negative and be used as an index for a write | Any received MIC-E packet with a 1 - 2 character comment | libFuzzer + ASan | 1.8.2 and dev |
| 3 | `ais.c` `ais_parse` | Checksum loop starts past the nul of an empty sentence | `{DA` with nothing after it | libFuzzer + ASan | 1.8.2 and dev |
| 4 | `ais.c` `get_field_signed` | Sign extension by shifting into the sign bit (UB) | Every AIS position | UBSan in fuzzing | 1.8.2 and dev |
| 5 | `direwolf.c` | `digi_regen` retransmits repaired / bad-FCS frames | `REGEN` users; more frames since `SOFT_FIX` default on | Code review, verified end to end | Upstream for FIX_BITS/PASSALL; PR #1 made it default |
| 6 | `hdlc_rec2.c` | Soft repair budget per demodulator copy, not per frame | PSK modes, multi-profile / multi-frequency channels | Code review, measured | PR #1 |
| 7 | `deviceid.c` | Built-in self test no longer compiles with glibc >= 2.38 and was never run by ctest | Test coverage | Build | 1.8.2 and dev |

Also seen, not changed: `deviceid_init` leaks about 650 bytes once at startup (LeakSanitizer);
upstream's plain decoder accepts about one random 9600 bps frame per 15 minutes of pure
noise (identical in old, PR#1 and new; no repair involved).

## 5. Changes

Each is a separate commit with its own measurements.

1. **Soft repair: one budget of FCS checks per frame** (`hdlc_rec2.c`, `multi_modem.c`).
   With several demodulators or slicers, failed copies are collected for the window
   `multi_modem` already uses to pick the best candidate (3 bit times).  If any copy decoded
   (cleanly or by `FIX_BITS`), nothing is repaired.  Otherwise the best matched slicer's copy
   from each demodulator (or the nearest slicer's, if the best one has no copy) is tried, with
   8 checks in total at level 1, spread by bit reliability rank.  At least 2 failed copies must
   arrive together (noise makes junk on one slicer at a time).  Copies with more than 30% of
   bits at confidence <= 30 are not tried: every repairable frame in the tests had at most 16%,
   noise junk over 40%.  Single demodulator/slicer channels repair immediately, as before.
2. **`REGEN` follows the digipeater rule** (`direwolf.c`): only error free or FEC corrected
   frames are regenerated.
3. **AIS and MIC-E fixes** (`ais.c`, `deviceid.c`) with unit tests.
4. **Upstream cherry-picks** listed in section 3.
5. **Benchmark** (`test/benchmark/rx_sensitivity.py`): `--generator numpy`, an AFSK modulator
   sharing no code with `gen_packets`; 99% thresholds by default.

### 5.1 How the repair policy was chosen (`results/round2/policy_*.csv`)

All on identical audio, Eb/N0 6 - 12 dB, 600 frames per point; repaired frames recovered
(none corrupted in any variant):

| Policy | A+ flat | A+ impulse | A+ twist -6 | AB flat | AB impulse | AB twist -6 |
|---|---|---|---|---|---|---|
| PR#1 (8 checks per copy, best slicer) | 207 | 298 | 63 | 257 | 336 | 40 |
| Fewest doubtful bits copy, 8 checks (other audio*) | 82 vs PR#1 110 | 191 vs PR#1 195 | - | - | - | - |
| Best slicer copy, deferred (P1) | 212 | 309 | 70 | 154 | 188 | 6 |
| Round robin over all copies (P2) | 192 | 262 | 81 | 193 | 224 | 36 |
| **Best copy per demodulator, round robin (final, 2 copies)** | **212** | **305** | **65** | **183** | **212** | **30** |

\* seed 7, Eb/N0 7 - 14 dB, 300 frames per point, compared with PR#1 on that same audio.

Choosing the copy with the fewest doubtful bits was worse than PR #1's tone balance based
choice: confidence values are not comparable between slicers with different offsets.

## 6. Results

All runs: identical WAV files for all three builds, `atest -F0`, default `SOFT_FIX` (1).
Thresholds are Eb/N0 (dB) for 90% / 99% frame success by linear interpolation; "nan" means
the curve does not reach that level in the measured range.  A frame counts only if its full
text matches what was sent; "bad" = corrupted frame accepted.  "lost" = frames the old build
decoded that this build did not, on the same audio.

### 6.1 1200 bps, 13 impairments (`final_1200_all*.csv`; PR #1's exact setup)

200 frames per point, 4 - 20 dB in 1 dB steps, seed 42.  Old and PR#1 reproduce PR #1's
report exactly.

| Condition | Demod | p90 old | p90 PR#1 | p90 new | p99 old | p99 PR#1 | p99 new | frames old / PR#1 / new |
|---|---|---|---|---|---|---|---|---|
| flat | A+ | 11.42 | 10.95 | 10.93 | 12.00 | 11.88 | 11.88 | 2088 / 2148 / 2151 |
| flat | A | 11.87 | 11.22 | 11.22 | 13.60 | 13.33 | 13.33 | 1998 / 2124 / 2124 |
| twist -6 dB | A+ | 14.11 | 13.74 | 13.74 | 15.33 | 15.00 | 15.00 | 1565 / 1611 / 1613 |
| twist -10 dB | A+ | 19.92 | 19.71 | 19.71 | nan | nan | nan | 377 / 420 / 425 |
| twist +6 dB | A+ | 12.85 | 12.49 | 12.50 | 13.91 | 13.00 | 13.33 | 1809 / 1889 / 1889 |
| bit rate +1% | A+ | 11.60 | 11.37 | 11.37 | 12.67 | 11.97 | 11.97 | 2046 / 2092 / 2090 |
| bit rate -1% | A+ | 11.11 | 10.89 | 10.89 | 12.50 | 11.92 | 11.92 | 2095 / 2159 / 2161 |
| tones +2% | A+ | 11.73 | 11.22 | 11.22 | 13.00 | 12.67 | 12.67 | 2018 / 2082 / 2082 |
| tones -50 Hz | A+ | 11.96 | 11.73 | 11.71 | 12.94 | 12.67 | 12.60 | 1955 / 2024 / 2026 |
| CTCSS + DC | A+ | 11.35 | 10.86 | 10.83 | 12.33 | 11.91 | 11.89 | 2103 / 2167 / 2171 |
| 1% level | A+ | 11.00 | 10.84 | 10.84 | 12.00 | 11.91 | 11.91 | 2112 / 2172 / 2175 |
| clipped | A+ | 12.51 | 11.92 | 11.92 | 13.33 | 12.86 | 12.86 | 1847 / 1924 / 1923 |
| impulses | A+ | 13.86 | 11.96 | 11.93 | nan | 19.33 | 19.33 | 1847 / 2024 / 2026 |
| flutter | A+ | 15.50 | 14.87 | 14.87 | 17.00 | 15.91 | 15.91 | 1303 / 1388 / 1388 |

Every `A` row is identical between PR#1 and new (single slicer: same code path).  For `A+`,
new is within -2 / +5 frames of PR#1 in every condition (net +20 over the 13 conditions).  The
small differences in either direction come from which copy is repaired: new falls back to a
neighboring slicer when the best one produced no copy, and does not repair when another copy
already decoded.  In all 26 rows: 0 bad,
0 duplicates, 0 lost against old.

### 6.2 99% threshold with more frames (`final_p99_1200*.csv`)

1000 frames per point, 8 - 16 dB in 0.5 dB steps, seed 44:

| Demod | p90 old | p90 PR#1 | p90 new | p99 old | p99 PR#1 | p99 new | frames old / PR#1 / new | bad |
|---|---|---|---|---|---|---|---|---|
| A+ | 11.16 | 10.70 | 10.70 | 12.31 | 12.05 | 12.05 | 12482 / 13152 / 13155 | 0 / 0 / 0 |
| A | 11.79 | 10.94 | 10.94 | 13.00 | 12.70 | 12.70 | 11827 / 12907 / 12907 | 0 / 0 / 0 |

The gain at 99% (0.26 / 0.30 dB) is smaller than at 90%: frames that fail at high Eb/N0 tend
to have errors repair can't fix (bursts, slips).

### 6.3 Independent modulator, 48 kHz (`final_numpy48k*.csv`)

`--generator numpy`, 300 frames per point, 4 - 18 dB, seed 45.  Same conclusions as with
`gen_packets`, so the gains don't depend on the project's own modulator:

| Condition | Demod | p90 old | p90 PR#1 | p90 new | frames old / PR#1 / new | bad |
|---|---|---|---|---|---|---|
| flat | A+ | 11.15 | 10.84 | 10.84 | 2541 / 2642 / 2644 | 0 |
| flat | A | 11.88 | 10.92 | 10.92 | 2472 / 2621 / 2621 | 0 |
| bit rate +1% | A+ | 11.56 | 11.26 | 11.25 | 2489 / 2569 / 2572 | 0 |
| tones -50 Hz | A+ | 11.83 | 11.51 | 11.49 | 2388 / 2477 / 2482 | 0 |
| twist -6 dB | A+ | 13.51 | 13.09 | 13.09 | 1891 / 1957 / 1960 | 0 |

### 6.4 Several demodulators: where the bounded budget costs sensitivity

| Mode / profile | Condition | p90 old | p90 PR#1 | p90 new | frames old / PR#1 / new | bad old / PR#1 / new |
|---|---|---|---|---|---|---|
| 1200 `AB` (400 frames/pt) | flat | 11.48 | 10.84 | 10.94 | 1712 / 1898 / 1847 | 0 / 0 / 0 |
| 1200 `AB` | impulse | nan | 12.33 | 13.95 | 1300 / 1620 / 1485 | 0 / 0 / 0 |
| 1200 `AB` | twist -6 dB | nan | nan | nan | 324 / 493 / 462 | 0 / 0 / 0 |
| 2400 QPSK `-J` (200/pt) | flat | 11.48 | 10.84 | 10.96 | 1060 / 1157 / 1119 | 0 / 0 / 0 |
| 2400 QPSK | impulse | nan | nan | nan | 558 / 722 / 676 | 0 / **1** / 0 |
| 4800 8PSK | flat | 16.71 | 16.00 | 16.33 | 1046 / 1131 / 1105 | 0 / 0 / 0 |
| 4800 8PSK | impulse | nan | nan | nan | 225 / 269 / 265 | 0 / 0 / 0 |

New keeps 58 - 91% of PR #1's extra frames (over old) on these channels.  The impulse p90 for `AB`
moves a lot because the curve is nearly flat there (old never reaches 90%); the frame counts
are the better measure.  **PR#1's corrupted 2400 frame** (`bad_frames.txt`): three characters
of a status report changed (`Oel1-i.B` -> `Ool1-i.D`, `@W` -> `@X`), accepted with a good FCS
after a single bit inversion, i.e. a hopeless frame that matched by chance.  It would have
gone to KISS clients marked `[SINGLE]`.  This is the risk the per-frame budget addresses.

### 6.5 Single demodulator modes (`final_mode_*.csv`)

200 frames per point, seed 42; plus 15 minutes of pure noise each.

| Mode | Condition | p90 old | p90 PR#1 | p90 new | frames old / PR#1 / new | bad | noise decodes old / PR#1 / new |
|---|---|---|---|---|---|---|---|
| 300 AFSK | flat | 11.20 | 10.80 | 10.80 | 1288 / 1363 / 1363 | 0 | 0 / 0 / 0 |
| 300 AFSK | impulse | 11.42 | 10.81 | 10.81 | 1279 / 1362 / 1362 | 0 | |
| 9600 G3RUH | flat | 10.50 | 9.58 | 9.58 | 1128 / 1276 / 1276 | 0 | 0 / 0 / 0 |
| 9600 G3RUH | impulse | nan | nan | nan | 381 / 472 / 472 | 0 | |

Identical to PR#1, as expected (one copy per frame; the doubtful bit limit never removed a
repairable frame).  2400 and 4800 noise: 1 / 0 decodes in all three builds (upstream's
plain decoder, not repair).

### 6.6 Weak signal stress test (`stress_*.csv`)

Eb/N0 7 and 8.5 dB (most frames fail, so most reach repair), flat and impulse noise,
1500 frames per point, seeds 101 - 105: 30,000 frames per profile and build.

| Profile | Build | decoded | corrupted accepted | of them by repair | lost vs old |
|---|---|---|---|---|---|
| A+ | old | 490 | 1 | - | - |
| A+ | PR#1 | 898 | 1 | 0 | 0 |
| A+ | new | 919 | 1 | 0 | 0 |
| A | old | 313 | 0 | - | - |
| A | PR#1 | 793 | 0 | 0 | 0 |
| A | new | 793 | 0 | 0 | 0 |
| AB | old | 284 | 1 | - | - |
| AB | PR#1 | 647 | 1 | 0 | 0 |
| AB | new | 531 | 1 | 0 | 0 |

The two corrupted frames (`bad_frames_stress.txt`) are the same in all three builds and were
**not** repaired: random FCS matches that upstream's plain decoder accepts too.  Upstream's
own false accept rate at these levels is therefore not zero either (about 1 in 15,000 frames
sent here); repair added none.  Over this round, the only corrupted frame accepted by repair
was PR#1's 2400 QPSK frame (section 6.4).

### 6.7 Pure noise

One hour of white noise, 1200 bps `A+`, `A` and `AB`: 0 decodes for old, PR#1 and new
(`noise_1200_summary.csv`).  Repair attempts on noise: section 1 and `noise_exposure.txt`.


### 6.8 CPU (`cpu.txt`, `callgrind.txt`)

Instructions executed (callgrind, deterministic), 60 s clips:

| Case | old | PR#1 | new | new vs PR#1 |
|---|---|---|---|---|
| 1200 A+, signal | 8.25 G | 7.02 G | 7.03 G | +0.2% |
| 1200 A, signal | 6.87 G | 5.63 G | 5.65 G | +0.3% |
| 1200 A+, noise | 8.24 G | 7.01 G | 7.02 G | +0.1% |
| 9600, noise | 2.45 G | 2.53 G | 2.48 G | -2.1% |
| 2400 QPSK `-j`, noise | 9.61 G | 9.66 G | 9.62 G | -0.4% |

CPU seconds, median of 3, x86-64 VM (wall clock noise about +-5%; measured before the
deadline commit, which removed ~2% of instructions):

| Case | old | PR#1 | new (pre-deadline) |
|---|---|---|---|
| 1200 A+, 15 min signal | 16.09 | 14.45 | 14.49 |
| 1200 A, 15 min signal | 13.45 | 12.01 | 12.27 |
| 1200 A+, 1 h noise | 63.30 | 59.42 | 57.41 |
| 9600, 1 h noise | 15.41 | 14.78 | 15.68 |
| 2400 QPSK `-j`, 1 h noise | 109.72 | 109.20 | 110.81 |

The first version of the deferred repair called a function for every audio sample (+1.8 - 2.6%
instructions, all modes).  It now uses a sample count and a deadline and costs about 0.2%;
decoded output is byte identical (8343 frames compared: 6899 at 1200 bps `A+`/`AB`/`A`,
1444 at 2400 QPSK `-J`, soft fix levels 1 and 2).  The benchmark rows in 6.1 - 6.7 were
produced before that change, which is why they still hold.

**PR #1's CPU claim does not reproduce on this machine.**  PR #1 reported 31% (A+) / 39% (A)
less CPU time.  Here its delay line change saves 15% (A+) / 18% (A) of instructions and
about 10% of CPU time.  The difference is probably the machine (PR #1 measured on a different
one; how much a `memmove` costs depends on the CPU and its caches), but I could not confirm
that.  The saving is real, but smaller than reported.  9600 and the PSK modes don't use the
AFSK delay lines; PR #1 made 9600 about 3% more instructions (bit confidence is now computed
and stored), and this round takes 2% of that back.

## 7. APRS / AX.25 compatibility

| Check | How | Result |
|---|---|---|
| On-air format unchanged | No transmit path code changed (modulators, HDLC/FX.25/IL2P encoders, KISS framing untouched in this round) | Unchanged |
| Frames decoded identically when no repair is involved | `-S0` lines of all modem tests unchanged; every benchmark row: 0 frames lost vs upstream; 9600/2400 noise decodes identical in all builds | Pass |
| Frame integrity | 0 corrupted frames accepted by repair in every run of the new code (6.1 - 6.7); the plain decoder's rare random FCS matches are identical to upstream (6.6); FCS checked on every frame; repaired frames must also pass the sanity test | Pass |
| Forwarding safety | Code: digipeater, cdigipeater, IGate and now REGEN require `retries == RETRY_NONE` or FX.25/IL2P.  End to end (`e2e_forwarding.txt`): REGEN 0 of 22 repaired frames (PR#1: 22 of 22), IGate 0 of 70, digipeater 0 of 11 | Pass |
| Interfaces | KISS TCP: 154/154 frames byte identical, escaping both ways; AGW, KISS pty, serial not exercised end to end | Pass (KISS TCP) |
| Configuration | No new keywords this round; `SOFT_FIX 0` still restores upstream decoding exactly | Unchanged |
| Malformed input | Fuzzing (raw AX.25, KISS, text -> decode_aprs): 3 memory safety bugs fixed; 18.7M executions clean after the fixes | Pass |
| AIS / MIC-E device id decoding of valid packets | `aistest` (MMSI, lat, lon), `deviceidtest` (Kenwood, Yaesu, Byonics, legacy and new suffix formats) | Pass |
| Unit and modem tests | ctest 26/26 (24 existing + `aistest`, `deviceidtest`) | Pass |

### 7.1 Builds and sanitizers (final code, each from an empty build directory)

| Build | Result | Warnings |
|---|---|---|
| gcc 13.3 Release, `-DUNITTEST=ON` | 26/26 ctest | none |
| gcc Debug (as CI) | 26/26 | pre-existing `aprs_tt.c` only |
| clang 18 Release | 26/26 | pre-existing only (`gen_packets.c`, `gen_tone.c` unused function, `external/`) |
| gcc Debug `-fsanitize=address,undefined` | 26/26, no reports | pre-existing `appserver.c`, `aprs_tt.c`, `tnctest.c` |
| ASan+UBSan `atest`, weak 1200 (`A+`, `AB`, `A`) and 2400 QPSK signals with repair (leak detection on) | no reports | |
| libFuzzer + ASan + UBSan, `test/fuzz/fuzz_rx.c` | 4 bugs found and fixed; then 18.7M executions clean | |

What "compatible" covers here: the receive path decodes the same frames as upstream (plus
repaired ones, marked), never accepts a frame with a bad FCS, and forwards only what
upstream would.  It does not cover interoperability with specific radios, TNCs or client
programs, which were not tested.

## 8. Remaining risks and decisions for the owner

1. **Repaired frames reach KISS/AGW clients by default (not changed).**  Dire Wolf itself
   never digipeats, regenerates or IGates them, but a client that does (APRSIS32, YAAC,
   Xastir as IGate/digi, ...) can't tell them apart: KISS has no field for it.  With upstream
   this only happens if the user sets `FIX_BITS`.  Options: (a) keep (current), (b) default
   `SOFT_FIX 0` and let users opt in, (c) a setting that keeps repaired frames from KISS/AGW
   clients.  The per-frame budget makes a corrupted repaired frame rarer than before (none in
   any run of this round), but not impossible.
2. **Sensitivity traded for bounded exposure on multi-demodulator channels** (section 6.4).
   If the owner prefers PR #1's behavior there, raising the level 1 budget with the number of
   demodulators (e.g. 8 + 4 per extra copy) is a one line change in `soft_fix_copies`; not
   measured.
3. **PSK repair attempts on noise** are 15 - 30x lower than PR#1 but not zero: the 30%
   doubtful limit is calibrated for AFSK/9600 confidence values.
4. **The three memory safety bugs are in upstream 1.8.2 and dev.**  They should be reported
   to upstream; the AIS one is a remote crash.
5. `deviceid_init` leaks ~650 bytes once; upstream's plain decoder accepts about one random
   frame per 15 minutes of pure noise at 9600 and 2400 (unchanged, not caused by repair).

## 9. Not verified

* **Platforms:** only x86-64 Linux (gcc 13.3, clang 18).  ARM / Raspberry Pi, Windows (MinGW),
  macOS were not built or run here.  The changes are plain C99; `gmtime_r` (from upstream)
  relies on `_POSIX_C_SOURCE`, which `direwolf.h` already sets for Windows and which upstream's
  CI builds.  The new unit tests link the project's own `strlcpy` when the C library lacks it
  (checked by building that way).
* **Real radios and channels:** no over the air tests; the WA8LMF TNC test CD was not
  available in this environment, so PR #1's CD results were not re-run.  FM receiver audio
  is not white noise.
* **Real-world false accept rate:** the synthetic runs bound it but can't prove it is zero.
* Not exercised end to end: AGW, KISS pty and serial, FX.25/IL2P transmit (unit tests pass),
  APRStt, connected mode.
* Not fuzzed: KISS receive state machine (`kiss_rec_byte`), FX.25/IL2P decoders, config file
  parser.
* Multi-frequency channels (e.g. `MODEM 300 7@30`) use the same deferred repair path as
  `AB` (several demodulators) but were not measured: `atest` has no option for them.
* CI (`.github/workflows/ci.yml`: Windows MinGW, macOS, Ubuntu) has not run on this branch
  as far as this environment can tell.

## 10. Worthwhile next steps, by value

1. Report the AIS crash and the MIC-E out of bounds read to upstream (private first if the
   owner prefers).
2. Decide item 8.1.  If repaired frames should not reach clients by default, the cleanest
   compatible design is to keep `SOFT_FIX` on for display/logging and add an opt-in to pass
   them to KISS/AGW.
3. Upstream PR #669 (AGWPE per-frame quality) could carry the retry level to clients that
   understand it, which would make option 8.1(c) unnecessary for them.
4. Calibrate a doubtful bit limit for PSK confidence values (cuts their noise exposure to ~0).
5. Run the WA8LMF CD and a Raspberry Pi build; add an ARM job to CI.
6. Extend fuzzing to `kiss_rec_byte`, the FX.25 and IL2P decoders and the config parser.
7. Coherent / MLSE detection remains the largest theoretical sensitivity gain (PR #1 report,
   section 8); it needs real FM audio recordings to evaluate safely.
