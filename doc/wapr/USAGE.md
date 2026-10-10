# Using the experimental WAPR modem

**Experimental.  Not compatible with AX.25 / APRS radios, TNCs or digipeaters on the
air.**  A WAPR channel only talks to other WAPR stations.  Nothing changes unless a
channel is configured for it.  Measurements so far are simulations
(`STAGE1_REPORT.md`, `STAGE2_REPORT.md`, `STAGE3_REPORT.md`).

## Configuration

    CHANNEL 1
    MODEM WAPR H150

| Profile | Use | Symbol rate | Tones | Airtime (32 byte payload) |
|---|---|---|---|---|
| `F600` | VHF/UHF FM | 600 Bd | 4, 900-2700 Hz | 0.67 s |
| `H150` | HF SSB | 150 Bd | 4, 1275-1725 Hz | 2.67 s |
| `R25` | HF, very weak signals | 25 Bd | 8, 1412-1588 Hz | 11.96 s |

The whole channel becomes WAPR: other `MODEM` options, `FX25TX` and `IL2PTX` do not
apply to it, and nothing on it is sent or received as AX.25.  `TXDELAY` and `TXTAIL`
become silence of the same length with the transmitter keyed.  The receiver searches
+-50 Hz around the nominal tones.

Offline tools: `gen_packets -W H150 -o x.wav file.txt` and `atest -W H150 x.wav`.

## What goes over the air

| Direction | Mapping |
|---|---|
| Transmit (from KISS / AGW clients, beacons) | UI frame, PID F0, **no digipeater path** (only `WAPRGW`, used by the gateway), information part at most **32 bytes**, source a callsign of 1-6 letters/digits with SSID 0-15.  The AX.25 destination (an APRS tocall) is not sent.  MIC-E is refused because its destination field carries data.  Anything else is refused with a message naming the reason; nothing falls back to AX.25. |
| Receive (to clients, the log and the display) | `SOURCE>APZWAP:payload` (or `SOURCE>DEST:...` if the frame has a destination), no path; frames relayed by a gateway with the path `WAPRGW*`.  Shown with `WAPR` and the estimated SNR and frequency offset. |

## Gateway (explicit rules only)

Without `WAPRGATE` rules, frames heard on a WAPR channel are **not** IGated, digipeated,
regenerated or passed to the APRStt gateway, and nothing heard elsewhere is sent on WAPR.

    WAPRGATE from to [types]

`from` and `to` are radio channel numbers, or `to` is `IS` for APRS-IS (through the
IGate, which must be configured).  One side must be a WAPR channel; IS to WAPR is not
supported.  `types` is a comma separated list of `POS`, `STATUS`, `MSG`, `OBJ`, `ITEM`,
`WX`, `TLM`, `OTHER`, or `ALL` (default).  Example, a gateway between an HF WAPR channel 1
and a VHF AX.25 channel 0, positions and messages only, WAPR also to APRS-IS:

    WAPRGATE 1 0 POS,MSG
    WAPRGATE 0 1 POS,MSG
    WAPRGATE 1 IS

| Direction | What is sent | Refused (logged as `[WAPR gate ...] not gated, reason`) |
|---|---|---|
| WAPR to AX.25 | `SOURCE>APZWAP,MYCALL*,NOGATE:info` | frames already relayed by a gateway |
| WAPR to IS | through the IGate (`qAO` / `qAR` as usual) | frames already relayed by a gateway |
| AX.25 to WAPR | WAPR type 2 ("relayed"), shown as `SOURCE>APZWAP,WAPRGW*:info` | tocall `APZWAP`; one of our calls in the path; `TCPIP` / `TCPXX` or third party format; our own transmissions; information part over 32 bytes; frames with a bad FCS |
| any | | the same source and information part sent to the same place within 60 s |

Loop prevention: a relayed frame (type 2) is never gated again by any gateway running
this code; frames put on AX.25 carry `NOGATE`, so no IGate (including this one, hearing
them again) puts them on APRS-IS and digipeaters don't spread them further.  The only way
from WAPR to APRS-IS is a `WAPRGATE ... IS` rule.  `test/wapr/wapr_gate_test.py` checks
all of this, including an echo of the gateway's own transmissions.

## Channel busy detection

A WAPR channel reports "busy" to the transmitter while the audio in its tone band is
6 dB above the tracked noise floor (clear again below 3 dB), so transmissions wait for a
clear channel as on AX.25 channels.  Signals weaker than the noise in the band, which
WAPR can still decode, are not detected.

## What does not happen (yet)

* No acknowledgements, retries or airtime limit; one size class (32 bytes), no
  fragmentation.
* `mheard` records stations heard on WAPR like any other channel.

## Resources

The decoder runs in its own thread per WAPR channel, never in the audio thread.  It
looks at overlapping windows of two frame lengths every half frame.  Measured with
`atest -W` on 120 s of noise at 48 kHz (the busiest case: every window has candidates
to decode), one x86-64 core:

| Profile | CPU | Memory per channel |
|---|---|---|
| F600 | 21 % | 0.6 MB |
| H150 | 6.6 % | 2 MB |
| R25 | 3.6 % | 13 MB |

F600 is expensive for a small computer; making the search cheaper is future work.  If
the decoder falls behind, audio is skipped and counted ("decoder can't keep up"); the
rest of Dire Wolf is not slowed down.
