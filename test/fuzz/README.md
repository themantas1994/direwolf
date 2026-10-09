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
