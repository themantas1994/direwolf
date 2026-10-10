# WAPR stage 2 report: minimal modem in C

Task: one transmit / receive profile in C with framing, synchronisation, FEC, CRC and
deterministic loopback tests, bit exact with the stage 1 reference.  H150 is the
primary profile; F600 and R25 share every line of code except their parameters and are
tested the same way.  **Not linked into `direwolf`, `atest` or `gen_packets`** (stage 3).

## 1. Changes made

| File | What |
|---|---|
| `src/wapr.h` | API: frame struct, pack / unpack, profiles, encode / decode, modulate / receive |
| `src/wapr_codec.c` | Header v0, CRC-24, whitening, IRA LDPC encoder and sum-product decoder, interleaver, Gray mapping, Costas layout |
| `src/wapr_modem.c` | GFSK modulator (any sample rate); receiver: baseband + decimation, coarse Costas search, whole frame fine search, log-I0 soft bits, decode |
| `src/wapr_tables.h` | Generated from the reference: the evaluated LDPC code and interleaver |
| `src/wapr_test.c` | `waprtest` (ctest) and measurement / replay modes |
| `test/CMakeLists.txt` | Builds `waprtest`, registers it with ctest |
| `test/wapr/wapr_frame.py`, `wapr_export.py`, `wapr_crosscheck.py`, `golden/` | Frame format reference, table and vector export, C vs Python on identical audio |
| `doc/wapr/FRAME_FORMAT_V0.md` | Versioned frame format |

The C receiver differs from the Python reference in one deliberate way: it mixes to
complex baseband and low-pass filters / decimates before correlating, so the work does
not grow with the sound card rate (and, as it turned out, it is more sensitive, below).
The decoder takes milliseconds to a few hundred milliseconds and keeps no state; stage 3
must call it outside the audio thread.

## 2. Tests executed

| Test | Result |
|---|---|
| `ctest` Release, `-DCMAKE_C_FLAGS=-Werror` (gcc 13) | **29 / 29** pass (28 existing + `waprtest`) |
| `ctest` Debug, CI flags (`-Werror` for C++ only) | 29 / 29 pass.  With `-Werror` for C as well the Debug build stops in `aprs_tt.c` (existing code, `-Wformat-truncation`), not in WAPR code |
| `waprtest` under AddressSanitizer + UBSan | pass, no reports |
| WAPR sources with clang `-Wall -Wextra -Wshadow`; gcc with `_FORTIFY_SOURCE=2` at -O0 ... -O3 | no warnings |
| Golden vectors: 8 frames (pack, unpack, tone sequence, decode) | bit exact with Python |
| Malformed headers: version, type, reserved flag, length, padding, broadcast source, address range; bad callsigns, SSID, length, sequence, type on the send side | all rejected with the expected error |
| 200 000 random information blocks through `wapr_frame_unpack` | no crash; 27 203 accepted, every one re-packs to identical bytes |
| 2000 blocks of pure noise LLRs through `wapr_decode` | 0 accepted |
| LDPC alone, BPSK Eb/N0 3 dB | 0 / 200 frame errors |
| Noise free loopback, F600 / H150 / R25 at 12 000, 44 100 (fractional samples per symbol for F600) and 48 000 Hz, random start and offset up to 90 % of the search range | all delivered, none wrong |
| H150, SNR_2500 -3 dB, 12 000 Hz | 20 / 20 |
| Pure noise, 60 s | 40 candidates decoded, 0 frames |
| 35 % of tones replaced at random | 0 / 20 delivered, 0 wrong |

## 3. C vs Python on identical audio (`wapr_crosscheck.py`, seed 4242)

| System | Channel | SNR_2500 | Python | C | Only Python | Only C |
|---|---|---|---|---|---|---|
| H150 | awgn | -6.75 | 77 / 200 | **151** | 1 | 75 |
| H150 | awgn | -6.25 | 173 / 200 | **198** | 0 | 25 |
| H150 | itu_mod | -2.00 | 149 / 200 | 154 | 0 | 5 |
| H150 | itu_mod | 0.00 | 184 / 200 | 188 | 0 | 4 |
| F600 | awgn | -0.50 | 117 / 200 | **163** | 4 | 50 |
| F600 | awgn | -0.16 | 177 / 200 | 189 | 2 | 14 |
| R25 | awgn | -13.60 | 76 / 100 | 87 | 1 | 12 |
| R25 | awgn | -13.35 | 93 / 100 | 95 | 1 | 3 |

C never delivered a wrong frame.  The C receiver is at least as sensitive everywhere and
clearly better in AWGN; the cause is the band-limiting front end (stage 1 report,
section 6.4), not a different code or decoder (on identical LLRs the two LDPC decoders
agreed on 100 / 100 frames).  So the stage 1 thresholds are conservative for this
implementation.  This was **not** pre-registered and is not a stage 1 result.

## 4. CPU and memory (C, `waprtest -m`, 48 kHz, x86-64, one core)

| Profile | Frame airtime | Audio searched per frame | CPU per frame | Peak RSS |
|---|---|---|---|---|
| F600 | 0.667 s | about 1.25 s | 23 ms | 11 MB |
| H150 | 2.667 s | about 5.0 s | 33 ms | 11 MB |
| R25 | 11.96 s | about 22 s | 120 ms | 19 MB |

Per buffer, a few percent of real time.  Real-time use in stage 3 will search overlapping
windows, multiplying this by the overlap factor.

## 5. Unresolved risks

* No integration yet: buffering, overlap and duplicate suppression across windows, DCD,
  PTT and TXDELAY handling are stage 3.
* Single payload size (32 bytes); a shorter payload still costs a full frame.
* `amp_window = 8` was kept to stay identical to the evaluated reference, although it
  costs sensitivity in AWGN (stage 1 report, 6.5).
* Windows and macOS builds not tried here; code is C99 + libm, no threads, no new
  dependencies.

## 6. Acceptance

| Criterion | Status |
|---|---|
| Bit exact with the reference (golden vectors) | pass |
| Malformed / excessive / corrupted input rejected, no false accepts | pass |
| Deterministic loopback tests in ctest | pass |
| C sensitivity not worse than the reference on identical audio | pass (better) |
| No regression: existing ctest suite, `-Werror` release build | pass |
| CPU and memory measured | pass |

**Stage 2 passes.**

## 7. Smallest next step

Stage 3: an opt-in `MODEM WAPR <profile>` channel type, a receive buffer with a decoder
thread (never in the audio thread), WAPR transmit through the existing transmit queue,
`atest` / `gen_packets` support for offline tests, and the legacy regression suite.
