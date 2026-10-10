# WAPR research simulator and reference implementation

WAPR (Weak-signal Adaptive Packet Radio) is an **experimental** research project.  This
directory holds a stand-alone simulator used to compare candidate waveforms and codes
before anything is built into Dire Wolf.  Nothing here is compiled into or changes Dire
Wolf, and nothing here is a claim about on-air performance.

Requirements: Python 3.8+ and numpy (the same as `test/benchmark`).  Legacy comparisons
also need this repository's `gen_packets` and `atest`.

| File | Purpose |
|---|---|
| `wapr_codes.py` | CRC-24, scrambler, K=7 r=1/2 convolutional code (soft Viterbi), IRA LDPC built by PEG (sum-product decoder) |
| `wapr_phy.py` | Framing, Costas sync, GFSK M-FSK modulator, receiver (search, refine, soft bits, decode) |
| `wapr_channel.py` | AWGN, frequency offset, Watterson HF fading; **SNR definition** |
| `wapr_bench.py` | Delivery vs SNR for WAPR and legacy systems through the same channel, with confidence intervals |
| `wapr_selftest.py` | Acceptance checks: codes, channel calibration, SER vs theory, loopback, determinism |
| `wapr_compare.py` | Applies the stage 1 decision rule to two systems' thresholds |
| `run_stage1_eval.sh` | The pre-registered stage 1 evaluation |
| `configs/stage1.json` | Candidate systems.  Each result row records this file's SHA-256 prefix |
| `wapr_frame.py` | Logical frame format v0 (header packing and checks), reference for the C code |
| `wapr_export.py` | Writes `src/wapr_tables.h` and `golden/wapr_vectors.txt` from the reference |
| `wapr_crosscheck.py` | Decodes identical audio with this simulator and the C receiver (`waprtest -r`) |

## Definitions

* **SNR_2500** = mean transmitted signal power while on the air / (N0 x 2500 Hz).
  Noise is white, sample variance N0 fs / 2, so results do not depend on the sample rate.
  With fading, the paths have unit total *mean* power.
* **Eb/N0** = energy per **payload** bit = SNR_2500 + 10 log10(2500 x airtime / payload bits).
  Headers, CRC, sync and FEC overhead are charged to the system that has them.
* **Airtime**: WAPR, the whole frame.  Legacy, the transmission `gen_packets` makes
  less 30 of its 32 preamble bytes (2 flags or IL2P preamble bytes kept).  TXDELAY is
  excluded for both.
* **Delivered**: the decoded payload equals what was sent.  Anything else that passes
  the integrity check is a **false accept**.
* Thresholds: SNR at 50 % / 90 % delivery from a binomial logistic fit; 95 % intervals
  from a parametric bootstrap (`--bootstrap`).  Per-point delivery has a Wilson interval.

The legacy messages carry a 32 byte APRS information part (newline included); WAPR
frames carry a 32 byte payload plus a 12 byte header (the budget for addresses, type,
length and sequence number; random bits in the simulation)
and a CRC-24.  Payloads and channel draws (fading, offsets) come from generators seeded
by (seed, condition, SNR, frame number), the same for every system.

## Usage

    python3 wapr_selftest.py
    python3 wapr_bench.py --systems F600_ldpc,afsk1200_il2p --conditions awgn \
        --snr=-4:10:1 --frames 300 --seed 1 \
        --atest ../../build/src/atest --gen-packets ../../build/src/gen_packets \
        --out results.csv --noise-seconds 600

Outputs: `results.csv` (per point), `results_summary.csv` (thresholds with intervals),
`results_bootstrap.npz` (bootstrap replicates, for differences between systems),
`results_noise.csv` (sync candidates and false decodes on pure noise).

`--snr-shift name=dB` moves one system's sweep so each one is measured around its own
threshold.

## Licensing and attribution

All code here is original to this project (GPL v2 or later, like Dire Wolf).  FT8 and
FT4 (WSJT-X, GPL v3) inspired the use of Costas array sync, GFSK shaping and soft
decision LDPC decoding of non-coherent M-FSK; no WSJT-X code, matrices or tables were
used.  The Costas arrays are the lexicographically first ones of each order, found by
search; the LDPC code is built by progressive edge growth from a fixed seed.
