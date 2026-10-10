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
| Transmit (from KISS / AGW clients, beacons) | UI frame, PID F0, **no digipeater path**, information part at most **32 bytes**, source a callsign of 1-6 letters/digits with SSID 0-15.  The AX.25 destination (an APRS tocall) is not sent.  MIC-E is refused because its destination field carries data.  Anything else is refused with a message naming the reason; nothing falls back to AX.25. |
| Receive (to clients, the log and the display) | `SOURCE>APZWAP:payload` (or `SOURCE>DEST:...` if the frame has a destination), no path.  Shown with `WAPR` and the estimated SNR and frequency offset. |

## What does not happen (yet)

* Frames received on a WAPR channel are **not** IGated, digipeated, regenerated or
  passed to the APRStt gateway.  Gateway behaviour with an explicit policy is stage 4.
* No channel-busy detection on WAPR channels: transmissions wait only for the usual
  SLOTTIME / PERSIST random delay.
* No acknowledgements, retries or fragmentation; one size class (32 bytes).
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
