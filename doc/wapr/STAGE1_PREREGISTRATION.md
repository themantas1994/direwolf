# WAPR stage 1: pre-registered evaluation

Written and committed **before** the evaluation run.  Development runs (seeds 1, 3, 5,
11-14, 99) were used to find bugs and choose receiver settings; the evaluation uses a
seed that no development run used.  Any deviation from this plan is reported with the
results.

## Frozen inputs

* Simulator: `test/wapr/*.py` at the commit that adds this file.
* Configuration: `test/wapr/configs/stage1.json` (SHA-256 prefix recorded in every
  result row as `config_sha`).
* Legacy binaries: `gen_packets` and `atest` built from the same commit
  (`cmake -DCMAKE_BUILD_TYPE=Release`).
* Held-out seed: **20261010**.  Bootstrap replicates: 1000.
* Payload: 32 bytes for every system (see `test/wapr/README.md` for what that means for
  each).  SNR_2500 and Eb/N0 per payload bit as defined there.

## Settings chosen during development (seeds 1, 3, 5)

| Setting | Before | After | Evidence (development seeds only) |
|---|---|---|---|
| Sync blocks per frame | 3 (12 symbols for M=4) | 6 | F600 at 0 dB: 78 -> 92 / 100 delivered; genie receiver 97-100 / 100 |
| Fine time / frequency search | sync symbols only | whole frame (`refine_data`) | F600 at 0 dB: 92 -> 99 / 100; frequency error 31 -> 14 Hz rms |
| Coarse frequency search | +-1 bin (300 Hz at 600 bd) | bins within `freq_search_hz` | removed out-of-range candidates |
| Amplitude tracking for soft bits | whole frame | 8 symbol window | H150 itu_mod at -2 dB: 64 -> 68 / 80 (within noise) |
| Fading tap scaling | bug: 1/sqrt(n) power | unit mean power | found by `wapr_selftest.py`; earlier fading runs discarded |

## Runs

| Run | Systems | Conditions | SNR grid (dB) | Frames / point |
|---|---|---|---|---|
| VHF | F600_ldpc, F600_ldpc_r23, F600_conv | awgn | -2.0 ... 2.0 step 0.25 | 300 |
| VHF legacy | afsk1200, afsk1200_fx25, afsk1200_il2p | awgn | 3 ... 11 step 0.5 | 300 |
| HF | H150_ldpc, H150_conv | awgn, itu_mod | awgn -7 ... -3 step 0.25; itu_mod -6 ... 10 step 1 | 300 |
| HF robust | R25_ldpc, R25_conv | awgn, itu_mod | awgn -15.5 ... -11.5 step 0.25; itu_mod -16 ... 0 step 1 | 200 |
| HF legacy | afsk300, afsk300_il2p | awgn, itu_mod | awgn -2 ... 6 step 0.5; itu_mod 0 ... 30 step 2 | 300 |
| Offset | H150_ldpc, R25_ldpc | itu_mod_off (uniform +-25 Hz) | as itu_mod | as above |
| Noise | all WAPR systems | pure noise | - | 600 s each |

If a curve has fewer than two points strictly between 5 % and 95 % delivery, or does not
reach 90 %, the grid for that system is extended once (same seed, same step) and both
runs are reported.

## Hypotheses (primary)

Delta = SNR_2500 at 90 % delivery of the reference system minus that of the WAPR system
(positive = WAPR needs less signal).

| Id | WAPR system | Reference | Condition | Why |
|---|---|---|---|---|
| H1 | F600_ldpc_r23 | afsk1200_il2p | awgn | best legacy FEC mode at 1200 bd; airtimes within 10 % (0.51 vs 0.47 s) |
| H2 | H150_ldpc | H150_conv | itu_mod | code selection: LDPC vs convolutional baseline on HF fading |
| H3 | H150_ldpc | afsk300_il2p | itu_mod | HF packet with FEC, the closest legacy HF mode |

Decision rule, for each hypothesis separately:

* **Supported**: point estimate of Delta >= 1.0 dB and the 95 % bootstrap interval of
  Delta lies above 0.
* **Strongly supported**: the lower end of the 95 % interval is >= 1.0 dB.
* **Not supported**: anything else; the measured Delta and interval are reported.
* If the reference never reaches 90 % delivery on the grid, Delta is reported as a lower
  bound (grid maximum minus the WAPR threshold) and the hypothesis is judged on that
  bound, stating that the reference has an error floor.

Three hypotheses are tested; no correction for multiplicity is applied to the decision
rule, but all three are reported whatever the outcome.

## Secondary (descriptive, no decision rule)

* Eb/N0 per payload bit at 90 %, and airtime, for every system.  A lower SNR threshold
  with a longer airtime is **not** an improvement in channel capacity.
* F600 LDPC vs convolutional (r = 1/2) on awgn; R25 LDPC vs convolutional on itu_mod.
* Offset run: threshold change from itu_mod to itu_mod_off.
* False accepts on all runs; sync candidates and false decodes on pure noise
  (upper bound by the rule of three when zero).
* CPU time per frame (Python simulator: relative, not representative of C).

## Known limitations declared in advance

* Simulation only: audio-domain AWGN does not model an FM receiver near threshold, a
  real SSB filter, AGC, or transmitter non-linearity.
* One frame per buffer with no other signals: collisions and interference are not
  part of stage 1.
* The WAPR receiver knows the frame length (single payload size); signalling the size is
  a stage 2 design item and may cost sensitivity.
* Legacy systems are decoded with `atest` defaults (`-F0`, default demodulator profile).
