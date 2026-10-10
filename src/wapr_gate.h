/* wapr_gate.c - explicit gateway between WAPR channels, AX.25 channels and APRS-IS (doc/wapr). */

#ifndef WAPR_GATE_H
#define WAPR_GATE_H 1

#include "audio.h"
#include "config.h"
#include "ax25_pad.h"
#include "dlq.h"

/* APRS data type classes a rule can carry. */
#define WAPR_GT_POS	0x01	/* ! = / @ (and MIC-E, which WAPR can't carry) */
#define WAPR_GT_STATUS	0x02	/* > */
#define WAPR_GT_MSG	0x04	/* : messages, bulletins, acks */
#define WAPR_GT_OBJ	0x08	/* ; */
#define WAPR_GT_ITEM	0x10	/* ) */
#define WAPR_GT_WX	0x20	/* _ */
#define WAPR_GT_TLM	0x40	/* T */
#define WAPR_GT_OTHER	0x80	/* anything else */
#define WAPR_GT_ALL	0xFF

/* Parse "POS,MSG,..." (or ALL).  Returns 0 if a name is unknown. */
unsigned int wapr_gate_types (const char *list);

/* Check the rules once the whole configuration is known. */
void wapr_gate_init (struct audio_s *pa, struct misc_config_s *mc);

/* Every frame received on a radio channel (direwolf.c). */
void wapr_gate_rec (int chan, int subchan, packet_t pp, fec_type_t fec_type, retry_t retries);

#endif
