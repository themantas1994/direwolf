#!/bin/bash
# Build matrix on the branch HEAD (round 3 final).  Writes a summary to $OUT/build_matrix.txt.
# Paths: S = scratch directory (builds go to $S/bm2, results to $S/final/bm; $S/build/fork and
# $S/build/upmaster hold Release builds of the fork and of upstream 1.8.2, $S/wineprefix a wine prefix),
# SRC = fork source tree, UPSRC = upstream 1.8.2 source tree.  As run in round 3:
S=/tmp/claude-0/-home-user-direwolf/a48a3e59-4ccf-5798-9957-6d53537fbf24/scratchpad
SRC=/home/user/direwolf
UPSRC=$S/src/up-master
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=$S/final/bm
mkdir -p $OUT
SUM=$OUT/build_matrix.txt
: > $SUM
echo "HEAD $(cd $SRC && git rev-parse HEAD)" >> $SUM
TESTS="ax25goldentest l2sendtest aistest deviceidtest kisstest pad2test xidtest lltest enctest tlmtest pftest dtest ttest tttexttest dtmftest il2p_test fxsend fxrec"

build() { name=$1; src=$2; shift 2; d=$S/bm2/$name; rm -rf $d; mkdir -p $d; cd $d
  "$@" $src > cmake.log 2>&1 && nice make -j2 > make.log 2>&1; b=$?
  cp make.log $OUT/$name.make.log
  w=$(grep -c "warning:" make.log)
  echo "$name: build rc=$b, compiler warnings=$w" >> $SUM; }

runctest() { name=$1; shift; cd $S/bm2/$name; env "$@" nice ctest -j2 > ctest.log 2>&1; cp ctest.log $OUT/$name.ctest.log
  echo "$name: ctest $* : $(grep -E 'tests passed' ctest.log)" >> $SUM
  grep -E '\*\*\*Failed|Failed  ' ctest.log | sed "s/^/    /" >> $SUM; }

build gcc-debug $SRC cmake -DCMAKE_BUILD_TYPE=Debug -DUNITTEST=ON
runctest gcc-debug
build clang-release $SRC env CC=clang CXX=clang++ cmake -DCMAKE_BUILD_TYPE=Release -DUNITTEST=ON
runctest clang-release
build up-clang-release $UPSRC env CC=clang CXX=clang++ cmake -DCMAKE_BUILD_TYPE=Release -DUNITTEST=ON
for n in clang-release up-clang-release; do
  echo "$n warnings by kind:" >> $SUM
  grep -o '\[-W[a-z0-9=-]*\]' $S/bm2/$n/make.log | sort | uniq -c | sort -rn | sed 's/^/    /' >> $SUM
done
echo "clang warnings (file:line: text) in fork but not in upstream 1.8.2:" >> $SUM
for n in clang-release up-clang-release; do grep -E '^/.*warning:' $S/bm2/$n/make.log | sed -E "s#^[^ ]*/src/##; s#:[0-9]+:[0-9]+:#:#" | sort -u > $OUT/$n.warnset; done
comm -23 $OUT/clang-release.warnset $OUT/up-clang-release.warnset | sed 's/^/    /' >> $SUM
build gcc-asan-ubsan $SRC env CFLAGS="-fsanitize=address,undefined -fno-omit-frame-pointer -fno-sanitize-recover=undefined" LDFLAGS="-fsanitize=address,undefined" cmake -DCMAKE_BUILD_TYPE=Debug -DUNITTEST=ON
runctest gcc-asan-ubsan ASAN_OPTIONS=detect_leaks=1
runctest gcc-asan-ubsan ASAN_OPTIONS=detect_leaks=0

# Windows, MinGW cross build, unit tests and modem script lines under wine.
mkdir -p $S/bm2/mingw; cp $HERE/toolchain-mingw.cmake $S/bm2/mingw.toolchain
build mingw $SRC cmake -DCMAKE_TOOLCHAIN_FILE=$S/bm2/mingw.toolchain -DCMAKE_BUILD_TYPE=Release -DUNITTEST=ON
export WINEPREFIX=$S/wineprefix WINEDEBUG=-all
W=/usr/lib/wine/wine64
pass=0; n=0
for t in $TESTS; do cd $S/bm2/mingw; [ "$t" = ax25goldentest ] && cd $SRC/test/compat/golden; [ "$t" = deviceidtest ] && cd $SRC/data
  timeout 600 $W $S/bm2/mingw/test/$t.exe > $OUT/mingw.$t.log 2>&1; rc=$?; n=$((n+1)); [ $rc = 0 ] && pass=$((pass+1)) || echo "    mingw $t rc=$rc" >> $SUM; done
echo "mingw: unit test programs under wine: $pass/$n exit 0" >> $SUM
mkdir -p $S/bm2/wintest; cd $S/bm2/wintest; cp $SRC/test/compat/aprs_ax25_corpus.txt corpus.txt
fails=0; total=0
for f in check-modem1200 check-modem9600 check-modem300 check-fx25 check-modem2400-b check-modem2400-a check-modem4800 check-modem1200-i check-modem9600-i check-modem2400-g check-modem19200; do
  [ -f $SRC/test/scripts/$f ] || continue
  while read -r line; do cmd=$(echo "$line" | sed "s#@GEN_PACKETS_BIN@#$W $S/bm2/mingw/src/gen_packets.exe#; s#@ATEST_BIN@#$W $S/bm2/mingw/src/atest.exe#; s#@FXSEND_BIN@#true#; s#@FXREC_BIN@#true#"); case "$cmd" in *SHABANG*|"") continue;; esac
    total=$((total+1)); if ! eval "timeout 900 $cmd" >/dev/null 2>&1; then fails=$((fails+1)); echo "    mingw FAIL: $line" >> $SUM; fi; done < $SRC/test/scripts/$f; done
echo "mingw: modem script lines under wine: $total run, $fails failed" >> $SUM
for m in "-B 1200" "-B 300" "-B 9600" "-B 2400 -J" "-B 4800" "-B 1200 -X 16" "-B 9600 -I 1"; do
  $W $S/bm2/mingw/src/gen_packets.exe $m -r 48000 -o win.wav corpus.txt >/dev/null 2>&1; $S/build/fork/src/gen_packets $m -r 48000 -o lin.wav corpus.txt >/dev/null 2>&1
  a=$(md5sum < win.wav | cut -c1-8); b=$(md5sum < lin.wav | cut -c1-8)
  dw=$($W $S/bm2/mingw/src/atest.exe ${m%% -[XI]*} lin.wav 2>&1 | grep -oE "^[0-9]+ packets decoded"); dl=$($S/build/upmaster/src/atest ${m%% -[XI]*} win.wav 2>&1 | grep -oE "^[0-9]+ packets decoded")
  echo "mingw: gen_packets $m: wav windows=$a linux=$b; windows atest on linux audio: $dw; upstream linux atest on windows audio: $dl" >> $SUM; done

# Linux aarch64 cross build, unit tests and modem lines under qemu-user.
cp $HERE/toolchain-aarch64.cmake $S/bm2/aarch64.toolchain
build aarch64 $SRC cmake -DCMAKE_TOOLCHAIN_FILE=$S/bm2/aarch64.toolchain -DCMAKE_BUILD_TYPE=Release -DUNITTEST=ON -DFORCE_SSE=OFF -DOPTIONAL_DNSSD=OFF
Q="qemu-aarch64 -L /usr/aarch64-linux-gnu -E LD_LIBRARY_PATH=/usr/lib/aarch64-linux-gnu"
pass=0; n=0
for t in $TESTS; do cd $S/bm2/aarch64; [ "$t" = ax25goldentest ] && cd $SRC/test/compat/golden; [ "$t" = deviceidtest ] && cd $SRC/data
  timeout 900 $Q $S/bm2/aarch64/test/$t > $OUT/aarch64.$t.log 2>&1; rc=$?; n=$((n+1)); [ $rc = 0 ] && pass=$((pass+1)) || echo "    aarch64 $t rc=$rc" >> $SUM; done
echo "aarch64: unit test programs under qemu: $pass/$n exit 0" >> $SUM
mkdir -p $S/bm2/armtest; cd $S/bm2/armtest; cp $SRC/test/compat/aprs_ax25_corpus.txt corpus.txt
fails=0; total=0
for f in check-modem1200 check-modem9600 check-modem2400-b; do
  while read -r line; do cmd=$(echo "$line" | sed "s#@GEN_PACKETS_BIN@#$Q $S/bm2/aarch64/src/gen_packets#; s#@ATEST_BIN@#$Q $S/bm2/aarch64/src/atest#"); case "$cmd" in *SHABANG*|"") continue;; esac
    total=$((total+1)); if ! eval "timeout 900 $cmd" >/dev/null 2>&1; then fails=$((fails+1)); echo "    aarch64 FAIL: $line" >> $SUM; fi; done < $SRC/test/scripts/$f; done
echo "aarch64: modem script lines under qemu: $total run, $fails failed" >> $SUM
for m in "-B 1200" "-B 9600" "-B 2400 -J" "-B 1200 -X 16"; do
  $Q $S/bm2/aarch64/src/gen_packets $m -r 48000 -o arm.wav corpus.txt >/dev/null 2>&1; $S/build/fork/src/gen_packets $m -r 48000 -o x86.wav corpus.txt >/dev/null 2>&1
  a=$(md5sum < arm.wav | cut -c1-8); b=$(md5sum < x86.wav | cut -c1-8)
  da=$(timeout 900 $Q $S/bm2/aarch64/src/atest ${m%% -[XI]*} x86.wav 2>&1 | grep -oE "^[0-9]+ packets decoded"); dx=$($S/build/upmaster/src/atest ${m%% -[XI]*} arm.wav 2>&1 | grep -oE "^[0-9]+ packets decoded")
  echo "aarch64: gen_packets $m: wav arm=$a x86=$b; arm atest on x86 audio: $da; upstream x86 atest on arm audio: $dx" >> $SUM; done
echo "done $(date)" >> $SUM
