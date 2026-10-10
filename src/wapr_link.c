//
//    This file is part of Dire Wolf, an amateur radio packet TNC.
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 2 of the License, or
//    (at your option) any later version.
//
//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with this program.  If not, see <http://www.gnu.org/licenses/>.
//


/*------------------------------------------------------------------
 *
 * Module:      wapr_link.c
 *
 * Purpose:   	Link behaviour of the experimental WAPR modem: sequence
 *		numbers, acknowledgement and bounded retransmission of frames
 *		that ask for it, duplicate suppression, and an airtime limit.
 *
 * Description:	Pure state machine: no threads, no I/O, time is passed in,
 *		so it can be tested deterministically (wapr_test.c).  The
 *		glue (wapr_rx.c, wapr_tx.c) serialises access.
 *
 *		Which frames ask for an acknowledgement is decided when a
 *		packet is mapped (wapr_tx.c): APRS messages to one station.
 *		Broadcasts (positions, status, bulletins, ...) never do: there
 *		is nobody in particular to answer, and the next beacon replaces
 *		a lost one anyway.
 *
 *		Retransmission: if no acknowledgement arrives within
 *		ack_timeout (three frame times plus 3 s: both frames, the
 *		decoding delays, TXDELAY and channel access) plus a random
 *		backoff of up to one frame time, the frame
 *		is sent again with the same sequence number, at most max_tries
 *		times in all.  The receiver suppresses the copies but
 *		acknowledges each one, in case its acknowledgement was lost.
 *
 *		Airtime limit: a token bucket.  The channel may transmit for
 *		duty * window seconds in any window; a frame that does not fit
 *		is refused (it is a beacon or a new message: the sender's
 *		application or the next beacon will try again).
 *		Acknowledgements are always allowed: refusing them only causes
 *		retransmissions.
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>
#if __WIN32__
#include <windows.h>
#endif

#include "textcolor.h"
#include "wapr.h"
#include "wapr_link.h"


static double rnd (wapr_link_t *L)
{
	L->rng = L->rng * 1103515245u + 12345u;
	return ((L->rng >> 8) / 16777216.0);
}

static unsigned int hash_frame (const wapr_frame_t *f)
{
	unsigned int h = 2166136261u ^ (unsigned int)f->type;
	for (int i = 0; i < f->len; i++) h = (h ^ f->payload[i]) * 16777619u;
	for (const char *p = f->dest; *p; p++) h = (h ^ (unsigned char)*p) * 16777619u;
	return (h);
}

static int same_frame (const wapr_frame_t *a, const wapr_frame_t *b)
{
	return (a->type == b->type && a->len == b->len && strcmp(a->source, b->source) == 0 &&
		strcmp(a->dest, b->dest) == 0 && memcmp(a->payload, b->payload, a->len) == 0);
}


void wapr_link_init (wapr_link_t *L, const wapr_profile_t *p, double duty, unsigned int seed)
{
	memset (L, 0, sizeof(*L));
	wapr_layout_t lay;
	wapr_layout (p, &lay);
	L->airtime = lay.n_sym / p->baud;
	/* frame out, the receiver finds it within half a frame (its decoding hop),
	   the acknowledgement waits for TXDELAY and SLOTTIME / PERSIST, goes back, and is
	   found again within half a frame: three frame times plus a few seconds */
	L->ack_timeout = 3.0 * L->airtime + 3.0;
	L->max_tries = 3;
	L->duty = duty;
	L->window = 600.0;
	L->budget = duty * L->window;
	L->budget_t = 0;
	L->rng = seed ? seed : 1;
	L->next_seq = (int)(rnd(L) * 1024) & 1023;	/* don't restart at 0: a receiver may remember us */
}


static void refill (wapr_link_t *L, double now)
{
	if (L->duty <= 0) return;
	if (L->budget_t == 0) L->budget_t = now;
	L->budget += (now - L->budget_t) * L->duty;
	if (L->budget > L->duty * L->window) L->budget = L->duty * L->window;
	L->budget_t = now;
}


int wapr_link_tx (wapr_link_t *L, wapr_frame_t *f, double now)
{
	refill (L, now);

	/* A retransmission keeps its sequence number. */
	for (int i = 0; i < WAPR_LINK_MAX_PENDING; i++) {
	  if (L->pend[i].used && L->pend[i].due && same_frame(&L->pend[i].f, f)) {
	    if (L->duty > 0 && L->budget < L->airtime) {
	      L->refused++;
	      return (-1);
	    }
	    f->seq = L->pend[i].f.seq;
	    f->ack = 1;
	    L->pend[i].due = 0;
	    L->pend[i].tries++;
	    L->pend[i].next = now + L->ack_timeout + rnd(L) * L->airtime;
	    L->budget -= L->airtime;
	    L->retries++;
	    L->sent++;
	    return (0);
	  }
	}

	if (f->type != WAPR_TYPE_LINK_ACK && L->duty > 0 && L->budget < L->airtime) {
	  L->refused++;
	  return (-1);
	}
	if (f->type == WAPR_TYPE_LINK_ACK) {
	  L->acks_sent++;		/* seq already set: the frame being acknowledged */
	}
	else {
	  f->seq = L->next_seq;
	  L->next_seq = (L->next_seq + 1) & 1023;
	}
	L->budget -= L->airtime;
	L->sent++;

	if (f->ack && f->type != WAPR_TYPE_LINK_ACK) {
	  int k = -1;
	  double oldest = 1e300;
	  for (int i = 0; i < WAPR_LINK_MAX_PENDING; i++) {
	    if (! L->pend[i].used) { k = i; break; }
	    if (L->pend[i].next < oldest) { oldest = L->pend[i].next; k = i; }
	  }
	  if (L->pend[k].used) L->gave_up++;	/* table full: forget the oldest */
	  L->pend[k].used = 1;
	  L->pend[k].due = 0;
	  L->pend[k].tries = 1;
	  L->pend[k].f = *f;
	  L->pend[k].next = now + L->ack_timeout + rnd(L) * L->airtime;
	}
	return (0);
}


int wapr_link_rx (wapr_link_t *L, const wapr_frame_t *f, const char *mycall, double now, int *send_ack, wapr_frame_t *ack)
{
	*send_ack = 0;

	if (f->type == WAPR_TYPE_LINK_ACK) {
	  if (strcmp(f->dest, mycall) == 0) {
	    for (int i = 0; i < WAPR_LINK_MAX_PENDING; i++) {
	      if (L->pend[i].used && L->pend[i].f.seq == f->seq && strcmp(L->pend[i].f.dest, f->source) == 0) {
	        L->pend[i].used = 0;
	        L->acked++;
	      }
	    }
	  }
	  return (WAPR_LINK_ACK);
	}

	int for_me = f->ack && strcmp(f->dest, mycall) == 0;
	if (for_me) {
	  memset (ack, 0, sizeof(*ack));
	  ack->type = WAPR_TYPE_LINK_ACK;
	  ack->seq = f->seq;
	  strlcpy (ack->source, mycall, sizeof(ack->source));
	  strlcpy (ack->dest, f->source, sizeof(ack->dest));
	  *send_ack = 1;
	}

	/* Copies of a frame arrive within max_tries retransmission times. */
	double window = L->max_tries * (L->ack_timeout + L->airtime);
	unsigned int h = hash_frame (f);
	for (int i = 0; i < L->nseen && i < WAPR_LINK_SEEN; i++) {
	  if (L->seen[i].seq == f->seq && L->seen[i].hash == h && now - L->seen[i].t < window &&
	      strcmp(L->seen[i].src, f->source) == 0) {
	    L->duplicates++;
	    return (WAPR_LINK_DUPLICATE);
	  }
	}
	int k = L->nseen % WAPR_LINK_SEEN;
	strlcpy (L->seen[k].src, f->source, sizeof(L->seen[k].src));
	L->seen[k].seq = f->seq;
	L->seen[k].hash = h;
	L->seen[k].t = now;
	L->nseen++;
	return (WAPR_LINK_DELIVER);
}


int wapr_link_poll (wapr_link_t *L, double now, wapr_frame_t *f)
{
	for (int i = 0; i < WAPR_LINK_MAX_PENDING; i++) {
	  if (! L->pend[i].used || now < L->pend[i].next) continue;
	  if (L->pend[i].due) {
	    L->pend[i].tries++;		/* the queued copy was never sent: count it anyway */
	    L->pend[i].due = 0;
	  }
	  if (L->pend[i].tries >= L->max_tries) {
	    L->pend[i].used = 0;
	    L->gave_up++;
	    continue;
	  }
	  L->pend[i].due = 1;
	  /* if the transmit queue never gets to it, try again later */
	  L->pend[i].next = now + L->ack_timeout;
	  *f = L->pend[i].f;
	  return (1);
	}
	return (0);
}


/*------------------------------------------------------------------
 * Per channel instances for Dire Wolf.
 *---------------------------------------------------------------*/

static wapr_link_t *links[MAX_RADIO_CHANS];
static dw_mutex_t link_mutex[MAX_RADIO_CHANS];
static void (*sender)(int chan, int prio, packet_t pp) = NULL;

void wapr_link_setup (int chan, const wapr_profile_t *p, double duty, unsigned int seed)
{
	if (chan < 0 || chan >= MAX_RADIO_CHANS) return;
	if (links[chan] == NULL) {
	  links[chan] = malloc (sizeof(wapr_link_t));
	  dw_mutex_init (&link_mutex[chan]);
	}
	wapr_link_init (links[chan], p, duty, seed);
}

wapr_link_t *wapr_link_chan (int chan)
{
	return (chan >= 0 && chan < MAX_RADIO_CHANS ? links[chan] : NULL);
}

void wapr_link_lock (int chan)
{
	dw_mutex_lock (&link_mutex[chan]);
}

void wapr_link_unlock (int chan)
{
	dw_mutex_unlock (&link_mutex[chan]);
}

void wapr_link_set_sender (void (*send)(int chan, int prio, packet_t pp))
{
	sender = send;
}

void wapr_link_send (int chan, int prio, packet_t pp)
{
	if (sender != NULL) {
	  sender (chan, prio, pp);
	}
	else {
	  ax25_delete (pp);
	}
}

double wapr_link_time (void)
{
#if __WIN32__
	LARGE_INTEGER freq, count;		/* not GetTickCount64: Dire Wolf targets older Windows */
	QueryPerformanceFrequency (&freq);
	QueryPerformanceCounter (&count);
	return ((double)count.QuadPart / (double)freq.QuadPart);
#else
	struct timespec ts;
	clock_gettime (CLOCK_MONOTONIC, &ts);
	return (ts.tv_sec + ts.tv_nsec * 1e-9);
#endif
}
