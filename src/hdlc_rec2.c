//    This file is part of Dire Wolf, an amateur radio packet TNC.
//
//    Copyright (C) 2011, 2012, 2013, 2014, 2015  John Langner, WB2OSZ
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



/********************************************************************************
 *
 * File:	hdlc_rec2.c
 *
 * Purpose:	Extract HDLC frame from a block of bits after someone
 *		else has done the work of pulling it out from between
 *		the special "flag" sequences.
 *
 *
 * New in version 1.1:
 *
 *		Several enhancements provided by Fabrice FAURE:
 *
 *		- Additional types of attempts to fix a bad CRC.
 *		- Optimized code to reduce execution time.
 *		- Improved detection of duplicate packets from different fixup attempts.
 *		- Set limit on number of packets in fix up later queue.
 *
 *		One of the new recovery attempt cases recovers three additional 
 *		packets that were lost before.  The one thing I disagree with is
 *		use of the word "swap" because that sounds like two things 
 *		are being exchanged for each other.  I would prefer "flip"
 *		or "invert" to describe changing a bit to the opposite state.
 *		I took "swap" out of the user-visible messages but left the 
 *		rest of the source code as provided.
 *
 * Test results:	We intentionally use the worst demodulator so there
 *			is more opportunity to try to fix the frames.
 *
 *		atest -P A -F n 02_Track_2.wav
 *
 *		n   	description	frames	sec
 *		--  	----------- 	------	---
 *		0	no attempt	963	40	error-free frames
 *		1	invert 1	979	41	16 more
 *		2	invert 2	982	42	3 more
 *		3	invert 3	982	42	no change
 *		4	remove 1	982	43	no change
 *		5	remove 2	982	43	no change
 *		6	remove 3	982	43	no change
 *		7	insert 1	982	45	no change
 *		8	insert 2	982	47	no change
 *		9	invert two sep	993	178	11 more, some visually obvious errors.
 *		10	invert many?	993	190	no change
 *		11	remove many	995	190	2 more, need to investigate in detail.		
 *		12	remove two sep	995	201	no change
 *
 * Observations:	The "insert" and "remove" techniques had no benefit.  I would not expect them to.
 *			We have a phase locked loop that attempts to track any slight variations in the 
 *			timing so we sample near the middle of the bit interval.  Bits can get corrupted 
 *			by noise but not disappear or just appear.  That would be a gap in the timing.	
 *			These should probably be removed in a future version.
 *
 *	
 * Version 1.2:	Now works for 9600 baud.
 *		This was more complicated due to the data scrambling.
 *		It was necessary to retain more initial state information after
 *		the start flag octet.
 *
 * Version 1.3: Took out all of the "insert" and "remove" cases because they
 *		offer no benenfit.
 *
 *		Took out the delayed processing and just do it realtime.
 *		Changed SWAP to INVERT because it is more descriptive.
 *
 *******************************************************************************/

#include "direwolf.h"

#include <stdio.h>
#include <stdlib.h>
#include <assert.h>
#include <ctype.h>
#include <string.h>

//Optimize processing by accessing directly to decoded bits
#define RRBB_C 1
#include "hdlc_rec2.h"
#include "demod.h"
#include "fcs_calc.h"
#include "textcolor.h"
#include "ax25_pad.h"
#include "rrbb.h"
#include "multi_modem.h"
#include "dtime_now.h"
#include "demod_9600.h"		/* for descramble() */
#include "audio.h"		/* for struct audio_s */
//#include "ax25_pad.h"		/* for AX25_MAX_ADDR_LEN */
#include "ais.h"

//#define DEBUG 1
//#define DEBUGx 1
//#define DEBUG_LATER 1

/* Audio configuration. */

static struct audio_s          *save_audio_config_p;


/* 
 * Minimum & maximum sizes of an AX.25 frame including the 2 octet FCS. 
 */

#define MIN_FRAME_LEN ((AX25_MIN_PACKET_LEN) + 2)
				
#define MAX_FRAME_LEN ((AX25_MAX_PACKET_LEN) + 2)	


/*
 * This is the current state of the HDLC decoder.
 *
 * It is possible to run multiple decoders concurrently by
 * having a separate set of state variables for each.
 *
 * Should have a reset function instead of initializations here.
 */

// TODO: Clean up. This is a remnant of splitting hdlc_rec.c into 2 parts.
// This is not the same as hdlc_state_s in hdlc_rec.c
// "2" was added to reduce confusion.  Can be trimmed down.

struct hdlc_state2_s {

	int prev_raw;			/* Keep track of previous bit so */
					/* we can look for transitions. */
					/* Should be only 0 or 1. */

	int is_scrambled;		/* Set for 9600 baud. */
	int lfsr;			/* Descrambler shift register for 9600 baud. */
	int prev_descram;		/* Previous unscrambled for 9600 baud. */


	unsigned char pat_det; 		/* 8 bit pattern detector shift register. */
					/* See below for more details. */

	unsigned char oacc;		/* Accumulator for building up an octet. */

	int olen;			/* Number of bits in oacc. */
					/* When this reaches 8, oacc is copied */
					/* to the frame buffer and olen is zeroed. */

	unsigned char frame_buf[MAX_FRAME_LEN];
					/* One frame is kept here. */

	int frame_len;			/* Number of octets in frame_buf. */
					/* Should be in range of 0 .. MAX_FRAME_LEN. */

};


static int try_decode (rrbb_t block, int chan, int subchan, int slice, alevel_t alevel, retry_conf_t retry_conf, int passall);

static int try_to_fix_quick_now (rrbb_t block, int chan, int subchan, int slice, alevel_t alevel);

static int try_soft_fix (rrbb_t block, int chan, int subchan, int slice, alevel_t alevel);

static int soft_fix_wanted (int chan);

static void soft_fix_defer (rrbb_t block, int chan, int subchan, int slice);

static void soft_fix_note_good (int chan);

static int sanity_check (unsigned char *buf, int blen, retry_t bits_flipped, enum sanity_e sanity_test);


/***********************************************************************************
 *
 * Name:	hdlc_rec2_init
 *
 * Purpose:	Initialization.   
 *
 * Inputs:	p_audio_config	 - Pointer to configuration settings.
 *				   This is what we care about for each channel.
 *
 *	   			enum retry_e fix_bits;	
 *					Level of effort to recover from 
 *					a bad FCS on the frame. 
 *					0 = no effort 
 *					1 = try inverting a single bit
 *					2... = more techniques... 
 *
 *	    			enum sanity_e sanity_test;
 *					Sanity test to apply when finding a good 
 *					CRC after changing one or more bits. 
 *					Must look like APRS, AX.25, or anything. 
 *
 *	    			int passall;		
 *					Allow thru even with bad CRC after exhausting
 *					all fixup attempts.
 *
 * Description:	Save pointer to configuration for later use.
 *
 ***********************************************************************************/

void hdlc_rec2_init (struct audio_s *p_audio_config)
{
	save_audio_config_p = p_audio_config;
}



/***********************************************************************************
 *
 * Name:	hdlc_rec2_block
 *
 * Purpose:	Extract HDLC frame from a stream of bits.
 *
 * Inputs:	block 		- Handle for bit array.
 *				  This will be deallocated so the caller
 *				  must not hold on to the address.
 *
 * Description:	The other (original) hdlc decoder took one bit at a time
 *		right out of the demodulator.
 *
 *		This is different in that it processes a block of bits
 *		previously extracted from between two "flag" patterns.
 *
 *		This allows us to try decoding the same received data more
 *		than once.
 *
 * Version 1.2:	Now works properly for G3RUH type scrambling.
 *
 ***********************************************************************************/


void hdlc_rec2_block (rrbb_t block)
{
	int chan = rrbb_get_chan(block);
	int subchan = rrbb_get_subchan(block);
	int slice = rrbb_get_slice(block);
	alevel_t alevel = rrbb_get_audio_level(block);
	retry_t fix_bits = save_audio_config_p->achan[chan].fix_bits;
	int passall = save_audio_config_p->achan[chan].passall;
	int ok;

#if DEBUGx
	text_color_set(DW_COLOR_DEBUG);
	dw_printf ("\n--- try to decode ---\n");
#endif

	/* Create an empty retry configuration */
	retry_conf_t retry_cfg;

	memset (&retry_cfg, 0, sizeof(retry_cfg));

/* 
 * For our first attempt we don't try to alter any bits.
 * Still let it thru if passall AND no retries are desired.
 */

	retry_cfg.type = RETRY_TYPE_NONE;
	retry_cfg.mode = RETRY_MODE_CONTIGUOUS;
	retry_cfg.retry = RETRY_NONE;
	retry_cfg.u_bits.contig.nr_bits = 0;
	retry_cfg.u_bits.contig.bit_idx = 0;

	ok = try_decode (block, chan, subchan, slice, alevel, retry_cfg, passall & (fix_bits == RETRY_NONE));
	if (ok) {
#if DEBUG
	  text_color_set(DW_COLOR_INFO);
	  dw_printf ("Got it the first time.\n");
#endif
	 soft_fix_note_good (chan);
	 rrbb_delete (block);
	 return;
	}

/*
 * Not successful with frame in original form.
 * See if we can "fix" it.
 */
	if (try_to_fix_quick_now (block, chan, subchan, slice, alevel)) {
	  soft_fix_note_good (chan);
	  rrbb_delete (block);
	  return;
	}

/*
 * With several demodulators or slicers, each delivers its own copy of
 * the frame.  Wait until all of them are in, then repair only the most
 * promising copy, and only if none of them was decoded.
 * hdlc_rec2_soft_fix_expire takes it from here and frees the block.
 */
	if (soft_fix_wanted (chan) &&
	    save_audio_config_p->achan[chan].num_subchan * save_audio_config_p->achan[chan].num_slicers > 1 &&
	    ! passall) {
	  soft_fix_defer (block, chan, subchan, slice);
	  return;
	}

	if (try_soft_fix (block, chan, subchan, slice, alevel)) {
	  rrbb_delete (block);
	  return;
	}


	if (passall) {
	  /* Exhausted all desired fix up attempts. */
	  /* Let thru even with bad CRC.  Of course, it still */
	  /* needs to be a minimum number of whole octets. */
	  ok = try_decode (block, chan, subchan, slice, alevel, retry_cfg, 1);
	}

	rrbb_delete (block);

} /* end hdlc_rec2_block */


/***********************************************************************************
 *
 * Name:	try_to_fix_quick_now
 *
 * Purpose:	Attempt some quick fixups that don't take very long.
 *
 * Inputs:	block	- Stream of bits that might be a frame.
 *		chan	- Radio channel from which it was received.
 *		subchan	- Which demodulator when more than one per channel.
 *		alevel	- Audio level for later reporting.
 *
 * Global In:	configuration fix_bits - Maximum level of fix up to attempt.
 *
 *				RETRY_NONE (0)	- Don't try any.
 *				RETRY_INVERT_SINGLE (1)  - Try inverting single bits.
 *				etc.
 *
 *		configuration passall - Let it thru with bad CRC after exhausting
 *				all fixup attempts.
 *
 *
 * Returns:	1 for success.  "try_decode" has passed the result along to the 
 *				processing step.
 *		0 for failure.  Caller might continue with more aggressive attempts.
 *
 * Original:	Some of the attempted fix up techniques are quick.
 *		We will attempt them immediately after receiving the frame.
 *		Others, that take time order N**2, will be done in a later section.
 *
 * Version 1.2:	Now works properly for G3RUH type scrambling.
 *
 * Version 1.3: Removed the extra cases that didn't help.
 *		The separated bit case is now handled immediately instead of
 *		being thrown in a queue for later processing.
 *
 ***********************************************************************************/

static int try_to_fix_quick_now (rrbb_t block, int chan, int subchan, int slice, alevel_t alevel)
{
	int ok;
	int len, i;
	retry_t fix_bits = save_audio_config_p->achan[chan].fix_bits;
	//int passall = save_audio_config_p->achan[chan].passall;


	len = rrbb_get_len(block);
	/* Prepare the retry configuration */
        retry_conf_t retry_cfg;

	memset (&retry_cfg, 0, sizeof(retry_cfg));

	/* Will modify only contiguous bits*/
	retry_cfg.mode = RETRY_MODE_CONTIGUOUS; 
/* 
 * Try inverting one bit.
 */
	if (fix_bits < RETRY_INVERT_SINGLE) {

	  /* Stop before single bit fix up. */

	  return 0;	/* failure. */
	}
	/* Try to swap one bit */
	retry_cfg.type = RETRY_TYPE_SWAP;
	retry_cfg.retry = RETRY_INVERT_SINGLE;
	retry_cfg.u_bits.contig.nr_bits = 1;

	for (i=0; i<len; i++) {
	  /* Set the index of the bit to swap */
	  retry_cfg.u_bits.contig.bit_idx = i;
	  ok = try_decode (block, chan, subchan, slice, alevel, retry_cfg, 0);
	  if (ok) {
#if DEBUG
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("*** Success by flipping SINGLE bit %d of %d ***\n", i, len);
#endif
	    return 1;
	  }
	}

/* 
 * Try inverting two adjacent bits.
 */
	if (fix_bits < RETRY_INVERT_DOUBLE) {
	  return 0;
	}
	/* Try to swap two contiguous bits */
	retry_cfg.retry = RETRY_INVERT_DOUBLE;
	retry_cfg.u_bits.contig.nr_bits = 2;


	for (i=0; i<len-1; i++) {
	  retry_cfg.u_bits.contig.bit_idx = i;
	  ok = try_decode (block, chan, subchan, slice, alevel, retry_cfg, 0);
	  if (ok) {
#if DEBUG
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("*** Success by flipping DOUBLE bit %d of %d ***\n", i, len);
#endif
	    return 1;
	  }
	}

/*
 * Try inverting adjacent three bits.
 */
	if (fix_bits < RETRY_INVERT_TRIPLE) {
	  return 0;
	}
	/* Try to swap three contiguous bits */
	retry_cfg.retry = RETRY_INVERT_TRIPLE;
	retry_cfg.u_bits.contig.nr_bits = 3;

	for (i=0; i<len-2; i++) {
	  retry_cfg.u_bits.contig.bit_idx = i;
	  ok = try_decode (block, chan, subchan, slice, alevel, retry_cfg, 0);
	  if (ok) {
#if DEBUG
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("*** Success by flipping TRIPLE bit %d of %d ***\n", i, len);
#endif
	    return 1;
	  }
	}


/*
 * Two  non-adjacent ("separated") single bits.
 * It chews up a lot of CPU time.  Usual test takes 4 times longer to run.
 *
 * Processing time is order N squared so time goes up rapidly with larger frames.
 */
	if (fix_bits < RETRY_INVERT_TWO_SEP) {
	  return 0;
	}

	retry_cfg.mode = RETRY_MODE_SEPARATED;
	retry_cfg.type = RETRY_TYPE_SWAP;
	retry_cfg.retry = RETRY_INVERT_TWO_SEP;
	retry_cfg.u_bits.sep.bit_idx_c = -1;

#ifdef DEBUG_LATER
	tstart = dtime_monotonic();
	dw_printf ("*** Try flipping TWO SEPARATED BITS %d bits\n", len);
#endif
	len = rrbb_get_len(block);
	for (i=0; i<len-2; i++) {
	  retry_cfg.u_bits.sep.bit_idx_a = i;
	  int j;

	  ok = 0;
	  for (j=i+2; j<len; j++) {
	    retry_cfg.u_bits.sep.bit_idx_b = j;
	    ok = try_decode (block, chan, subchan, slice, alevel, retry_cfg, 0);
	    if (ok) {
	      break;
	    }

	  }	  
	  if (ok) {
#if DEBUG
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("*** Success by flipping TWO SEPARATED bits %d and %d of %d \n", i, j, len);
#endif
	    return (1);
	  }
	}

	return 0;
}



/***********************************************************************************
 *
 * Name:	try_soft_fix
 *
 * Purpose:	Soft decision repair of a frame with a bad FCS.
 *
 * Inputs:	block	- Stream of bits that might be a frame.
 *			  Includes the demodulator's confidence for each bit.
 *		chan	- Radio channel from which it was received.
 *		subchan	- Which demodulator when more than one per channel.
 *		slice	- Which slicer.
 *		alevel	- Audio level for later reporting.
 *
 * Global In:	configuration soft_fix - 0 = off, 1 = single bits (default),
 *				2 = more single bits and pairs.
 *		configuration fix_bits - Don't repeat what try_to_fix_quick_now did.
 *
 * Returns:	1 for success.  "try_decode" has passed the result along to the
 *				processing step.
 *		0 for failure.
 *
 * Description:	Near the decoding threshold, most lost frames have only one or
 *		two bits in error and those bits are nearly always the ones the
 *		demodulator was least sure about.
 *
 *		FIX_BITS 1 tries inverting every bit of the frame, one at a time.
 *		That finds single bit errors but needs hundreds of FCS checks per
 *		frame, and each extra check is another chance of a corrupted frame
 *		getting a good FCS by accident.
 *
 *		Here we invert only the least reliable bits, one at a time:
 *		SOFT_FIX_SINGLES_1 of them for level 1, SOFT_FIX_SINGLES_2 for
 *		level 2.  Level 2 then tries pairs from the SOFT_FIX_PAIRS least
 *		reliable.  Bits with confidence above SOFT_FIX_MAX_QUALITY are
 *		never touched, and nothing is tried if more than
 *		SOFT_FIX_MAX_DOUBTFUL per cent of the bits are doubtful.
 *
 *		Every FCS check on a frame that has more errors than we can fix
 *		is about a 1 in 65536 chance of accepting it with a wrong bit.
 *		After NRZI, the AX.25 FCS always catches one or two remaining
 *		bit errors, so a single inversion can't turn a frame with one bit
 *		error into a wrong frame; a pair can (about 1 in 32767).  Level 1,
 *		at most 8 checks, is the default.  Level 2, at most 16 + 66 checks,
 *		recovers more frames but measurably more corrupted ones too.
 *
 *		With multiple demodulators or slicers, the same budget of checks
 *		applies to the frame as a whole, not to each copy of it
 *		(soft_fix_defer, soft_fix_copies).
 *
 *		As with FIX_BITS, the result must pass the sanity test and is
 *		reported with the corresponding retry level, so it is displayed and
 *		sent to client applications but never digipeated or IGated.
 *		Not used for AIS (no sanity test) or with sanity test NONE.
 *
 ***********************************************************************************/

#define SOFT_FIX_SINGLES_1 8		/* FCS checks for single bit inversion per frame, level 1. */
#define SOFT_FIX_SINGLES_2 16		/* FCS checks for single bit inversion per frame, level 2. */
#define SOFT_FIX_PAIRS 12		/* Candidates for pairs, level 2.  Must be <= SOFT_FIX_SINGLES_2. */
#define SOFT_FIX_MAX_QUALITY 30		/* Leave alone bits with confidence above this. */
					/* (0 - 100 scale, as passed to hdlc_rec_bit.) */
					/* In tests, every successful inversion was below 28. */
#define SOFT_FIX_MAX_DOUBTFUL 30	/* Don't try if more than this per cent of the bits */
					/* are at or below SOFT_FIX_MAX_QUALITY.  That is noise, or */
					/* a frame with too many errors.  In tests, frames that */
					/* could be repaired had at most 16 per cent; noise over 40. */
#define SOFT_FIX_MAX_COPIES 9		/* Failed copies of one frame kept for repair. */
#define SOFT_FIX_MIN_COPIES 2		/* With several demodulators/slicers, repair only if at */
					/* least this many failed copies arrive together. */

static int soft_fix_wanted (int chan)
{
	int soft_fix = save_audio_config_p->achan[chan].soft_fix;
	retry_t fix_bits = save_audio_config_p->achan[chan].fix_bits;

	return (soft_fix > 0 &&
		! (soft_fix < 2 && fix_bits >= RETRY_INVERT_SINGLE) &&	/* Already tried every single bit. */
		fix_bits < RETRY_INVERT_TWO_SEP &&			/* Already tried every single bit and pair. */
		save_audio_config_p->achan[chan].modem_type != MODEM_AIS &&
		save_audio_config_p->achan[chan].sanity_test != SANITY_NONE);
}


/*
 * Positions of the least reliable bits, least reliable first.
 * Simple insertion into a short sorted list.
 * Bit 0 is the last bit of the opening flag so leave it alone.
 * None if too many bits are doubtful to be worth trying.
 */

static int soft_fix_candidates (rrbb_t block, int *cand, int maxcand)
{
	int len = rrbb_get_len(block);
	int ncand = 0;
	int doubtful = 0;
	int i;

	for (i = 1; i < len; i++) {
	  int q = rrbb_get_quality(block, i);
	  int j;

	  if (q > SOFT_FIX_MAX_QUALITY) continue;
	  doubtful++;
	  if (ncand == maxcand && q >= rrbb_get_quality(block, cand[ncand-1])) continue;

	  j = (ncand < maxcand) ? ncand++ : ncand - 1;
	  while (j > 0 && rrbb_get_quality(block, cand[j-1]) > q) {
	    cand[j] = cand[j-1];
	    j--;
	  }
	  cand[j] = i;
	}

	if (doubtful * 100 > len * SOFT_FIX_MAX_DOUBTFUL) {
	  return (0);
	}
	return (ncand);
}


/* Is this copy bit for bit the same as another?  Then it would give the same results. */

static int soft_fix_same (rrbb_t a, rrbb_t b)
{
	return (rrbb_get_len(a) == rrbb_get_len(b) &&
		rrbb_get_is_scrambled(a) == rrbb_get_is_scrambled(b) &&
		rrbb_get_descram_state(a) == rrbb_get_descram_state(b) &&
		rrbb_get_prev_descram(a) == rrbb_get_prev_descram(b) &&
		memcmp (a->fdata, b->fdata, rrbb_get_len(a)) == 0);
}


/***********************************************************************************
 *
 * Name:	soft_fix_copies
 *
 * Purpose:	Try to repair a frame, given one or more copies of it with a bad FCS.
 *
 * Inputs:	copies	- Copies of the same frame from different demodulators or
 *			  slicers, most promising first.
 *		ncopies	- How many.
 *		chan	- Radio channel.
 *
 * Returns:	1 if a repaired frame was passed along.
 *
 * Description:	The number of FCS checks per frame is fixed: SOFT_FIX_SINGLES_1
 *		for level 1, however many copies there are.  They are spread over
 *		the copies by rank: the least reliable bit of each copy, then the
 *		second least reliable bit of each, and so on.  Copies identical to
 *		an earlier one are skipped since they would give the same results.
 *		Level 2 then tries pairs of bits in the first copy.
 *
 ***********************************************************************************/

static int soft_fix_copies (rrbb_t *copies, int ncopies, int chan)
{
	int soft_fix = save_audio_config_p->achan[chan].soft_fix;
	retry_t fix_bits = save_audio_config_p->achan[chan].fix_bits;
	int budget = soft_fix >= 2 ? SOFT_FIX_SINGLES_2 : SOFT_FIX_SINGLES_1;
	int cand[SOFT_FIX_MAX_COPIES][SOFT_FIX_SINGLES_2];	/* Bit positions, least reliable first. */
	int ncand[SOFT_FIX_MAX_COPIES];
	retry_conf_t retry_cfg;
	int c, r, a, b;
	int checks = 0;

	assert (ncopies >= 1 && ncopies <= SOFT_FIX_MAX_COPIES);

	for (c = 0; c < ncopies; c++) {
	  int d;
	  ncand[c] = soft_fix_candidates (copies[c], cand[c], budget);
	  for (d = 0; d < c; d++) {
	    if (soft_fix_same (copies[c], copies[d])) {
	      ncand[c] = 0;
	      break;
	    }
	  }
	}

/*
 * Single bits.
 */
	if (fix_bits < RETRY_INVERT_SINGLE) {
	  memset (&retry_cfg, 0, sizeof(retry_cfg));
	  retry_cfg.type = RETRY_TYPE_SWAP;
	  retry_cfg.mode = RETRY_MODE_CONTIGUOUS;
	  retry_cfg.retry = RETRY_INVERT_SINGLE;
	  retry_cfg.u_bits.contig.nr_bits = 1;

	  for (r = 0; r < budget && checks < budget; r++) {
	    for (c = 0; c < ncopies && checks < budget; c++) {
	      rrbb_t block = copies[c];

	      if (r >= ncand[c]) continue;
	      retry_cfg.u_bits.contig.bit_idx = cand[c][r];
	      checks++;
	      if (try_decode (block, chan, rrbb_get_subchan(block), rrbb_get_slice(block), rrbb_get_audio_level(block), retry_cfg, 0)) {
#if DEBUG
	        text_color_set(DW_COLOR_DEBUG);
	        dw_printf ("*** Soft fix: inverted bit %d (rank %d, quality %d) of %d, copy %d ***\n",
				cand[c][r], r, rrbb_get_quality(block, cand[c][r]), rrbb_get_len(block), c);
#endif
	        return 1;
	      }
	    }
	  }
	}

	if (soft_fix < 2) {
	  return 0;
	}

/*
 * Pairs of bits, first copy only.  Use the existing retry types so the result
 * is reported and treated the same as from the FIX_BITS options.
 */
	rrbb_t block = copies[0];
	int *cd = cand[0];

	for (a = 1; a < SOFT_FIX_PAIRS && a < ncand[0]; a++) {
	  for (b = 0; b < a; b++) {
	    int x = cd[a] < cd[b] ? cd[a] : cd[b];
	    int y = cd[a] < cd[b] ? cd[b] : cd[a];

	    memset (&retry_cfg, 0, sizeof(retry_cfg));
	    retry_cfg.type = RETRY_TYPE_SWAP;
	    if (y == x + 1) {
	      if (fix_bits >= RETRY_INVERT_DOUBLE) continue;	/* Already tried. */
	      retry_cfg.mode = RETRY_MODE_CONTIGUOUS;
	      retry_cfg.retry = RETRY_INVERT_DOUBLE;
	      retry_cfg.u_bits.contig.bit_idx = x;
	      retry_cfg.u_bits.contig.nr_bits = 2;
	    }
	    else {
	      retry_cfg.mode = RETRY_MODE_SEPARATED;
	      retry_cfg.retry = RETRY_INVERT_TWO_SEP;
	      retry_cfg.u_bits.sep.bit_idx_a = x;
	      retry_cfg.u_bits.sep.bit_idx_b = y;
	      retry_cfg.u_bits.sep.bit_idx_c = -1;
	    }
	    if (try_decode (block, chan, rrbb_get_subchan(block), rrbb_get_slice(block), rrbb_get_audio_level(block), retry_cfg, 0)) {
#if DEBUG
	      text_color_set(DW_COLOR_DEBUG);
	      dw_printf ("*** Soft fix: inverted bits %d and %d (quality %d, %d) of %d ***\n",
				x, y, rrbb_get_quality(block, x), rrbb_get_quality(block, y), rrbb_get_len(block));
#endif
	      return 1;
	    }
	  }
	}

	return 0;

} /* end soft_fix_copies */


/*
 * Repair right away.  Used when there is only one demodulator and slicer
 * on the channel, so there is only one copy of each frame, and with PASSALL.
 */

static int try_soft_fix (rrbb_t block, int chan, int subchan, int slice, alevel_t alevel)
{
	(void) alevel;

	if ( ! soft_fix_wanted (chan)) {
	  return 0;
	}

/*
 * With multiple slicers, each one delivers its own copy of a frame.
 * Repairing all of them separately multiplies the chances of a corrupted
 * frame getting a good FCS by accident, so use only the best matched one.
 */
	if (save_audio_config_p->achan[chan].num_slicers > 1 &&
	    slice != demod_best_slicer (chan, subchan)) {
	  return 0;
	}

	return (soft_fix_copies (&block, 1, chan));
}


/***********************************************************************************
 *
 * Name:	soft_fix_defer, soft_fix_note_good, hdlc_rec2_soft_fix_expire
 *
 * Purpose:	Soft decision repair when a channel has more than one demodulator
 *		or slicer.
 *
 * Description:	Each demodulator/slicer delivers its own copy of a frame, a few
 *		bit times apart at most.  The failed copies are collected until
 *		the time window multi_modem uses to pick the best candidate has
 *		passed.  If any copy was decoded (with no bits changed, or by
 *		FIX_BITS) nothing more is done.  Otherwise the most promising
 *		copy from each demodulator goes to soft_fix_copies, with the same
 *		number of FCS checks as for a single copy.  The most promising is
 *		the best matched slicer's (demod_best_slicer), or the nearest
 *		slicer's if that one has no copy.
 *
 *		Noise produces junk "frames" at random times on each slicer.
 *		A real frame produces copies on several at the same time, so at
 *		least SOFT_FIX_MIN_COPIES failed copies must arrive together.
 *		On noise this cut the number of FCS checks about tenfold compared
 *		to repairing every copy from the best slicer.
 *
 *		All called from the audio receive thread, as is hdlc_rec2_block.
 *
 ***********************************************************************************/

static struct {
	rrbb_t block[SOFT_FIX_MAX_COPIES];	/* Failed copies, most promising first. */
	int prio[SOFT_FIX_MAX_COPIES];		/* Lower is more promising. */
	int n;					/* Number in block[]; 0 if nothing pending. */
	int copies;				/* Number of failed copies in this window. */
	int good;				/* Another copy of this frame was decoded. */
} soft_pending[MAX_RADIO_CHANS];

static int64_t soft_last_good[MAX_RADIO_CHANS];	/* multi_modem sample count when a frame was last */
						/* decoded without soft repair.  0 if never. */

static void soft_fix_note_good (int chan)
{
	soft_last_good[chan] = multi_modem_sample_count (chan);
	if (soft_pending[chan].n > 0) {
	  soft_pending[chan].good = 1;
	}
}

static void soft_fix_defer (rrbb_t block, int chan, int subchan, int slice)
{
	int64_t now = multi_modem_sample_count (chan);
	int window = multi_modem_window (chan);
	int prio = 0;
	int i;

	if (soft_last_good[chan] != 0 && now - soft_last_good[chan] <= window) {
	  rrbb_delete (block);		/* Another copy of this frame was just decoded. */
	  return;
	}

	if (save_audio_config_p->achan[chan].num_slicers > 1) {
	  prio = abs (slice - demod_best_slicer (chan, subchan));
	}

	if (soft_pending[chan].n == 0) {
	  soft_pending[chan].copies = 0;
	  soft_pending[chan].good = 0;
	  multi_modem_soft_fix_due (chan, now + window);	/* Call hdlc_rec2_soft_fix_expire then. */
	}
	soft_pending[chan].copies++;

	i = soft_pending[chan].n;
	if (i == SOFT_FIX_MAX_COPIES) {
	  if (prio >= soft_pending[chan].prio[i-1]) {
	    rrbb_delete (block);
	    return;
	  }
	  i--;
	  rrbb_delete (soft_pending[chan].block[i]);
	}
	else {
	  soft_pending[chan].n++;
	}

	/* Insert, keeping arrival order among equals. */
	while (i > 0 && soft_pending[chan].prio[i-1] > prio) {
	  soft_pending[chan].block[i] = soft_pending[chan].block[i-1];
	  soft_pending[chan].prio[i] = soft_pending[chan].prio[i-1];
	  i--;
	}
	soft_pending[chan].block[i] = block;
	soft_pending[chan].prio[i] = prio;
}


/*
 * Called by multi_modem when the window for other copies of the frame has passed.
 */

void hdlc_rec2_soft_fix_expire (int chan)
{
	int i;

	if (soft_pending[chan].n == 0) {
	  return;
	}

	if ( ! soft_pending[chan].good && soft_pending[chan].copies >= SOFT_FIX_MIN_COPIES) {

	  /* The most promising copy from each demodulator. */

	  rrbb_t use[SOFT_FIX_MAX_COPIES];
	  int nuse = 0;
	  int j;

	  for (i = 0; i < soft_pending[chan].n; i++) {
	    int sc = rrbb_get_subchan(soft_pending[chan].block[i]);
	    for (j = 0; j < nuse && rrbb_get_subchan(use[j]) != sc; j++) ;
	    if (j == nuse) use[nuse++] = soft_pending[chan].block[i];
	  }
	  soft_fix_copies (use, nuse, chan);
	}

	for (i = 0; i < soft_pending[chan].n; i++) {
	  rrbb_delete (soft_pending[chan].block[i]);
	  soft_pending[chan].block[i] = NULL;
	}
	soft_pending[chan].n = 0;
}


// TODO:  Remove this.  but first figure out what to do in atest.c



int hdlc_rec2_try_to_fix_later (rrbb_t block, int chan, int subchan, int slice, alevel_t alevel)
{
	int ok;
	//int len;
	//retry_t fix_bits = save_audio_config_p->achan[chan].fix_bits;
	int passall = save_audio_config_p->achan[chan].passall;
#if DEBUG_LATER
	double tstart, tend;
#endif
	retry_conf_t retry_cfg;

	memset (&retry_cfg, 0, sizeof(retry_cfg));

	//len = rrbb_get_len(block);


/*
 * All fix up attempts have failed.  
 * Should we pass it along anyhow with a bad CRC?
 * Note that we still need a minimum number of whole octets.
 */
	if (passall) {

	  retry_cfg.type = RETRY_TYPE_NONE;
	  retry_cfg.mode = RETRY_MODE_CONTIGUOUS;
	  retry_cfg.retry = RETRY_NONE;
	  retry_cfg.u_bits.contig.nr_bits = 0;
	  retry_cfg.u_bits.contig.bit_idx = 0;
	  ok = try_decode (block, chan, subchan, slice, alevel, retry_cfg, passall);
	  return (ok);
	}

	return (0);

}  /* end hdlc_rec2_try_to_fix_later */



/* 
 * Check if the specified index of bit has been modified with the current type of configuration
 * Provide a specific implementation for contiguous mode to optimize number of tests done in the loop 
 */

inline static char is_contig_bit_modified(int bit_idx, retry_conf_t retry_conf) {
	  int cont_bit_idx = retry_conf.u_bits.contig.bit_idx;
	  int cont_nr_bits = retry_conf.u_bits.contig.nr_bits;

	  if (bit_idx >= cont_bit_idx && (bit_idx < cont_bit_idx + cont_nr_bits )) 
		return 1;
	  else 
		return 0;
}

/* 
 * Check  if the specified index of bit has been modified with the current type of configuration in separated bit index mode
 * Provide a specific implementation for separated mode to optimize number of tests done in the loop 
 */

inline static char is_sep_bit_modified(int bit_idx, retry_conf_t retry_conf) {
	  if (bit_idx == retry_conf.u_bits.sep.bit_idx_a || 
	      bit_idx == retry_conf.u_bits.sep.bit_idx_b ||
	      bit_idx == retry_conf.u_bits.sep.bit_idx_c)
	    return 1;
	  else
	    return 0;
}



/***********************************************************************************
 *
 * Name:	try_decode
 *
 * Purpose:	   
 *
 * Inputs:	block		- Bit string that was collected between "flag" patterns.
 *
 *		chan, subchan	- where it came from.
 *
 *		alevel		- audio level for later reporting.
 *
 *		retry_conf	- Controls changes that will be attempted to get a good CRC.
 *
 *	   			retry:	
 *					Level of effort to recover from a bad FCS on the frame.
 *				                RETRY_NONE = 0
 *				                RETRY_INVERT_SINGLE = 1
 *				                RETRY_INVERT_DOUBLE = 2
 *		                                RETRY_INVERT_TRIPLE = 3
 *		                                RETRY_INVERT_TWO_SEP = 4
 *
 *	    			mode:	RETRY_MODE_CONTIGUOUS - change adjacent bits.
 *						contig.bit_idx - first bit position
 *						contig.nr_bits - number of bits
 *
 *				        RETRY_MODE_SEPARATED  - change bits not next to each other.
 *						sep.bit_idx_a - bit positions
 *						sep.bit_idx_b - bit positions
 *						sep.bit_idx_c - bit positions
 *
 *				type:	RETRY_TYPE_NONE	- Make no changes.
 *					RETRY_TYPE_SWAP - Try inverting.
 *					
 *		passall		- All it thru even with bad CRC.
 *				  Valid only when no changes make.  i.e.
 *					retry == RETRY_NONE, type == RETRY_TYPE_NONE
 *
 * Returns:	1 = successfully extracted something.
 *		0 = failure.
 *
 ***********************************************************************************/

static int try_decode (rrbb_t block, int chan, int subchan, int slice, alevel_t alevel, retry_conf_t retry_conf, int passall)
{
	struct hdlc_state2_s H2;
	int blen;			/* Block length in bits. */
	int i;
	int raw;			/* From demodulator.  Should be 0 or 1. */
#if DEBUGx
	int crc_failed = 1;
#endif
	int retry_conf_mode = retry_conf.mode;
	int retry_conf_type = retry_conf.type;
	int retry_conf_retry = retry_conf.retry;


	H2.is_scrambled = rrbb_get_is_scrambled (block);
	H2.prev_descram = rrbb_get_prev_descram (block);
	H2.lfsr = rrbb_get_descram_state (block);
	H2.prev_raw = rrbb_get_bit (block, 0);	  /* Actually last bit of the */
					/* opening flag so we can derive the */
					/* first data bit.  */

	/* Does this make sense? */
	/* This is the last bit of the "flag" pattern. */
	/* If it was corrupted we wouldn't have detected */
	/* the start of frame. */

	if ((retry_conf.mode == RETRY_MODE_CONTIGUOUS && is_contig_bit_modified(0, retry_conf)) ||
	    (retry_conf.mode == RETRY_MODE_SEPARATED && is_sep_bit_modified(0, retry_conf))) {
	  H2.prev_raw = ! H2.prev_raw;
	}

	H2.pat_det = 0;
	H2.oacc = 0;
	H2.olen = 0;
	H2.frame_len = 0;

	blen = rrbb_get_len(block);

#if DEBUGx
	text_color_set(DW_COLOR_DEBUG);
        if (retry_conf.type == RETRY_TYPE_NONE) 
        	dw_printf ("try_decode: blen=%d\n", blen);
#endif
	for (i=1; i<blen; i++) {
	  /* Get the value for the current bit */
	  raw = rrbb_get_bit (block, i);
	  /* If swap two sep mode , swap the bit if needed */
	  if (retry_conf_retry == RETRY_INVERT_TWO_SEP) {
	      if (is_sep_bit_modified(i, retry_conf))
	        raw = ! raw;
	  } 
	  /* Else handle all the others contiguous modes */
	  else if (retry_conf_mode == RETRY_MODE_CONTIGUOUS) {

            if (retry_conf_type == RETRY_TYPE_SWAP) {
	        /* If this is the bit to swap */
	        if (is_contig_bit_modified(i, retry_conf))
	          raw = ! raw;
            } 

	  } else {
	  }
/*
 * Octets are sent LSB first.
 * Shift the most recent 8 bits thru the pattern detector.
 */
	    H2.pat_det >>= 1;

/*
 * Using NRZI encoding,
 *   A '0' bit is represented by an inversion since previous bit.
 *   A '1' bit is represented by no change.
 *   Note: this code can be factorized with the raw != H2.prev_raw code at the cost of processing time 
 */

	    int dbit ;

	    if (H2.is_scrambled) {
	      int descram;

	      descram = descramble(raw, &(H2.lfsr));

	      dbit = (descram == H2.prev_descram);
	      H2.prev_descram = descram;
	      H2.prev_raw = raw;
	    }
	    else {

	      dbit = (raw == H2.prev_raw);
	      H2.prev_raw = raw;
	    }

	    if (dbit) {

	      H2.pat_det |= 0x80;
	      /* Valid data will never have 7 one bits in a row: exit. */
	      if (H2.pat_det == 0xfe) {
#if DEBUGx
	        text_color_set(DW_COLOR_DEBUG);
	        dw_printf ("try_decode: found abort, i=%d\n", i);
#endif
	        return 0;
	      }
	      H2.oacc >>= 1;
	      H2.oacc |= 0x80;
	    } else {
	      
	      /* The special pattern 01111110 indicates beginning and ending of a frame: exit. */
	      if (H2.pat_det == 0x7e) {
#if DEBUGx
	        text_color_set(DW_COLOR_DEBUG);
	        dw_printf ("try_decode: found flag, i=%d\n", i);
#endif
	      return 0;
/*
 * If we have five '1' bits in a row, followed by a '0' bit,
 *
 *	011111xx
 *
 * the current '0' bit should be discarded because it was added for 
 * "bit stuffing."
 */
	
	      } else if ( (H2.pat_det >> 2) == 0x1f ) {
	        continue;
	      }
	      H2.oacc >>= 1;
	    }

/*
 * Now accumulate bits into octets, and complete octets
 * into the frame buffer.
 */

	    H2.olen++;

	    if (H2.olen & 8) {
	      H2.olen = 0;

	      if (H2.frame_len < MAX_FRAME_LEN) {
	        H2.frame_buf[H2.frame_len] = H2.oacc;
		H2.frame_len++;
	      
	      }
	    }
	  }	/* end of loop on all bits in block */
/* 
 * Do we have a minimum number of complete bytes?
 */

#if DEBUGx
	text_color_set(DW_COLOR_DEBUG);
	dw_printf ("try_decode: olen=%d, frame_len=%d\n", H2.olen, H2.frame_len);
#endif

	if (H2.olen == 0 && H2.frame_len >= MIN_FRAME_LEN) {

	  unsigned short actual_fcs, expected_fcs;

#if DEBUGx 
        if (retry_conf.type == RETRY_TYPE_NONE) {
	  int j;
	  text_color_set(DW_COLOR_DEBUG);
	  dw_printf ("NEW WAY: frame len = %d\n", H2.frame_len);
	  for (j=0; j<H2.frame_len; j++) {
	    dw_printf ("  %02x", H2.frame_buf[j]);
	  }
	  dw_printf ("\n");

        }
#endif
	  /* Check FCS, low byte first, and process... */

	  /* Alternatively, it is possible to include the two FCS bytes */
	  /* in the CRC calculation and look for a magic constant.  */
	  /* That would be easier in the case where the CRC is being */
	  /* accumulated along the way as the octets are received. */
	  /* I think making a second pass over it and comparing is */
	  /* easier to understand. */

	  actual_fcs = H2.frame_buf[H2.frame_len-2] | (H2.frame_buf[H2.frame_len-1] << 8);

	  expected_fcs = fcs_calc (H2.frame_buf, H2.frame_len - 2);

	  if (actual_fcs == expected_fcs && save_audio_config_p->achan[chan].modem_type == MODEM_AIS) {

	      // Sanity check for AIS.
	      if (ais_check_length((H2.frame_buf[0] >> 2) & 0x3f, H2.frame_len - 2) == 0) {
	          multi_modem_process_rec_frame (chan, subchan, slice, H2.frame_buf, H2.frame_len - 2, alevel, retry_conf.retry, 0);   /* len-2 to remove FCS. */
	          return 1;		/* success */
	      }
	      else {
	          return 0;		/* did not pass sanity check */
	      }
	  }
	  else if (actual_fcs == expected_fcs &&
			sanity_check (H2.frame_buf, H2.frame_len - 2, retry_conf.retry, save_audio_config_p->achan[chan].sanity_test)) {

	      // TODO: Shouldn't be necessary to pass chan, subchan, alevel into
	      // try_decode because we can obtain them from block.
	      // Let's make sure that assumption is good...

	      assert (rrbb_get_chan(block) == chan);
	      assert (rrbb_get_subchan(block) == subchan);
	      multi_modem_process_rec_frame (chan, subchan, slice, H2.frame_buf, H2.frame_len - 2, alevel, retry_conf.retry, 0);   /* len-2 to remove FCS. */
	      return 1;		/* success */

	  } else if (passall) {
	    if (retry_conf_retry == RETRY_NONE && retry_conf_type == RETRY_TYPE_NONE) {

	      //text_color_set(DW_COLOR_ERROR);
	      //dw_printf ("ATTEMPTING PASSALL PROCESSING\n");
  
	      multi_modem_process_rec_frame (chan, subchan, slice, H2.frame_buf, H2.frame_len - 2, alevel, RETRY_MAX, 0);   /* len-2 to remove FCS. */
	      return 1;		/* success */
	    }
	    else {
	      text_color_set(DW_COLOR_ERROR);
	      dw_printf ("try_decode: internal error passall = %d, retry_conf_retry = %d, retry_conf_type = %d\n", 
				passall, retry_conf_retry, retry_conf_type);
	    }
	  } else {

              goto failure;
          }
	} else {
#if DEBUGx
              crc_failed = 0;
#endif
              goto failure;
	}
failure:
#if DEBUGx
        if (retry_conf.type == RETRY_TYPE_NONE ) {
              int j;
	      text_color_set(DW_COLOR_ERROR);
              if (crc_failed)
	            dw_printf ("CRC failed\n");
	      if (H2.olen != 0)
		      dw_printf ("Bad olen: %d \n", H2.olen);
	      else if (H2.frame_len < MIN_FRAME_LEN) {
		      dw_printf ("Frame too small\n");
                      goto end;
	      }

	      dw_printf ("FAILURE with frame: frame len = %d\n", H2.frame_len);
	      dw_printf ("\n");
	      for (j=0; j<H2.frame_len; j++) {
                      dw_printf (" %02x", H2.frame_buf[j]);
	      }
	  dw_printf ("\nDEC\n");
	  for (j=0; j<H2.frame_len; j++) {
	    dw_printf ("%c", H2.frame_buf[j]>>1);
	  }
	  dw_printf ("\nORIG\n");
          for (j=0; j<H2.frame_len; j++) {
	    dw_printf ("%c", H2.frame_buf[j]);
	  }
	  dw_printf ("\n");
        }
end:
#endif
	return 0;	/* failure. */

} /* end try_decode */



/***********************************************************************************
 *
 * Name:	sanity_check
 *
 * Purpose:	Try to weed out bogus packets from initially failed FCS matches.
 *
 * Inputs:	buf
 *
 *		blen
 *
 *		bits_flipped
 *
 *		sanity		How much sanity checking to perform:
 *					SANITY_APRS - Looks like APRS.  See User Guide,
 *						section that discusses bad apples.
 *					SANITY_AX25 - Has valid AX.25 address part.
 *						No checking of the rest.  Useful for 
 *						connected mode packet.
 *					SANITY_NONE - No checking.  Would be suitable
 *						only if using frames that don't conform
 *						to AX.25 standard.
 *
 * Returns:	1 if it passes the sanity test.
 *
 * Description:	This is NOT a validity check.
 *		We don't know if modifying the frame fixed the problem or made it worse.
 *		We can only test if it looks reasonable.
 *
 ***********************************************************************************/


static int sanity_check (unsigned char *buf, int blen, retry_t bits_flipped, enum sanity_e sanity_test)
{
	int alen;		/* Length of address part. */
	int j;

/*
 * No sanity check if we didn't try fixing the data.
 * Should we have different levels of checking depending on 
 * how much we try changing the raw data?
 */
	if (bits_flipped == RETRY_NONE) {
	  return 1;
	}


/*
 * If using frames that do not conform to AX.25, it might be
 * desirable to skip the sanity check entirely.
 */
	if (sanity_test == SANITY_NONE) {
	  return (1);
	}

/*
 * Address part must be a multiple of 7. 
 */

	alen = 0;
	for (j=0; j<blen && alen==0; j++) {
	  if (buf[j] & 0x01) {
	    alen = j + 1;
	  }
	}

	if (alen % 7 != 0) {
#if DEBUGx
	  text_color_set(DW_COLOR_ERROR);
	  dw_printf ("sanity_check: FAILED.  Address part length %d not multiple of 7.\n", alen);
#endif
	  return 0;
	}

/*
 * Need at least 2 addresses and maximum of 8 digipeaters. 
 */

	if (alen/7 < 2 || alen/7 > 10) {
#if DEBUGx
	  text_color_set(DW_COLOR_ERROR);
	  dw_printf ("sanity_check: FAILED.  Too few or many addresses.\n");
#endif
	  return 0;
	}

/* 
 * Addresses can contain only upper case letters, digits, and space. 
 */

	for (j=0; j<alen; j+=7) {

	  char addr[7];

	  addr[0] = buf[j+0] >> 1;
	  addr[1] = buf[j+1] >> 1;
	  addr[2] = buf[j+2] >> 1;
	  addr[3] = buf[j+3] >> 1;
	  addr[4] = buf[j+4] >> 1;
	  addr[5] = buf[j+5] >> 1;
	  addr[6] = '\0';


	  if ( (! isupper(addr[0]) && ! isdigit(addr[0])) ||
	       (! isupper(addr[1]) && ! isdigit(addr[1]) && addr[1] != ' ') ||
	       (! isupper(addr[2]) && ! isdigit(addr[2]) && addr[2] != ' ') ||
	       (! isupper(addr[3]) && ! isdigit(addr[3]) && addr[3] != ' ') ||
	       (! isupper(addr[4]) && ! isdigit(addr[4]) && addr[4] != ' ') ||
	       (! isupper(addr[5]) && ! isdigit(addr[5]) && addr[5] != ' ')) {
#if DEBUGx	  
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("sanity_check: FAILED.  Invalid characters in addresses \"%s\"\n", addr);
#endif
	    return 0;
	  }
	}


/*
 * That's good enough for the AX.25 sanity check.
 * Continue below for additional APRS checking.
 */
	if (sanity_test == SANITY_AX25) {
	  return (1);
	}

/*
 * The next two bytes should be 0x03 and 0xf0 for APRS.
 */

	if (buf[alen] != 0x03 || buf[alen+1] != 0xf0) {
	  return (0);
	}

/*
 * Finally, look for bogus characters in the information part.
 * In theory, the bytes could have any values.
 * In practice, we find only printable ASCII characters and:
 *	
 *	0x0a	line feed
 *	0x0d	carriage return	
 *	0x1c	MIC-E
 *	0x1d	MIC-E
 *	0x1e	MIC-E
 *	0x1f	MIC-E
 *	0x7f	MIC-E
 *	0x80	"{UIV32N}<0x0d><0x9f><0x80>"
 *	0x9f	"{UIV32N}<0x0d><0x9f><0x80>"
 *	0xb0	degree symbol, ISO LATIN1
 *		  (Note: UTF-8 uses two byte sequence 0xc2 0xb0.)
 *	0xbe	invalid MIC-E encoding.
 *	0xf8	degree symbol, Microsoft code page 437
 *
 * So, if we have something other than these (in English speaking countries!), 
 * chances are that we have bogus data from twiddling the wrong bits.
 *
 * Notice that we shouldn't get here for good packets.  This extra level
 * of checking happens only if we twiddled a couple of bits, possibly
 * creating bad data.  We want to be very fussy.
 */

 	for (j=alen+2; j<blen; j++) {
	  int ch = buf[j];

	  if ( ! (( ch >= 0x1c && ch <= 0x7f) 
			|| ch == 0x0a 
			|| ch == 0x0d
			|| ch == 0x80
			|| ch == 0x9f
			|| ch == 0xc2
			|| ch == 0xb0
			|| ch == 0xf8) ) {
#if DEBUGx
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("sanity_check: FAILED.  Probably bogus info char 0x%02x\n", ch);
#endif
	    return 0;
	  }
	}

	return 1;
}


/* end hdlc_rec2.c */


