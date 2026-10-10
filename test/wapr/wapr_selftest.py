#!/usr/bin/env python3
"""WAPR simulator self tests (stage 1 acceptance checks).

    python3 wapr_selftest.py [--config configs/stage1.json]

Exit status 0 when every check passes.  Takes about a minute.
"""

import argparse
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import wapr_bench      # noqa: E402
import wapr_channel as ch   # noqa: E402
import wapr_codes      # noqa: E402
import wapr_phy        # noqa: E402

FAILS = []


def check(name, cond, detail=''):
    print('%-4s %s %s' % ('ok' if cond else 'FAIL', name, detail))
    if not cond:
        FAILS.append(name)


def test_codes():
    rng = np.random.default_rng(11)
    k = 376
    for kind in ('conv', 'ldpc'):
        code = wapr_codes.make_code(kind, k)
        good = True
        for _ in range(20):
            u = rng.integers(0, 2, k).astype(np.uint8)
            d, ok = code.decode(1.0 - 2.0 * code.encode(u))
            good &= ok and np.array_equal(d, u)
        check('%s noiseless round trip' % kind, good)
    ldpc = wapr_codes.make_code('ldpc', k)
    check('ldpc has no 4-cycles', ldpc.girth4_free())
    u = rng.integers(0, 2, k).astype(np.uint8)
    check('ldpc codeword satisfies all checks', ldpc.syndrome_ok(ldpc.encode(u)))
    # CRC: every single bit error and the all-zero word are detected
    info = rng.integers(0, 2, 352).astype(np.uint8)
    c = wapr_codes.crc24(info)
    miss = 0
    for i in range(len(info)):
        e = info.copy()
        e[i] ^= 1
        miss += np.array_equal(wapr_codes.crc24(e), c)
    check('crc24 detects all single bit errors', miss == 0)
    z = np.zeros(352, dtype=np.uint8)
    check('crc24 of zeros is not zero', wapr_codes.crc24(z).any())


def test_loopback(systems):
    rng = np.random.default_rng(12)
    for name, s in systems.items():
        if s['kind'] != 'wapr':
            continue
        phy = wapr_phy.Phy(s['profile'])
        lim = s['profile']['rx']['freq_search_hz']
        ok = 0
        n = 5
        for _ in range(n):
            info = rng.integers(0, 2, phy.k_info - phy.prof['crc_bits']).astype(np.uint8)
            x = phy.modulate(phy.encode_bits(info))
            lead = int(rng.uniform(0.1, 1.0) * phy.airtime * phy.fs)
            buf = np.concatenate([np.zeros(lead), x, np.zeros(phy.sps * 8)])
            y, _ = ch.apply_channel(buf, phy.fs, dict(freq_offset_hz=('uniform', 0.9 * lim)), rng)
            out, _ = phy.receive(y + 1e-4 * rng.standard_normal(len(y)))
            ok += len(out) == 1 and np.array_equal(out[0], info)
        check('%s noise free loopback, random start and offset' % name, ok == n, '%d/%d' % (ok, n))


def test_ser_theory(systems):
    """Uncoded non-coherent M-FSK symbol error rate (CPFSK, known timing) vs theory."""
    for name in ('F600_ldpc', 'R25_ldpc'):
        if name not in systems:
            continue
        prof = dict(systems[name]['profile'])
        prof['bt'] = None
        phy = wapr_phy.Phy(prof)
        rng = np.random.default_rng(13)
        for snr in ((0.0, 2.0) if phy.baud > 100 else (-14.0, -12.0)):
            err = tot = 0
            for _ in range(30):
                syms = rng.integers(0, phy.M, phy.n_sym)
                x = phy.modulate(syms)
                ps = float(np.mean(x ** 2))
                y = x + rng.standard_normal(len(x)) * ch.noise_sigma(ps, phy.fs, snr)
                E = phy._tone_energies(y, 0, 0.0, np.arange(phy.n_sym))
                err += int(np.sum(E.argmax(axis=1) != syms))
                tot += phy.n_sym
            es = 10 ** ((snr + 10 * math.log10(ch.REF_BW / phy.baud)) / 10)
            M = phy.M
            th = sum((-1) ** (k + 1) * math.comb(M - 1, k) / (k + 1) * math.exp(-k / (k + 1) * es)
                     for k in range(1, M))
            p = err / tot
            # ramps on the first / last symbol cost a little; allow 4 sigma + 3 %
            tol = 4 * math.sqrt(th * (1 - th) / tot) + 0.03 * th
            check('%s SER vs theory at SNR %.0f dB' % (phy.prof['name'], snr), abs(p - th) <= tol,
                  'measured %.4f theory %.4f' % (p, th))


def test_channel():
    rng = np.random.default_rng(14)
    fs = 12000
    # SNR definition
    t = np.arange(fs * 20) / fs
    x = np.sin(2 * math.pi * 1500 * t)
    sig = ch.noise_sigma(0.5, fs, 3.0)
    n = rng.standard_normal(len(x)) * sig
    n0 = np.mean(n ** 2) / (fs / 2.0)
    snr = 10 * math.log10(0.5 / (n0 * ch.REF_BW))
    check('measured SNR_2500 matches request', abs(snr - 3.0) < 0.05, '%.3f dB' % snr)
    # Watterson taps: unit mean power, Doppler 2 sigma width
    g = ch.fading_taps(fs * 2000, fs, 1.0, rng)[::fs // 100]
    pw = np.mean(np.abs(g) ** 2)
    check('fading tap mean power ~ 1', abs(pw - 1.0) < 0.1, '%.3f' % pw)
    # Gaussian spectrum autocorrelation exp(-2 pi^2 sd^2 tau^2), sd = 0.5 Hz
    tau = 0.25
    lag = int(tau * 100)
    r = np.abs(np.mean(g[lag:] * np.conj(g[:-lag]))) / pw
    th = math.exp(-2 * math.pi ** 2 * 0.5 ** 2 * tau ** 2)
    check('fading tap autocorrelation matches 1 Hz spread', abs(r - th) < 0.06, '%.3f vs %.3f' % (r, th))
    # frequency offset moves a tone
    y, fo = ch.apply_channel(x[:fs], fs, dict(freq_offset_hz=('fixed', 37.0)), rng)
    f = np.argmax(np.abs(np.fft.rfft(y))) * fs / len(y)
    check('frequency offset applied', abs(f - 1537.0) <= 1.0, '%.1f Hz' % f)


def test_determinism(systems):
    name = 'H150_ldpc'
    if name not in systems:
        return
    job = dict(kind='wapr', system=name, profile=systems[name]['profile'], cond='itu_mod_off', snr=-2.0,
               seed=99, frame_ids=list(range(6)))
    a = wapr_bench.wapr_job(job)
    b = wapr_bench.wapr_job(job)
    check('same seed gives same result', (a['ok'], a['bad'], a['cands']) == (b['ok'], b['bad'], b['cands']),
          '%d/%d ok' % (a['ok'], a['n']))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--config', default=os.path.join(HERE, 'configs', 'stage1.json'))
    a = ap.parse_args()
    _, systems = wapr_bench.load_config(a.config)
    test_codes()
    test_channel()
    test_ser_theory(systems)
    test_loopback(systems)
    test_determinism(systems)
    print('%d failure(s)' % len(FAILS))
    sys.exit(1 if FAILS else 0)


if __name__ == '__main__':
    main()
