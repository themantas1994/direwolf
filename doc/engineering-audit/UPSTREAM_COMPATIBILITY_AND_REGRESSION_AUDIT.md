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
| Maximum frame size | PASS | 2048 byte info accepted, 2108 rejected (vectors); 1132 byte frame transmitted and decoded end to end; 512 byte frames over KISS |
| Truncated / corrupted / malformed received frames | PASS | Fuzzing (section 8.4); FCS rejects; sanity tests |
| Connected mode (link layer), v2.2 XID negotiation, segmentation | PASS | Fork <-> upstream 1.8.2 and fork <-> dev, each side calling, data both ways incl. all byte values and > PACLEN blocks (section 5.4) |

### 3.2 APRS

| Feature | Status | Evidence |
|---|---|---|
| Decoding of every APRS data type (positions, compressed, Mic-E, objects, items, messages, acks/rejs, bulletins, NWS, status, capabilities, queries, weather, telemetry and PARM/UNIT/EQNS/BITS, third party, user defined, NMEA, Maidenhead) | PASS | 12000 packets: fork = upstream 1.8.2 except 77 packets explained by intended fixes (section 4.2) |
| Interpretation of positions | PASS | aprslib agrees with the fork on all 3225 comparable positions; 496 ambiguity-convention differences (same in upstream) |
| Malformed APRS payloads | PASS | 12000 truncated/substituted packets: no crash in any build; valgrind 0 errors for the fork (858 for upstream) |
| Device identification (tocalls, Mic-E suffixes) | PASS | `deviceidtest`; decode comparison |
| AIS user data | PARTIAL | `aistest`, fuzzing, decode comparison; no AIS RF signal test |
| Digipeater: WIDEn-N, aliases, TRACE, preemption of used paths, duplicate suppression | PASS | 13 input frames -> identical 12 digipeated frames in fork, upstream and dev (section 5.3) |
| Connected mode digipeater (CDIGIPEAT) | PASS | Same test |
| IGate RF > APRS-IS (q construct, TCPIP/NOGATE/RFONLY rules) | PASS | 41 identical lines uploaded to a local server by all three builds |
| IGate APRS-IS > RF (message to a station heard on RF, third party format) | PASS | Identical frame transmitted by all three builds; message to an unheard station not gated by any |
| Beacons (PBEACON incl. PHG, frequency/tone, ambiguity; CBEACON; OBJECT) | PASS | Identical transmitted beacons (version in tocall ignored) |
| Filtering (FILTER, IGFILTER, CFILTER) | PARTIAL | Parsed identically (config test); filter behaviour covered by upstream's `pftest` (passes); no end to end filter test |
| APRStt | PARTIAL | `ttest`, `tttexttest`, `dtmftest`; config parsing; no DTMF audio test |
| REGEN | PARTIAL | Code inspection and round 2 end to end test; not rerun |

### 3.3 Modems (receive and transmit)

| Mode | Status | Evidence |
|---|---|---|
| 1200 AFSK, profiles A, A+, B, AB, E+ | PASS | TX audio identical to upstream; cross decoding 103/103 at 8 - 192 kHz; independent modulator 103/103; multimon-ng 103/103; noisy audio: default fork identical to upstream below 54.9 kHz |
| 1200 AFSK at 64 - 192 kHz (automatic decimation) | PASS | Clean signals 103/103; noisy: PENDING-DECIM |
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
| Audio from UDP (SDR) | PENDING-UDP | |
| Audio output | PARTIAL | ALSA `file` plugin; no sound card |
| Configuration files (generic sample, sdr.conf, many-directive config) | PASS | Startup output identical to upstream and dev apart from version strings |
| New keyword `SOFT_FIX` | PASS | Parsed; invalid value reported; upstream configs need no change |
| Command line | PARTIAL | `-t`, `-c`, `-p` used; other options not compared |
| A client that stops reading | FAIL | Same as upstream 1.8.2 (defect 9.7, section 10.1) |
| PTT / DCD / TXINH, GPS, rig control, CM108, GPIO | NOT TESTED | No hardware |
| Multi-channel (ACHANNELS 2) | PARTIAL | Stereo decode (atest); config parsing; not with direwolf end to end |

### 3.5 Platforms and builds

| Platform | Status | Evidence |
|---|---|---|
| Linux x86-64, gcc 13.3 Release / Debug | PASS | ctest 28/28 |
| Linux x86-64, clang 18 | PASS | ctest 28/28 |
| ASan + UBSan | PASS | 28/28 with leak detection off; 0 memory errors, 0 UB reports (leak reports: upstream code, section 8.3) |
| Windows x86-64 (MinGW cross) | PARTIAL | Builds; unit tests 18/18 and modem script lines 41/41 under wine; direwolf.exe not run |
| Linux aarch64 (cross, qemu-user) | PARTIAL | Builds with 0 warnings; unit tests 18/18 and modem script lines 24/24 under qemu; TX audio identical to x86 |
| macOS, 32 bit ARM, real Raspberry Pi | NOT TESTED | Not available |

PENDING-SECTIONS-4-6

## 7. Upstream issues, pull requests and `dev` commits reviewed

Issues and PRs were read on github.com (open and closed, sorted by activity, plus keyword
searches for crash, decode, KISS, FX.25, IL2P, digipeat, IGate); `dev` commits were read as
diffs.  Previous rounds' conclusions (UPSTREAM_COMPATIBILITY_AND_OPTIMIZATION_AUDIT.md
section 3) were rechecked against the current upstream state.

| Ref | Topic | In fork? | Finding | Decision |
|---|---|---|---|---|
| [#597](https://github.com/wb2osz/direwolf/pull/597) `edb6210` | Morse: duplicate, wrong code for `-` | **Now yes** | 1.8.2 sends `-` as `-...-` (=).  multimon-ng decodes the 1.8.2 / fork CW ID `N0CALL-1` as `N0CALL=1`, dev's as `N0CALL-1` | **Adopted** (cherry-pick `-x`, author kept); regression check in `interop.py` |
| [#671](https://github.com/wb2osz/direwolf/pull/671) `9acba4c` | A stalled AGW client blocks the receive thread (and can hold PTT on) | No | Reproduced in fork and 1.8.2: a client that stops reading stops frames to every other client (53 / 4000 delivered).  Fixed for AGW in dev (4000 / 4000).  **KISS TCP has the same problem in dev too** (71 / 4000) | **Not adopted**: 473 lines of new threading in `server.c`, conflicts with #669, Windows paths can't be tested here.  Top remaining risk (section 10).  Test: `interfaces.py --only stall` |
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

PENDING-SECTION-8

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
sentences.  valgrind over 12000 test packets: upstream 1.8.2 858 errors in 117 contexts (all
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
   corrupted repaired frame rare (none at level 1 in this round's runs, section 6.3), not
   impossible.  Recommendation if it should ever be on by default: deliver repaired frames
   only to clients that ask for them (e.g. upstream PR #669's AGW signal quality extension).
3. **IL2P at 9600 bd with frames over 1023 bytes** (upstream defect 9.8): sent unscrambled,
   lost.  Rare; would need the scrambler state to follow the frame type plus extra flags.
4. **Automatic decimation (>= 54.9 kHz)** changes which marginal frames decode at 64 - 192
   kHz: better on average (section 6.2) but individual frames upstream decodes can be lost.
5. `deviceid_init` keeps ~650 bytes for the life of the process (LeakSanitizer) and
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
| PENDING-LAST-COMMITS | | |

Nothing was force-pushed and no upstream or third-party repository was modified.


