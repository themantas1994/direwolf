#!/bin/sh
# WAPR stage 1 pre-registered evaluation (doc/wapr/STAGE1_PREREGISTRATION.md).
#
#   test/wapr/run_stage1_eval.sh BUILD_DIR OUT_DIR
#
# BUILD_DIR holds src/atest and src/gen_packets built from the same commit.
set -e
BUILD=${1:?build directory}
OUT=${2:?output directory}
HERE=$(cd "$(dirname "$0")" && pwd)
SEED=20261010
B="python3 $HERE/wapr_bench.py --seed $SEED --bootstrap 1000 --workdir $OUT/work \
   --atest $BUILD/src/atest --gen-packets $BUILD/src/gen_packets"
mkdir -p "$OUT"

$B --systems F600_ldpc,F600_ldpc_r23,F600_conv --conditions awgn --snr=-2:2:0.25 \
   --frames 300 --noise-seconds 600 --out "$OUT/vhf.csv"
$B --systems afsk1200,afsk1200_fx25,afsk1200_il2p --conditions awgn --snr=3:11:0.5 \
   --frames 300 --out "$OUT/vhf_legacy.csv"
$B --systems H150_ldpc,H150_conv --conditions awgn --snr=-7:-3:0.25 \
   --frames 300 --noise-seconds 600 --out "$OUT/hf_awgn.csv"
$B --systems H150_ldpc,H150_conv --conditions itu_mod --snr=-6:10:1 \
   --frames 300 --out "$OUT/hf_itu_mod.csv"
$B --systems R25_ldpc,R25_conv --conditions awgn --snr=-15.5:-11.5:0.25 \
   --frames 200 --noise-seconds 600 --out "$OUT/robust_awgn.csv"
$B --systems R25_ldpc,R25_conv --conditions itu_mod --snr=-16:0:1 \
   --frames 200 --out "$OUT/robust_itu_mod.csv"
$B --systems afsk300,afsk300_il2p --conditions awgn --snr=-2:6:0.5 \
   --frames 300 --out "$OUT/hf_legacy_awgn.csv"
$B --systems afsk300,afsk300_il2p --conditions itu_mod --snr=0:30:2 \
   --frames 300 --out "$OUT/hf_legacy_itu_mod.csv"
$B --systems H150_ldpc --conditions itu_mod_off --snr=-6:10:1 \
   --frames 300 --out "$OUT/offset_h150.csv"
$B --systems R25_ldpc --conditions itu_mod_off --snr=-16:0:1 \
   --frames 200 --out "$OUT/offset_r25.csv"
