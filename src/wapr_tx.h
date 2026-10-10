/* wapr_tx.c - transmit side of the experimental WAPR modem (doc/wapr). */

#ifndef WAPR_TX_H
#define WAPR_TX_H 1

#include "audio.h"
#include "ax25_pad.h"
#include "wapr.h"

/*
 * Packet to WAPR frame.  Only what WAPR can carry is accepted: a UI frame,
 * PID 0xF0, no digipeater path, at most WAPR_PAYLOAD_AREA bytes of information,
 * representable callsigns, and not MIC-E (its destination field is data).
 * Returns WAPR_OK, or a negative error with a reason in why[].
 */
int wapr_frame_from_packet (packet_t pp, wapr_frame_t *f, char *why, int whylen);

/* Transmit one packet on a MODEM WAPR channel.  Returns symbols sent, or -1 if rejected. */
int wapr_send_frame (int chan, packet_t pp, struct audio_s *pa);

/* Silence of nsym symbol times (TXDELAY / TXTAIL stand-in).  Returns nsym. */
int wapr_send_silence (int chan, int nsym, struct audio_s *pa);

#endif
