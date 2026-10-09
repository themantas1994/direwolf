# Raw results

Produced by `test/benchmark/rx_sensitivity.py` (column meanings in
`test/benchmark/README.md`).  `*_summary.csv` files hold per condition totals and
Eb/N0 thresholds; the others have one row per Eb/N0 point.  Rows carry SHA-256
prefixes of the binaries and WAV files and the seed, so any row can be regenerated.

## Final configuration (what is committed)

| file | content |
|---|---|
| `final_1200_all*.csv` | 1200 bps, 13 impairments, A+ and A, 200 frames/point, Eb/N0 4 - 20 dB, seed 42, plus 1 hour of noise |
| `final_mode_{300,2400,4800,9600}*.csv` | other modems, AWGN, 200 frames/point, seed 1 |
| `final_stress_1200.csv` | 1200 A+, Eb/N0 7 and 8.5 dB, 2000 frames each, seeds 101 - 105 |
| `cd_tracks.txt` | WA8LMF TNC Test CD tracks 1 - 4: frames decoded, gained and lost |
| `cpu_track1.txt` | CPU seconds for CD track 1, 3 runs each |
| `bad_frames.txt` | every corrupted frame accepted in the final runs, with what was sent |

Labels: `base` = original code (commit `eda1383`, upstream 1.8.2); `new` = final code
with defaults (`SOFT_FIX 1`); `new_S2` = final code with `SOFT_FIX 2` (`atest -S2`).

Since round 3 the default is `SOFT_FIX 0` (repair off, like upstream); `new` above is the
repair at level 1, now enabled with `SOFT_FIX 1` / `atest -S1`.  Round 3's raw results are in
`round3/` (see `../UPSTREAM_COMPATIBILITY_AND_REGRESSION_AUDIT.md`).

## Development history (superseded settings, kept as evidence)

| file | content |
|---|---|
| `early_final_1200_all*.csv`, `early_mode_*.csv` | same tests as above for the first version (16 single bits + pairs from 12, on every slicer; label `new`).  `new_S0` (repair off) matches `base` frame for frame in every row: the demodulator changes are transparent. |
| `rate96k*.csv` | 96 kHz: `new_S0` shows the automatic decimation alone; `new` is the first version of the repair |
| `stress_1200_lowsnr.csv` | 60,000 frames at Eb/N0 7 and 8.5 dB, seeds 101 - 115; first version (`k16p12q30`), larger budget (`k24p12q30`) and brute force `F1` |
| `stress_{2400,4800}_lowsnr.csv` | PSK at low SNR, first version at levels 1 and 2 |
| `policy_levels.csv` | levels 1 and 2 of the first version under flat, flutter and impulse noise |
| `policy_budget.csv` | 4, 8, 16 single bits, 8 + pairs of 5, 16 + pairs of 12 (every slicer) |
| `policy_best_slicer.csv` | repair on every slicer (`all_*`) vs. only the best matched one (`gate_*`) |
| `policy_slicer_spread.csv` | best slicer only (`sp0`), best +-1 (`sp1`), every slicer (`sp9`) |
| `diag_pll_tuning.csv` | PLL inertia via `TUNE_PLL_*` (no worthwhile gain) |
| `diag_filter_tuning.csv` | RRC width/roll-off and prefilter width via `TUNE_*` (already optimal) |
| `diag_fix_bits_oracle.csv` | brute force `-F1/-F2/-F4`: upper bound and its false accepts |
| `diag_soft_fix_budget.csv` | first soft repair prototype sweep |

Prototype labels `kN` / `kNpMqQ`: N single bit candidates, pairs from M, maximum
confidence Q, repair on every slicer.
