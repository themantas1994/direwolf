# Round 3 raw results

Evidence for `../../UPSTREAM_COMPATIBILITY_AND_REGRESSION_AUDIT.md`.  Exact command lines:
`commands.txt`.  Builds: fork = this branch (source identical to the PR head), upstream =
wb2osz/direwolf `master` 1.8.2 (`eda1383`), upstream-dev = `dev` (`f1f9d09`).

| File | Content | Report section |
|---|---|---|
| `ax25golden.txt` | 749 AX.25 vectors against the fork's and upstream's code | 4.1 |
| `aprs_decode.csv`, `aprs_decode_log.txt` | 12000 APRS packets: differences by category, aprslib comparison, valgrind | 4.2 |
| `valgrind_decode_aprs_{upstream,dev}.txt` | valgrind summary for upstream's `decode_aprs` on the same packets | 4.2 |
| `interop.csv`, `interop_log.txt` | 436 modem checks: identical TX audio, cross decoding, independent modulator, multimon-ng, Morse, noise | 5.1 |
| `interfaces.csv`, `interfaces_log.txt`, `interfaces_beacon_rerun.csv` | Real `direwolf` processes: KISS / AGW receive, KISS TCP / AGW / pty / serial transmit, FEC fallback, digipeater, beacons, IGate, configuration files, connected mode | 5.2 - 5.5 |
| `stall.csv`, `stall_log.txt` | A client that stops reading (KISS TCP and AGW) | 5.6 |
| `udp.txt` | Audio over UDP, decode latency | 5.6, 6.6 |
| `sens_*.csv`, `sens_*_summary.csv` | Sensitivity sweeps (one row per Eb/N0 point; summaries with thresholds) | 6.2 - 6.4 |
| `sens_192k_7db*.csv`, `bad_192k_7db.txt` | The one corrupted frame, reproduced alone | 6.5 |
| `noise_{1200,9600}*.csv` | One hour of pure noise per build and profile | 6.5 |
| `noise_fa/` | Pure noise at 96 / 192 kHz and low Eb/N0 stress at 48 / 96 / 192 kHz; `noise_fa.sh` | 6.5 |
| `cpu_mem.csv` | CPU seconds and peak memory of atest, 3 runs each | 6.6 |
| `build_matrix.txt`, `build_matrix_addendum.txt`, `buildmatrix.sh`, `toolchain-*.cmake` | Debug / Release / clang / ASan + UBSan / MinGW + wine / aarch64 + qemu builds and tests | 8.1 |
| `ctest_fork_release.txt` | ctest of the Release build | 8.1 |
| `cppcheck_{fork,upstream}.txt`, `cppcheck_files.txt` | cppcheck on the files the fork changes, both trees | 8.2 |
| `asan_leaks.txt` | LeakSanitizer totals, fork and upstream | 8.3 |
| `fuzz/` | Fuzzing runs: progress, coverage, crashes (none) | 8.4 |

`--dump-bad` files exist only where a corrupted frame was accepted: of all the sweeps, only
`bad_192k_7db.txt` (and those under `noise_fa/`, if any).

The scripts `buildmatrix.sh` and `noise_fa.sh` are kept as they were run; edit the paths at
the top to rerun them.  `udp.txt` and `cpu_mem.csv` come from the committed tools
`test/compat/udp_audio.py` and `test/benchmark/cpu_mem.py`.  The `.log` files of the runs are
stored as `*_log.txt` because the repository ignores `*.log`.
