# Upstream Compatibility, Functional Verification and Regression Audit

Third audit round of `themantas1994/direwolf`, after PR #1 (receive sensitivity) and
PR #2 (second round).  The question is whether the fork is still a drop-in, interoperable
Dire Wolf: does it decode what standard implementations transmit, do they decode what it
transmits, and do clients, digipeaters, IGates and configuration files behave as with
upstream.  Every claim marked PASS below comes from a test that was run in this round; the
raw results are in `results/round3/`.

> **Draft:** sections marked PENDING are filled in when the final measurement runs complete.

## 1. Reference versions

| What | Commit | Notes |
|---|---|---|
| Upstream default branch `master` | `eda1383f5fa9d8ba3cb27f99db1d2c79494404c9` | "Bump patch version to 2" = 1.8.2 (2026-05-19), not tagged |
| Latest upstream release | tag `1.8.1` = `a231971a652bfb574a4bae9a5d875fbce53d2267` | GitHub release "Version 1.8.1 - November 2025", marked Latest |
| Upstream `dev` (1.9 development) | `f1f9d0987a79ba1a58132ff29b2a094503f5b5f3` | 2026-09-22, 76 commits not in 1.8.2 |
| Fork `master` = PR #2 merge | `6e9fbda262c8370504bad993464459661ae7993e` | PR #2 head `88f11fe`, base `301de93` (PR #1 merge); merged 2026-10-09 19:26 UTC by themantas1994 |
| Merge base fork / upstream `master` | `eda1383` | The fork contains all of upstream 1.8.2 |
| Merge base fork / upstream `dev` | `e494c94` | 1.8 release point |
| This round | branch `ccr-be42e86b-00fj6u`, PR #3 | Commits listed in section 12 |

The compatibility reference is upstream `master` (1.8.2, the newest code on upstream's
default branch, a superset of release 1.8.1).  Upstream `dev` was built and tested as a
second reference for on-air and client compatibility, since that is what the next release
will be.

Commands used to establish this:

    git fetch https://github.com/wb2osz/direwolf.git 'refs/heads/*:refs/remotes/upstream/*' 'refs/tags/*:refs/tags/*'
    git ls-remote --symref https://github.com/wb2osz/direwolf.git HEAD      # -> refs/heads/master
    git merge-base HEAD upstream/master ; git merge-base HEAD upstream/dev
    git log --oneline HEAD..upstream/dev ; git diff --stat upstream/master HEAD -- src/
    # PR #2 merge state via the GitHub API (pull_request_read #2): merged, head 88f11fe

## 2. What the fork changes, compared with upstream 1.8.2

31 files under `src/` (+1438 / -103 lines, including 2 new test programs).  By area:

| Area | Files | Externally observable? |
|---|---|---|
| AFSK demodulator delay lines (memmove -> circular buffers) | `demod_afsk.c`, `fsk_demod_state.h` | No: bit exact (identical decodes in every test) |
| Automatic decimation when the 1200 bd prefilter would not fit (>= 54.9 kHz) | `demod.c` | Yes, receive only, at 64 kHz and above: different (better) filtering |
| Soft decision repair (`SOFT_FIX`, `atest -S`), bit confidence plumbing | `hdlc_rec2.c`, `hdlc_rec.c/h`, `rrbb.h`, `demod_9600.c`, `multi_modem.c/h`, `demod.c/h`, `demod_afsk.c/h`, `config.c`, `audio.h`, `atest.c`, `direwolf.c` | Only when enabled (now off by default, section 9) |
| `REGEN` only for error free or FEC frames | `direwolf.c` | Only with `REGEN` plus `FIX_BITS`/`PASSALL`/`SOFT_FIX`: upstream retransmits repaired and even bad-FCS frames |
| UB fixes (signed shifts) | `demod_9600.h`, `gen_tone.c`, `fx25_rec.c`, `decode_aprs.c`, `ais.c` | No (same results with gcc/clang on x86-64 and aarch64) |
| Received packet robustness (AIS, MIC-E device id, sscanf) | `ais.c`, `deviceid.c`, `decode_aprs.c` | Only for malformed packets: no crash / no garbage values |
| NRZI continuity between AX.25 and FX.25 senders | `gen_tone.c/h`, `hdlc_send.c`, `fx25_send.c` | Yes, transmit with `FX25TX`: removes a bit error upstream makes (section 9) |
| Upstream fixes cherry-picked from `dev` | `server.c` (#641), `decode_aprs.c` (#655, #656, #657), `il2p_test.c` (#576), `morse.c` (#597) | #597: Morse '-' now sent correctly |
| Tests | `test/compat/*`, `test/fuzz/*`, `test/benchmark/*`, new ctests `aistest`, `deviceidtest`, `l2sendtest`, `ax25goldentest` | No |

Not changed by the fork: AX.25 frame building and parsing (`ax25_pad*.c`), FCS, HDLC
encoder, FX.25 and IL2P encoders and decoders, KISS framing, AGW protocol (except #641),
transmit queue and timing, PTT, digipeater, IGate, beacons, APRStt, connected mode link layer,
configuration keywords except the new `SOFT_FIX`.

## 3. Feature verification matrix

PASS = verified by a test run in this round.  PARTIAL = some scenarios verified, coverage
incomplete.  NOT TESTED = no executed test.  FAIL = confirmed defect still present.
"vs upstream" means compared with upstream 1.8.2 (and dev where noted) on identical input.

### 3.1 AX.25 and framing

| Feature | Status | Evidence |
|---|---|---|
| UI frame encoding from monitor text (addresses, SSIDs 0-15, 0-8 digipeaters, H bits, C bits) | PASS | `ax25goldentest`: 103 corpus frames bit exact against the independent encoder; upstream 1.8.2 and dev pass the same vectors |
| Frame decoding: addresses, info, control, PID | PASS | `ax25goldentest` (decode direction) |
| Frame types I, RR, RNR, REJ, SREJ, SABM(E), DISC, DM, UA, FRMR, UI, XID, TEST; C/R, P/F, N(R), N(S), modulo 8 and 128 | PASS | `ax25goldentest`: 636 vectors from the AX.25 v2.2 bit layouts |
| FCS (CRC-16/X.25) generation and check | PASS | Check value 0x906E; FCS of every vector; every frame decoded over the air has a good FCS by construction |
| Invalid frames: length < 15 or > 2123, bad address field, no end bit, > 10 addresses | PASS | `ax25goldentest` L vectors (same result in upstream) |
| HDLC bit stuffing, flags, NRZI; binary payloads (all 256 byte values, 0x7E/0x7D/0xC0/0xDB, runs of ones) | PASS | Corpus and raw frames through every modem, decoded bit exact by every build and by multimon-ng; independent modulator decoded by every build |
| Maximum frame size | PASS | 2064 byte frame (2048 byte info) accepted, 2124 byte frame rejected (vectors); 1132 byte frame transmitted and decoded end to end; 512 byte frames over KISS |
| Truncated / corrupted / malformed received frames | PASS | Fuzzing (section 8.4); FCS rejects; sanity tests |
| Connected mode (link layer), v2.2 XID negotiation, segmentation | PASS | Fork <-> upstream 1.8.2 and fork <-> dev, each side calling, data both ways incl. all byte values and > PACLEN blocks (section 5.4) |

### 3.2 APRS

| Feature | Status | Evidence |
|---|---|---|
| Decoding of every APRS data type (positions, compressed, Mic-E, objects, items, messages, acks/rejs, bulletins, NWS, status, capabilities, queries, weather, telemetry and PARM/UNIT/EQNS/BITS, third party, user defined, NMEA, Maidenhead) | PASS | 12000 packets: fork = upstream 1.8.2 except 77 packets explained by intended fixes (section 4.2) |
| Interpretation of positions | PASS | aprslib agrees with the fork on all 3225 comparable positions; 496 ambiguity-convention differences (same in upstream) |
| Malformed APRS payloads | PASS | 12000 truncated/substituted packets: no crash in any build; valgrind 0 errors for the fork (858 for upstream 1.8.2) |
| Device identification (tocalls, Mic-E suffixes) | PASS | `deviceidtest`; decode comparison |
| AIS user data | PARTIAL | `aistest`, fuzzing, decode comparison; no AIS RF signal test |
| Digipeater: WIDEn-N, aliases, TRACE, preemption of used paths, duplicate suppression | PASS | 13 input frames -> identical 12 digipeated frames in fork, upstream and dev (section 5.3) |
| Connected mode digipeater (CDIGIPEAT) | PASS | Same test |
| IGate RF > APRS-IS (q construct, TCPIP/NOGATE/RFONLY rules) | PASS | 41 identical lines uploaded to a local server by all three builds |
| IGate APRS-IS > RF (message to a station heard on RF, third party format) | PASS | Identical frame transmitted by all three builds; message to an unheard station not gated by any |
| Beacons (PBEACON incl. PHG, compressed, frequency/tone, ambiguity; CBEACON) | PASS | The same 5 transmitted beacons in fork, 1.8.2 and dev (version in tocall ignored); OBEACON / TBEACON not tested (section 5.4) |
| Filtering (FILTER, IGFILTER, CFILTER) | PARTIAL | Parsed identically (config test); filter behaviour covered by upstream's `pftest` (passes); no end to end filter test |
| APRStt | PARTIAL | `ttest`, `tttexttest`, `dtmftest`; config parsing; no DTMF audio test |
| REGEN | PARTIAL | Code inspection and round 2 end to end test; not rerun |

### 3.3 Modems (receive and transmit)

| Mode | Status | Evidence |
|---|---|---|
| 1200 AFSK, profiles A, A+, B, AB, E+ | PASS | TX audio identical to upstream; cross decoding 103/103 at 8 - 192 kHz; independent modulator 103/103; multimon-ng 103/103; noisy audio: default fork identical to upstream below 54.9 kHz |
| 1200 AFSK at 64 - 192 kHz (automatic decimation) | PASS | Clean signals 103/103.  Noisy (88.2 / 96 / 192 kHz): more frames than upstream in total and at P90 / P99, but not a superset: 25 - 65 of 3300 frames upstream decodes are lost per run, all near threshold (section 6.3); 1 corrupted frame accepted at 192 kHz (section 6.5) |
| 300 AFSK (A, B) | PASS | Same tests (11 - 96 kHz) |
| 2400 QPSK V.26 A and B (default B) | PASS | TX identical; cross decoding; noisy identical |
| 2400 G3RUH | PASS | TX identical; cross decoding |
| 4800 8PSK | PASS | TX identical; cross decoding; noisy identical |
| 9600 G3RUH (incl. `+` slicers) | PASS | TX identical; cross decoding; independent modulator; multimon-ng FSK9600 |
| 19200 G3RUH | PASS | TX identical; cross decoding at 96/192 kHz |
| FX.25 (16, 32, 64 check bytes; 1200 and 9600) | PASS | Cross decoding 103/103; noisy identical; TX audio differs only by the NRZI fix; fallback frames now decodable |
| IL2P (1200, 9600, inverted polarity, weak FEC) | PASS | TX identical; cross decoding (IL2P type 1 header C bit rule modelled) |
| IL2P fallback for frames > 1023 bytes | PARTIAL | 1200: PASS.  9600: FAIL in fork and upstream alike (defect 9.8) |
| EAS SAME | PASS | TX identical; fork and upstream decode 6/6 identically |
| AIS (receive only) | NOT TESTED | No AIS signal generator (parser tested, see APRS) |
| Morse code ID | PASS | multimon-ng MORSE_CW decodes the fork's CW correctly (upstream 1.8.2: `-` as `=`) |
| Stereo (two channels) | PASS | `gen_packets -2`: identical TX; 140 frames, identical decode output |
| Soft decision repair (opt in) | PASS | Superset of upstream in every noisy test; every extra frame marked; false accepts: PENDING-SOFTFIX |

### 3.4 Interfaces and configuration

| Feature | Status | Evidence |
|---|---|---|
| KISS TCP receive | PASS | Identical frames to upstream in 11 receive configurations |
| KISS TCP transmit (incl. all frame types, escapes) | PASS | 127 frames, every modem, decoded bit exact by every build |
| AGW raw monitoring (`k`) and raw send (`K`) | PASS | AGW output identical to KISS output and to upstream; 127/127 transmitted |
| AGW connected mode (`C`, `D`, `d`, `X`) | PASS | Section 5.4 |
| KISS pseudo terminal (`-p`, /tmp/kisstnc) | PASS | 127/127 transmitted |
| Serial KISS (SERIALKISS, socat pty pair) | PASS | 127/127 transmitted |
| Audio from stdin | PASS | All receive interface tests |
| Audio from UDP (SDR) | PASS | Real time UDP datagrams: same 75 frames as stdin, fork = upstream (section 5.6) |
| Audio output | PARTIAL | ALSA `file` plugin; no sound card |
| Configuration files (generic sample, sdr.conf, many-directive config) | PASS | Startup output identical to upstream and dev apart from version strings |
| New keyword `SOFT_FIX` | PASS | Parsed; invalid value reported; upstream configs need no change |
| Command line | PARTIAL | `-t`, `-c`, `-p` used; other options not compared |
| A client that stops reading | FAIL | Same as upstream 1.8.2: 53 / 4000 (AGW), 60 / 4000 (KISS) frames reach the other client; dev fixes AGW only (defect 9.7, section 5.6) |
| PTT / DCD / TXINH, GPS, rig control, CM108, GPIO | NOT TESTED | No hardware |
| Multi-channel (ACHANNELS 2) | PARTIAL | Stereo decode (atest); config parsing; not with direwolf end to end |

### 3.5 Platforms and builds

| Platform | Status | Evidence |
|---|---|---|
| Linux x86-64, gcc 13.3 Release / Debug | PASS | ctest 28/28; Release 0 warnings (section 8.1) |
| Linux x86-64, clang 18 | PASS | ctest 28/28; same 54 warnings as upstream 1.8.2 with clang, none in fork code |
| ASan + UBSan | PASS | 28/28 with leak detection off; 0 memory errors, 0 UB reports (leak reports: upstream code, section 8.3) |
| Windows x86-64 (MinGW cross) | PARTIAL | Builds with 0 warnings; unit test programs 18/18 and modem script lines 71/71 under wine; TX audio identical to Linux (7 modes); direwolf.exe not run |
| Linux aarch64 (cross, qemu-user) | PARTIAL | Builds with 0 warnings; unit test programs 18/18 and modem script lines 24/24 under qemu; TX audio identical to x86 (4 modes); not on real hardware |
| macOS, 32 bit ARM, real Raspberry Pi | NOT TESTED | Not available |

## 4. AX.25 and APRS compatibility results

### 4.1 AX.25 frames

Reference: `test/compat/ax25ref.py`, an encoder/decoder written for this audit from the
AX.25 v2.2 specification (address encoding, SSID byte, C/H bits, control field layouts for
modulo 8 and 128, CRC-16/X.25), not derived from Dire Wolf's code.  `make_golden.py` writes
749 vectors to `test/compat/golden/ax25_vectors.txt`, checked by the new ctest
`ax25goldentest` (`src/ax25golden_test.c`):

| Vectors | Content | Fork | Upstream 1.8.2 code | Upstream dev code |
|---|---|---|---|---|
| 103 `T` | Every corpus packet (all APRS types, 0 - 8 digipeaters, SSIDs 0 - 15, H bits, all 256 byte values, 0x7E / 0x7D / 0xC0 / 0xDB, 256 byte info): monitor text -> exact frame bytes, FCS, and back to the same addresses and info | 103 / 103 | 103 / 103 | 103 / 103 |
| 636 `R` | I, RR, RNR, REJ, SREJ, SABM, SABME, DISC, DM, UA, FRMR, UI, XID, TEST with every combination of C/R bits, P/F, N(R), N(S), modulo 8 and 128: frame type, C/R, P/F, N(R), N(S); frame kept intact | 636 / 636 | 636 / 636 | 636 / 636 |
| 10 `L` | 1 and 14 byte frames, 2124 byte frame (rejected); 15 byte frame and 2048 byte info (accepted); end bit on the destination, 11 addresses, bit 0 set in a callsign octet, no end bit (invalid address field); 10 addresses (accepted) | 10 / 10 | 10 / 10 | 10 / 10 |

The same test program was compiled against upstream's `ax25_pad.c` and `fcs_calc.c` (1.8.2
and dev) to show the vectors describe upstream's behaviour, not the fork's.  A copy of
the vectors with two lines deliberately altered (one bit of a destination address, one C/R
bit) fails with "6 errors in 749 vectors": the test detects what it should.  Raw results:
`results/round3/ax25golden.txt`.

On the air, every corpus frame plus 24 raw frames (all frame types, binary info) were
sent through every modem by each build and by an independent modulator, and decoded bit
exact (section 5.1); the FCS of each received frame is checked by construction.

### 4.2 APRS decoding

`test/compat/aprs_decode_compare.py` runs `decode_aprs` from each build on 12000 packets:
the 123 line corpus (`aprs_ax25_corpus.txt`: positions with and without timestamp,
compressed, Mic-E incl. telemetry and device suffixes, objects, items, messages, acks,
rejects, bulletins, NWS, status, capabilities, queries, weather (positionless, with
position, Peet / Ultimeter raw), telemetry with PARM/UNIT/EQNS/BITS, third party, user
defined, raw NMEA, Maidenhead, DF / PHG / RNG / DFS extensions), every truncation of each
information field, random single character substitutions, and a regression list of the
packets behind the defects in section 9.  Output is compared line by line.

| Comparison | Identical | Differences, all explained |
|---|---|---|
| fork vs upstream 1.8.2 | 11923 / 12000 | 47 invalid PHG/DFS height shown without a height instead of -999999 (9.4); 8 uninitialized course/speed/range no longer shown (9.3); 21 debug output removed (#656); 1 "100%" no longer read as a CTCSS tone (#657) |
| fork vs upstream dev | 10636 / 12000 | 1315 from dev changes not in 1.8.2 (more weather fields, symbol names, tocalls, display); 47 PHG height (9.4); 2 uninitialized values (9.3; dev still has the defect) |

No build crashed on any of the 12000 packets (each packet is run separately on a crash, so
one crash can't hide others).  valgrind on all 12000: **fork 0 errors**; upstream 1.8.2 858
errors in 118 contexts; upstream dev 776 in 116 (uses of uninitialized values in the code
fixed in 9.3 and in round 2's AIS / Mic-E fixes).

Independent interpretation: [aprslib](https://github.com/rossengeorgiev/aprs-python)
(Python APRS parser) decoded the same packets; for every packet both decode as a position,
latitude and longitude are compared (to 0.0001 degree).  3225 agree; 496 differ only in
the position ambiguity convention (Dire Wolf gives the corner of the ambiguity box,
aprslib its centre; upstream identical to the fork); 0 corpus packets differ; 21 mutated
(invalid) packets are read differently by the two parsers, identically by fork and
upstream.  Raw results: `results/round3/aprs_decode.csv`, `aprs_decode_log.txt`.

## 5. Transmit-to-receive and receive-to-transmit interoperability

### 5.1 Modem audio: fork, upstream, an independent modulator and multimon-ng

`test/compat/interop.py`, final run on the branch head: **436 checks, 0 failed**
(`results/round3/interop.csv`).  Frames: the 103 packet corpus (section 4.1).  "Decoded"
means the received frame is byte identical to the transmitted one (address bits included;
for IL2P, after the C bit normalisation the IL2P type 1 header defines).

| Check | What | Result |
|---|---|---|
| TX identical (51) | `gen_packets` of fork, upstream 1.8.2 and dev on the corpus, md5 of the audio: 300 bd (11 - 96 kHz), 1200 bd A / A+ / B / AB (8 - 192 kHz), 2400 V.26A / V.26B / G3RUH, 4800, 9600 (44.1 - 192 kHz), 19200, IL2P 1200 / 9600 incl. inverted and weak FEC | 46 byte identical; 5 FX.25 cases differ, **only** by the NRZI fix (9.2), labelled EXPECTED-DIFF |
| Cross decoding (244) | Every build's audio decoded by every build (fork, fork with `-S1`, upstream, dev), every mode and rate above, FX.25 16 / 32 / 64 check bytes in all 12 TX x RX combinations | 103 / 103 everywhere |
| Independent modulator (40) | `ax25ref.py` writes AFSK 1200 (Bell 202, phase continuous), AFSK 300 and G3RUH 9600 (NRZI, x^17+x^12+1 scrambler, baseband) audio from the spec, without Dire Wolf code; decoded by every build | 103 / 103 everywhere |
| multimon-ng (14) | Independent decoder (AFSK1200, FSK9600 at 22050 Hz) on each build's audio and on the independent modulator's | 103 / 103 everywhere (multimon-ng's text compared with ax25ref's rendering of each transmitted frame) |
| Morse ID (3) | multimon-ng MORSE_CW on the CW identification | fork and dev correct; upstream 1.8.2 sends `-` as `=` (UPSTREAM-DEFECT, fixed in the fork by #597) |
| Noise (84) | One noisy test file per mode (gen_packets `-n`); the default fork must decode **exactly** the same frames as upstream; `-S1` must be a superset | 1200 / 300 / 2400 / 4800 / 9600 / 19200 / FX.25 / IL2P at 22.05 - 48 kHz: identical.  1200 bd at 96 kHz: fork gains 2 / loses 0 (A+) and gains 3 / loses 1 (A) because of automatic decimation (EXPECTED-DIFF).  `-S1` never misses a frame the default decodes |

Both directions therefore hold for every modem: the fork decodes all frames transmitted
by upstream (1.8.2 and dev) and by an independent modulator, and upstream and multimon-ng
decode all frames the fork transmits.  The FX.25 difference is in the fork's favour: upstream
1.8.2 and dev cannot decode a plain AX.25 frame that follows an FX.25 frame in the same
transmission from their *own* transmitter (9.2).

### 5.2 Receive through the client interfaces

`test/compat/interfaces.py` runs real `direwolf` processes of each build (as an ordinary
user, no sound card: receive audio on stdin, transmit audio through ALSA's `file` plugin)
with a KISS TCP client and an AGW client in raw monitoring mode (`k`) connected
(`results/round3/interfaces.csv`; final run: 208 rows, 0 failures, 19 rows labelled
UPSTREAM-DEFECT, all upstream's own transmissions or the IL2P 9600 case in every build).
Receive audio: `gen_packets -n 100` (100 frames with increasing noise) from upstream.

| Check | Result |
|---|---|
| Same receive audio, default configuration, 11 cases: 1200 A+ (default), E+, B, 1200 at 48 kHz, 300, 2400 V.26B and V.26A, 4800, 9600, 1200 FX.25, 9600 IL2P | **Identical** KISS and AGW output, fork vs 1.8.2 and fork vs dev, in all 11 cases (75, 75, 71, 75, 72, 81, 81, 69, 67, 88, 78 frames); 0 repaired frames |
| AGW raw monitoring output = KISS output, same build | 11 / 11 |
| `SOFT_FIX 1` / `SOFT_FIX 2` (opt in) | Superset: 79 and 83 frames vs 75; the 4 and 8 extra frames are exactly those marked repaired on the console; none of upstream's frames missing |

### 5.3 Transmit through the client interfaces, and FEC fallback

Frames: the 103 corpus frames plus 24 raw frames (SABM, SABME, UA, DM, DISC, FRMR, XID with
the AX.25 v2.2 parameter example, TEST, I frames modulo 8 and 128 incl. flag / escape bytes in
the information field, RR, RNR, REJ, SREJ, RR modulo 128, UI with PID 0xCF (NET/ROM) and
0xCC (IP), UI as a response, UI with both C bits clear (v1), a path with two of three
digipeaters used, all 256 byte values, a 512 byte information field, no information field)
are written by a client, transmitted, captured and decoded by every build's `atest`; the
decoded frames must be exactly those sent, in any order (direwolf sends frames with a used
digipeater first).  IL2P: C bits as the IL2P type 1 header normalises them, and without
the v1 "both C bits clear" frame, which that header can't express.

| Interface | Modems | Result |
|---|---|---|
| KISS TCP | 1200, 300, 2400 V.26B, 2400 V.26A, 4800, 9600, 1200 FX.25, 9600 FX.25, 1200 IL2P, 9600 IL2P | Fork transmits: 127 / 127 frames decoded by fork, 1.8.2 and dev in all 10 modems.  1.8.2 and dev transmit: 127 / 127 everywhere except FX.25, where every receiver gets 126 / 127 (1200) and 125 / 127 (9600) from them: their own NRZI defect (9.2), fixed in the fork |
| AGW raw send (`K`) | 1200 | 127 / 127, fork to upstream and upstream / dev to fork |
| KISS pseudo terminal (`-p`) | 1200 | 127 / 127, same directions |
| Serial KISS (`SERIALKISS`, socat pty pair) | 1200 | 127 / 127, same directions |
| Transmit audio md5, same frames from KISS | all of the above | Transmit audio byte identical to 1.8.2 and dev for 1200, 300, 2400 V.26B / V.26A, 4800, 9600, IL2P 1200 / 9600; FX.25 differs only by the NRZI fix |
| Frames too long for FX.25 / IL2P sent as AX.25 | FX.25 1200 / 9600, IL2P 1200 / 9600 | FX.25 then a long AX.25 frame: fork 2 / 2 (1200) and 4 / 4 (9600), 1.8.2 and dev 1 / 2 and 3 / 4.  IL2P 1200: 2 / 2 everywhere.  IL2P 9600 with a frame > 1023 bytes: 1 / 2 in **every** build (upstream defect 9.8, not fixed) |

### 5.4 Digipeater, beacons, IGate

All with `FULLDUP ON` (receive audio arrives faster than real time, and half duplex
Dire Wolf mutes its receiver while transmitting).

* **Digipeater** (`DIGIPEAT 0 0 ^WIDE[3-7]-[1-7]$|^TEST$ ^WIDE[12]-[12]$ TRACE`,
  `CDIGIPEAT 0 0`): 13 received frames (WIDE1-1, WIDE2-2, WIDE1-1 + WIDE2-1, explicit
  address, WIDE3-3 and WIDE7-7 trapped, alias TEST, after another digipeater, a duplicate,
  an exhausted path, no path, a position, a non-APRS frame via us).  Transmitted: 12 frames,
  **identical** in fork, 1.8.2 and dev, in the same order: duplicate suppressed, exhausted
  and empty paths ignored, trapped paths replaced by our call, used digipeater marked with
  H.  Frames addressed explicitly to us are repeated by both `DIGIPEAT` and `CDIGIPEAT`
  (two transmissions), identically in all three builds.
* **Beacons** (`PBEACON` with PHG and overlay, compressed with altitude, with frequency /
  tone / offset, with position ambiguity; `CBEACON`): the **same 5 frames** transmitted by
  all three builds (the version digits of the tocall ignored), e.g. the compressed one as
  `!/8wPp<K"D>  !/A=003281compressed` (1000 m = 3281 ft).  In the full run the compressed
  beacon's line had options `PBEACON` doesn't accept, so all three builds rejected it alike;
  the line was fixed and the beacon test rerun (`interfaces_beacon_rerun.csv`).
* **IGate** against a local fake APRS-IS server (`IGSERVER`, `IGLOGIN`, `IGTXVIA`,
  `IGTXLIMIT`): 45 received frames (40 corpus frames, a station to be heard, `TCPIP*`,
  `NOGATE`, `RFONLY`, a third party frame).  All three builds log in identically (apart from
  the version), upload the **same 41 lines** with the same q construct (`qAR,N0CALL-1`), and
  gate none of the TCPIP / NOGATE / RFONLY / third party frames.  From APRS-IS, a message
  to the station heard on RF is transmitted as the **same third party frame** by all three
  (`N0CALL-1>APDW18,WIDE1-1:}K1ABC>APRS,TCPIP,N0CALL-1*::W1XYZ ...`); a message to a station
  not heard is gated by none.

### 5.5 Connected mode and configuration files

* **Connected mode**: two `direwolf` instances, A (N0CALL-1) and B (N0CALL-2), each with an
  AGW client, linked by a simulated half duplex channel (each one's transmit audio becomes
  the other's receive audio, in real time).  A connects, both send data (text, all 256
  byte values, 1024 and 700 byte blocks, larger than `PACLEN 128` so they are segmented),
  A disconnects.  Fork calls 1.8.2, 1.8.2 calls fork, fork calls dev, dev calls fork: **all
  four connect, deliver 968 / 968 and 1395 / 1395 bytes in order, and disconnect**, with
  the same number of frames on the air (20 and 13) in every pairing.
* **Configuration files**: the generic `direwolf.conf` and `sdr.conf` generated by the build
  and `test/compat/configs/kitchen_sink.conf` (two channels, digipeater and filters, IGate
  options, APRStt / DTMF, IL2P, FIX_BITS, timing, connected mode parameters, SmartBeaconing,
  logging; 56 output lines).  Lines naming audio devices, ports, PTT / DCD / GPS, the IGate
  server and beacons are removed, since they need hardware or the network.  Startup output
  **identical** to 1.8.2 and to dev apart from version strings.  The fork's only new keyword, `SOFT_FIX`, isn't needed by any
  existing configuration.

### 5.6 Stalled clients, audio over UDP

* **A client that stops reading** (`interfaces.py --only stall`, `results/round3/stall.csv`).
  One client connects and never reads (AGW with raw monitoring on, or KISS TCP); a second,
  healthy client counts the frames it receives while 4000 frames of 9600 bd audio are
  decoded.  The kernel's send buffer is lowered for the test (`net.ipv4.tcp_wmem` 4096
  8192 16384, restored afterwards), otherwise it would absorb about 4 MB first.

  | Stalled client | Fork | Upstream 1.8.2 | Upstream dev |
  |---|---|---|---|
  | AGW | **FAIL**: 53 / 4000 | **FAIL**: 53 / 4000 | PASS: 4000 / 4000 |
  | KISS TCP | **FAIL**: 60 / 4000 | **FAIL**: 60 / 4000 | **FAIL**: 72 / 4000 |

  The fork behaves exactly like upstream 1.8.2: once the stalled client's buffers are full,
  the receive thread blocks in `send()` and nothing more is decoded or delivered to anyone.
  Upstream dev fixes the AGW case (#671), not KISS TCP.  Not fixed here (defect 9.7,
  section 10.1).
* **Audio over UDP** (`ADEVICE udp:PORT`, as fed by SDR programs; `test/compat/udp_audio.py`,
  `results/round3/udp.txt`).  The 78 s receive file of section 5.2 sent in real time as
  1024 byte datagrams: fork and 1.8.2 each give the **same 75 frames as through stdin**,
  identical between them.  Decode latency, on the same path, in section 6.6.

## 6. DSP and modem results

### 6.1 Method

`test/benchmark/rx_sensitivity.py`: UI frames with random 10 - 120 character payloads and a
sequence number are modulated by **upstream's** `gen_packets` (so the transmitter is the
reference, not the fork), white Gaussian noise is added at a given Eb/N0 (signal power
measured while transmitting, N0 of real white noise over the full audio band), optionally
with an impairment (`deemph6`: space tone 6 dB below mark, as after FM de-emphasis;
`impulse`: 20 random clicks per second), and the same WAV file is decoded by each build's
`atest`.  A frame counts as decoded only if its text is exactly the one sent (address,
info, sequence number); anything else that passes the FCS is a **corrupted frame** (false
acceptance).  `gained` / `lost` count frames one build decodes and upstream doesn't, and
the reverse, on identical audio.  Thresholds: Eb/N0 for 90 % and 99 % decoding, by linear
interpolation between points and by a logistic fit (with its standard error); "-" where the
sweep doesn't reach that probability.  Seeds, sample counts and binary / WAV hashes are in
every CSV row.  Repaired frames (only with `-S1`/`-S2`) are counted separately
(`frames_fixed`), and the plain decoder's count is reported next to them.

Confidence: with 300 - 500 frames per point, a decode probability near 50 % has a
standard error of about 2 - 3 percentage points per point; the logistic thresholds'
standard errors are 0.05 - 0.09 dB.  Differences of 0.1 dB between builds are not
significant; identical frame sets (the default fork vs upstream below 54.9 kHz) need no
statistics.

### 6.2 Default configuration: identical to upstream where the receive path is unchanged

| Mode, rate | Condition, profile | Frames | Upstream decoded | Fork decoded | Gained / lost | P90 (interp.) upstream / fork | P99 upstream / fork |
|---|---|---|---|---|---|---|---|
| 1200, 44.1 kHz | flat, A+ | 4500 | 2252 | 2252 | 0 / 0 | 11.35 / 11.35 dB | 12.29 / 12.29 dB |
| 1200, 44.1 kHz | flat, A | 4500 | 2083 | 2083 | 0 / 0 | 11.86 / 11.86 | 12.97 / 12.97 |
| 1200, 44.1 kHz | impulse, A+ | 4500 | 1829 | 1829 | 0 / 0 | 13.62 / 13.62 | not reached |
| 1200, 44.1 kHz | impulse, A | 4500 | 1516 | 1516 | 0 / 0 | not reached | not reached |
| 300, 44.1 kHz | flat | 3900 | 2608 | 2608 | 0 / 0 | 10.85 / 10.85 | 11.80 / 11.80 |
| 2400 QPSK, 44.1 kHz | flat | 3900 | 2237 | 2237 | 0 / 0 | 12.61 / 12.61 | 13.80 / 13.80 |
| 4800 8PSK, 44.1 kHz | flat | 3900 | 903 | 903 | 0 / 0 | 17.19 / 17.19 | not reached |
| 9600, 44.1 kHz | flat | 3900 | 3084 | 3084 | 0 / 0 | 9.55 / 9.55 | 10.83 / 10.83 |

Sweeps: 1200 bd Eb/N0 6 - 14 dB, 500 frames per point, seed 304; other modes 6 - 18 dB,
300 frames per point, seed 305 (`sens_44k*.csv`, `sens_{300,2400,4800,9600}*.csv`).  The
default fork decodes **exactly the same frames** as upstream 1.8.2: the round 1 / 2
demodulator changes (circular buffers, UB fixes) are transparent, and with repair off
nothing else differs.  The same holds in every noisy test of section 5.1 (300 to 19200 bd,
FX.25, IL2P, 22.05 - 48 kHz).  The logistic fit fails for 9600 and 4800 bd (degenerate fit:
it returns the 50 % point with a zero error); the interpolated thresholds are used.

### 6.3 Automatic decimation (1200 bd at 54.9 kHz and above)

Upstream truncates the 1200 bd filters to 479 taps when they don't fit `MAX_FILTER_SIZE`
(480), with a warning suggesting `-D2` / `-D3`; the fork decimates automatically first, so the
filters have their intended length.  This changes the
receive DSP, so decodes are not identical:

| Rate | Condition, profile | Frames | Upstream | Fork | Gained / lost | P90 upstream / fork | P99 upstream / fork |
|---|---|---|---|---|---|---|---|
| 88.2 kHz | flat, A+ | 3300 | 1918 | 1942 | 69 / 45 | 11.30 / 11.29 dB | 12.50 / 12.00 dB |
| 96 kHz | flat, A+ | 3300 | 1873 | 1930 | 88 / 31 | 11.57 / 11.36 | 12.25 / 11.98 |
| 96 kHz | flat, A | 3300 | 1742 | 1848 | 133 / 27 | 12.15 / 11.78 | 13.83 / 12.87 |
| 96 kHz | flat, B | 3300 | 1893 | 1893 | 0 / 0 | 11.67 / 11.67 | 12.75 / 12.75 |
| 96 kHz | deemph6, A+ | 3300 | 1061 | 1163 | 148 / 46 | 14.32 / 14.00 | 15.40 / 14.93 |
| 96 kHz | deemph6, A | 3300 | 914 | 967 | 118 / 65 | 14.94 / 14.91 | not reached |
| 192 kHz | flat, A+ | 3300 | 1721 | 1933 | 237 / 25 | 11.95 / 11.42 | 12.88 / 12.67 |
| 192 kHz | flat, A | 3300 | 1506 | 1840 | 360 / 26 | 12.90 / 11.86 | 14.89 / 13.00 |

Eb/N0 6 - 16 dB, 300 frames per point (seeds 301 - 303, `sens_{88k,96k,192k}*.csv`).  Profile B
doesn't use the long prefilter and is not decimated: identical.  The fork is better at
every rate in total and at the 90 / 99 % points (up to 1 - 1.9 dB at 192 kHz), but **the
frame sets differ**: every lost frame is in the transition region (Eb/N0 8 - 14 dB flat, 11 -
16 dB de-emphasised) where both builds decode only part of the frames, the usual
behaviour of two different demodulators near threshold.  From 13 dB up (flat) the fork
decodes 300 / 300 at every rate; upstream at 192 kHz profile A does not (298 / 300 at 16
dB).  Clean signals: 103 / 103 at 64 - 192 kHz in every check of section 5.1.  At 48 kHz and
below the receive path is unchanged (section 6.2).  One corrupted frame was accepted at
192 kHz, section 6.5.

### 6.4 Soft decision repair (opt in), measured separately

| Run | Condition, profile | Frames | Plain decoder (= default fork = upstream) | With `-S1`: repaired / corrupted / lost | With `-S2`: repaired / corrupted / lost |
|---|---|---|---|---|---|
| 1200, 44.1 kHz | flat, A+ | 4500 | 2252 | +155 / 0 / 0 | +266 / 0 / 0 |
| 1200, 44.1 kHz | flat, A | 4500 | 2083 | +266 / 0 / 0 | +392 / 0 / 0 |
| 1200, 44.1 kHz | impulse, A+ | 4500 | 1829 | +264 / 0 / 0 | +434 / 0 / 0 |
| 1200, 44.1 kHz | impulse, A | 4500 | 1516 | +504 / 0 / 0 | +721 / 0 / 0 |
| 300, 44.1 kHz | flat | 3900 | 2608 | +104 / 0 / 0 | - |
| 2400, 44.1 kHz | flat | 3900 | 2237 | +94 / 0 / 0 | - |
| 4800, 44.1 kHz | flat | 3900 | 903 | +101 / 0 / 0 | - |
| 9600, 44.1 kHz | flat | 3900 | 3084 | +94 / 0 / 0 | - |

The plain decoder's frames with `-S1` / `-S2` are exactly the default's (repair only runs
after a failed FCS), so the extra frames are entirely from repair, and every one of them
is marked as repaired.  P90 with `-S1`: 1200 A+ 11.35 -> 10.90 dB, A 11.86 -> 10.97 dB, 300 bd
10.85 -> 10.53 dB, 2400 12.61 -> 12.34 dB, 4800 17.19 -> 16.76 dB, 9600 9.55 -> 9.18 dB.  In section 5.1's noisy files,
`-S1` gained 0 - 6 frames per mode and lost none.  No repaired frame was corrupted in this
round (18,000 frame decodes at 1200 bd above alone; see also section 6.5).

PENDING-SECTION-6-REST

## 7. Upstream issues, pull requests and `dev` commits reviewed

Issues and PRs were read on github.com (open and closed, sorted by activity, plus keyword
searches for crash, decode, KISS, FX.25, IL2P, digipeat, IGate); `dev` commits were read as
diffs.  Previous rounds' conclusions (UPSTREAM_COMPATIBILITY_AND_OPTIMIZATION_AUDIT.md
section 3) were rechecked against the current upstream state.

| Ref | Topic | In fork? | Finding | Decision |
|---|---|---|---|---|
| [#597](https://github.com/wb2osz/direwolf/pull/597) `edb6210` | Morse: duplicate, wrong code for `-` | **Now yes** | 1.8.2 sends `-` as `-...-` (=).  multimon-ng decodes the 1.8.2 / fork CW ID `N0CALL-1` as `N0CALL=1`, dev's as `N0CALL-1` | **Adopted** (cherry-pick `-x`, author kept); regression check in `interop.py` |
| [#671](https://github.com/wb2osz/direwolf/pull/671) `9acba4c` | A stalled AGW client blocks the receive thread (and can hold PTT on) | No | Reproduced in fork and 1.8.2 alike: a client that stops reading stops frames to every other client (53 / 4000 delivered with a stalled AGW client, 60 / 4000 with a stalled KISS client).  Fixed for AGW in dev (4000 / 4000).  **KISS TCP has the same problem in dev too** (72 / 4000) | **Not adopted**: 473 lines of new threading in `server.c`, conflicts with #669, Windows paths can't be tested here.  Top remaining risk (section 10).  Test: `interfaces.py --only stall` |
| [#620](https://github.com/wb2osz/direwolf/issues/620) (open) | Windows crash, "received frame queue out of control" with network KISS | Same code | Consistent with the KISS stall above (slow client, blocking send, queue grows).  `610fbc0` in dev only adds debugging and `TCP_WMEM` | Documented |
| [#426](https://github.com/wb2osz/direwolf/issues/426) (open) | Corrupted TX bitstream with FX.25 | Fixed one cause | The NRZI defect found here (section 9.2) corrupts the bit after each switch between FX.25 and AX.25 output.  #426 describes corruption mid-frame on one Raspberry Pi set-up that went away; **not confirmed to be the same** | Possibly related; the NRZI fix should be reported upstream |
| [#669](https://github.com/wb2osz/direwolf/pull/669) `6fb888c` | AGWPE extension: per-frame signal quality | No | New feature in dev; would let clients see repaired frames | Not adopted (feature; future item) |
| [#668](https://github.com/wb2osz/direwolf/pull/668), [#615](https://github.com/wb2osz/direwolf/pull/615), `d0eb822`, `2e587f4`, `f95c4e3`, `cee25a8` | TIMESTAMP, AGW heard list, multiple KISS pty, ADIGIBUNDLE (#667), SCHANNEL, DNS-SD | No | New 1.9 features.  Config files using `TIMESTAMP`, `KISSPTY`, `ADIGIBUNDLE`, `SCHANNEL`, `TCP_WMEM` give "unrecognized" warnings on the fork (and on 1.8.2) | Not adopted (features of an unreleased version) |
| `9aba4b3`, `591a897`, `f24021b`, `4849a7f`, `ae961a7`, `37400ad`, `160cbb4` | AX.25 v2.2 SREJ, window size, mod 128 hint | No | Connected mode changes in dev.  **Fork <-> dev connected mode sessions work in both directions** (section 5.4) | Not needed for compatibility |
| `7926195`, `9caba55` (#675) and hamlib CMake commits | hamlib 5 API | No | Only needed when building against hamlib 5 (Ubuntu 24.04 ships 4.5.5) | Not adopted; note for packagers |
| [#641](https://github.com/wb2osz/direwolf/pull/641), [#576](https://github.com/wb2osz/direwolf/pull/576), [#655](https://github.com/wb2osz/direwolf/pull/655), [#656](https://github.com/wb2osz/direwolf/pull/656), [#657](https://github.com/wb2osz/direwolf/pull/657) | AGW M frame NULL, IL2P test, gmtime_r, debug output, CTCSS "100%" | Yes (round 2) | Re-verified: #657 also stops "100%" being *interpreted* as PL 100.0 (upstream 1.8.2 does) | Present |
| [#651](https://github.com/wb2osz/direwolf/issues/651), [#652](https://github.com/wb2osz/direwolf/pull/652), [#653](https://github.com/wb2osz/direwolf/pull/653), [#647](https://github.com/wb2osz/direwolf/issues/647) | CVE-2025-34457/34458 (KISS overflow, MIC-E abort) | Yes (in 1.8.2) | Fuzzed again this round (KISS state machine) | Present |
| [#569](https://github.com/wb2osz/direwolf/issues/569) | `MODEM AIS` baud not set | Yes (in 1.8) | `config.c` sets 9600 for the AIS sentinel | Present |
| [#106](https://github.com/wb2osz/direwolf/issues/106), [#342](https://github.com/wb2osz/direwolf/issues/342)/[#619](https://github.com/wb2osz/direwolf/issues/619), [#516](https://github.com/wb2osz/direwolf/issues/516) | XKISS/ACKMODE unsupported; KISSPORT doesn't disable 8001; 64 bit time_t on 32 bit | Same as upstream | #342 observed in these tests (port 8001 always opened) | Upstream behaviour, unchanged |
| [#670](https://github.com/wb2osz/direwolf/issues/670) | WA8LMF CD results for 1.8.1 (1032 / 1018 decodes, tracks 1 / 2) | - | Useful external reference; the CD was not available here | Not reproduced |
| [#667](https://github.com/wb2osz/direwolf/issues/667), [#664](https://github.com/wb2osz/direwolf/issues/664), [#623](https://github.com/wb2osz/direwolf/issues/623) `8804243`, `ee4d3e4`, `078b2e8`, `01c3114`, `7558a17` | Digipeater bundling option, 2x beacon definitions, DX info, more weather fields, ack display, symbol names, tocalls | No | Features or display; 1315 of 12000 test packets decode differently in dev because of these (section 4.2) | Not adopted |
| `eb16e43`, `3b20d82`, `eee56cc`, `f11c82b`, `b0aa22c`, `3827837`, `610fbc0`, `e62d608`, `ddfc5ec` | Buffer size in xid.c test, compiler warnings, IGFILTER / AGW messages, debugging | No | Test-only buffer, warnings on other compilers, messages | Not needed (fork Release build has 0 warnings) |
| [#572](https://github.com/wb2osz/direwolf/pull/572), [#507](https://github.com/wb2osz/direwolf/issues/507), [#628](https://github.com/wb2osz/direwolf/pull/628), [#638](https://github.com/wb2osz/direwolf/pull/638), [#665](https://github.com/wb2osz/direwolf/pull/665) | IL2P CRC, IQ input, LoRa, OFDM (open) | No | New on-air formats / modems | Not adopted |

No upstream issue or pull request reports a receive decoding regression in 1.8.x; upstream
has no soft decision repair.

## 8. Build, test, sanitizer and fuzzing results

All on the branch head (`results/round3/build_matrix.txt`, `buildmatrix.sh`).

### 8.1 Builds and unit tests

| Configuration | Build | Warnings | Tests |
|---|---|---|---|
| gcc 13.3 Release (`-DUNITTEST=ON`) | OK | 0 | ctest 28 / 28 |
| gcc 13.3 Debug | OK | 1 (`aprs_tt.c` format truncation; upstream code, same in 1.8.2) | ctest 28 / 28 |
| clang 18.1 Release | OK | 54, **the same 54 as upstream 1.8.2** built with clang (50 `-Wnan-infinity-disabled` from `gps.h` with `-ffast-math`, 2 unused `interpol8`, 2 literal conversions in `gen_packets.c`); none in code the fork changed | ctest 28 / 28 |
| gcc ASan + UBSan (`-fno-sanitize-recover=undefined`), Debug | OK | 4 (format truncation, upstream code) | 28 / 28 with leak detection off; 0 sanitizer errors (see 8.3) |
| Windows x86-64, mingw-w64 gcc 13 cross build | OK | 0 | 18 / 18 unit test programs and 71 / 71 modem test lines (`test/scripts/check-*`, 11 scripts) under wine 9.0 |
| Linux aarch64, gcc 13.3 cross build | OK | 0 | 18 / 18 unit test programs and 24 / 24 modem test lines under qemu-user 8.2 |

The 28 ctests are upstream 1.8.2's 24 (its `check-modem*` scripts with upstream's lines
unchanged plus the fork's `-S1`/`-S2` lines, section 9.1),
round 2's `aistest` and `deviceidtest`, and this round's `l2sendtest` and `ax25goldentest`.

Windows and aarch64 audio: `gen_packets` output of the Windows build (under wine) and of
the aarch64 build (under qemu) is byte identical to the x86-64 Linux build's for every mode
tried (1200, 300, 9600, 2400 V.26B, 4800, FX.25 and IL2P on Windows; 1200, 9600, 2400 and
FX.25 on aarch64), and each decodes the other's audio 103 / 103.  These are emulated runs:
they show the code computes the same thing on those targets, not that sound cards, serial
ports or networking work there (section 10.2).  In the matrix run, the 7 lines of
`check-modem300` under wine failed because `gen_packets` did not complete while the disk
was nearly full; rerun afterwards, 7 / 7 pass (`build_matrix_addendum.txt`).

### 8.2 Static analysis

cppcheck 2.13 (`--enable=warning,portability`) on the 21 C files the fork changes or adds, in the
fork and in upstream 1.8.2: 20 findings in the fork, 22 in upstream.  The two upstream
findings the fork no longer has are the signed 64 bit shift in `fx25_rec.c` (fixed in round 2)
and a string comparison in `il2p_test.c` (#576).  The fork adds none (`cppcheck_*.txt`).

### 8.3 Sanitizers and valgrind

* ASan + UBSan, all 28 ctests: no memory error and no undefined behaviour report.  With
  leak detection on, `dtest` and `deviceidtest` report leaks: `dtest` leaks 114904 bytes in
  53 allocations in the fork and **exactly the same in upstream 1.8.2** built with ASan
  (test packets the digipeater unit test never frees); `deviceidtest` reports 665 bytes,
  the tables `deviceid_init` keeps for the life of the process (`asan_leaks.txt`).
* valgrind, `decode_aprs` on 12000 packets: fork 0 errors; upstream 1.8.2 858; dev 776
  (section 4.2).
* Every fuzzing run below ran with ASan and UBSan.

PENDING-SECTION-8-FUZZ

## 9. Defects found and fixes applied in this round

| # | Defect | Where | Found by | In upstream? | Status |
|---|---|---|---|---|---|
| 9.1 | Soft-repaired frames delivered to KISS/AGW clients **by default** | fork default `SOFT_FIX 1` | End to end (KISS client: 79 vs upstream 75 frames) | No (fork only) | **Fixed**: default off |
| 9.2 | A plain AX.25 frame right after an FX.25 frame in one transmission is undecodable (NRZI level not shared) | `hdlc_send.c`, `fx25_send.c` | `interfaces.py` tx tests; confirmed with multimon-ng | **Yes**, 1.8.2 and dev | **Fixed**, `l2sendtest` |
| 9.3 | Uninitialized int used for wind direction / course / speed / range (`sscanf` returns EOF) | `decode_aprs.c` | `aprs_decode_compare.py`, valgrind | **Yes**, 1.8.2 and dev | **Fixed** |
| 9.4 | Invalid PHG/DFS height shown as -999999 ft | `decode_aprs.c` | Decode comparison | No (round 2 fix's display) | **Fixed** |
| 9.5 | Morse `-` sent as `=` | `morse.c` | multimon-ng MORSE_CW | **Yes**, 1.8.2 (fixed in dev) | **Fixed** (cherry-pick #597) |
| 9.6 | `NEAR` macro clashes with Windows headers (warning) | `ais.c` test code | mingw cross build | No (round 2 test) | **Fixed** |
| 9.7 | A client that stops reading stops frame delivery to all clients | `server.c`, `kissnet.c` | `interfaces.py --only stall` | **Yes**; AGW part fixed in dev | **Not fixed** (section 10) |
| 9.8 | IL2P at 9600 bd: a frame too long for IL2P (> 1023 bytes) falls back to AX.25 but is sent without the G3RUH scrambler; undecodable | `gen_tone.c` (scrambler disabled for the whole channel in IL2P mode) | `interfaces.py`-style test | **Yes**, 1.8.2 and dev | **Not fixed** (rare, needs a design change; reported here) |
| 9.9 | Audio device names longer than 29 characters silently truncated (config allows 80) | `audio.c` `audio_in_name[30]` | Harness (ALSA file plugin) | **Yes** | Not fixed (cosmetic / upstream) |

### 9.1 Soft repair default

With `SOFT_FIX 1` (PR #1's default), frames recovered by inverting bits were sent to KISS
and AGW clients.  Upstream only does that when the user sets `FIX_BITS`, which upstream
ships off because "even a single bit fix up will occasionally let corrupted packets
through" (`audio.h`).  KISS has no way to mark such a frame, so a client that digipeats or
IGates (APRSIS32, YAAC, Xastir, ...) forwards it as a frame received without errors.
Measured with `direwolf` itself on identical audio: upstream gave its KISS client 75 frames,
the fork 79, the 4 extra being repaired frames.  This contradicts the requirement that
repaired or uncertain frames are not forwarded as trustworthy, so `DEFAULT_SOFT_FIX` is now
0.  With the default configuration the fork's KISS and AGW output is identical to
upstream's in all 11 receive configurations tested (section 5.2).

`SOFT_FIX 1` / `2` (`atest -S1` / `-S2`) still enable repair.  The rules are then those of
`FIX_BITS`: shown on the console as `[SINGLE]` etc., logged with the retry level, sent to
KISS/AGW clients, never digipeated, regenerated (REGEN) or IGated.  Verified end to end:
`SOFT_FIX 1` adds 4 frames to the KISS output on the test audio, all 4 marked as repaired;
`SOFT_FIX 2` adds 8, all marked; no frame decoded by upstream is missing.  Other consumers,
same as upstream with `FIX_BITS`: the heard list (`mheard`), the log file (with the retry
level), waypoint sentences, the APRStt test path (`t` packets, only with a TT gateway
configured) and connected mode (only reachable with `FIX_BITS n AX25`, since the default
sanity test accepts only UI frames with PID F0).

The upstream modem test lines (`test/scripts/check-*`) are back to exactly upstream's text
(no `-S0`), so they now check that the default decodes what upstream decodes; the fork's
`-S1`/`-S2` lines are kept.

### 9.2 NRZI after FX.25 (upstream defect)

`hdlc_send.c` (flags, AX.25 frames) and `fx25_send.c` (FX.25 tag and block) each kept a
private NRZI line level.  When the transmitter switches from one to the other, the first
bit is inverted whenever the two levels differ.  After an FX.25 frame, that bit is the
first bit of the next AX.25 frame's only opening flag, so the frame is lost.  With
`FX25TX`, a frame too long for FX.25 (about 239 bytes) is sent as AX.25, and if it follows
an FX.25 frame in the same transmission (KISS clients and digipeaters queue several
frames), no receiver decodes it: not upstream Dire Wolf (1.8.2 or dev), not multimon-ng.
Measured through KISS TCP with the corpus plus raw frames: upstream 126/127 at 1200 bd and
125/127 at 9600 bd; the fork now 127/127.

Fix: `tone_gen_put_bit` remembers the last bit sent per channel and both NRZI encoders
continue from it.  Plain AX.25 and IL2P transmit audio stays byte identical to upstream's
(all modes, KISS and gen_packets).  FX.25 audio differs only where upstream inverted a bit;
upstream receivers decode it as well as before (6000 noisy frame trials: 1713 decoded from
the fork's transmissions vs 1701 from upstream's, differences in both directions per
point).  `l2sendtest` captures the bit stream of FX.25 / AX.25 / FX.25 frames, decodes it
as a plain receiver would and requires every frame; with the old `hdlc_send.c` and
`fx25_send.c` it fails 4 of 12 cases.

### 9.3 Uninitialized values in received APRS packets (upstream defect)

`if (sscanf (p, "%3d", &n))` is true for EOF (-1), when only a nul or spaces remain, and
then uses `n` uninitialized.  Reachable from received packets: a nul in a weather report's
wind field (`_<nul>90/000...`), a truncated report (`_090/`), course/speed followed by
spaces (`088/   `), `RNG` followed by spaces.  Upstream printed garbage such as "wind 253.2
mph", "direction -1479100416", "range=21918.0", in the display, log file and waypoint
sentences.  valgrind over 12000 test packets: upstream 1.8.2 858 errors in 118 contexts (all
in this code and in the AIS / MIC-E code the fork fixed in round 2), fork 0.

## 10. Known limitations, untested areas and remaining risks

### 10.1 Remaining risks, by importance

1. **A stalled client freezes everything (upstream defect, not fixed).**  A KISS TCP or AGW
   client that stays connected but stops reading eventually blocks Dire Wolf's receive
   processing thread on `send()`: no more decoding output, digipeating, IGating or
   connected mode timers until the client goes away, and (AGW monitoring) possibly the
   transmit thread with PTT on.  With the default 4 MB socket buffers it takes a lot of
   traffic or hours to trigger; upstream #620 looks like a field report of it.  Upstream
   `dev` fixes the AGW side (#671, 473 lines); KISS TCP is not fixed upstream at all.
   Recommendation: adopt #671 when it is released (or as a separate, Windows-tested PR) and
   report the KISS TCP case upstream with `interfaces.py --only stall` as the reproduction.
2. **Opting in to soft repair** (`SOFT_FIX 1/2`) sends repaired frames to KISS/AGW clients,
   which can't tell them apart (same as `FIX_BITS` upstream).  The repair budget makes a
   corrupted repaired frame rare (none at level 1 or 2 in this round's runs, sections 6.4 - 6.5), not
   impossible.  Recommendation if it should ever be on by default: deliver repaired frames
   only to clients that ask for them (e.g. upstream PR #669's AGW signal quality extension).
3. **IL2P at 9600 bd with frames over 1023 bytes** (upstream defect 9.8): sent unscrambled,
   lost.  Rare; would need the scrambler state to follow the frame type plus extra flags.
4. **Automatic decimation (>= 54.9 kHz)** changes which marginal frames decode at 64 - 192
   kHz: better on average (section 6.3) but individual frames upstream decodes can be lost.
5. `deviceid_init` keeps 665 bytes for the life of the process (LeakSanitizer) and
   upstream's `digipeater.c` unit test leaks test packets; both upstream, harmless.

### 10.2 Not tested, and why

| Area | Status | Why / what was done instead |
|---|---|---|
| Real radios, sound cards, over the air | NOT TESTED | No hardware.  Audio was generated and captured in software (ALSA file plugin, stdin, UDP); FM receiver audio is not white noise |
| WA8LMF TNC test CD | NOT TESTED | Not available here (upstream #670 has 1.8.1 numbers) |
| macOS build | NOT TESTED | No macOS; CI on the fork does not run (no check runs on PR #3) |
| Windows *runtime* (sound, serial, network) | PARTIAL | MinGW cross build; unit tests and modem tests run under wine; direwolf.exe itself not run |
| Raspberry Pi / ARM hardware | PARTIAL | aarch64 cross build; unit and modem tests under qemu-user; not on real hardware, not 32 bit armhf |
| PTT (serial, GPIO, CM108, hamlib), DCD/TXINH lines | NOT TESTED | Hardware; code not changed by the fork |
| GPS (gpsd, NMEA), tracker beacons, SmartBeaconing | NOT TESTED | No GPS source; code not changed by the fork; config parsing of SMARTBEACONING / TBEACON checked |
| APRStt / DTMF end to end | PARTIAL | `dtmftest`, `ttest`, `tttexttest` unit tests and config parsing; no DTMF audio test |
| AIS reception from RF | PARTIAL | AIS parser: unit test, fuzzing, decode comparison; no AIS signal generator |
| KISS over Bluetooth, SERIALKISSPOLL, NCHANNEL / network TNC | NOT TESTED | Not exercised |
| Configuration file fuzzing | NOT TESTED | config parser is local, trusted input; three configs compared instead (section 5.5) |
| Long duration (days) operation | NOT TESTED | Longest runs: 1 h of noise per build; 30 min fuzzing; no soak test of `direwolf` |

## 11. How to reproduce

Build each tree in its own directory (`cmake -DCMAKE_BUILD_TYPE=Release -DUNITTEST=ON ..
&& make -j4 && ctest`).  Tools needed: `python3`, `numpy`, `multimon-ng`, `socat`,
`valgrind`, optionally `aprslib` (pip), `wine64`, `qemu-user`, mingw-w64, aarch64 cross gcc,
clang with `libclang-rt` for fuzzing.  Then, from the fork's source directory:

    # Modems: TX identical, cross decoding, independent modulators, multimon-ng, Morse, noise
    python3 test/compat/interop.py --build fork=build --build upstream=../up/build \
        --build upstream-dev=../dev/build --out /tmp/interop --csv interop.csv

    # APRS decoding: 12000 packets, crashes, valgrind, aprslib positions
    python3 test/compat/aprs_decode_compare.py --build fork=build --build upstream=../up/build \
        --build upstream-dev=../dev/build --work /tmp/aprsdec --valgrind

    # Real direwolf processes (run as an ordinary user; see test/compat/README.md)
    DW_RUN_AS=nobody python3 test/compat/interfaces.py --dut fork=/path/fork \
        --ref upstream=/path/up --ref upstream-dev=/path/dev --work /tmp/dwrun/if \
        --conf-dir /path/with/confs --only rx,tx,digi,beacon,igate,config,connected
    sudo DW_RUN_AS=nobody python3 test/compat/interfaces.py ... --only stall

    # Sensitivity, frame by frame on identical audio (examples from section 6)
    python3 test/benchmark/rx_sensitivity.py --atest upstream=../up/build/src/atest \
        --atest fork=build/src/atest --gen-packets ../up/build/src/gen_packets --rate 96000 \
        --profiles A+,A,B --conditions flat,deemph6 --ebn0 6:16:1 --frames 300 --seed 301

    # Fuzzing: see test/fuzz/README.md (fuzz_rx and fuzz_kiss_fec)

    # AX.25 golden vectors (also run by ctest): regenerate from the independent encoder
    python3 test/compat/make_golden.py

The exact command lines of every run in this report are in `results/round3/commands.txt`.

## 12. Changes made in this round

| Commit | Change | Evidence |
|---|---|---|
| `da41e5e` | `SOFT_FIX` off by default; upstream's own modem test lines restored | 9.1; section 5.2 (KISS/AGW identical to upstream in 11 configurations); ctest |
| `0817928` | NRZI continuity between AX.25 and FX.25 senders; new ctest `l2sendtest` | 9.2; `l2sendtest` fails 4/12 on the old code; tx-kiss and tx-fallback rows; 6000-trial noise comparison |
| `930f61d` | `decode_aprs`: sscanf EOF checks; PHG without unknown height | 9.3, 9.4; valgrind 858 -> 0 errors; decode comparison categories |
| `be95fa1` | `test/compat` suite; ctest `ax25goldentest` (749 vectors) | Sections 4 - 5; upstream 1.8.2 and dev pass the same vectors |
| `2a91953` | Windows build warning in a test; cppcheck finding in a test | mingw build 0 warnings in fork code; cppcheck |
| `270fb06` | Cherry-pick of upstream #597 (Morse `-`) | multimon-ng MORSE_CW: `N0CALL=1` -> `N0CALL-1`; identical to upstream dev's audio |
| `79bc61e` | Morse check and result labels in `interop.py` | Section 5.1 |
| `cb4d6dc` | Fuzzing harness for KISS input and the FX.25 / IL2P decoders (`test/fuzz/fuzz_kiss_fec.c`); FEC fallback checks in `interfaces.py` | Section 8.4; tx-fallback rows (section 5.3) |
| `a5db110` | `interop.py` noise checks: default fork must equal upstream (decimated rates reported as gained / lost), `-S1` must be a superset | Section 5.1: 84 noise rows |
| `30bff2c` | This report (draft) | - |
| PENDING-FINAL-COMMIT | Report completed; raw results in `results/round3/`; scripts used for the measurements (`results/round3/*.py`, `*.sh`) | This document |

Nothing was force-pushed and no upstream or third-party repository was modified.


