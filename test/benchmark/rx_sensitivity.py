#!/usr/bin/env python3
"""
Reproducible receive-sensitivity benchmark for Dire Wolf demodulators.

Valid frames are generated with the project's own gen_packets tool, impaired
and mixed with seeded white Gaussian noise, then decoded with one or more atest
binaries.  Every binary decodes byte-identical audio files, so results can be
compared frame by frame.

Requirements: Python 3.8+, numpy.  (Benchmark only - not needed to build.)

Example - compare two builds at 1200 bps, direwolf default demodulator (A+):

    python3 test/benchmark/rx_sensitivity.py \\
        --atest base=/path/to/old/atest --atest new=build/src/atest \\
        --gen-packets build/src/gen_packets --profiles A+,A \\
        --conditions flat,deemph6 --ebn0 4:14:0.5 --frames 200 \\
        --out results.csv

Signal-to-noise ratio is expressed as Eb/N0 (dB), using the average power of
the signal while it is being transmitted (gaps between frames excluded) and the
one-sided noise density N0 = 2 * sigma^2 / fs of real white noise.  The SNR in a
3 kHz bandwidth is also recorded for reference.

A decoded frame counts as "ok" only if its complete monitor-format text matches
the frame that was transmitted with that sequence number.  Anything else that
is decoded is counted as "bad" (a corrupt frame accepted by the receiver).
"""

import argparse
import concurrent.futures
import csv
import hashlib
import math
import os
import re
import resource
import subprocess
import sys
import time
import wave

import numpy as np

# ----------------------------------------------------------------------------
# Modes and impairment conditions
# ----------------------------------------------------------------------------

# gen: arguments for gen_packets, rx: arguments for atest, rb: bits per second.
MODES = {
    '300':   dict(gen=['-B', '300'],  rx=['-B', '300'],  rb=300,  mark=1600, space=1800, baud=300),
    '1200':  dict(gen=['-B', '1200'], rx=['-B', '1200'], rb=1200, mark=1200, space=2200, baud=1200),
    '2400':  dict(gen=['-B', '2400', '-J'], rx=['-B', '2400', '-J'], rb=2400),  # V.26 B, MFJ-2400 compatible
    '4800':  dict(gen=['-B', '4800'], rx=['-B', '4800'], rb=4800),
    '9600':  dict(gen=['-B', '9600'], rx=['-B', '9600'], rb=9600),
}

# Each condition may add gen_packets arguments ('gen'), apply a tone tilt
# ('twist_db' = gain at the space tone relative to the mark tone, AFSK only),
# add interference, change level/clipping, or vary the noise level over time.
CONDITIONS = {
    'flat':      dict(),
    'deemph6':   dict(twist_db=-6.0),          # receiver de-emphasis: space tone 6 dB weaker
    'preemph6':  dict(twist_db=+6.0),          # flat receiver, pre-emphasized transmitter
    'deemph10':  dict(twist_db=-10.0),
    'fast1pct':  dict(baud_scale=1.01),        # transmitter bit clock 1% fast
    'slow1pct':  dict(baud_scale=0.99),        # transmitter bit clock 1% slow
    'tone+2pct': dict(tone_scale=1.02),        # both tones 2% high (e.g. 1224/2244 Hz)
    'tone-50hz': dict(tone_offset=-50),        # both tones shifted -50 Hz
    'ctcss':     dict(ctcss_db=-6.0, dc=0.05), # unfiltered 100 Hz CTCSS tone + DC offset
    'lowlevel':  dict(level=0.01),             # peak signal about 1% of full scale
    'clipped':   dict(level=3.0),              # heavily clipped by the sound card
    'impulse':   dict(impulses_per_s=20.0),    # random clicks
    'flutter':   dict(flutter_db=6.0, flutter_hz=4.0),  # noise level varies +-6 dB at 4 Hz
}

FS = 44100


# ----------------------------------------------------------------------------
# Test messages
# ----------------------------------------------------------------------------

PAYLOAD_CHARS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,;:!?/()=+-*#@'


def make_messages(n, seed, min_len, max_len):
    """Deterministic, distinct packets in TNC2 monitor format."""
    rng = np.random.default_rng([seed, 0x5EED])
    msgs = []
    for i in range(n):
        ln = int(rng.integers(min_len, max_len + 1))
        body = ''.join(PAYLOAD_CHARS[k] for k in rng.integers(0, len(PAYLOAD_CHARS), ln))
        src = 'N0CALL' + ('-%d' % (i % 16) if i % 16 else '')   # atest omits SSID 0
        path = ['', ',WIDE1-1', ',WIDE1-1,WIDE2-1'][i % 3]
        msgs.append('%s>APDWBN%s:>%05d %s' % (src, path, i, body))
    return msgs


# ----------------------------------------------------------------------------
# WAV I/O
# ----------------------------------------------------------------------------

def read_wav(path):
    with wave.open(path, 'rb') as w:
        assert w.getnchannels() == 1 and w.getsampwidth() == 2, 'expect 16 bit mono'
        fs = w.getframerate()
        data = np.frombuffer(w.readframes(w.getnframes()), dtype='<i2').astype(np.float64)
    return fs, data


def write_wav(path, fs, x):
    """Write float samples (full scale = 32767), with hard clipping."""
    clipped = int(np.count_nonzero(np.abs(x) > 32767))
    y = np.clip(np.round(x), -32768, 32767).astype('<i2')
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(fs)
        w.writeframes(y.tobytes())
    return clipped


# ----------------------------------------------------------------------------
# Impairments
# ----------------------------------------------------------------------------

def apply_twist(x, fs, f_mark, f_space, twist_db):
    """Gain linear in dB vs log(frequency): 0 dB at mark, twist_db at space."""
    n = len(x)
    nfft = 1 << (n - 1).bit_length()
    X = np.fft.rfft(x, nfft)
    f = np.fft.rfftfreq(nfft, 1.0 / fs)
    f = np.maximum(f, 50.0)
    slope = twist_db / math.log(f_space / f_mark)
    g_db = np.clip(slope * np.log(f / f_mark), -30.0, 30.0)
    X *= 10.0 ** (g_db / 20.0)
    return np.fft.irfft(X, nfft)[:n]


def active_mask(clean, fs, baud):
    """True where a frame is being transmitted (gen_packets leaves exact zeros between frames)."""
    nz = (clean != 0).astype(np.float64)
    w = max(3, int(fs / baud))   # bridge zero crossings
    return np.convolve(nz, np.ones(w), mode='same') > 0


def impair(clean, fs, mode, cond, ebn0_db, rng):
    """Return (impaired float signal in int16 units, metadata dict)."""
    m = MODES[mode]
    sig = clean.copy()
    act = active_mask(clean, fs, m['rb'])

    if cond.get('twist_db') and 'mark' in m:
        sig = apply_twist(sig, fs, m['mark'], m['space'], cond['twist_db'])

    ps = float(np.mean(sig[act] ** 2))           # signal power while transmitting
    n0 = ps / (m['rb'] * 10.0 ** (ebn0_db / 10.0))  # Eb/N0 = (ps/rb)/n0
    sigma = math.sqrt(n0 * fs / 2.0)
    noise = rng.standard_normal(len(sig)) * sigma

    if cond.get('flutter_db'):
        t = np.arange(len(sig)) / fs
        noise *= 10.0 ** (cond['flutter_db'] / 20.0 * np.sin(2 * math.pi * cond['flutter_hz'] * t))

    x = sig + noise
    a = math.sqrt(2.0 * ps)                        # equivalent sine amplitude

    if cond.get('ctcss_db') is not None:
        t = np.arange(len(x)) / fs
        x += a * 10.0 ** (cond['ctcss_db'] / 20.0) * np.sin(2 * math.pi * 100.0 * t)
    if cond.get('dc'):
        x += cond['dc'] * 32767.0
    if cond.get('impulses_per_s'):
        k = rng.poisson(cond['impulses_per_s'] * len(x) / fs)
        pos = rng.integers(0, len(x) - 8, k)
        amp = rng.choice([-1.0, 1.0], k) * a * rng.uniform(2.0, 6.0, k)
        for p, v in zip(pos, amp):
            x[p:p + 8] += v * np.exp(-np.arange(8) / 2.0)

    if cond.get('level') is not None:
        # Scale so the clean signal peak is level * full scale.
        x *= cond['level'] * 32767.0 / max(1.0, float(np.max(np.abs(sig))))

    snr3k_db = 10 * math.log10(ps / (sigma ** 2 * 3000.0 / (fs / 2.0)))
    return x, dict(snr3k_db=snr3k_db, sigma=sigma, ps=ps)


# ----------------------------------------------------------------------------
# Decoding
# ----------------------------------------------------------------------------

ANSI = re.compile(r'\x1b\[[0-9;]*[A-Za-z]')
FRAME = re.compile(r'^\[\d+[^\]]*\] (.*)$')
RETRY = re.compile(r'\[(SINGLE|DOUBLE|TRIPLE|TWO_SEP|PASSALL)\]')
SEQ = re.compile(r':>(\d{5}) ')


def run_atest(atest, args, wav_path):
    """Run atest in this worker process.

    Returns ([(frame text, repaired?)], user+sys CPU seconds, wall seconds).
    A frame is "repaired" if atest reports bits were changed to get a good FCS.
    """
    r0 = resource.getrusage(resource.RUSAGE_CHILDREN)
    t0 = time.time()
    p = subprocess.run([atest] + args + [wav_path], stdout=subprocess.PIPE,
                       stderr=subprocess.STDOUT, check=False)
    wall = time.time() - t0
    r1 = resource.getrusage(resource.RUSAGE_CHILDREN)
    cpu = (r1.ru_utime - r0.ru_utime) + (r1.ru_stime - r0.ru_stime)
    out = ANSI.sub('', p.stdout.decode('latin-1'))
    frames = []
    repaired = False
    for line in out.splitlines():
        line = line.rstrip('\r')
        if line.startswith('DECODED['):
            repaired = RETRY.search(line) is not None
            continue
        mm = FRAME.match(line)
        if mm:
            frames.append((mm.group(1).rstrip(), repaired))
            repaired = False
    return frames, cpu, wall


def score(frames, expected):
    """expected: dict seq -> text.

    Returns (ok set, bad count, dup count, ok frames repaired, bad frames repaired).
    """
    ok = set()
    bad = dup = ok_fixed = bad_fixed = 0
    for f, repaired in frames:
        mm = SEQ.search(f)
        seq = int(mm.group(1)) if mm else None
        if seq is not None and expected.get(seq) == f:
            if seq in ok:
                dup += 1
            else:
                ok_fixed += repaired
            ok.add(seq)
        else:
            bad += 1
            bad_fixed += repaired
    return ok, bad, dup, ok_fixed, bad_fixed


def decode_job(job):
    frames, cpu, wall = run_atest(job['atest'], job['args'], job['wav'])
    return job, frames, cpu, wall


# ----------------------------------------------------------------------------
# Statistics
# ----------------------------------------------------------------------------

def logistic_threshold(xs, ks, ns, target):
    """Binomial MLE fit of p = 1/(1+exp(-(a+b x))).  Returns (x_target, std err) or (nan, nan)."""
    xs = np.asarray(xs, float)
    ks = np.asarray(ks, float)
    ns = np.asarray(ns, float)
    if len(xs) < 3 or ks.sum() == 0 or ks.sum() == ns.sum():
        return float('nan'), float('nan')
    X = np.column_stack([np.ones_like(xs), xs])
    beta = np.array([-xs.mean(), 1.0])
    for _ in range(100):
        p = 1 / (1 + np.exp(-np.clip(X @ beta, -50, 50)))
        p = np.clip(p, 1e-9, 1 - 1e-9)
        W = ns * p * (1 - p)
        g = X.T @ (ks - ns * p)
        H = X.T @ (X * W[:, None])
        try:
            step = np.linalg.solve(H, g)
        except np.linalg.LinAlgError:
            return float('nan'), float('nan')
        beta += step
        if np.max(np.abs(step)) < 1e-9:
            break
    a, b = beta
    if b <= 0:
        return float('nan'), float('nan')
    lt = math.log(target / (1 - target))
    xt = (lt - a) / b
    if not xs.min() <= xt <= xs.max():
        return float('nan'), float('nan')   # don't extrapolate
    try:
        cov = np.linalg.inv(H)
    except np.linalg.LinAlgError:
        return xt, float('nan')
    grad = np.array([-1 / b, -(lt - a) / b ** 2])
    return xt, float(math.sqrt(max(0.0, grad @ cov @ grad)))


def interp_threshold(xs, ps, target):
    """Last upward crossing of the target by linear interpolation (xs ascending).

    nan if the target is never reached, -inf if already reached at the lowest point.
    """
    if len(ps) and ps[0] >= target and min(ps) >= target:
        return float('-inf')
    for i in range(len(xs) - 1, 0, -1):
        if ps[i - 1] < target <= ps[i]:
            return xs[i - 1] + (target - ps[i - 1]) * (xs[i] - xs[i - 1]) / (ps[i] - ps[i - 1])
    return float('nan')


# ----------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------

def parse_range(s):
    if ':' in s:
        a, b, st = (float(v) for v in s.split(':'))
        n = int(round((b - a) / st)) + 1
        return [round(a + i * st, 3) for i in range(n)]
    return [float(v) for v in s.split(',')]


def file_sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()[:12]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--atest', action='append', required=True, metavar='LABEL=PATH',
                    help='atest binary to evaluate; repeat to compare several builds (first is the reference)')
    ap.add_argument('--gen-packets', required=True, help='gen_packets binary used to create the clean signal')
    ap.add_argument('--mode', default='1200', choices=sorted(MODES))
    ap.add_argument('--profiles', default='A+', help='comma separated atest -P values ("" = atest default)')
    ap.add_argument('--extra', default='', help='extra atest arguments, e.g. "-D3"')
    ap.add_argument('--conditions', default='flat', help='comma separated: ' + ','.join(CONDITIONS))
    ap.add_argument('--ebn0', default='4:14:1', help='Eb/N0 points in dB, "start:stop:step" or list')
    ap.add_argument('--noise-only', type=float, default=0.0, metavar='SECONDS',
                    help='also decode this much pure noise and count false decodes')
    ap.add_argument('--rate', type=int, default=FS, help='audio sample rate (default %d)' % FS)
    ap.add_argument('--frames', type=int, default=200)
    ap.add_argument('--min-len', type=int, default=10, help='minimum info payload characters')
    ap.add_argument('--max-len', type=int, default=120, help='maximum info payload characters')
    ap.add_argument('--seed', type=int, default=1)
    ap.add_argument('--jobs', type=int, default=os.cpu_count() or 1)
    ap.add_argument('--workdir', default='rx_bench_work')
    ap.add_argument('--keep-wav', action='store_true')
    ap.add_argument('--out', default='rx_sensitivity.csv', help='per point results (CSV)')
    ap.add_argument('--summary', default=None, help='threshold summary (CSV), default <out>_summary.csv')
    ap.add_argument('--targets', default='0.5,0.9', help='decode probabilities for threshold report')
    ap.add_argument('--dump-bad', default=None, metavar='FILE',
                    help='append every incorrectly accepted frame (and what was sent) to this file')
    a = ap.parse_args()

    binaries = []
    for spec in a.atest:
        label, _, path = spec.partition('=')
        if not path:
            label, path = os.path.basename(spec), spec
        binaries.append((label, os.path.abspath(path)))
    profiles = a.profiles.split(',')
    conds = a.conditions.split(',')
    for c in conds:
        if c not in CONDITIONS:
            sys.exit('unknown condition ' + c)
    points = parse_range(a.ebn0)
    targets = [float(t) for t in a.targets.split(',')]
    extra = a.extra.split()
    mode = MODES[a.mode]
    os.makedirs(a.workdir, exist_ok=True)

    msgs = make_messages(a.frames, a.seed, a.min_len, a.max_len)
    msg_file = os.path.join(a.workdir, 'messages_%d_%d.txt' % (a.seed, a.frames))
    with open(msg_file, 'w') as f:
        f.write(''.join(m + '\n' for m in msgs))
    # gen_packets keeps the trailing newline in the info part; atest shows it as <0x0a>.
    expected = {i: m + '<0x0a>' for i, m in enumerate(msgs)}

    meta = dict(mode=a.mode, seed=a.seed, frames=a.frames, rate=a.rate, extra=' '.join(extra),
                gen_packets_sha=file_sha(a.gen_packets))
    shas = {label: file_sha(path) for label, path in binaries}

    rows = []
    t_start = time.time()
    pool = concurrent.futures.ProcessPoolExecutor(max_workers=a.jobs)

    for ci, cname in enumerate(conds):
        cond = CONDITIONS[cname]
        gen = list(mode['gen'])
        if 'baud' in mode:
            baud = round(mode['baud'] * cond.get('baud_scale', 1.0))
            ts = cond.get('tone_scale', 1.0)
            off = cond.get('tone_offset', 0)
            gen += ['-b', str(baud), '-m', str(round(mode['mark'] * ts + off)),
                    '-s', str(round(mode['space'] * ts + off))]
        clean_wav = os.path.join(a.workdir, 'clean_%s_%s_%d_%d_%d.wav' % (a.mode, cname, a.seed, a.frames, a.rate))
        subprocess.run([a.gen_packets, '-r', str(a.rate)] + gen + ['-o', clean_wav, msg_file],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
        fs, clean = read_wav(clean_wav)
        audio_s = len(clean) / fs

        jobs = []
        info = {}
        for pi, ebn0 in enumerate(points):
            rng = np.random.default_rng([a.seed, ci, int(round(ebn0 * 1000)) + 100000])
            x, im = impair(clean, fs, a.mode, cond, ebn0, rng)
            wav = os.path.join(a.workdir, 'n_%s_%s_%+.2f.wav' % (a.mode, cname, ebn0))
            clipped = write_wav(wav, fs, x)
            info[wav] = dict(ebn0=ebn0, clipped=clipped, sha=file_sha(wav), **im)
            for prof in profiles:
                for label, path in binaries:
                    args = list(mode['rx']) + (['-P', prof] if prof else []) + ['-F0'] + extra
                    jobs.append(dict(atest=path, label=label, args=args, wav=wav, prof=prof, cond=cname))

        results = {}
        for job, frames, cpu, wall in pool.map(decode_job, jobs):
            results[(job['wav'], job['prof'], job['label'])] = (frames, cpu, wall)

        for prof in profiles:
            for wav in sorted(info, key=lambda w: info[w]['ebn0']):
                ref_ok = None
                for label, _ in binaries:
                    frames, cpu, wall = results[(wav, prof, label)]
                    ok, bad, dup, ok_fixed, bad_fixed = score(frames, expected)
                    if bad and a.dump_bad:
                        with open(a.dump_bad, 'a') as f:
                            for text, repaired in frames:
                                mm = SEQ.search(text)
                                sent = expected.get(int(mm.group(1))) if mm else None
                                if sent != text:
                                    f.write('%s %s %s Eb/N0=%s repaired=%d\n  got:  %r\n  sent: %r\n' % (
                                        cname, prof, label, info[wav]['ebn0'], repaired, text, sent))
                    if ref_ok is None:
                        ref_ok = ok
                    rows.append(dict(
                        condition=cname, profile=prof or 'default', binary=label, binary_sha=shas[label],
                        ebn0_db=info[wav]['ebn0'], snr3k_db=round(info[wav]['snr3k_db'], 2),
                        frames_tx=a.frames, frames_ok=len(ok), frames_bad=bad, frames_dup=dup,
                        frames_fixed=ok_fixed, bad_fixed=bad_fixed,
                        frames_missed=a.frames - len(ok), p_decode=round(len(ok) / a.frames, 4),
                        gained_vs_ref=len(ok - ref_ok), lost_vs_ref=len(ref_ok - ok),
                        cpu_s=round(cpu, 3), audio_s=round(audio_s, 2),
                        clipped_samples=info[wav]['clipped'], wav_sha=info[wav]['sha'], **meta))
            for wav in info:
                if not a.keep_wav and os.path.exists(wav):
                    os.remove(wav)
        print('[%6.0fs] %s done' % (time.time() - t_start, cname), file=sys.stderr)

    # Noise only: false positive check.
    fp_rows = []
    if a.noise_only > 0:
        rng = np.random.default_rng([a.seed, 0xF00D])
        n = int(a.noise_only * a.rate)
        wav = os.path.join(a.workdir, 'noise_only.wav')
        write_wav(wav, a.rate, rng.standard_normal(n) * 3000.0)
        jobs = [dict(atest=path, label=label, wav=wav, prof=prof, cond='noise_only',
                     args=list(mode['rx']) + (['-P', prof] if prof else []) + ['-F0'] + extra)
                for prof in profiles for label, path in binaries]
        for job, frames, cpu, wall in pool.map(decode_job, jobs):
            fp_rows.append(dict(condition='noise_only', profile=job['prof'] or 'default',
                                binary=job['label'], binary_sha=shas[job['label']], audio_s=a.noise_only,
                                false_decodes=len(frames), cpu_s=round(cpu, 3), wav_sha=file_sha(wav), **meta))
        if not a.keep_wav:
            os.remove(wav)
    pool.shutdown()

    with open(a.out, 'w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)

    # Threshold summary.
    summary = []
    for cname in conds:
        for prof in profiles:
            for label, _ in binaries:
                rr = [r for r in rows if r['condition'] == cname and r['profile'] == (prof or 'default')
                      and r['binary'] == label]
                xs = [r['ebn0_db'] for r in rr]
                ks = [r['frames_ok'] for r in rr]
                ns = [r['frames_tx'] for r in rr]
                s = dict(condition=cname, profile=prof or 'default', binary=label,
                         frames_tx=sum(ns), frames_ok=sum(ks), frames_bad=sum(r['frames_bad'] for r in rr),
                         frames_fixed=sum(r['frames_fixed'] for r in rr),
                         gained_vs_ref=sum(r['gained_vs_ref'] for r in rr),
                         lost_vs_ref=sum(r['lost_vs_ref'] for r in rr),
                         cpu_s=round(sum(r['cpu_s'] for r in rr), 2))
                for t in targets:
                    xi = interp_threshold(xs, [k / n for k, n in zip(ks, ns)], t)
                    xl, se = logistic_threshold(xs, ks, ns, t)
                    s['ebn0_p%02d_interp' % round(t * 100)] = round(xi, 2)
                    s['ebn0_p%02d_logit' % round(t * 100)] = round(xl, 2)
                    s['ebn0_p%02d_logit_se' % round(t * 100)] = round(se, 2)
                summary.append(s)
    for r in fp_rows:
        summary.append(dict(condition='noise_only', profile=r['profile'], binary=r['binary'],
                            frames_bad=r['false_decodes'], cpu_s=r['cpu_s']))

    sp = a.summary or os.path.splitext(a.out)[0] + '_summary.csv'
    keys = []
    for s in summary:
        keys += [k for k in s if k not in keys]
    with open(sp, 'w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=keys)
        w.writeheader()
        w.writerows(summary)

    # Human readable.
    for s in summary:
        print(' '.join('%s=%s' % (k, v) for k, v in s.items()))


if __name__ == '__main__':
    main()
