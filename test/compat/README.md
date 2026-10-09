# Upstream compatibility tests

Tools that compare this fork with upstream Dire Wolf builds, and with code that
shares nothing with Dire Wolf, so that two implementations with the same bug
can't pass by agreeing with each other.  Results of the last full run are in
`doc/engineering-audit/UPSTREAM_COMPATIBILITY_AND_REGRESSION_AUDIT.md`.

| File | What |
|---|---|
| `ax25ref.py` | Independent AX.25 / HDLC / FCS / KISS encoder and decoder and AFSK 300/1200 and G3RUH 9600 modulators, written from the specifications. |
| `aprs_ax25_corpus.txt` | 103 packets: every APRS data type and AX.25 address / information field edge cases. |
| `make_golden.py`, `golden/ax25_vectors.txt` | 749 AX.25 test vectors from `ax25ref.py`, checked by the `ax25goldentest` ctest (`src/ax25golden_test.c`). |
| `interop.py` | Modem level: transmit audio identical between builds; every build decodes every build's audio, every frame bit exact; independent modulators; multimon-ng as an independent decoder; noisy audio. |
| `interfaces.py`, `dw_session.py` | Real `direwolf` processes: KISS TCP / AGW / KISS pty / serial KISS receive and transmit, digipeater, beacons, IGate (local fake APRS-IS), configuration files, connected mode between two instances, stalled clients. |
| `aprs_decode_compare.py` | APRS decoding of 12000 packets (corpus, truncations, substitutions) compared between builds, crash detection, valgrind, positions checked against aprslib. |
| `configs/kitchen_sink.conf` | Configuration using many directives, for the config comparison. |

## Running

Build the fork and the reference(s), e.g. upstream `master` and `dev`, each in
its own CMake build directory (`cmake -DUNITTEST=ON .. && make`).

    python3 test/compat/interop.py --build fork=build --build upstream=../upstream/build \
        --out /tmp/interop --csv interop.csv            # --quick for 44.1/48 kHz only

    python3 test/compat/aprs_decode_compare.py --build fork=build \
        --build upstream=../upstream/build --work /tmp/aprsdec --valgrind

The interface tests run `direwolf` itself (Linux, ALSA's `file` plugin for
transmit audio, so no sound card is needed).  direwolf waits 15 seconds when
started as root, so name an ordinary user in `DW_RUN_AS` and put the binaries
where that user can read them:

    DW_RUN_AS=nobody python3 test/compat/interfaces.py --dut fork=/path/to/fork-build \
        --ref upstream=/path/to/upstream-build --work /tmp/dwrun/if \
        --conf-dir /path/with/sample/confs  [--only rx,tx,digi,beacon,igate,config,connected]

`--only stall` (as root) checks that a client that stops reading does not stop
frames reaching other clients; it lowers `net.ipv4.tcp_wmem` while it runs.

Optional: `numpy` (modulators), `multimon-ng` (independent decoder),
`aprslib` (independent APRS parser), `valgrind`, `socat` (serial KISS).
