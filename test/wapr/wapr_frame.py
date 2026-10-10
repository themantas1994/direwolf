"""WAPR logical frame, version 0 (experimental).  Reference implementation.

The 12 byte header and the payload area are the information bits of one FEC codeword
(see wapr_phy.Phy.encode_bits): they are protected by the CRC-24 and the FEC, so the
header has no separate check.  The profile (waveform) is identified by the receiver that
found the frame; it is not repeated in the header.

Header, 96 bits, most significant bit first:

    bits  field    meaning
    2     version  0 for this format.  Others: reject.
    4     type     0 raw bytes (tests), 1 APRS information part (text).  Others: reject.
    2     flags    bit 1: acknowledgement requested; bit 0: reserved, must be 0.
    6     length   payload length in bytes, 0 .. payload area size (32).  More: reject.
    10    seq      message identity, 0 .. 1023, chosen by the sender.
    36    source   address (below).  Must not be the broadcast value.
    36    dest     address, or 0 = broadcast (no particular destination).

Address: callsign of 1 to 6 characters from A-Z 0-9, left justified and padded with
spaces, as a base 37 number (space 0, '0'-'9' 1-10, 'A'-'Z' 11-36, first character most
significant), times 16, plus the SSID 0 .. 15.

Payload area: 32 bytes; bytes after `length` must be zero (else reject).
"""

import numpy as np

VERSION = 0
HEADER_BYTES = 12
PAYLOAD_AREA = 32
TYPE_RAW = 0
TYPE_APRS = 1
TYPES = (TYPE_RAW, TYPE_APRS)
FLAG_ACK = 2

ALPHABET = ' 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ'


class FrameError(ValueError):
    pass


def addr_pack(call):
    """'N0CALL-7' -> 36 bit integer.  '' or None -> 0 (broadcast)."""
    if not call:
        return 0
    base, sep, ssid = call.upper().partition('-')
    if not 1 <= len(base) <= 6 or any(c not in ALPHABET[1:] for c in base):
        raise FrameError('bad callsign %r' % call)
    if sep and not (ssid.isdigit() and len(ssid) <= 2 and int(ssid) <= 15):
        raise FrameError('bad SSID %r' % call)
    s = int(ssid) if ssid else 0
    v = 0
    for c in base.ljust(6):
        v = v * 37 + ALPHABET.index(c)
    return v * 16 + s


def addr_unpack(v):
    if v == 0:
        return ''
    if v >= 37 ** 6 * 16:
        raise FrameError('address out of range')
    s = v % 16
    v //= 16
    chars = []
    for _ in range(6):
        chars.append(ALPHABET[v % 37])
        v //= 37
    base = ''.join(reversed(chars))
    if base[0] == ' ' or ' ' in base.rstrip(' '):
        raise FrameError('address has embedded or leading space')
    base = base.rstrip(' ')
    return base + ('-%d' % s if s else '')


def _bits(value, n):
    return [(value >> (n - 1 - i)) & 1 for i in range(n)]


def _val(bits):
    v = 0
    for b in bits:
        v = (v << 1) | int(b)
    return v


def pack(ftype, source, dest, seq, payload, ack=False):
    """Header + payload area as an array of 8 * (12 + 32) bits."""
    if ftype not in TYPES:
        raise FrameError('unsupported type')
    if len(payload) > PAYLOAD_AREA:
        raise FrameError('payload too long')
    if not 0 <= seq < 1024:
        raise FrameError('bad sequence number')
    src = addr_pack(source)
    if src == 0:
        raise FrameError('source must not be broadcast')
    hdr = (_bits(VERSION, 2) + _bits(ftype, 4) + _bits(FLAG_ACK if ack else 0, 2) +
           _bits(len(payload), 6) + _bits(seq, 10) + _bits(src, 36) + _bits(addr_pack(dest), 36))
    body = bytes(payload) + bytes(PAYLOAD_AREA - len(payload))
    pl = [(b >> (7 - i)) & 1 for b in body for i in range(8)]
    return np.array(hdr + pl, dtype=np.uint8)


def unpack(bits):
    """Inverse of pack.  Returns dict; raises FrameError for anything not valid v0."""
    bits = [int(b) for b in bits]
    if len(bits) != 8 * (HEADER_BYTES + PAYLOAD_AREA):
        raise FrameError('wrong size')
    ver = _val(bits[0:2])
    if ver != VERSION:
        raise FrameError('unsupported version %d' % ver)
    ftype = _val(bits[2:6])
    if ftype not in TYPES:
        raise FrameError('unsupported type %d' % ftype)
    flags = _val(bits[6:8])
    if flags & 1:
        raise FrameError('reserved flag set')
    length = _val(bits[8:14])
    if length > PAYLOAD_AREA:
        raise FrameError('length %d exceeds payload area' % length)
    seq = _val(bits[14:24])
    src = _val(bits[24:60])
    dst = _val(bits[60:96])
    if src == 0:
        raise FrameError('broadcast source')
    body = bytes(_val(bits[96 + 8 * i:104 + 8 * i]) for i in range(PAYLOAD_AREA))
    if any(body[length:]):
        raise FrameError('non-zero padding')
    return dict(type=ftype, ack=bool(flags & FLAG_ACK), seq=seq, source=addr_unpack(src),
                dest=addr_unpack(dst), payload=body[:length])
