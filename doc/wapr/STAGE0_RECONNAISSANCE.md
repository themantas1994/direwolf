# WAPR Stage 0: reconnaissance

WAPR (Weak-signal Adaptive Packet Radio) is an experimental modem research project in
this fork.  This report records the repository state and the integration points found
before any WAPR code was written.  No source files were changed in this stage.

## Repository state (checked 2026-10-10)

| Item | Value | How checked |
|---|---|---|
| Branch | `claude/zen-galileo-nohtc4`, same commit as `master` (`ec0be68`), clean tree | `git status`, `git log` |
| Upstream | `wb2osz/direwolf` `master` = `eda1383` (1.8.2), `dev` = `b631b96` | `git ls-remote` |
| Upstream base | `eda1383` is an ancestor of `HEAD` | `git merge-base --is-ancestor` |
| Fork changes | Documented in `doc/engineering-audit/UPSTREAM_COMPATIBILITY_AND_REGRESSION_AUDIT.md` section 2 (receive improvements, fixes, tests) | read |
| License | GPL v2 or later (source headers) | `LICENSE`, `src/il2p_codec.c` |
| Build | CMake; C99 + pthreads; ALSA/udev on Linux; CI builds Linux, macOS, Windows MinGW with `-Werror -DUNITTEST=1` | `.github/workflows/ci.yml` |

## Baseline tests (this container, Ubuntu, gcc)

    cmake <src> -DUNITTEST=1 -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS=-Werror && make -j4 && ctest -j4

Result: build clean (0 warnings), **28 / 28 ctest tests passed**.  `libasound2-dev` and
`libudev-dev` had to be installed first (environment, not source).  Further regression
tools already in the tree: `test/compat` (upstream comparison, golden AX.25 vectors,
KISS/AGW interface tests), `test/fuzz`, `test/benchmark/rx_sensitivity.py`
(numpy, drives `gen_packets` + `atest`).

## Integration points

| Need | Existing mechanism | Notes for WAPR |
|---|---|---|
| Per-channel modem choice | `enum modem_t` in `src/audio.h`; `MODEM` keyword parsed in `src/config.c` (~line 1454) | Add one value at the **end** of the enum (existing values unchanged) and an explicit `MODEM` option.  A WAPR channel is WAPR only, so legacy channels can never send WAPR. |
| Receive samples | `recv.c` -> `multi_modem_process_sample` -> `demod_process_sample` (`src/demod.c`), one call per sample in the audio thread | WAPR decoding is block based and slow (search + LDPC).  Samples must go into a buffer; decoding runs in a separate thread so the audio thread never blocks. |
| Received frame delivery | `multi_modem_process_rec_frame` / `dlq_rec_frame (chan, subchan, slice, pp, alevel, fec_type, retries, spectrum)` | `fec_type_t` (`src/dlq.h`) is switched on in `direwolf.c` and `atest.c`; a new value needs each switch checked.  `direwolf.c` lines ~1628-1664 decide digipeat / IGate eligibility from `fec_type`. |
| Transmit | `xmit.c` `send_one_frame` -> `layer2_send_frame` (`src/hdlc_send.c`) -> bit level `tone_gen_put_bit`; preamble via `layer2_preamble_postamble` | WAPR produces whole waveforms, so it needs a sample level path (`gen_tone_put_sample`, already public) and its own TXDELAY handling instead of HDLC flags. |
| Channel busy | `dcd_change` / `hdlc_rec_data_detect_any` (`src/hdlc_rec.c`), used by `xmit.c` | WAPR needs its own busy indication (energy or sync detection), reported through the same `dcd_change`. |
| Offline decode / test harness | `atest` (WAV in, frames out) and `gen_packets` (frames in, WAV out) | Both read `modem_type`; WAPR support there gives deterministic loopback tests without audio hardware. |
| Threads | `pthread` on Unix, Win32 threads in `#if __WIN32__` blocks throughout | A decoder thread must follow the same pattern (see `dlq.c`, `kissnet.c`). |

## Risks

1. **Real-time path**: a decoder in the audio thread would cause sample loss for every
   channel on that device.  Mitigation: ring buffer + worker thread; measured decode time.
2. **Licensing**: WSJT-X (FT8/FT4) is GPL v3.  Copying its LDPC matrices or code would
   change this project's licence terms.  WAPR will use its own codes, built by a
   documented deterministic procedure, and cite FT8/FT4 papers for ideas only.
3. **Packet model mismatch**: Dire Wolf's interior is AX.25 `packet_t`.  A compact WAPR
   address format needs explicit translation (gateway, stage 4); until then frames must
   map to `packet_t` without pretending to be AX.25 on air.
4. **Fair comparison**: slower modems trivially need less SNR.  Comparisons must report
   SNR in a fixed reference bandwidth **and** Eb/N0 per information bit **and** airtime,
   with the same channel model applied to legacy audio and WAPR audio.
5. **Existing benchmark gaps**: `rx_sensitivity.py` has no HF fading model and no
   FX.25 / IL2P option, although `gen_packets -X / -I` exist.
6. **Windows / macOS**: cannot be built here; new C code must stay C99 + existing thread
   wrappers so CI catches problems.

## Smallest next step (stage 1)

A self-contained simulator under `test/wapr/` (Python + numpy, the existing benchmark's
only dependency) that:

* generates GFSK M-FSK candidates (one fast, one robust) with sync, CRC, interleaving,
  a convolutional baseline and an LDPC candidate;
* applies one channel model (AWGN, frequency offset, Watterson HF fading) shared with
  legacy `gen_packets` audio decoded by `atest` (AFSK 300 / 1200, FX.25, IL2P);
* reports delivery vs SNR with confidence intervals, false accepts, sync false alarms
  and airtime, from fixed seeds and frozen configuration files.
