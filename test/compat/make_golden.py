#!/usr/bin/env python3
"""
Write golden/ax25_vectors.txt: AX.25 test vectors computed by ax25ref.py
(written from the AX.25 v2.2 specification, not from Dire Wolf's code).
src/ax25golden_test.c checks Dire Wolf against them (ctest ax25goldentest).

Line formats (fields separated by single spaces):

  T <frame hex> <fcs hex> <monitor text>
      UI frame built from monitor text: ax25_from_text must give exactly these
      bytes, the FCS must match, and taking the bytes apart again must give the
      same addresses and information part.
  R <type> <cr> <pf> <nr> <ns> <modulo> <frame hex> <fcs hex>
      Any frame type: ax25_frame_type must report these values (-1 = not
      applicable).  cr: cmd, res, 00 or 11 (destination and source C bits).
  L <frame hex> <expected>
      Malformed frame.  expected: NULL (rejected for its length) or the number
      of addresses found, 0 meaning the address field is invalid.

Regenerate with:  python3 test/compat/make_golden.py
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ax25ref  # noqa: E402


def addr_field(dest, src, digis=(), c=(1, 0), used=0):
    def sp(t):
        a, s, _ = ax25ref.split_call(t)
        return a, s
    out = ax25ref.encode_address(*sp(dest), c[0], False)
    out += ax25ref.encode_address(*sp(src), c[1], len(digis) == 0)
    for i, d in enumerate(digis):
        out += ax25ref.encode_address(*sp(d), 1 if i < used else 0, i == len(digis) - 1)
    return out


CR = {(1, 0): "cmd", (0, 1): "res", (0, 0): "00", (1, 1): "11"}


def r_line(name, c, pf, nr, ns, modulo, frame):
    return "R %s %s %d %d %d %d %s %04x" % (name, CR[c], pf, nr, ns, modulo, frame.hex(), fcs_word(frame))


def fcs_word(frame):
    return ax25ref.fcs16(frame)


def main():
    out = []
    for line in ax25ref.read_corpus(os.path.join(HERE, "aprs_ax25_corpus.txt")):
        f = ax25ref.tnc2_to_frame(line)
        out.append("T %s %04x %s" % (f.hex(), fcs_word(f), line))

    paths = [((), 0), (("WIDE1-1",), 0), (("R1", "R2", "R3"), 2), (("D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8"), 8)]
    for c in ((1, 0), (0, 1), (0, 0), (1, 1)):
        for digis, used in paths[:2] if c in ((0, 0), (1, 1)) else paths:
            a = addr_field("N0CALL-2", "W1ABC-15", digis, c, used)
            # Modulo 8.  Control field bit layouts from AX.25 v2.2 section 4.2.
            for ns, nr, pf in ((0, 0, 0), (3, 5, 1), (7, 7, 0), (1, 6, 1)):
                ctl = (nr << 5) | (pf << 4) | (ns << 1)
                out.append(r_line("I", c, pf, nr, ns, 8, a + bytes([ctl, 0xF0]) + b"I frame data"))
            for name, ss in (("RR", 0), ("RNR", 1), ("REJ", 2), ("SREJ", 3)):
                for nr, pf in ((0, 0), (7, 1), (4, 0)):
                    ctl = (nr << 5) | (pf << 4) | (ss << 2) | 1
                    out.append(r_line(name, c, pf, nr, -1, 8, a + bytes([ctl])))
            for name, base in (("SABME", 0x6F), ("SABM", 0x2F), ("DISC", 0x43), ("DM", 0x0F), ("UA", 0x63),
                               ("FRMR", 0x87), ("UI", 0x03), ("XID", 0xAF), ("TEST", 0xE3)):
                for pf in (0, 1):
                    ctl = base | (pf << 4)
                    body = b""
                    if name == "UI":
                        body = bytes([0xF0]) + b"UI data"
                    elif name == "FRMR":
                        body = bytes([0x10, 0x46, 0x01])
                    elif name in ("XID", "TEST"):
                        body = b"payload"
                    out.append(r_line(name, c, pf, -1, -1, 8, a + bytes([ctl]) + body))
            # Modulo 128: two control octets for I and S frames.
            for ns, nr, pf in ((0, 0, 0), (64, 100, 1), (127, 127, 0), (5, 126, 1)):
                c1, c2 = ns << 1, (nr << 1) | pf
                out.append(r_line("I", c, pf, nr, ns, 128, a + bytes([c1, c2, 0xF0]) + b"I mod 128"))
            for name, ss in (("RR", 0), ("RNR", 1), ("REJ", 2), ("SREJ", 3)):
                for nr, pf in ((0, 0), (127, 1), (77, 0)):
                    out.append(r_line(name, c, pf, nr, -1, 128, a + bytes([(ss << 2) | 1, (nr << 1) | pf])))
            # U frames have one control octet with either modulo.
            for name, base in (("SABME", 0x6F), ("UA", 0x63), ("DISC", 0x43)):
                out.append(r_line(name, c, 1, -1, -1, 128, a + bytes([base | 0x10])))

    # Malformed frames.
    good = addr_field("N0CALL-2", "W1ABC")
    out.append("L 00 NULL")                                                             # 1 byte
    out.append("L %s NULL" % (good[:14]).hex())                                        # addresses only, no control
    out.append("L %s 2" % (good + bytes([0x03])).hex())                                 # shortest valid: 15 bytes
    big = good + bytes([0x03, 0xF0]) + bytes(2048)                                      # 2048 byte info: allowed
    out.append("L %s 2" % big.hex())
    out.append("L %s NULL" % (good + bytes([0x03, 0xF0]) + bytes(2108)).hex())          # 2124 bytes: too long
    one = bytearray(good)
    one[6] |= 1                                                                         # end bit on the destination
    out.append("L %s 0" % (bytes(one) + bytes([0x03, 0xF0]) + b"x").hex())
    eleven = addr_field("N0CALL-2", "W1ABC", ["D%d" % i for i in range(1, 10)])        # 11 addresses
    out.append("L %s 0" % (eleven + bytes([0x03, 0xF0]) + b"x").hex())
    ten = addr_field("N0CALL-2", "W1ABC", ["D%d" % i for i in range(1, 9)])             # 10 addresses: allowed
    out.append("L %s 10" % (ten + bytes([0x03, 0xF0]) + b"x").hex())
    odd = bytearray(good)
    odd[3] |= 1                                                                         # bit 0 set in a callsign octet
    out.append("L %s 0" % (bytes(odd) + bytes([0x03, 0xF0]) + b"x").hex())
    noend = bytearray(good)
    noend[13] &= 0xFE                                                                   # no end bit anywhere
    out.append("L %s 0" % (bytes(noend) + bytes([0x02, 0xF0]) + b"xxxx").hex())

    path = os.path.join(HERE, "golden", "ax25_vectors.txt")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="\n") as f:
        f.write("# AX.25 test vectors from test/compat/make_golden.py (independent of Dire Wolf's code).\n")
        f.write("# See that file for the format.  %d vectors.\n" % len(out))
        f.write("\n".join(out) + "\n")
    print("%s: %d vectors" % (path, len(out)))


if __name__ == "__main__":
    main()
