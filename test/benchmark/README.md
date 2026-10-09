# Receive sensitivity benchmark

`rx_sensitivity.py` measures how many frames a build of `atest` decodes from
weak, noisy and impaired signals, and how many corrupted frames it accepts.
It is not part of the regular `ctest` run because a useful measurement takes
minutes rather than seconds.

Requirements: Python 3.8 or later and numpy.  Nothing else is needed and the
benchmark is not needed to build or run Dire Wolf.

## How it works

1. Distinct, reproducible APRS packets are created from `--seed`.  Each has a
   sequence number and a random payload of `--min-len` to `--max-len` characters.
2. The project's own `gen_packets` converts them to a clean 44.1 kHz WAV file.
   With `--generator numpy` an independent AFSK modulator in the script is used
   instead (300 and 1200 bps): HDLC flags, bit stuffing, NRZI and the
   CRC-16/X.25 FCS written from the AX.25 description, sharing no code with
   `gen_packets`, so a mistake in the project's modulator can't hide the same
   mistake in its demodulator.  Bit rate and tone impairments are applied
   exactly rather than rounded to whole Hz / bps.  A noise free run must
   decode every frame (checked at 22.05, 44.1, 48 and 96 kHz).
3. For each impairment condition and each Eb/N0 point the clean signal is
   impaired, seeded white Gaussian noise is added and a WAV file is written.
4. Every `atest` binary given with `--atest` decodes the same WAV files, with
   `-F0` (no brute force bit fixing) and the `-P` profiles given.
5. A decoded frame is "ok" only if its complete text matches what was sent.
   Anything else decoded is "bad", i.e. a corrupted frame accepted as valid.

Eb/N0 is the energy per bit (average signal power while a frame is being sent,
divided by the bit rate) over the one-sided noise density N0 = 2 sigma^2 / fs.
The SNR in a 3 kHz bandwidth is also listed; for 1200 bps it is Eb/N0 - 4.0 dB.

## Impairment conditions

| name        | what                                                              |
|-------------|-------------------------------------------------------------------|
| `flat`      | AWGN only                                                         |
| `deemph6`   | space tone 6 dB weaker than mark (typical speaker / de-emphasized audio) |
| `deemph10`  | space tone 10 dB weaker                                           |
| `preemph6`  | space tone 6 dB stronger (flat receiver, pre-emphasized transmitter) |
| `fast1pct` / `slow1pct` | transmitter bit rate 1% fast / slow                   |
| `tone+2pct` | both tones 2% high                                                |
| `tone-50hz` | both tones 50 Hz low                                              |
| `ctcss`     | 100 Hz tone 6 dB below the data signal, plus a DC offset           |
| `lowlevel`  | peak signal about 1% of full scale (quantization)                 |
| `clipped`   | signal plus noise overdrives the sound card 3x and is hard clipped |
| `impulse`   | 20 random clicks per second, 2 - 6 times the signal amplitude     |
| `flutter`   | noise level swings +-6 dB at 4 Hz                                 |

The twist conditions only apply to AFSK.  `--noise-only SECONDS` additionally
decodes pure noise; anything decoded there is a false decode.

## Output

* `--out FILE.csv` - one row per condition, profile, binary and Eb/N0 point:
  frames sent / ok / bad / duplicated / repaired (`frames_fixed`) / missed,
  frames gained and lost relative to the first `--atest` binary on the same
  audio, CPU seconds, SHA-256 prefixes of the binaries and of each WAV file,
  and the seed.
* `FILE_summary.csv` - per condition, profile and binary: totals and the
  Eb/N0 needed for 50%, 90% and 99% frame success (`--targets`), both by linear
  interpolation and by a logistic fit with its standard error.  Thresholds
  are blank (`nan`) when the curve does not reach the target inside the
  measured range; widen `--ebn0` in that case.
* `--dump-bad FILE` - every incorrectly accepted frame with what was sent.

## Examples

Compare two builds with the default receive configuration (A+; soft
decision repair is off by default):

    python3 test/benchmark/rx_sensitivity.py \
        --atest old=../old/build/src/atest --atest new=build/src/atest \
        --gen-packets build/src/gen_packets \
        --profiles A+,A --conditions flat,deemph6 --ebn0 6:16:0.5 --frames 300

Compare soft decision repair settings of one build (`atest -S`).  Frames
recovered by repair are counted separately from the plain decoder's
(`frames_fixed`, and `bad_fixed` for corrupted ones):

    printf '#!/bin/sh\nexec build/src/atest -S1 "$@"\n' > /tmp/atest_s1
    chmod +x /tmp/atest_s1
    python3 test/benchmark/rx_sensitivity.py --atest S0=build/src/atest \
        --atest S1=/tmp/atest_s1 --gen-packets build/src/gen_packets \
        --ebn0 7:12:0.5 --frames 1000 --noise-only 3600

Other modes: `--mode 300|1200|2400|4800|9600`.  Other sample rates: `--rate 96000` etc.

Measure with the independent modulator, at another sample rate:

    python3 test/benchmark/rx_sensitivity.py --atest new=build/src/atest \
        --gen-packets build/src/gen_packets --generator numpy --rate 48000 \
        --profiles A+ --conditions flat,fast1pct,tone-50hz --ebn0 6:16:1 --frames 300

The 99% threshold needs many frames per point (1000 or more) to be meaningful.

What it does not measure: real FM receiver audio (discriminator noise is not
white, and squelch tails and fading are not modelled), and false decodes on
real channels, where the frames that fail are not random.  The WA8LMF TNC
test CD recordings cover some of that.

Results depend only on the binaries, the seed and the arguments, so a run can
be repeated exactly.  Around the decoding threshold use at least a few hundred
frames per point.  The `gained_vs_ref` and `lost_vs_ref` columns compare the
binaries frame by frame on identical audio, which is much more sensitive than
comparing two independent decode rates.
