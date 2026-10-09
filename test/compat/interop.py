#!/usr/bin/env python3
"""
Cross implementation transmit / receive compatibility tests.

Compares Dire Wolf builds with each other and with independent code:

  tx-identical   gen_packets of every build makes byte identical audio from
                 the same packets (same modem, same sample rate).
  cross-decode   audio made by each build is decoded by every build's atest;
                 every frame must come out bit for bit as the independent
                 encoder (ax25ref.py) says it should.
  indep-mod      audio from the independent modulators in ax25ref.py
                 (AFSK 300/1200, G3RUH 9600) is decoded by every build.
  multimon       audio made by each build is decoded by multimon-ng, an
                 independent decoder (AFSK1200, FSK9600), if installed.
  noise          gen_packets -n (frames with increasing noise) from one build
                 is decoded by every build.  With soft repair off the fork must
                 decode exactly the frames upstream does; with it on, a
                 superset.

Usage:
  interop.py --build fork=BUILD_DIR --build upstream=BUILD_DIR [--build ...]
             [--dut fork] --out WORK_DIR [--quick] [--csv results.csv]

BUILD_DIR is a CMake build directory containing src/gen_packets and src/atest.
The --dut build is the one with the -S (soft repair) option; it is also run
with -S0.  Exit status is 1 if any check fails.
"""

import argparse
import concurrent.futures as cf
import csv
import hashlib
import os
import re
import shutil
import subprocess
import sys
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ax25ref  # noqa: E402

# name, gen_packets options, atest options, sample rates, expected frames
# ("corpus" = every corpus frame; "count:N" = only a count, for EAS)
MODES = [
    ("300 AFSK",          ["-B", "300"],                 ["-B", "300"],         [11025, 22050, 44100, 48000, 96000]),
    ("1200 AFSK",         ["-B", "1200"],                ["-B", "1200"],        [8000, 11025, 22050, 44100, 48000, 96000, 192000]),
    ("1200 AFSK A+",      ["-B", "1200"],                ["-B", "1200", "-P", "A+"], [22050, 44100, 48000, 96000]),
    ("1200 AFSK B",       ["-B", "1200"],                ["-B", "1200", "-P", "B"],  [22050, 44100, 48000, 96000]),
    ("1200 AFSK AB",      ["-B", "1200"],                ["-B", "1200", "-P", "AB"], [44100, 48000]),
    ("2400 QPSK V.26A",   ["-B", "2400", "-j"],          ["-B", "2400", "-j"],  [44100, 48000, 96000]),
    ("2400 QPSK V.26B",   ["-B", "2400", "-J"],          ["-B", "2400", "-J"],  [44100, 48000, 96000]),
    ("2400 G3RUH",        ["-B", "2400", "-g"],          ["-B", "2400", "-g"],  [44100, 48000]),
    ("4800 8PSK",         ["-B", "4800"],                ["-B", "4800"],        [44100, 48000, 96000]),
    ("9600 G3RUH",        ["-B", "9600"],                ["-B", "9600"],        [44100, 48000, 96000, 192000]),
    ("19200 G3RUH",       ["-B", "19200"],               ["-B", "19200"],       [96000, 192000]),
    ("1200 FX.25 16",     ["-B", "1200", "-X", "16"],    ["-B", "1200"],        [44100, 48000]),
    ("1200 FX.25 64",     ["-B", "1200", "-X", "64"],    ["-B", "1200"],        [44100]),
    ("9600 FX.25 32",     ["-B", "9600", "-X", "32"],    ["-B", "9600"],        [48000, 96000]),
    ("1200 IL2P",         ["-B", "1200", "-I", "1"],     ["-B", "1200"],        [44100, 48000]),
    ("1200 IL2P weak FEC", ["-B", "1200", "-I", "0"],    ["-B", "1200"],        [44100]),
    ("1200 IL2P inverted", ["-B", "1200", "-i", "1"],    ["-B", "1200"],        [44100]),
    ("9600 IL2P",         ["-B", "9600", "-I", "1"],     ["-B", "9600"],        [48000, 96000]),
    ("9600 IL2P inverted", ["-B", "9600", "-i", "1"],    ["-B", "9600"],        [48000]),
]

QUICK_RATES = {44100, 48000}

NOISE_MODES = [
    ("300 AFSK",        ["-B", "300"],         [["-B", "300", "-P", "A"], ["-B", "300", "-P", "B"]]),
    ("1200 AFSK",       [],                    [["-P", "A"], ["-P", "A+"], ["-P", "B"], ["-P", "AB"], ["-P", "A", "-F", "1"]]),
    ("1200 AFSK 96k",   ["-r", "96000"],       [["-P", "A+"], ["-P", "A"]]),
    ("1200 AFSK 22k",   ["-r", "22050"],       [["-P", "A+"], ["-P", "A"]]),
    ("2400 QPSK V.26A", ["-B", "2400", "-j"],  [["-B", "2400", "-j"]]),
    ("2400 QPSK V.26B", ["-B", "2400", "-J"],  [["-B", "2400", "-J"]]),
    ("4800 8PSK",       ["-B", "4800"],        [["-B", "4800"]]),
    ("9600 G3RUH",      ["-B", "9600"],        [["-B", "9600"], ["-B", "9600", "-P", "+"]]),
    ("19200 G3RUH",     ["-B", "19200", "-r", "96000"], [["-B", "19200"]]),
    ("1200 FX.25",      ["-X", "16"],          [[]]),
    ("9600 FX.25",      ["-B", "9600", "-X", "32"], [["-B", "9600"]]),
    ("1200 IL2P",       ["-I", "1"],           [[]]),
    ("9600 IL2P",       ["-B", "9600", "-I", "1"], [["-B", "9600"]]),
]

_HEXLINE = re.compile(r"^\s+[0-9a-f]{3}:\s+((?:[0-9a-f]{2} )+)")
_ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")


def run(cmd, cwd=None, inp=None):
    p = subprocess.run(cmd, cwd=cwd, input=inp, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return p.returncode, p.stdout


def gen(build, args, rate, infile, out):
    cmd = [os.path.join(build, "src", "gen_packets"), "-r", str(rate), "-o", out] + args
    if infile:
        cmd.append(infile)
    rc, txt = run(cmd)
    if rc != 0 or not os.path.exists(out):
        raise RuntimeError("gen_packets failed: %s\n%s" % (" ".join(cmd), txt.decode(errors="replace")))
    return out


def decode(build, wav, args):
    """Frames (bytes, no FCS) decoded by atest, in order."""
    cmd = [os.path.join(build, "src", "atest"), "-h"] + args + [wav]
    rc, out = run(cmd)
    frames, cur = [], None
    for line in _ANSI.sub("", out.decode("latin-1")).splitlines():
        m = _HEXLINE.match(line)
        if m:
            if line.lstrip().startswith("000:"):
                cur = bytearray()
                frames.append(cur)
            cur += bytes.fromhex(m.group(1).replace(" ", ""))
    return [bytes(f) for f in frames]


def md5(path):
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()


def resample_fft(x, r_in, r_out):
    import numpy as np
    n_out = int(round(len(x) * r_out / r_in))
    X = np.fft.rfft(x)
    Y = np.zeros(n_out // 2 + 1, dtype=complex)
    m = min(len(X), len(Y))
    Y[:m] = X[:m]
    return np.fft.irfft(Y, n_out) * (n_out / len(x))


def multimon(wav, demod):
    """
    multimon-ng wants raw 22050 Hz samples.  Other rates are resampled first.
    Returns list of TNC-2 byte strings.
    """
    if shutil.which("multimon-ng") is None:
        return None
    with wave.open(wav) as w:
        assert w.getnchannels() == 1
        rate = w.getframerate()
        raw = w.readframes(w.getnframes())
    if rate != 22050:
        import numpy as np
        x = np.frombuffer(raw, "<i2").astype(float) / 32768.0
        raw = np.clip(resample_fft(x, rate, 22050) * 32767, -32768, 32767).astype("<i2").tobytes()
    rc, out = run(["multimon-ng", "-q", "-A", "-a", demod, "-t", "raw", "-"], inp=raw)
    chunks = out.split(b"APRS: ")[1:]
    return [c[:-1] if c.endswith(b"\n") else c for c in chunks]


def multimon_text(frame):
    """
    The frame as multimon-ng -A prints it: TNC-2 style, info bytes not escaped,
    and (unlike the TNC-2 convention) a '*' after every repeater whose H bit
    is set, not just the last one.
    """
    n = 0
    while not frame[n * 7 + 6] & 1:
        n += 1
    naddr = n + 1

    def fmt(i):
        call, ssid, h, _ = ax25ref.decode_address(frame[i * 7:i * 7 + 7])
        return (call if ssid == 0 else "%s-%d" % (call, ssid)) + ("*" if i >= 2 and h else "")
    hdr = "%s>%s" % (fmt(1), ",".join([fmt(0)] + [fmt(i) for i in range(2, naddr)]))
    return hdr.encode() + b":" + frame[naddr * 7 + 2:]


class Results:
    def __init__(self):
        self.rows = []
        self.failures = 0

    def add(self, test, mode, rate, tx, rx, n_ok, n_exp, note="", fail=None, status=None):
        ok = n_ok == n_exp if fail is None else not fail
        if not ok:
            self.failures += 1
        status = status or ("PASS" if ok else "FAIL")
        self.rows.append(dict(test=test, mode=mode, rate=rate, tx=tx, rx=rx,
                              ok=n_ok, expected=n_exp, status=status, note=note))
        print("%-5s %-12s %-20s %6s  tx=%-14s rx=%-16s %4d/%-4d %s" %
              (status, test, mode, rate, tx, rx, n_ok, n_exp, note), flush=True)


def il2p_expected(frame):
    """
    IL2P header type 1 (used when there are no digipeaters) has a single
    command/response bit, so a frame with both AX.25 C bits set, as APRS
    frames usually are, comes out as a command: destination C = 1, source
    C = 0.  Frames with digipeaters are sent with the transparent header
    type 0 and come out unchanged.  Same in upstream Dire Wolf.
    """
    if len(frame) - 16 > 1023:                 # Too long for IL2P: sent as plain AX.25.
        return frame
    if frame[13] & 0x01 and frame[6] & 0x80 and frame[13] & 0x80:   # No digipeaters, both C bits set.
        frame = bytearray(frame)
        frame[13] &= 0x7F
        return bytes(frame)
    return frame


def compare(got, expected):
    """Number of expected frames found, in order, and number of unexpected extra frames."""
    j = 0
    found = 0
    extra = 0
    for f in got:
        if j < len(expected) and f == expected[j]:
            found += 1
            j += 1
        elif f in expected[j:]:
            k = expected.index(f, j)
            found += 1
            j = k + 1
        else:
            extra += 1
    return found, extra


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--build", action="append", required=True, help="NAME=BUILD_DIR")
    ap.add_argument("--dut", default="fork", help="build with the -S option (default: fork)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--corpus", default=os.path.join(HERE, "aprs_ax25_corpus.txt"))
    ap.add_argument("--quick", action="store_true", help="44.1 and 48 kHz only")
    ap.add_argument("--csv")
    ap.add_argument("--jobs", type=int, default=os.cpu_count() or 2)
    ap.add_argument("--only", default="", help="comma separated: tx,cross,indep,multimon,morse,noise")
    a = ap.parse_args()

    builds = dict(b.split("=", 1) for b in a.build)
    os.makedirs(a.out, exist_ok=True)
    only = set(a.only.split(",")) if a.only else {"tx", "cross", "indep", "multimon", "morse", "noise"}

    corpus = ax25ref.read_corpus(a.corpus)
    corpus_file = os.path.join(a.out, "corpus.txt")
    with open(corpus_file, "w", encoding="utf-8") as f:
        f.write("\n".join(corpus) + "\n")
    # gen_packets reads lines with fgets so the line feed ends up in the info part.
    expected = [ax25ref.tnc2_to_frame(line) + b"\n" for line in corpus]
    expected_nolf = [ax25ref.tnc2_to_frame(line) for line in corpus]

    decoders = [(n, d, []) for n, d in builds.items()]
    if a.dut in builds:
        decoders.append((a.dut + " -S0", builds[a.dut], ["-S", "0"]))

    R = Results()
    pool = cf.ThreadPoolExecutor(max_workers=a.jobs)

    # ---------------- tx-identical and cross-decode ----------------
    if "tx" in only or "cross" in only:
        for mode, gargs, dargs, rates in MODES:
            for rate in rates:
                if a.quick and rate not in QUICK_RATES:
                    continue
                tag = re.sub(r"[^A-Za-z0-9]+", "_", mode) + "_%d" % rate
                wavs = {}
                futs = {n: pool.submit(gen, d, gargs, rate, corpus_file, os.path.join(a.out, "%s_%s.wav" % (tag, n)))
                        for n, d in builds.items()}
                for n, fu in futs.items():
                    wavs[n] = fu.result()
                sums = {n: md5(w) for n, w in wavs.items()}
                ref = sums[next(iter(sums))]
                same = all(s == ref for s in sums.values())
                if "tx" in only:
                    if "FX.25" in mode and not same:
                        # Expected: the fork keeps NRZI continuous between the HDLC
                        # flags and the FX.25 block (gen_tone.c tone_gen_last_bit),
                        # upstream may invert the first bit after each switch.
                        # cross-decode below checks the result is still compatible.
                        R.add("tx-identical", mode, rate, "all", "-", 0, 0,
                              "differs as expected (FX.25 NRZI continuity fix) " +
                              " ".join("%s:%s" % (n, s[:8]) for n, s in sums.items()), fail=False,
                              status="EXPECTED-DIFF")
                    else:
                        R.add("tx-identical", mode, rate, "all", "-", sum(1 for s in sums.values() if s == ref), len(sums),
                              "md5 " + ref[:8] if same else " ".join("%s:%s" % (n, s[:8]) for n, s in sums.items()))
                if "cross" in only:
                    exp = [il2p_expected(f) for f in expected] if "IL2P" in mode else expected
                    jobs = []
                    for tn, w in wavs.items():
                        if same and tn != next(iter(wavs)):
                            continue      # Identical audio, decoding it again proves nothing new.
                        for dn, d, extra in decoders:
                            jobs.append((tn if not same else "any", dn, pool.submit(decode, d, w, dargs + extra)))
                    for tn, dn, fu in jobs:
                        got = fu.result()
                        found, extra = compare(got, exp)
                        R.add("cross-decode", mode, rate, tn, dn, found, len(exp),
                              "extra=%d" % extra if extra else "", fail=(found != len(exp) or extra != 0))

    # ---------------- independent modulators ----------------
    if "indep" in only:
        try:
            import numpy  # noqa: F401
            have_np = True
        except ImportError:
            have_np = False
            print("numpy missing: skipping independent modulator tests")
        if have_np:
            for mode, dargs, rates in [("afsk1200", ["-B", "1200"], [22050, 44100, 48000, 96000]),
                                       ("afsk1200", ["-B", "1200", "-P", "A+"], [44100]),
                                       ("afsk1200", ["-B", "1200", "-P", "B"], [44100]),
                                       ("afsk300", ["-B", "300"], [44100, 48000]),
                                       ("g3ruh9600", ["-B", "9600"], [48000, 96000])]:
                for rate in rates:
                    if a.quick and rate not in QUICK_RATES:
                        continue
                    w = os.path.join(a.out, "indep_%s_%d_%s.wav" % (mode, rate, "".join(dargs[2:]) or "def"))
                    ax25ref.write_wav(w, ax25ref.modulate_frames(expected_nolf, mode, rate), rate)
                    jobs = [(dn, pool.submit(decode, d, w, dargs + extra)) for dn, d, extra in decoders]
                    for dn, fu in jobs:
                        got = fu.result()
                        found, extra = compare(got, expected_nolf)
                        R.add("indep-mod", mode + " " + " ".join(dargs[2:]), rate, "ax25ref", dn, found, len(expected_nolf),
                              "extra=%d" % extra if extra else "", fail=(found != len(expected_nolf) or extra != 0))

    # ---------------- multimon-ng ----------------
    if "multimon" in only:
        if shutil.which("multimon-ng") is None:
            print("multimon-ng not installed: skipping")
        else:
            exp_txt = [multimon_text(f) for f in expected]
            exp_txt_nolf = [multimon_text(f) for f in expected_nolf]
            # multimon-ng runs at 22050 Hz only.  9600 bd generated directly at 22050 Hz
            # (2.3 samples per bit) is decoded poorly by it, whoever generates it, so
            # Dire Wolf's audio is generated at a usual rate and resampled.
            for mode, gargs, demod, imode, grate in [("1200 AFSK", ["-B", "1200"], "AFSK1200", "afsk1200", 44100),
                                                     ("9600 G3RUH", ["-B", "9600"], "FSK9600", "g3ruh9600", 48000)]:
                sources = [(n, gen(d, gargs, grate, corpus_file, os.path.join(a.out, "mm_%s_%s.wav" % (demod, n))), exp_txt)
                           for n, d in builds.items()]
                try:
                    w = os.path.join(a.out, "mm_%s_ax25ref.wav" % demod)
                    ax25ref.write_wav(w, ax25ref.modulate_frames(expected_nolf, imode, 22050), 22050)
                    sources.append(("ax25ref", w, exp_txt_nolf))
                except Exception as e:      # numpy missing
                    print("independent modulator for multimon skipped:", e)
                missed = {}
                for n, w, et in sources:
                    got = multimon(w, demod)
                    missed[n] = [i for i, e in enumerate(et) if e not in got]
                    # multimon-ng's own limits are found by giving it the independent
                    # modulator's audio too: frames it misses from there as well
                    # are not held against the Dire Wolf transmitter.
                    R.add("multimon", mode, 22050, n, "multimon-ng " + demod, len(et) - len(missed[n]), len(et),
                          "missed frames %s" % missed[n] if missed[n] else "")
                for n in builds:
                    worse = [i for i in missed[n] if i not in missed.get("ax25ref", [])]
                    R.add("multimon-cmp", mode, 22050, n, "vs ax25ref audio", len(exp_txt) - len(worse), len(exp_txt),
                          "missed only from Dire Wolf audio: %s" % worse if worse else "same or better than independent audio")

    # ---------------- Morse code (CW identification) ----------------
    # multimon-ng's MORSE_CW decoder is independent of Dire Wolf.  Upstream 1.8.2
    # sends '-' as -...- ('=') because morse.c had two entries for it (upstream
    # PR #597, in dev); the fork has the fix.
    if "morse" in only and shutil.which("multimon-ng"):
        text = "N0CALL-1 W1AW/7 TEST-2 E"     # trailing E: the decoder drops the last character
        for n, d in builds.items():
            w = os.path.join(a.out, "morse_%s.wav" % n)
            cmd = [os.path.join(d, "src", "gen_packets"), "-M", "15", "-r", "22050", "-o", w, "-"]
            subprocess.run(cmd, input=text.encode(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            with wave.open(w) as wf:
                raw = wf.readframes(wf.getnframes())
            rc, out = run(["multimon-ng", "-q", "-a", "MORSE_CW", "-t", "raw", "-"], inp=raw)
            got = out.decode(errors="replace").replace("\n", "").strip()
            ok = got.startswith(text[:-2])
            known = not ok and n != a.dut
            R.add("morse", "CW ident", 22050, n, "multimon-ng MORSE_CW", int(ok), 1,
                  "decoded %r%s" % (got, " (upstream 1.8.2 defect, PR #597)" if known else ""),
                  fail=(not ok and n == a.dut), status="UPSTREAM-DEFECT" if known else None)

    # ---------------- noise ----------------
    if "noise" in only:
        names = list(builds)
        for mode, gargs, dvariants in NOISE_MODES:
            gname = next((n for n in names if n != a.dut), names[0])   # generate with a reference build
            w = gen(builds[gname], gargs + ["-n", "100"], 44100 if "-r" not in gargs else int(gargs[gargs.index("-r") + 1]),
                    None, os.path.join(a.out, "noise_%s.wav" % re.sub(r"[^A-Za-z0-9]+", "_", mode)))
            for dv in dvariants:
                jobs = [(dn, pool.submit(decode, d, w, dv + extra)) for dn, d, extra in decoders]
                res = {dn: fu.result() for dn, fu in jobs}
                ref_names = [n for n in names if n != a.dut]
                for dn, frames in res.items():
                    note = "decoded=%d" % len(frames)
                    fail = False
                    if dn == a.dut + " -S0" and ref_names:
                        same = all(frames == res[r] for r in ref_names)
                        note += " identical to %s" % ",".join(ref_names) if same else " DIFFERS from upstream"
                        fail = not same
                    if dn == a.dut and ref_names:
                        missing = [f for f in res[ref_names[0]] if f not in frames]
                        note += " missing_vs_%s=%d" % (ref_names[0], len(missing))
                        fail = bool(missing)
                    R.add("noise", mode, "-", gname, dn + " " + " ".join(dv), len(frames), len(frames), note, fail=fail)

    pool.shutdown()
    if a.csv:
        with open(a.csv, "w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=list(R.rows[0].keys()), lineterminator="\n")
            w.writeheader()
            w.writerows(R.rows)
    print("\n%d checks, %d failed" % (len(R.rows), R.failures))
    return 1 if R.failures else 0


if __name__ == "__main__":
    sys.exit(main())
