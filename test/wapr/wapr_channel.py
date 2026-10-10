"""WAPR research simulator: channel models, shared by WAPR and legacy (gen_packets) audio.

SNR definition used everywhere in the WAPR benchmarks
-----------------------------------------------------
* S  = mean power of the clean transmitted audio while the frame is on the air
       (before fading; Watterson paths have unit total mean power).
* N0 = one-sided density of the added white Gaussian noise; sample variance
       sigma^2 = N0 * fs / 2, so the definition does not depend on the sample rate.
* SNR_2500 = S / (N0 * 2500 Hz)   (the reference bandwidth used by WSJT-X)
* Eb/N0    = S * airtime / (N0 * payload bits)  -- energy per *useful* payload bit,
             so protocol overhead and FEC are charged to the mode that has them.

Channel conditions
------------------
* awgn             white Gaussian noise only (audio domain; for VHF FM this models the
                   receiver output above FM threshold, without de-emphasis).
* frequency offset applied to the analytic signal (models SSB mistuning on HF; not
                   meaningful for FM, where it is not used).
* watterson        two independent Rayleigh paths of equal mean power, path delay
                   `delay_ms`, Gaussian Doppler spectrum whose two-sigma width is
                   `spread_hz` (ITU-R F.1487 / CCIR 520 convention):
                   good 0.5 ms / 0.1 Hz, moderate 1 ms / 0.5 Hz, poor 2 ms / 1 Hz.
"""

import math

import numpy as np

REF_BW = 2500.0

CONDITIONS = {
    'awgn':       dict(),
    'awgn_off':   dict(freq_offset_hz=('uniform', 25.0)),
    'itu_good':   dict(delay_ms=0.5, spread_hz=0.1),
    'itu_mod':    dict(delay_ms=1.0, spread_hz=0.5),
    'itu_mod_off': dict(delay_ms=1.0, spread_hz=0.5, freq_offset_hz=('uniform', 25.0)),
    'itu_poor':   dict(delay_ms=2.0, spread_hz=1.0),
}


def analytic(x):
    n = len(x)
    X = np.fft.fft(x)
    h = np.zeros(n)
    h[0] = 1.0
    if n % 2 == 0:
        h[n // 2] = 1.0
        h[1:n // 2] = 2.0
    else:
        h[1:(n + 1) // 2] = 2.0
    return np.fft.ifft(X * h)


def fading_taps(n, fs, spread_hz, rng):
    """Complex Gaussian process, unit mean power, Gaussian Doppler spectrum (2 sigma = spread)."""
    if spread_hz <= 0:
        return np.ones(n, dtype=complex)
    sd = spread_hz / 2.0
    fst = max(20.0 * spread_hz, 10.0)             # tap sample rate
    m = int(math.ceil(n / fs * fst)) + 4
    nf = 1 << int(math.ceil(math.log2(max(m, 64) * 2)))
    f = np.fft.fftfreq(nf, 1.0 / fst)
    shape = np.exp(-f ** 2 / (2 * sd ** 2))
    w = (rng.standard_normal(nf) + 1j * rng.standard_normal(nf)) / math.sqrt(2.0)
    g = np.fft.ifft(w * np.sqrt(shape)) * (nf / math.sqrt(shape.sum()))   # unit mean power
    t_tap = np.arange(nf) / fst
    t = np.arange(n) / fs
    return np.interp(t, t_tap, g.real) + 1j * np.interp(t, t_tap, g.imag)


def apply_channel(x, fs, cond, rng):
    """Fading and frequency offset (no noise).  Returns (signal, applied offset Hz)."""
    c = CONDITIONS[cond] if isinstance(cond, str) else cond
    fo = 0.0
    spec = c.get('freq_offset_hz')
    if spec:
        kind, val = spec
        fo = float(rng.uniform(-val, val)) if kind == 'uniform' else float(val)
    if not c.get('spread_hz') and fo == 0.0:
        return x.copy(), fo
    s = analytic(x)
    if c.get('spread_hz'):
        d = int(round(c['delay_ms'] * 1e-3 * fs))
        a1 = fading_taps(len(x), fs, c['spread_hz'], rng)
        a2 = fading_taps(len(x), fs, c['spread_hz'], rng)
        sd = np.concatenate([np.zeros(d, dtype=complex), s[:len(s) - d]])
        s = (a1 * s + a2 * sd) / math.sqrt(2.0)
    if fo:
        s = s * np.exp(2j * math.pi * fo * np.arange(len(s)) / fs)
    return s.real, fo


def noise_sigma(ps, fs, snr_db):
    """Noise sample std for signal power ps at SNR_2500 = snr_db."""
    n0 = ps / (REF_BW * 10.0 ** (snr_db / 10.0))
    return math.sqrt(n0 * fs / 2.0)


def ebn0_from_snr(snr_db, airtime_s, payload_bits):
    """Eb/N0 per payload bit for a frame of the given airtime at SNR_2500."""
    return snr_db + 10.0 * math.log10(REF_BW * airtime_s / payload_bits)
