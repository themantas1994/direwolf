#!/usr/bin/env python3
"""
Independent reference implementation of the parts of AX.25, HDLC, KISS and the
AFSK / G3RUH physical layers needed to check Dire Wolf's compatibility.

Written from the specifications, not from Dire Wolf's source:

  * AX.25 Link Access Protocol v2.2 (address field, control field, PID, FCS).
  * ISO 3309 HDLC framing (flags, bit stuffing, LSB first), NRZI as used by
    Bell 202 packet radio.
  * CRC-16/X.25 (CCITT, reflected, init 0xFFFF, final xor 0xFFFF), sent low
    byte first.
  * KISS (Chepponis & Karn): FEND C0, FESC DB, TFEND DC, TFESC DD.
  * G3RUH 9600 bd: NRZI then self synchronizing scrambler x^17 + x^12 + 1.

Only the Python standard library and numpy are used.
"""

import re
import struct
import wave

try:
    import numpy as np
except ImportError:          # Only the modulators need numpy.
    np = None


# --------------------------------------------------------------------------
# CRC / FCS
# --------------------------------------------------------------------------

def fcs16(data: bytes) -> int:
    """CRC-16/X.25 over data.  Returned value is what goes on air (low byte first)."""
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0x8408 if crc & 1 else crc >> 1
    return crc ^ 0xFFFF


def add_fcs(frame: bytes) -> bytes:
    f = fcs16(frame)
    return frame + bytes([f & 0xFF, (f >> 8) & 0xFF])


def check_fcs(frame_with_fcs: bytes) -> bool:
    if len(frame_with_fcs) < 3:
        return False
    return fcs16(frame_with_fcs[:-2]) == (frame_with_fcs[-2] | (frame_with_fcs[-1] << 8))


# --------------------------------------------------------------------------
# AX.25 addresses and TNC-2 monitor text
# --------------------------------------------------------------------------

_HEX = re.compile(rb"<0x([0-9a-fA-F]{2})>")


def unescape_info(text: str) -> bytes:
    """TNC-2 text info part to bytes: <0xNN> becomes one byte, other characters are UTF-8."""
    raw = text.encode("utf-8")
    return _HEX.sub(lambda m: bytes([int(m.group(1), 16)]), raw)


def encode_address(call: str, ssid: int, c_or_h: int, last: bool) -> bytes:
    if not (1 <= len(call) <= 6) or not all(ch.isdigit() or ("A" <= ch <= "Z") for ch in call):
        raise ValueError("bad callsign %r" % call)
    if not 0 <= ssid <= 15:
        raise ValueError("bad ssid %d" % ssid)
    out = bytes((ord(ch) << 1) for ch in call.ljust(6))
    # SSID octet: C/H (bit 7), two reserved bits set to 1 (bits 6, 5),
    # SSID (bits 4 - 1), address extension bit (bit 0, 1 = last address).
    return out + bytes([(c_or_h << 7) | 0x60 | (ssid << 1) | (1 if last else 0)])


def decode_address(b: bytes):
    call = "".join(chr(x >> 1) for x in b[:6]).rstrip(" ")
    ssid = (b[6] >> 1) & 0x0F
    return call, ssid, (b[6] >> 7) & 1, b[6] & 1


def split_call(tok: str):
    star = tok.endswith("*")
    if star:
        tok = tok[:-1]
    if "-" in tok:
        call, s = tok.split("-", 1)
        ssid = int(s)
    else:
        call, ssid = tok, 0
    return call, ssid, star


def tnc2_to_frame(line: str, dest_c=1, src_c=1, control=0x03, pid=0xF0) -> bytes:
    """
    Monitor format text to an AX.25 UI frame, without FCS.

    In TNC-2 format the '*' marks the last repeater that has repeated the
    frame, so it and every repeater before it have the H bit set.

    AX.25 v2 says a command has destination C = 1 and source C = 0.  APRS
    and most TNCs (TNC-2 firmware included) send UI frames with both bits
    set, and so does Dire Wolf for frames built from monitor text.  That is
    the default here; tests for real command/response frames pass the bits.
    """
    header, info = line.split(":", 1)
    src, rest = header.split(">", 1)
    parts = rest.split(",")
    dest, digis = parts[0], parts[1:]

    scall, sssid, _ = split_call(src)
    dcall, dssid, _ = split_call(dest)
    digi_parsed = [split_call(d) for d in digis]
    if len(digi_parsed) > 8:
        raise ValueError("too many digipeaters")

    last_used = -1
    for i, (_, _, star) in enumerate(digi_parsed):
        if star:
            last_used = i

    out = encode_address(dcall, dssid, dest_c, False)
    out += encode_address(scall, sssid, src_c, len(digi_parsed) == 0)
    for i, (c, s, _) in enumerate(digi_parsed):
        out += encode_address(c, s, 1 if i <= last_used else 0, i == len(digi_parsed) - 1)
    return out + bytes([control, pid]) + unescape_info(info)


def frame_to_tnc2(frame: bytes) -> str:
    """AX.25 frame (no FCS) to monitor text.  Non-printable info bytes as <0xNN>."""
    addrs = []
    i = 0
    while True:
        if i + 7 > len(frame):
            raise ValueError("truncated address field")
        addrs.append(decode_address(frame[i:i + 7]))
        i += 7
        if addrs[-1][3]:
            break
    if len(addrs) < 2:
        raise ValueError("fewer than two addresses")

    def fmt(a):
        return a[0] if a[1] == 0 else "%s-%d" % (a[0], a[1])

    digis = addrs[2:]
    last_h = max([n for n, a in enumerate(digis) if a[2]], default=-1)
    path = [fmt(a) + ("*" if n == last_h else "") for n, a in enumerate(digis)]
    info = frame[i + 2:]
    txt = "".join(chr(b) if 0x20 <= b < 0x7F else "<0x%02x>" % b for b in info)
    return "%s>%s:%s" % (fmt(addrs[1]), ",".join([fmt(addrs[0])] + path), txt)


def read_corpus(path: str):
    """Lines of a corpus file, without comments and blank lines."""
    out = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if line and not line.startswith("#"):
                out.append(line)
    return out


# --------------------------------------------------------------------------
# KISS
# --------------------------------------------------------------------------

FEND, FESC, TFEND, TFESC = 0xC0, 0xDB, 0xDC, 0xDD


def kiss_encode(frame: bytes, port=0, cmd=0) -> bytes:
    body = bytearray([((port & 0x0F) << 4) | (cmd & 0x0F)])
    for b in frame:
        if b == FEND:
            body += bytes([FESC, TFEND])
        elif b == FESC:
            body += bytes([FESC, TFESC])
        else:
            body.append(b)
    return bytes([FEND]) + bytes(body) + bytes([FEND])


class KissDecoder:
    """Incremental KISS decoder.  feed() returns a list of (port, cmd, payload)."""

    def __init__(self):
        self.buf = bytearray()
        self.in_frame = False
        self.esc = False

    def feed(self, data: bytes):
        out = []
        for b in data:
            if b == FEND:
                if self.in_frame and len(self.buf) > 0:
                    t = self.buf[0]
                    out.append((t >> 4, t & 0x0F, bytes(self.buf[1:])))
                self.buf = bytearray()
                self.in_frame = True
                self.esc = False
            elif not self.in_frame:
                continue
            elif self.esc:
                self.buf.append(FEND if b == TFEND else FESC if b == TFESC else b)
                self.esc = False
            elif b == FESC:
                self.esc = True
            else:
                self.buf.append(b)
        return out


# --------------------------------------------------------------------------
# HDLC bits
# --------------------------------------------------------------------------

def hdlc_bits(frame_with_fcs: bytes, preamble_flags=32, postamble_flags=4):
    """Bits on the line before NRZI: flags, stuffed frame (LSB first), flags."""
    flag = [0, 1, 1, 1, 1, 1, 1, 0]
    bits = flag * preamble_flags
    ones = 0
    for byte in frame_with_fcs:
        for k in range(8):
            bit = (byte >> k) & 1
            bits.append(bit)
            if bit:
                ones += 1
                if ones == 5:
                    bits.append(0)
                    ones = 0
            else:
                ones = 0
    return bits + flag * postamble_flags


def nrzi(bits, level=0):
    """0 = change the level, 1 = keep it."""
    out = []
    for b in bits:
        if b == 0:
            level ^= 1
        out.append(level)
    return out


def scramble_g3ruh(bits, state=0):
    """Self synchronizing scrambler, x^17 + x^12 + 1."""
    out = []
    for b in bits:
        x = b ^ ((state >> 16) & 1) ^ ((state >> 11) & 1)
        state = ((state << 1) | x) & 0x1FFFF
        out.append(x)
    return out


# --------------------------------------------------------------------------
# Modulators
# --------------------------------------------------------------------------

def afsk(levels, rate, baud, mark, space, amplitude=0.5):
    """Continuous phase AFSK.  Level 1 = mark tone, level 0 = space tone."""
    n = len(levels)
    t_edges = np.round(np.arange(n + 1) * rate / baud).astype(np.int64)
    freqs = np.empty(t_edges[-1])
    for i, lv in enumerate(levels):
        freqs[t_edges[i]:t_edges[i + 1]] = mark if lv else space
    phase = 2 * np.pi * np.cumsum(freqs) / rate
    return amplitude * np.sin(phase)


def baseband(levels, rate, baud, amplitude=0.5):
    """Polar NRZ with a windowed sinc low pass (cut off 0.6 x baud), for G3RUH."""
    n = len(levels)
    t_edges = np.round(np.arange(n + 1) * rate / baud).astype(np.int64)
    x = np.empty(t_edges[-1])
    for i, lv in enumerate(levels):
        x[t_edges[i]:t_edges[i + 1]] = 1.0 if lv else -1.0
    fc = 0.6 * baud / rate
    m = int(4 * rate / baud) | 1
    k = np.arange(m) - m // 2
    h = np.sinc(2 * fc * k) * np.hamming(m)
    h /= h.sum()
    return amplitude * np.convolve(x, h, mode="same")


def write_wav(path, samples, rate):
    s = np.clip(np.asarray(samples) * 32767.0, -32768, 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(s.tobytes())


def read_raw_s16(path):
    with open(path, "rb") as f:
        data = f.read()
    return struct.unpack("<%dh" % (len(data) // 2), data[: len(data) // 2 * 2])


def raw_to_wav(raw_path, wav_path, rate):
    with open(raw_path, "rb") as f:
        data = f.read()
    with wave.open(wav_path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(data[: len(data) // 2 * 2])


def modulate_frames(frames, mode, rate, gap_s=0.05):
    """
    Frames (without FCS) to audio.  mode: "afsk1200", "afsk300" or "g3ruh9600".
    Each frame is its own transmission with silence between them.
    """
    parts = []
    gap = np.zeros(int(gap_s * rate))
    for fr in frames:
        bits = hdlc_bits(add_fcs(fr))
        lv = nrzi(bits)
        if mode == "afsk1200":
            parts.append(afsk(lv, rate, 1200, 1200, 2200))
        elif mode == "afsk300":
            parts.append(afsk(lv, rate, 300, 1600, 1800))
        elif mode == "g3ruh9600":
            parts.append(baseband(scramble_g3ruh(lv), rate, 9600))
        else:
            raise ValueError(mode)
        parts.append(gap)
    return np.concatenate([gap] + parts)


if __name__ == "__main__":
    # Self test with the check value from the CRC catalogue:
    # CRC-16/X-25 of "123456789" is 0x906E.
    assert fcs16(b"123456789") == 0x906E
    f = tnc2_to_frame("N0CALL>APRS,WIDE1-1*,WIDE2-1:!test")
    assert frame_to_tnc2(f) == "N0CALL>APRS,WIDE1-1*,WIDE2-1:!test", frame_to_tnc2(f)
    assert check_fcs(add_fcs(f))
    k = kiss_encode(bytes([0xC0, 0xDB, 0x01]))
    assert k == bytes([0xC0, 0x00, 0xDB, 0xDC, 0xDB, 0xDD, 0x01, 0xC0])
    assert KissDecoder().feed(k) == [(0, 0, bytes([0xC0, 0xDB, 0x01]))]
    print("ax25ref self test OK")
