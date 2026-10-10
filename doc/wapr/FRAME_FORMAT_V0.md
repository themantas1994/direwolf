# WAPR frame format, version 0 (experimental)

Status: research prototype.  Not compatible with AX.25, APRS, FX.25 or IL2P on the air.
Reference implementations: `test/wapr/wapr_frame.py`, `test/wapr/wapr_phy.py` (Python),
`src/wapr_codec.c`, `src/wapr_modem.c` (C).  Golden vectors:
`test/wapr/golden/wapr_vectors.txt`, checked by `waprtest` (ctest).

## Layers

| Layer | Content |
|---|---|
| Logical frame | 12 byte header + 32 byte payload area (one size class, no fragmentation) |
| Integrity | CRC-24 over the 352 bits above |
| Whitening | xor with the x^9 + x^5 + 1 sequence, register seed 0x1FF |
| FEC | IRA LDPC, K = 376, N = 752 (rate 1/2) |
| Interleaving | fixed permutation of the 752 code bits |
| Symbols | Gray mapped M-FSK, Costas sync blocks inserted |
| Waveform | continuous phase GFSK, tone spacing = symbol rate (h = 1) |

The profile (waveform) is not in the header: it is identified by the receiver that
found the sync pattern.  Every profile carries the same 752 code bits.

## Header (96 bits, most significant bit first)

| Bits | Field | Values |
|---|---|---|
| 2 | version | 0.  Anything else is rejected. |
| 4 | type | 0 raw bytes (tests), 1 APRS information part, 2 APRS information part relayed by a gateway (must never be gated again).  Others rejected. |
| 2 | flags | bit 1: acknowledgement requested; bit 0 reserved, must be 0. |
| 6 | length | payload bytes, 0 to 32.  More is rejected. |
| 10 | seq | message identity 0 to 1023, chosen by the sender. |
| 36 | source | address, must not be 0 |
| 36 | dest | address, 0 = broadcast |

**Address**: callsign of 1 to 6 characters from `A-Z 0-9`, left justified and padded
with spaces, read as a base 37 number (space 0, `0`-`9` 1-10, `A`-`Z` 11-36, first
character most significant), times 16, plus the SSID 0-15.  Values from 37^6 x 16 up,
and values whose callsign has a leading or embedded space, are rejected.  SSID 0 is
written without a suffix (`N0CALL`, not `N0CALL-0`).

**Payload area**: 32 bytes; bytes after `length` must be zero, else the frame is
rejected.

## Integrity

CRC-24, polynomial 0x864CFB, register initialised to 0xFFFFFF, no reflection, final xor
0xFFFFFF, over the 352 header and payload bits, appended most significant bit first.
A frame is accepted only if the LDPC decoder reaches a zero syndrome, the CRC matches
and every header check passes.

## FEC

Systematic IRA code: code bits = 376 message bits then 376 parity bits.  Parity bit j
is in checks j and j+1 (the last only in check 375), so p_j = p_(j-1) xor s_j, where s_j
is the xor of the message bits in check j.  Each message bit is in 3 checks, listed in
`src/wapr_tables.h` (`wapr_info_chk`), built by progressive edge growth from seed 1 and
free of 4-cycles.  Receivers may use any decoder; the reference uses sum-product,
at most 50 iterations.

## Symbols

Transmitted bit i is code bit `wapr_perm[i]` (`src/wapr_tables.h`).  Bits are taken
log2(M) at a time, most significant first, zero padded at the end, and sent as the tone
whose Gray code equals that value.  Costas blocks of M symbols (order 4: 0 1 3 2;
order 8: 0 1 4 6 5 3 7 2) are placed at the start, at the end, and evenly in between,
6 blocks in all, splitting the data symbols as evenly as possible (earlier parts get
the remainder).

## Profiles (frozen in stage 1)

| Name | M | Symbol rate | Centre | GFSK BT | Symbols | Airtime | Occupied (tones) |
|---|---|---|---|---|---|---|---|
| F600 | 4 | 600 Bd | 1800 Hz | 1.0 | 400 | 0.667 s | 900-2700 Hz |
| H150 | 4 | 150 Bd | 1500 Hz | 1.0 | 400 | 2.667 s | 1275-1725 Hz |
| R25 | 8 | 25 Bd | 1500 Hz | 2.0 | 299 | 11.96 s | 1412-1588 Hz |

Tone m is at centre + (m - (M - 1) / 2) x symbol rate.  The first and last quarter
symbol are shaped with a raised cosine.  The occupied bandwidth is roughly the tone span
plus one symbol rate.

## Changes that would need a new version

Any change to the header layout, CRC, whitening, code, interleaver, mapping or sync
pattern.  Adding profiles or payload types does not: receivers reject types they do not
know.  Type 2 was added in stage 4 for loop prevention.
