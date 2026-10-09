# Round 2 raw results

Produced with `test/benchmark/rx_sensitivity.py`; "old" = upstream 1.8.2 (`eda1383`),
"pr1" = `301de93`, "new" = this branch.  Each `*_summary.csv` has the thresholds; the
plain `.csv` has one row per Eb/N0 point and build.  See
`../../UPSTREAM_COMPATIBILITY_AND_OPTIMIZATION_AUDIT.md` for the commands and discussion.

| File | What |
|---|---|
| `final_1200_all*` | 1200 bps, 13 impairments, A+ and A, 200 frames/pt, seed 42 (PR #1's setup) |
| `final_p99_1200*` | 1200 flat, 1000 frames/pt, 0.5 dB steps (99% thresholds) |
| `final_numpy48k*` | independent numpy modulator, 48 kHz |
| `final_multi_AB*` | two demodulators (`-P AB`) |
| `final_mode_{300,2400,4800,9600}*` | other modes, flat and impulse, plus 15 min noise |
| `stress_10[1-5].csv` | Eb/N0 7 and 8.5 dB, 1500 frames/pt, A+, A, AB |
| `noise_1200_summary.csv` | 1 hour of noise, A+, A, AB |
| `policy_*.csv` | repair policy experiments (section 5.1); binary labels are the variants |
| `bad_frames*.txt` | every corrupted frame accepted in these runs, with what was sent |
| `noise_exposure.txt` | repair FCS checks on pure noise, per mode |
| `e2e_forwarding.txt` | end to end REGEN / IGate / digipeater / KISS / AIS crash checks |
| `cpu.txt`, `callgrind.txt` | CPU time and instruction counts |
