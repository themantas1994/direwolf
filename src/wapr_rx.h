/* wapr_rx.c - receive side of the experimental WAPR modem (doc/wapr). */

#ifndef WAPR_RX_H
#define WAPR_RX_H 1

#include "audio.h"
#include "ax25_pad.h"
#include "wapr.h"

/* Before multi_modem_init: 1 = decode in the caller's thread (atest), 0 = decoder threads (default). */
void wapr_rx_set_synchronous (int sync);

/* Called by multi_modem_init.  Does nothing for channels that are not MODEM_WAPR. */
void wapr_rx_init (struct audio_s *pa);

/* One audio sample, from the audio thread.  Never blocks. */
void wapr_rx_sample (int chan, int sam);

/* End of input (atest): decode what is left in the buffer. */
void wapr_rx_flush (int chan);

/* Frame to packet, as delivered to applications: SOURCE>DEST:payload, no path. */
packet_t wapr_packet_from_frame (const wapr_frame_t *f);

#define WAPR_BROADCAST_TOCALL "APZWAP"	/* experimental tocall used as AX.25 destination for broadcasts */

#endif
