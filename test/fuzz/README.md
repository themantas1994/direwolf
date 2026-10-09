# Fuzzing received packet parsing

`fuzz_rx.c` is a [libFuzzer](https://llvm.org/docs/LibFuzzer.html) harness for
the code that handles packets received over the air or from APRS-IS:
`ax25_from_frame`, `kiss_unwrap`, `ax25_from_text`, the address and frame type
functions, and `decode_aprs` with all the APRS data types it understands
(MIC-E, objects, messages, telemetry, weather, AIS user data, ...).

It is not part of the normal build: it needs clang with the fuzzer and
sanitizer runtimes (`libclang-rt-*-dev` on Debian/Ubuntu).  Build from the
`src` directory of a configured tree (`build/` from the normal cmake build):

    cd src
    clang -g -O1 -fsanitize=fuzzer,address,undefined -fno-sanitize-recover=undefined \
        -DDECAMAIN -DUSE_REGEX_STATIC -Dmain=decode_aprs_main \
        -I. -I../build/src -I../external/geotranz -I../external/misc \
        decode_aprs.c deviceid.c ais.c kiss_frame.c ax25_pad.c dwgpsnmea.c dwgps.c \
        dwgpsd.c serial_port.c symbols.c textcolor.c fcs_calc.c latlong.c log.c \
        telemetry.c tt_text.c ../test/fuzz/fuzz_rx.c \
        ../external/misc/strlcpy.c ../external/misc/strlcat.c -lm -lpthread -o fuzz_rx

Run it from the `data` directory so `tocalls.yaml` is found:

    python3 ../test/fuzz/make_seeds.py /tmp/corpus
    cd ../data
    ASAN_OPTIONS=detect_leaks=0 ../src/fuzz_rx -detect_leaks=0 -max_len=700 -max_total_time=1200 -jobs=3 -workers=3 -artifact_prefix=/tmp/ /tmp/corpus

`-detect_leaks=0` because `deviceid_init` keeps its tables for the life of the
process (and leaks a few hundred bytes once).  Adding
`-ftrivial-auto-var-init=pattern` makes reads of uninitialized stack show up
reliably.

Found with it so far, all fixed: a crash on an AIS sentence missing a field,
an out of bounds read before short MIC-E comments, a read past the end of an
empty AIS sentence, and a signed shift in AIS position decoding.

# Fuzzing KISS input and the FX.25 / IL2P decoders

`fuzz_kiss_fec.c` covers input that `fuzz_rx` does not reach:

* selector 0: a KISS byte stream from a client application (KISS TCP, serial,
  pty), through `kiss_rec_byte`, its state machine and `kiss_process_msg`
  with the TXDELAY / PERSIST / SLOTTIME / TXTAIL / FULLDUP / SETHW commands;
* selector 1: FX.25 as received over the air: a valid correlation tag
  followed by the fuzzer's bytes as the Reed-Solomon block, through
  `fx25_rec_bit` (RS decoding, unstuffing, FCS, frame extraction);
* selector 2: IL2P: preamble and sync word followed by the fuzzer's bytes,
  through `il2p_rec_bit` (header and payload RS decoding, descrambling,
  conversion to AX.25).

Build from `src`, as above:

    clang -g -O1 -fsanitize=fuzzer,address,undefined -fno-sanitize-recover=undefined \
        -DMAJOR_VERSION=1 -DMINOR_VERSION=8 -DUSE_REGEX_STATIC \
        -I. -I../build/src -I../external/geotranz -I../external/misc \
        kiss_frame.c ax25_pad.c ax25_pad2.c fcs_calc.c textcolor.c fx25_rec.c fx25_extract.c \
        fx25_init.c fx25_encode.c il2p_rec.c il2p_codec.c il2p_header.c il2p_payload.c \
        il2p_scramble.c il2p_init.c ../test/fuzz/fuzz_kiss_fec.c -lm -lpthread -o fuzz_kiss_fec

Seeds: build the same files without `-fsanitize=fuzzer` and with `-DMAKE_SEEDS`
(`-o make_seeds_fec`), then

    mkdir /tmp/corpus_fec
    grep -v '^#' ../test/compat/aprs_ax25_corpus.txt | ./make_seeds_fec /tmp/corpus_fec
    # FX.25 seeds: real RS blocks, some with errors, from the fxsend unit test.
    (cd /tmp && ../build/test/fxsend)        # writes fx01.dat ... fx0b.dat
    python3 -c "
    import glob, os
    for f in glob.glob('/tmp/fx*.dat'):
        d = open(f, 'rb').read(); t = int(os.path.basename(f)[2:4], 16)
        open('/tmp/corpus_fec/sel1_fx%02x' % t, 'wb').write(bytes([1, t - 1]) + d[24:-16])"

    ASAN_OPTIONS=detect_leaks=0 ./fuzz_kiss_fec -detect_leaks=0 -max_len=3000 \
        -max_total_time=1800 -jobs=3 -workers=3 /tmp/corpus_fec

`FUZZ_STATS=1 ./fuzz_kiss_fec -runs=0 /tmp/corpus_fec` prints how many frames
came out of each path, to check that the seeds reach the decoders (with the
seeds above: KISS 103, FX.25 12, IL2P 104).
