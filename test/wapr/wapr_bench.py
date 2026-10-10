#!/usr/bin/env python3
"""WAPR stage 1 benchmark: packet delivery vs SNR for WAPR candidates and legacy modes.

All systems see the same channel model (wapr_channel.py) and the same SNR definition.
Legacy modes are made by this repository's gen_packets and decoded by its atest; WAPR
candidates are simulated by wapr_phy.py.  See README.md for usage and definitions.

Reproducibility: every random number comes from numpy generators seeded with
(seed, purpose, condition, SNR, frame).  Payload content and channel realizations
(fading, offset) depend only on these, not on the system, so all systems are
compared on the same payloads and fading draws (common random numbers).
"""

import argparse
import csv
import hashlib
import json
import math
import multiprocessing
import os
import resource
import sys
import time
import zlib

# One BLAS thread per worker process: the pool provides the parallelism, and CPU time
# per frame then measures the decoder rather than thread overhead.
for _v in ('OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS'):
    os.environ.setdefault(_v, '1')

import numpy as np    # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, '..', 'benchmark'))

import rx_sensitivity as rxs      # noqa: E402  (WAV I/O, atest runner, logistic fit)
import wapr_channel as ch          # noqa: E402
import wapr_phy                    # noqa: E402

PAYLOAD_BYTES_DEFAULT = 32
CHARS = rxs.PAYLOAD_CHARS


def tag(s):
    return zlib.crc32(s.encode()) & 0x7FFFFFFF


def rng_for(seed, purpose, *parts):
    return np.random.default_rng([seed, tag(purpose)] + [int(p) & 0x7FFFFFFF for p in parts])


def snr_key(snr):
    return int(round(snr * 100)) + 100000


# ----------------------------------------------------------------------------
# WAPR systems
# ----------------------------------------------------------------------------

_PHY = {}


def get_phy(name, prof):
    if name not in _PHY:
        _PHY[name] = wapr_phy.Phy(prof)
    return _PHY[name]


def wapr_job(job):
    """Decode job['frames'] frames of one WAPR system at one condition and SNR."""
    phy = get_phy(job['system'], job['profile'])
    fs = phy.fs
    ok = bad = cands = 0
    r0 = resource.getrusage(resource.RUSAGE_SELF)
    for i in job['frame_ids']:
        prng = rng_for(job['seed'], 'payload', i)
        info = prng.integers(0, 2, phy.k_info - phy.prof['crc_bits']).astype(np.uint8)
        x = phy.modulate(phy.encode_bits(info))
        crng = rng_for(job['seed'], 'channel', tag(job['cond']), snr_key(job['snr']), i)
        lead = int(crng.uniform(0.25, 1.0) * phy.airtime * fs) + phy.sps
        buf = np.concatenate([np.zeros(lead), x, np.zeros(int(0.25 * phy.airtime * fs) + phy.sps)])
        ps = float(np.mean(x ** 2))
        y, _ = ch.apply_channel(buf, fs, job['cond'], crng)
        y = y + crng.standard_normal(len(y)) * ch.noise_sigma(ps, fs, job['snr'])
        out, tried = phy.receive(y)
        cands += tried
        for o in out:
            if np.array_equal(o, info):
                ok += 1
            else:
                bad += 1
    r1 = resource.getrusage(resource.RUSAGE_SELF)
    cpu = (r1.ru_utime - r0.ru_utime) + (r1.ru_stime - r0.ru_stime)
    return dict(ok=ok, bad=bad, n=len(job['frame_ids']), cpu=cpu, cands=cands)


def wapr_noise_job(job):
    """Pure noise: count sync candidates and (false) decodes."""
    phy = get_phy(job['system'], job['profile'])
    rng = rng_for(job['seed'], 'noise', job['chunk'])
    n = int(job['seconds'] * phy.fs)
    y = rng.standard_normal(n)
    out, tried = phy.receive(y)
    return dict(decodes=len(out), cands=tried, seconds=job['seconds'])


# ----------------------------------------------------------------------------
# Legacy systems (gen_packets + atest)
# ----------------------------------------------------------------------------

LEGACY_BATCH = 50


def legacy_messages(ids, seed, payload_bytes):
    """TNC2 messages whose info part is exactly payload_bytes long, newline included."""
    body_len = payload_bytes - 8          # '>' + 5 digits + ' ' + body + '\n'
    out = []
    for i in ids:
        r = rng_for(seed, 'legacy_payload', i)
        body = ''.join(CHARS[k] for k in r.integers(0, len(CHARS), body_len))
        out.append('N0CALL>APDWBN:>%05d %s' % (i, body))
    return out


def legacy_clean(sysdef, ids, seed, payload_bytes, workdir, gen_packets):
    """Make (or reuse) the clean WAV for frames `ids`.

    Returns (signal, fs, active mask, expected texts, airtime per frame).
    """
    import subprocess
    os.makedirs(workdir, exist_ok=True)
    msgs = legacy_messages(ids, seed, payload_bytes)
    key = hashlib.sha256(('%s|%d|%d|%s' % (sysdef['gen'], sysdef['rate'], payload_bytes,
                                            '\n'.join(msgs))).encode()).hexdigest()[:16]
    mfile = os.path.join(workdir, 'msgs_%s.txt' % key)
    wav = os.path.join(workdir, 'clean_%s.wav' % key)
    if not os.path.exists(wav):
        with open(mfile, 'w', newline='\n') as f:
            f.write(''.join(m + '\n' for m in msgs))
        tmp = wav + '.%d.tmp' % os.getpid()
        subprocess.run([gen_packets] + sysdef['gen'] + ['-r', str(sysdef['rate']), '-o', tmp, mfile],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        os.replace(tmp, wav)
    fs, sig = rxs.read_wav(wav)
    act = rxs.active_mask(sig, fs, sysdef['rb'])
    # frame airtime: active run length, less 30 of gen_packets' 32 preamble bytes
    edges = np.flatnonzero(np.diff(np.r_[0, act.astype(np.int8), 0]))
    runs = (edges[1::2] - edges[0::2]) / fs
    if len(runs) != len(ids):
        raise RuntimeError('expected %d transmissions in %s, found %d' % (len(ids), wav, len(runs)))
    airtime = float(np.mean(runs)) - 30 * 8.0 / sysdef['rb']
    expected = {i: m + '<0x0a>' for i, m in zip(ids, msgs)}
    return sig, fs, act, expected, airtime


def legacy_job(job):
    sysdef = job['sysdef']
    ids = job['frame_ids']
    sig, fs, act, expected, airtime = legacy_clean(sysdef, ids, job['seed'], job['payload_bytes'],
                                                   job['workdir'], job['gen_packets'])
    ps = float(np.mean(sig[act] ** 2))
    crng = rng_for(job['seed'], 'channel', tag(job['cond']), snr_key(job['snr']), 1000000 + ids[0])
    y, _ = ch.apply_channel(sig, fs, job['cond'], crng)
    y = y + crng.standard_normal(len(y)) * ch.noise_sigma(ps, fs, job['snr'])
    wav = os.path.join(job['workdir'], 'rx_%d.wav' % os.getpid())
    clipped = rxs.write_wav(wav, fs, y)
    frames, cpu, wall = rxs.run_atest(job['atest'], sysdef['rx'], wav)
    os.remove(wav)
    ok, bad, dup, _, _ = rxs.score(frames, expected)
    return dict(ok=len(ok), bad=bad, n=len(ids), cpu=cpu, clipped=clipped, airtime=airtime * len(ids))


# ----------------------------------------------------------------------------
# Statistics
# ----------------------------------------------------------------------------

def wilson(k, n, z=1.96):
    if n == 0:
        return float('nan'), float('nan')
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return c - h, c + h


def fit_logistic(xs, ks, ns):
    """Binomial MLE of p = 1 / (1 + exp(-(a + b x))).  Returns (a, b) or None."""
    if len(xs) < 2 or ks.sum() == 0 or ks.sum() == ns.sum():
        return None
    X = np.column_stack([np.ones_like(xs), xs])
    beta = np.array([-xs.mean(), 1.0])
    for _ in range(200):
        p = np.clip(1 / (1 + np.exp(-np.clip(X @ beta, -50, 50))), 1e-12, 1 - 1e-12)
        W = ns * p * (1 - p) + 1e-9
        H = X.T @ (X * W[:, None])
        try:
            step = np.linalg.solve(H, X.T @ (ks - ns * p))
        except np.linalg.LinAlgError:
            return None
        # a separable data set has no finite MLE; cap the slope at 50 / dB
        beta += step
        if beta[1] > 50:
            beta = np.array([-50 * xs[np.argmax(ks / ns >= 0.5)], 50.0])
            break
        if np.max(np.abs(step)) < 1e-10:
            break
    return (float(beta[0]), float(beta[1])) if beta[1] > 0 else None


def threshold(fit, target, xs):
    if fit is None:
        return float('nan')
    a, b = fit
    xt = (math.log(target / (1 - target)) - a) / b
    return xt if xs.min() - 0.5 <= xt <= xs.max() + 0.5 else float('nan')


def boot_thresholds(xs, ks, ns, target, B, seed, label=''):
    """SNR at `target` delivery and parametric bootstrap replicates.

    Replicates are drawn from the fitted curve (not the raw proportions), so points
    measured at 0 % or 100 % still contribute their binomial uncertainty.
    """
    xs, ks, ns = (np.asarray(v, float) for v in (xs, ks, ns))
    fit = fit_logistic(xs, ks, ns)
    est = threshold(fit, target, xs)
    reps = np.full(B, np.nan)
    if fit is None:
        return est, reps
    pf = 1 / (1 + np.exp(-np.clip(fit[0] + fit[1] * xs, -50, 50)))
    rng = np.random.default_rng([seed, tag('bootstrap'), tag(label)])   # independent per system
    for b in range(B):
        kb = rng.binomial(ns.astype(int), pf).astype(float)
        reps[b] = threshold(fit_logistic(xs, kb, ns), target, xs)
    return est, reps


# ----------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------

def load_config(path):
    with open(path) as f:
        cfg = json.load(f)
    systems = {}
    base = cfg.get('wapr_base', {})
    for name, s in cfg['systems'].items():
        if s['kind'] == 'wapr':
            prof = json.loads(json.dumps(base))
            for k, v in s['profile'].items():
                if isinstance(v, dict) and isinstance(prof.get(k), dict):
                    prof[k].update(v)
                else:
                    prof[k] = v
            prof['name'] = name
            systems[name] = dict(kind='wapr', profile=prof)
        else:
            systems[name] = dict(kind='legacy', sysdef=s)
    return cfg, systems


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--config', default=os.path.join(HERE, 'configs', 'stage1.json'))
    ap.add_argument('--systems', required=True, help='comma separated system names from the config')
    ap.add_argument('--conditions', default='awgn', help='comma separated: ' + ','.join(ch.CONDITIONS))
    ap.add_argument('--snr', default='-10:10:1', help='SNR_2500 points in dB, "start:stop:step" or list')
    ap.add_argument('--snr-shift', default='', help='per system SNR offsets "name=dB,..." to centre the sweep')
    ap.add_argument('--frames', type=int, default=200)
    ap.add_argument('--seed', type=int, default=1)
    ap.add_argument('--jobs', type=int, default=os.cpu_count() or 1)
    ap.add_argument('--atest', default=None)
    ap.add_argument('--gen-packets', default=None)
    ap.add_argument('--workdir', default='wapr_bench_work')
    ap.add_argument('--noise-seconds', type=float, default=0.0, help='also decode this much pure noise per WAPR system')
    ap.add_argument('--bootstrap', type=int, default=1000)
    ap.add_argument('--out', default='wapr_bench.csv')
    a = ap.parse_args()

    cfg, systems = load_config(a.config)
    names = [s for s in a.systems.split(',') if s]
    for s in names:
        if s not in systems:
            sys.exit('unknown system %s' % s)
    conds = [c for c in a.conditions.split(',') if c]
    snrs = rxs.parse_range(a.snr)
    shift = {}
    for kv in filter(None, a.snr_shift.split(',')):
        k, v = kv.split('=')
        shift[k] = float(v)
    payload_bytes = cfg.get('payload_bytes', PAYLOAD_BYTES_DEFAULT)

    # Build codes before forking so the workers share them.
    for s in names:
        if systems[s]['kind'] == 'wapr':
            assert systems[s]['profile']['payload_bytes'] == payload_bytes
            get_phy(s, systems[s]['profile'])
        elif not (a.atest and a.gen_packets):
            sys.exit('--atest and --gen-packets are needed for legacy system %s' % s)

    jobs = []
    for s in names:
        for c in conds:
            for snr0 in snrs:
                snr = round(snr0 + shift.get(s, 0.0), 3)
                if systems[s]['kind'] == 'wapr':
                    step = max(1, a.frames // (4 * a.jobs))
                    for f0 in range(0, a.frames, step):
                        jobs.append(dict(kind='wapr', system=s, profile=systems[s]['profile'], cond=c, snr=snr,
                                         seed=a.seed, frame_ids=list(range(f0, min(a.frames, f0 + step)))))
                else:
                    sd = dict(systems[s]['sysdef'])
                    for f0 in range(0, a.frames, LEGACY_BATCH):
                        jobs.append(dict(kind='legacy', system=s, sysdef=sd, cond=c, snr=snr, seed=a.seed,
                                         frame_ids=list(range(f0, min(a.frames, f0 + LEGACY_BATCH))),
                                         payload_bytes=payload_bytes, workdir=os.path.abspath(a.workdir),
                                         atest=a.atest, gen_packets=a.gen_packets))
    # make legacy clean files once, before the pool
    for s in names:
        if systems[s]['kind'] == 'legacy':
            for f0 in range(0, a.frames, LEGACY_BATCH):
                legacy_clean(systems[s]['sysdef'], list(range(f0, min(a.frames, f0 + LEGACY_BATCH))), a.seed,
                             payload_bytes, os.path.abspath(a.workdir), a.gen_packets)

    t0 = time.time()
    results = {}
    with multiprocessing.Pool(a.jobs) as pool:
        for job, r in pool.imap_unordered(_run, jobs):
            key = (job['system'], job['cond'], job['snr'])
            acc = results.setdefault(key, dict(ok=0, bad=0, n=0, cpu=0.0, cands=0, airtime=0.0))
            for k in ('ok', 'bad', 'n', 'cpu', 'cands', 'airtime'):
                acc[k] += r.get(k, 0)
    wall = time.time() - t0

    cfg_sha = hashlib.sha256(open(a.config, 'rb').read()).hexdigest()[:12]
    rows = []
    for (s, c, snr), r in sorted(results.items()):
        if systems[s]['kind'] == 'wapr':
            phy = get_phy(s, systems[s]['profile'])
            air = phy.airtime
        else:
            air = r['airtime'] / r['n']
        lo, hi = wilson(r['ok'], r['n'])
        rows.append(dict(system=s, condition=c, snr_db=snr,
                         ebn0_db=round(ch.ebn0_from_snr(snr, air, 8 * payload_bytes), 3),
                         frames=r['n'], ok=r['ok'], bad=r['bad'], pdr=round(r['ok'] / r['n'], 4),
                         pdr_lo=round(lo, 4), pdr_hi=round(hi, 4), airtime_s=round(air, 4),
                         cpu_ms_per_frame=round(1000 * r['cpu'] / r['n'], 2),
                         sync_cands_per_frame=round(r['cands'] / r['n'], 3) if r['cands'] else '',
                         seed=a.seed, config_sha=cfg_sha))
    with open(a.out, 'w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()), lineterminator='\n')
        w.writeheader()
        w.writerows(rows)

    # thresholds
    summ = []
    reps_store = {}
    for s in names:
        for c in conds:
            pts = [r for r in rows if r['system'] == s and r['condition'] == c]
            xs = [r['snr_db'] for r in pts]
            ks = [r['ok'] for r in pts]
            ns = [r['frames'] for r in pts]
            for target in (0.5, 0.9):
                est, reps = boot_thresholds(xs, ks, ns, target, a.bootstrap, a.seed, '%s|%s|%g' % (s, c, target))
                reps_store[(s, c, target)] = reps
                good = reps[np.isfinite(reps)]
                lo, hi = (np.percentile(good, [2.5, 97.5]) if len(good) > 0.9 * len(reps) else (float('nan'),) * 2)
                air = pts[0]['airtime_s']
                it = rxs.interp_threshold(xs, [k / n for k, n in zip(ks, ns)], target)
                nmid = sum(0.05 < k / n < 0.95 for k, n in zip(ks, ns))
                note = ('not reached by %g dB' % max(xs) if math.isnan(it) else
                        'reached at lowest point' if it == float('-inf') else
                        'grid too coarse (%d points between 5 and 95 %%)' % nmid if nmid < 2 else '')
                summ.append(dict(system=s, condition=c, target=target, snr_db=round(est, 2),
                                 snr_interp=round(it, 2) if math.isfinite(it) else '', note=note,
                                 snr_lo=round(float(lo), 2), snr_hi=round(float(hi), 2),
                                 ebn0_db=round(ch.ebn0_from_snr(est, air, 8 * payload_bytes), 2) if math.isfinite(est) else '',
                                 airtime_s=air, false_accepts=sum(r['bad'] for r in pts),
                                 frames=sum(ns), seed=a.seed, config_sha=cfg_sha))
    sfile = a.out.replace('.csv', '') + '_summary.csv'
    with open(sfile, 'w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(summ[0].keys()), lineterminator='\n')
        w.writeheader()
        w.writerows(summ)
    np.savez(a.out.replace('.csv', '') + '_bootstrap.npz',
             **{'%s|%s|%g' % k: v for k, v in reps_store.items()})

    for r in summ:
        print('%-16s %-12s p=%.2f  SNR_2500 %6.2f dB [%6.2f, %6.2f]  Eb/N0 %s  airtime %.3f s  false %d  %s' % (
            r['system'], r['condition'], r['target'], r['snr_db'], r['snr_lo'], r['snr_hi'],
            r['ebn0_db'], r['airtime_s'], r['false_accepts'], r['note']))

    # pure noise
    if a.noise_seconds > 0:
        nrows = []
        for s in names:
            if systems[s]['kind'] != 'wapr':
                continue
            phy = get_phy(s, systems[s]['profile'])
            chunk = max(3 * phy.airtime, 2.0)
            nj = [dict(system=s, profile=systems[s]['profile'], seed=a.seed, chunk=i, seconds=chunk)
                  for i in range(int(math.ceil(a.noise_seconds / chunk)))]
            with multiprocessing.Pool(a.jobs) as pool:
                rs = pool.map(wapr_noise_job, nj)
            tot = sum(r['seconds'] for r in rs)
            nrows.append(dict(system=s, noise_seconds=round(tot, 1), sync_candidates=sum(r['cands'] for r in rs),
                              false_decodes=sum(r['decodes'] for r in rs), seed=a.seed, config_sha=cfg_sha))
            print('%-16s noise %.0f s: %d sync candidates decoded, %d false decodes' % (
                s, tot, nrows[-1]['sync_candidates'], nrows[-1]['false_decodes']))
        if nrows:
            with open(a.out.replace('.csv', '') + '_noise.csv', 'w', newline='') as f:
                w = csv.DictWriter(f, fieldnames=list(nrows[0].keys()), lineterminator='\n')
                w.writeheader()
                w.writerows(nrows)
    print('wall %.0f s' % wall)


def _run(job):
    return job, (wapr_job(job) if job['kind'] == 'wapr' else legacy_job(job))


if __name__ == '__main__':
    main()
