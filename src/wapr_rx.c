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
 * Module:      wapr_rx.c
 *
 * Purpose:   	Receive side of the experimental WAPR modem for channels
 *		configured with "MODEM WAPR profile".
 *
 * Description:	The audio thread only stores samples in a ring buffer
 *		(wapr_rx_sample): no locks, no waiting, no decoding.
 *
 *		A decoder thread per WAPR channel takes overlapping windows
 *		of two frame lengths, every half frame, so every frame lies
 *		completely inside at least two windows, and runs the search
 *		and decoder (wapr_modem.c) on them.  A frame found in more
 *		than one window is delivered once.  If the decoder falls
 *		behind by more than the buffer holds, windows are skipped and
 *		counted, the audio thread is never slowed down.
 *
 *		atest uses the synchronous mode instead: windows are decoded
 *		in the caller's thread as soon as they are complete, so the
 *		results do not depend on timing.
 *
 *		Frames are delivered to the usual received frame queue as
 *		SOURCE>DEST:payload with no digipeater path and fec_type_wapr.
 *		DEST is the frame's destination, or WAPR_BROADCAST_TOCALL
 *		for a broadcast.  direwolf.c does not IGate, digipeat or
 *		regenerate them.
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <assert.h>

#if __WIN32__
#include <windows.h>
#include <process.h>
#else
#include <pthread.h>
#include <unistd.h>
#endif

#include "textcolor.h"
#include "audio.h"
#include "ax25_pad.h"
#include "dlq.h"
#include "demod.h"
#include "wapr.h"
#include "wapr_rx.h"

#define HISTORY 16
#define MAX_RESULTS 8

struct wapr_chan_s {
	int active;
	int chan;
	const wapr_profile_t *p;
	int fs;
	int L, W, H;			/* frame, window and hop length, samples */

	short *ring;			/* written only by the audio thread */
	unsigned int mask;
	volatile unsigned int wcount;	/* samples written, modulo 2^32 */

	/* Everything below belongs to the decoding side. */
	unsigned int seen32;
	long long total;		/* samples available, 64 bit */
	long long next_end;		/* end of the next window to decode */
	long long done_end;		/* end of the last window decoded */
	float *buf;
	struct { unsigned char info[WAPR_INFO_BYTES]; long long start; } hist[HISTORY];
	int nhist;
	long overruns;
};

static struct wapr_chan_s wc[MAX_RADIO_CHANS];
static int synchronous = 0;

void wapr_rx_set_synchronous (int sync)
{
	synchronous = sync;
}


packet_t wapr_packet_from_frame (const wapr_frame_t *f)
{
	char text[64];
	snprintf (text, sizeof(text), "%s>%s:", f->source, f->dest[0] != '\0' ? f->dest : WAPR_BROADCAST_TOCALL);
	packet_t pp = ax25_from_text (text, 1);
	if (pp != NULL) {
	  ax25_set_info (pp, (unsigned char *)f->payload, f->len);
	}
	return (pp);
}


static void deliver (struct wapr_chan_s *c, const wapr_rx_result_t *r, long long abs_start)
{
	unsigned char info[WAPR_INFO_BYTES];

	if (wapr_frame_pack(&r->frame, info) != WAPR_OK) {
	  return;		/* can't happen: it was unpacked from these bits */
	}
	for (int i = 0; i < c->nhist && i < HISTORY; i++) {
	  long long d = abs_start - c->hist[i].start;
	  if (d < 0) d = -d;
	  if (d < c->L / 2 && memcmp(info, c->hist[i].info, WAPR_INFO_BYTES) == 0) {
	    return;		/* same frame, seen in an earlier window */
	  }
	}
	int k = c->nhist % HISTORY;
	memcpy (c->hist[k].info, info, WAPR_INFO_BYTES);
	c->hist[k].start = abs_start;
	c->nhist++;

	packet_t pp = wapr_packet_from_frame (&r->frame);
	if (pp == NULL) {
	  text_color_set(DW_COLOR_ERROR);
	  dw_printf ("WAPR: channel %d: could not convert frame from %s\n", c->chan, r->frame.source);
	  return;
	}
	alevel_t alevel = demod_get_audio_level (c->chan, 0);
	char spectrum[40];
	snprintf (spectrum, sizeof(spectrum), "SNR %.0f dB, %+.0f Hz", r->snr_est, r->freq_offset);
	dlq_rec_frame (c->chan, 0, 0, pp, alevel, fec_type_wapr, RETRY_NONE, spectrum);
}


/* Bring c->total up to date with the audio thread's count. */

static void update_total (struct wapr_chan_s *c)
{
	unsigned int w = c->wcount;
	__sync_synchronize ();
	c->total += (unsigned int)(w - c->seen32);
	c->seen32 = w;
}


/* Decode the window [end - len, end).  Returns 0, or -1 if the audio was overwritten. */

static int decode_window (struct wapr_chan_s *c, long long end, int len)
{
	long long start = end - len;
	unsigned int ring_size = c->mask + 1;

	if (c->total - start > ring_size) {
	  return (-1);
	}
	for (int i = 0; i < len; i++) {
	  c->buf[i] = c->ring[(unsigned int)(start + i) & c->mask];
	}
	update_total (c);
	if (c->total - start > ring_size) {
	  return (-1);		/* overwritten while copying */
	}

	wapr_rx_result_t res[MAX_RESULTS];
	int n = wapr_receive (c->p, c->buf, len, c->fs, res, MAX_RESULTS, NULL);
	for (int i = 0; i < n; i++) {
	  deliver (c, &res[i], start + (long long)res[i].start);
	}
	c->done_end = end;
	return (0);
}


/* Decode every complete window.  Returns number decoded. */

static int process_ready (struct wapr_chan_s *c)
{
	int count = 0;
	update_total (c);
	while (c->total >= c->next_end) {
	  if (c->total - (c->next_end - c->W) > (long long)(c->mask + 1) || decode_window(c, c->next_end, c->W) != 0) {
	    /* Fell behind: skip to the newest complete window. */
	    c->overruns++;
	    if (c->overruns == 1 || c->overruns % 100 == 0) {
	      text_color_set(DW_COLOR_ERROR);
	      dw_printf ("WAPR: channel %d: decoder can't keep up, audio skipped (%ld times).\n", c->chan, c->overruns);
	    }
	    long long behind = (c->total - c->next_end) / c->H;
	    c->next_end += (behind + 1) * (long long)c->H;
	    continue;
	  }
	  c->next_end += c->H;
	  count++;
	}
	return (count);
}


void wapr_rx_flush (int chan)
{
	struct wapr_chan_s *c = &wc[chan];
	if (! c->active) return;
	process_ready (c);
	if (c->total > c->done_end) {
	  int len = c->total < c->W ? (int)c->total : c->W;
	  decode_window (c, c->total, len);
	}
}


#if __WIN32__
static unsigned __stdcall wapr_rx_thread (void *arg)
#else
static void * wapr_rx_thread (void *arg)
#endif
{
	struct wapr_chan_s *c = (struct wapr_chan_s *)arg;

	while (1) {
	  if (process_ready(c) == 0) {
	    SLEEP_MS (50);
	  }
	}
#if __WIN32__
	return (0);
#else
	return (NULL);
#endif
}


void wapr_rx_init (struct audio_s *pa)
{
	for (int chan = 0; chan < MAX_RADIO_CHANS; chan++) {
	  struct wapr_chan_s *c = &wc[chan];

	  if (c->active && synchronous) {
	    /* atest: called again for each file, which may have another sample rate. */
	    free (c->ring);
	    free (c->buf);
	    c->ring = NULL;
	    c->buf = NULL;
	    c->active = 0;
	  }
	  if (pa->chan_medium[chan] != MEDIUM_RADIO || pa->achan[chan].modem_type != MODEM_WAPR || c->active) {
	    continue;
	  }
	  memset (c, 0, sizeof(*c));
	  c->chan = chan;
	  c->p = wapr_profile_find (pa->achan[chan].wapr_profile);
	  if (c->p == NULL) {
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("WAPR: channel %d: unknown profile \"%s\".\n", chan, pa->achan[chan].wapr_profile);
	    continue;
	  }
	  c->fs = pa->adev[ACHAN2ADEV(chan)].samples_per_sec;
	  c->L = wapr_samples_needed (c->p, c->fs);
	  int sym = (int)(c->fs / c->p->baud) + 1;
	  c->W = 2 * c->L + 4 * sym;
	  c->H = c->L / 2;
	  unsigned int size = 1;
	  while (size < 2u * (unsigned int)c->W) size <<= 1;
	  c->mask = size - 1;
	  c->ring = calloc (size, sizeof(short));
	  c->buf = malloc (sizeof(float) * c->W);
	  if (c->ring == NULL || c->buf == NULL) {
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("WAPR: channel %d: out of memory.\n", chan);
	    exit (EXIT_FAILURE);
	  }
	  c->next_end = c->W;
	  c->active = 1;

	  text_color_set(DW_COLOR_INFO);
	  dw_printf ("Channel %d: EXPERIMENTAL WAPR %s receiver, %.0f Bd %d-FSK at %.0f Hz, frame %.2f s, %s decoding.\n",
		chan, c->p->name, c->p->baud, c->p->tones, c->p->f_center, (double)c->L / c->fs,
		synchronous ? "synchronous" : "background");

	  if (! synchronous) {
#if __WIN32__
	    HANDLE th = (HANDLE)_beginthreadex (NULL, 0, wapr_rx_thread, c, 0, NULL);
	    if (th == NULL) {
	      text_color_set(DW_COLOR_ERROR);
	      dw_printf ("WAPR: could not create decoder thread for channel %d.\n", chan);
	      exit (EXIT_FAILURE);
	    }
#else
	    pthread_t tid;
	    if (pthread_create (&tid, NULL, wapr_rx_thread, c) != 0) {
	      text_color_set(DW_COLOR_ERROR);
	      perror ("WAPR: could not create decoder thread");
	      exit (EXIT_FAILURE);
	    }
	    pthread_detach (tid);
#endif
	  }
	}
}


__attribute__((hot))
void wapr_rx_sample (int chan, int sam)
{
	struct wapr_chan_s *c = &wc[chan];

	if (! c->active) return;

	unsigned int w = c->wcount;
	c->ring[w & c->mask] = (short)(sam > 32767 ? 32767 : sam < -32768 ? -32768 : sam);
	__sync_synchronize ();		/* sample visible before the count */
	c->wcount = w + 1;

	if (synchronous && (long long)(c->total + (unsigned int)(w + 1 - c->seen32)) >= c->next_end) {
	  process_ready (c);
	}
}
