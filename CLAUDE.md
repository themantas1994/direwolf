# CLAUDE.md: Dire Wolf fork

Goal of this fork: better receive sensitivity and packet-radio performance **without breaking
compatibility with upstream Dire Wolf**. Every change is judged against both.

## Layout pointers
- Modem and decoder: `src/` (C). Windows GUI: `windows-gui/` (.NET).
- Tests: `test/CMakeLists.txt` (ctest), `test/compat/` (upstream comparison),
  `test/benchmark/` (sensitivity, CPU/memory). Each has a README with exact commands.
- Previous results: `doc/engineering-audit/`, `doc/wapr/`.

## Model routing for this repo
- Opus: demodulator/decoder DSP, HDLC/FX.25/IL2P framing, anything that could change what
  frames are decoded or accepted.
- Sonnet: config, KISS/AGW interfaces, GUI, build system, tests.
- Haiku (`explorer`): code search, reading benchmark CSVs, summarizing build logs.

## Verification ladder
1. Build and unit tests (always):
   `cmake -S . -B build -DUNITTEST=1 && cmake --build build --parallel && (cd build && ctest --output-on-failure)`
2. Decoder or modem changes: sensitivity against the previous build, same seed:
   `python3 test/benchmark/rx_sensitivity.py --atest old=<old-build>/src/atest --atest new=build/src/atest --gen-packets build/src/gen_packets --profiles A+,A --conditions flat,deemph6 --ebn0 6:16:0.5 --frames 300`
   Report both "ok" and "bad" (corrupted frames accepted). A sensitivity gain that raises "bad"
   is a regression. Use 1000+ frames per point for 99% thresholds.
3. Interfaces, framing or protocol changes: the upstream comparison in `test/compat/README.md`
   (`interop.py`, `aprs_decode_compare.py`) against an upstream build.
4. GUI changes: `dotnet test windows-gui/DireWolfGui.slnx -c Release`.

Don't claim a sensitivity improvement without the benchmark output and the seed used.
