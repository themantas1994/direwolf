# WAPR stage 1 report: simulation and baseline comparison

Plan: `STAGE1_PREREGISTRATION.md` (committed in `6f3efe5` before the evaluation run).
Raw results: `results/stage1/` (per point CSV, threshold summaries, bootstrap replicates,
pure noise runs).  Everything below comes from the held-out seed 20261010, configuration
SHA-256 prefix `e65cc3659983`, simulator and binaries at `6f3efe5`.

All of this is **simulation**: audio-domain AWGN and Watterson fading, one frame per
buffer, no interference, no real radios.  Nothing here shows on-air performance.

## 1. Changes made

* `test/wapr/`: Python + numpy simulator (codes, GFSK M-FSK modem with Costas sync,
  channel models, benchmark, self tests, comparison script, evaluation script).
* `doc/wapr/`: stage 0 report, pre-registration, this report, results.
* No Dire Wolf source file changed in stage 1.

## 2. Tests executed

| Test | Result |
|---|---|
| `wapr_selftest.py` (codes, CRC, SNR calibration, fading statistics, SER vs theory, loopback of all 6 candidates with random start and offset, determinism) | 21 / 21 pass |
| Pre-registered evaluation, `run_stage1_eval.sh` (10 runs, 94 200 WAPR frames + 30 000 legacy frames) | completed, exit 0, 47 min of wall time on 4 cores |
| Dire Wolf `ctest` (unchanged code) | 28 / 28 pass (stage 0 baseline) |

## 3. Results: SNR_2500 for 90 % delivery

95 % parametric bootstrap intervals; 32 byte payload; airtime excludes TXDELAY.

| System | Condition | SNR_2500 (dB) | Eb/N0 per payload bit (dB) | Airtime (s) | False accepts |
|---|---|---|---|---|---|
| F600_ldpc (4-GFSK 600 Bd, LDPC r 1/2) | awgn | -0.16 [-0.20, -0.12] | 7.98 | 0.667 | 0 / 5100 |
| F600_ldpc_r23 (LDPC r 2/3) | awgn | 1.05 [1.01, 1.09] | 8.03 | 0.510 | 0 / 5100 |
| F600_conv (K=7 r 1/2) | awgn | 0.13 [0.08, 0.18] | 8.33 | 0.677 | 0 / 5100 |
| afsk1200 (AX.25) | awgn | 7.98 [7.88, 8.08] | 13.63 | 0.376 | 0 / 5100 |
| afsk1200_fx25 | awgn | 5.97 [5.88, 6.06] | 13.75 | 0.614 | 0 / 5100 |
| afsk1200_il2p | awgn | 6.01 [5.91, 6.10] | 12.66 | 0.474 | **6 / 5100** |
| H150_ldpc (4-GFSK 150 Bd) | awgn | -6.24 [-6.28, -6.20] | 7.92 | 2.667 | 0 |
| H150_conv | awgn | -5.93 [-5.98, -5.87] | 8.30 | 2.707 | 0 |
| afsk300 (AX.25) | awgn | 1.60 [1.50, 1.69] | 13.27 | 1.505 | 0 |
| afsk300_il2p | awgn | 0.25 [0.16, 0.34] | 12.93 | 1.897 | **4** |
| H150_ldpc | itu_mod | -0.56 [-0.81, -0.32] | 13.59 | 2.667 | 0 |
| H150_conv | itu_mod | -0.35 [-0.58, -0.11] | 13.87 | 2.707 | 0 |
| H150_ldpc | itu_mod_off (+-25 Hz) | -0.89 [-1.12, -0.64] | 13.27 | 2.667 | 0 |
| afsk300 | itu_mod | **not reached** (best 71 % at 30 dB) | - | 1.505 | 0 |
| afsk300_il2p | itu_mod | **not reached** (best 81 % at 26 dB) | - | 1.897 | **1** |
| R25_ldpc (8-GFSK 25 Bd) | awgn | -13.35 [-13.40, -13.30] | 7.32 | 11.96 | 0 |
| R25_conv | awgn | -13.10 [-13.15, -13.04] | 7.63 | 12.12 | 0 |
| R25_ldpc | itu_mod | -9.53 [-9.74, -9.32] | 11.14 | 11.96 | 0 |
| R25_conv | itu_mod | -9.22 [-9.42, -9.02] | 11.51 | 12.12 | 0 |
| R25_ldpc | itu_mod_off | -9.46 [-9.68, -9.25] | 11.21 | 11.96 | 0 |

Pure noise, 600 s per WAPR system: 0 false decodes for every system (rule of three:
fewer than 0.005 per second at 95 % confidence).  Sync candidates passed to the
decoder: about 2 per second for F600, 0.5 for H150, 0.1 for R25.

## 4. Hypotheses (decision rule as pre-registered)

| Id | Comparison | Delta (dB) [95 %] | Verdict |
|---|---|---|---|
| H1 | F600_ldpc_r23 vs afsk1200_il2p, awgn, airtimes 0.51 / 0.47 s | **4.96 [4.85, 5.05]** | strongly supported |
| H2 | H150_ldpc vs H150_conv, itu_mod | 0.21 [-0.11, 0.54] | **not supported** |
| H3 | H150_ldpc vs afsk300_il2p, itu_mod | > 30 (lower bound) | supported by the rule, see below |

H3 needs care: the legacy HF modes never reach 90 % delivery under Watterson "moderate"
fading at any SNR tested (error floor near 70-80 %).  The "> 30 dB" is a statement about
that floor, not a 30 dB gain.  Under AWGN the same comparison gives 6.49 dB
[6.39, 6.58], at 1.4 times the airtime of afsk300_il2p; per payload bit 5.0 dB.

Secondary comparisons (no decision rule): LDPC beats the convolutional code by
0.29 dB (F600, awgn), 0.31 dB (H150, awgn), 0.25 dB (R25, awgn), 0.31 dB (R25, itu_mod).
Real but small: the convolutional baseline is a reasonable fallback.  A frequency offset
of up to +-25 Hz did not measurably change either HF threshold.

## 5. Interpretation

* At **equal airtime** on VHF (H1), the FEC + coherent use of the whole frame energy
  gives about 5 dB over AFSK 1200 with IL2P, in simulation.  Per payload bit it is
  4.6 dB.  The likely sources: 4-FSK non-coherent detection (vs AFSK through a
  discriminator-like demodulator), soft decision decoding, and no HDLC/NRZI overhead.
* On HF fading the gain is mostly the elimination of the error floor (interleaving
  over a 2.7 s frame plus FEC), not sensitivity.
* LDPC vs convolutional is a 0.2-0.3 dB question at this block length.  It does not
  justify itself by sensitivity alone; it was kept for stage 2 because it also has a
  decoder-side integrity check (a zero syndrome) and decoded faster in the simulator.
* The R25 profile is about 7 dB more sensitive than H150 at 4.5 times the airtime; a
  12 s frame is long for packet traffic.

## 6. Negative results, deviations and problems found

1. **H2 failed.**  LDPC is not 1 dB better than the convolutional baseline.
2. **IL2P false accepts.**  Legacy IL2P accepted 11 corrupted frames in 15 000
   (`afsk1200_il2p` 6, `afsk300_il2p` 5), plain AX.25 none.  This is a property of the
   existing mode; worth reporting separately.
3. **Threshold fitting bug, fixed after the run.**  The slope cap for perfectly separated
   data fired on intermediate Newton steps, giving nonsense on the shallow fading curves
   (e.g. H150 itu_mod 50 % and 90 % points 0.04 dB apart).  The estimator was corrected
   (step halving; the cap only for truly separated data) and **all summaries were
   recomputed from the unchanged per point data** (`wapr_bench.py --summarize`).  The
   analysis plan did not change; `results/stage1/eval_run.log` still shows the pre-fix
   numbers for the runs affected.
4. **The Python receiver is pessimistic by about 0.3-0.5 dB.**  Found while building the
   C receiver (stage 2): correlating unfiltered audio lets wideband noise in through the
   sidelobes of the rectangular correlators.  On identical audio a band limited front
   end delivered 83 / 100 frames where the reference delivered 53 / 100 (H150, -6.75 dB,
   known timing).  The pre-registered numbers above are from the unmodified reference.
5. **Amplitude tracking window (8 symbols) costs sensitivity in AWGN.**  Chosen on weak
   development evidence (fading only); with known timing at H150 -6.75 dB, window 8
   delivered 53 / 100 and no window 63 / 100.  To be re-evaluated, not changed silently.
6. CPU figures are for the Python simulator, partly measured while other jobs ran.
   They rank the systems only roughly; stage 2 measures the C implementation.

## 7. Acceptance

| Criterion | Status |
|---|---|
| Noise-free loopback 100 % for every candidate | pass |
| Channel and SNR calibration against theory | pass (SER within 3 %, SNR within 0.05 dB) |
| Legacy AFSK, FX.25, IL2P under identical channel and SNR definition | pass |
| Reproducible (fixed seeds, frozen config, determinism test) | pass |
| Pre-registration before the evaluation | pass |
| Intervals and negative results reported | pass |

**Stage 1 passes its acceptance criteria.**  The 1 dB research target is met in one
pre-registered condition (H1, VHF AWGN) and not met for the code comparison (H2).

## 8. Smallest next step

Stage 2: one C profile, framing, sync, FEC, CRC and deterministic loopback tests,
bit exact with this reference.  Profile choice: **H150** (HF; the condition where legacy
modes fail outright), with F600 and R25 sharing the same code because only the
waveform parameters differ.
