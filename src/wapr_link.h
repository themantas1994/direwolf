/* wapr_link.c - link behaviour of the experimental WAPR modem (doc/wapr). */

#ifndef WAPR_LINK_H
#define WAPR_LINK_H 1

#include "wapr.h"

#define WAPR_LINK_MAX_PENDING 8
#define WAPR_LINK_SEEN 64

typedef struct wapr_link_s {
	double airtime;			/* one frame, seconds */
	double ack_timeout;		/* wait this long for an acknowledgement, seconds */
	int max_tries;			/* transmissions of a frame that asks for one */
	double duty;			/* airtime limit as a fraction of time, 0 = none */
	double window;			/* averaging window of the limit, seconds */
	double budget, budget_t;	/* airtime still allowed, and when it was updated */
	unsigned int rng;
	int next_seq;
	struct {
	  int used, due, tries;
	  double next;
	  wapr_frame_t f;
	} pend[WAPR_LINK_MAX_PENDING];
	struct {
	  char src[WAPR_ADDR_LEN];
	  int seq;
	  unsigned int hash;
	  double t;
	} seen[WAPR_LINK_SEEN];
	int nseen;
	long sent, retries, gave_up, acked, duplicates, acks_sent, refused;
} wapr_link_t;

/* duty: fraction of time the channel may transmit (0 = no limit), over a 600 s window. */
void wapr_link_init (wapr_link_t *L, const wapr_profile_t *p, double duty, unsigned int seed);

/*
 * About to transmit f (from the queue).  Gives it its sequence number (a frame due for
 * retransmission keeps its number) and registers it for acknowledgement if f->ack.
 * Returns 0 to send, -1 if the airtime limit does not allow it now.
 */
int wapr_link_tx (wapr_link_t *L, wapr_frame_t *f, double now);

enum wapr_link_rx_e { WAPR_LINK_DELIVER = 1, WAPR_LINK_DUPLICATE = 2, WAPR_LINK_ACK = 3 };

/*
 * A frame was received.  mycall is this station's call on the channel.  Returns what to
 * do with it; *send_ack is set, with *ack filled in, when an acknowledgement is owed
 * (also for a duplicate: the first acknowledgement may have been lost).
 */
int wapr_link_rx (wapr_link_t *L, const wapr_frame_t *f, const char *mycall, double now, int *send_ack, wapr_frame_t *ack);

/* A frame whose acknowledgement did not come in time and may be sent again.  Returns 1 and the frame, or 0. */
int wapr_link_poll (wapr_link_t *L, double now, wapr_frame_t *f);

/*
 * The packet form of an acknowledgement, for the transmit queue: SOURCE>APZWAP,WAPRAK:<seq> <dest>.
 * wapr_tx.c turns it into a type 3 frame.
 */
#define WAPR_ACK_MARK "WAPRAK"


/*
 * Per channel instances for Dire Wolf (wapr_rx.c, wapr_tx.c).  Access to an instance is
 * serialised with wapr_link_lock / wapr_link_unlock.  The sender hook queues a packet for
 * transmission (tq_append in direwolf; not set in atest, which never transmits).
 */

#include "ax25_pad.h"

void wapr_link_setup (int chan, const wapr_profile_t *p, double duty, unsigned int seed);
wapr_link_t *wapr_link_chan (int chan);		/* NULL if the channel has none */
void wapr_link_lock (int chan);
void wapr_link_unlock (int chan);
void wapr_link_set_sender (void (*send)(int chan, int prio, packet_t pp));
void wapr_link_send (int chan, int prio, packet_t pp);	/* deletes pp if there is no sender */
double wapr_link_time (void);			/* seconds, monotonic */

#endif
