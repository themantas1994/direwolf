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
 * Module:      wapr_modem.c
 *
 * Purpose:   	WAPR (experimental) GFSK M-FSK modulator and receiver.
 *
 * Description:	Follows test/wapr/wapr_phy.py, the reference used for the
 *		stage 1 measurements, with one difference: the receiver first
 *		mixes the audio to complex baseband and decimates it, so the
 *		work does not grow with the sound card sample rate.
 *
 *		Receiver steps, for one buffer of audio:
 *		1. Baseband, low pass filter, decimate.
 *		2. Coarse search: tone powers on a grid of 1/4 symbol in time
 *		   and 1/2 tone spacing in frequency; Costas sync metric
 *		   (energy in the expected tones / energy in all tones).
 *		3. For the best few candidates: fine search of time and
 *		   frequency using the whole frame (sync tones plus the
 *		   strongest tone of each data symbol).
 *		4. Tone energies -> bit LLRs (non-coherent, log I0 metric,
 *		   amplitude tracked over a few symbols) -> LDPC -> CRC ->
 *		   frame format checks.
 *
 *		Nothing here keeps state between calls; the caller decides
 *		which audio to pass and must not call it from a real-time
 *		audio thread (it takes milliseconds to seconds).
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>

#include "wapr.h"

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#define TIME_STEPS 4		/* coarse time grid, per symbol */
#define FREQ_OVERSAMPLE 2	/* coarse frequency grid, per tone spacing */
#define FINE_STEPS 8
#define MAX_CANDIDATES 4
#define SYNC_MIN 1.5
#define RAMP_SYMBOLS 0.25


/*------------------------------------------------------------------
 * Modulator
 *---------------------------------------------------------------*/

int wapr_samples_needed (const wapr_profile_t *p, int fs)
{
	wapr_layout_t lay;
	wapr_layout (p, &lay);
	return ((int)ceil(lay.n_sym * fs / p->baud));
}


/* Frequency pulse of one GFSK symbol at tau symbols from its centre (area 1). */

static double gfsk_pulse (double bt, double tau)
{
	if (bt <= 0) {
	  return (tau >= -0.5 && tau < 0.5 ? 1.0 : 0.0);
	}
	if (tau <= -1.5 || tau >= 1.5) {
	  return (0.0);
	}
	double c = M_PI * sqrt(2.0 / log(2.0));
	return (0.5 * (erf(c * bt * (tau + 0.5)) - erf(c * bt * (tau - 0.5))));
}


int wapr_modulate (const wapr_profile_t *p, const unsigned char *syms, int nsym, int fs, float amp, float *out, int maxout)
{
	double sps = fs / p->baud;
	int n = (int)round(nsym * sps);
	double spacing = p->baud;		/* h = 1 */
	double half = (p->tones - 1) / 2.0;
	double phase = 0;

	if (n > maxout) {
	  return (-1);
	}
	int nr = (int)round(RAMP_SYMBOLS * sps);
	for (int i = 0; i < n; i++) {
	  double t = (i + 0.5) / sps;		/* time in symbols, sample centre */
	  int k0 = (int)floor(t);
	  double dev = 0;
	  for (int k = k0 - 2; k <= k0 + 2; k++) {
	    int kk = k < 0 ? 0 : k >= nsym ? nsym - 1 : k;	/* repeat first / last symbol */
	    if (k < -1 || k > nsym) continue;
	    dev += (syms[kk] - half) * gfsk_pulse(p->bt, t - k - 0.5);
	  }
	  phase += 2 * M_PI * (p->f_center + spacing * dev) / fs;
	  if (phase > 2 * M_PI) phase -= 2 * M_PI;
	  double a = amp;
	  if (i < nr) a *= 0.5 - 0.5 * cos(M_PI * (i + 0.5) / nr);
	  if (i >= n - nr) a *= 0.5 - 0.5 * cos(M_PI * (n - i - 0.5) / nr);
	  out[i] = (float)(a * sin(phase));
	}
	return (n);
}


/*------------------------------------------------------------------
 * Receiver
 *---------------------------------------------------------------*/

typedef struct {
	const wapr_profile_t *p;
	wapr_layout_t lay;
	double sps;		/* samples per symbol after decimation (may be fractional) */
	int win;		/* correlation window, samples */
	double spacing;
	double toff[WAPR_MAX_TONES];	/* tone offsets from f_center */
	float *zr, *zi;		/* complex baseband */
	int n;
} rx_t;


/* Mix to baseband around f_center, low pass, decimate by D. */

static int baseband (rx_t *r, const float *x, int n, int fs)
{
	const wapr_profile_t *p = r->p;
	double bw = p->tones * r->spacing + 2 * p->freq_search;
	int D = (int)floor(fs / fmax(16.0 * p->baud, 2.5 * bw));
	if (D < 1) D = 1;
	double fsd = (double)fs / D;
	double fc = (p->tones / 2.0) * r->spacing + p->freq_search + r->spacing / 2.0;
	double trans = fsd / 2.0 - fc;
	if (trans < fsd * 0.05) trans = fsd * 0.05;
	int taps = ((int)ceil(5.5 * fs / trans)) | 1;
	double *h = malloc (sizeof(double) * taps);
	double hs = 0;
	for (int i = 0; i < taps; i++) {
	  double m = i - (taps - 1) / 2.0;
	  double s = m == 0 ? 2 * fc / fs : sin(2 * M_PI * fc / fs * m) / (M_PI * m);
	  double w = 0.42 - 0.5 * cos(2 * M_PI * i / (taps - 1)) + 0.08 * cos(4 * M_PI * i / (taps - 1));
	  h[i] = s * w;
	  hs += h[i];
	}
	for (int i = 0; i < taps; i++) h[i] /= hs;

	float *mr = malloc (sizeof(float) * n);
	float *mi = malloc (sizeof(float) * n);
	double w0 = 2 * M_PI * p->f_center / fs;
	for (int i = 0; i < n; i++) {
	  double ph = fmod(w0 * (double)i, 2 * M_PI);
	  mr[i] = (float)(x[i] * cos(ph));
	  mi[i] = (float)(-x[i] * sin(ph));
	}
	int nd = n / D;
	r->zr = malloc (sizeof(float) * (nd + 1));
	r->zi = malloc (sizeof(float) * (nd + 1));
	for (int j = 0; j < nd; j++) {
	  /* output j is centred on input sample j*D: keeps start times aligned */
	  double ar = 0, ai = 0;
	  int i0 = j * D - (taps - 1) / 2;
	  int t0 = i0 < 0 ? -i0 : 0;
	  int t1 = i0 + taps > n ? n - i0 : taps;
	  for (int t = t0; t < t1; t++) {
	    ar += h[t] * mr[i0 + t];
	    ai += h[t] * mi[i0 + t];
	  }
	  r->zr[j] = (float)(2 * ar);
	  r->zi[j] = (float)(2 * ai);
	}
	free (mr);
	free (mi);
	free (h);
	r->n = nd;
	r->sps = fsd / p->baud;
	r->win = (int)round(r->sps);
	return (D);
}


/* |correlation|^2 of the window starting at s with frequency f (Hz, baseband). */

static double corr_power (const rx_t *r, int s, double f, double fsd)
{
	double ar = 0, ai = 0;
	double w = 2 * M_PI * f / fsd;
	double dc = cos(w), ds = sin(w);
	double c = 1, sn = 0;		/* exp(j w i) by rotation; win is at most a few hundred */
	for (int i = 0; i < r->win; i++) {
	  double zr = r->zr[s + i], zi = r->zi[s + i];
	  ar += zr * c + zi * sn;
	  ai += zi * c - zr * sn;
	  double nc = c * dc - sn * ds;
	  sn = sn * dc + c * ds;
	  c = nc;
	}
	return (ar * ar + ai * ai);
}


/* Energies E[k*M + m] for every symbol at start t0 (decimated samples) and offset fo. */

static int energies (const rx_t *r, double t0, double fo, double fsd, double *E)
{
	int M = r->p->tones;
	int last = (int)round(t0 + (r->lay.n_sym - 1) * r->sps);
	if (t0 < 0 || last + r->win > r->n) return (-1);
	for (int k = 0; k < r->lay.n_sym; k++) {
	  int s = (int)round(t0 + k * r->sps);
	  for (int m = 0; m < M; m++) {
	    E[k * M + m] = corr_power (r, s, r->toff[m] + fo, fsd);
	  }
	}
	return (0);
}

static double frame_metric (const rx_t *r, const double *E)
{
	int M = r->p->tones;
	double num = 0, den = 0;
	for (int k = 0; k < r->lay.n_sym; k++) {
	  double mx = 0;
	  for (int m = 0; m < M; m++) {
	    den += E[k * M + m];
	    if (E[k * M + m] > mx) mx = E[k * M + m];
	  }
	  num += r->lay.is_sync[k] ? E[k * M + r->lay.sync_tone[k]] : mx;
	}
	return (M * num / fmax(den, 1e-30));
}

static double log_i0 (double x)
{
	if (x < 50.0) {
	  /* series, adequate to double precision for x < 50 */
	  double s = 1, t = 1, q = x * x / 4;
	  for (int k = 1; k < 200; k++) {
	    t *= q / ((double)k * k);
	    s += t;
	    if (t < s * 1e-17) break;
	  }
	  return (log(s));
	}
	return (x - 0.5 * log(2 * M_PI * x) + log1p(1.0 / (8.0 * x)));
}


/* Bit LLRs, transmitted order, from data symbol energies.  Returns rough SNR_2500 (dB). */

static float soft_bits (const rx_t *r, const double *E, float *llr)
{
	int M = r->p->tones;
	int bps = r->lay.bits_per_sym;
	int D = r->lay.n_data;
	double *ed = malloc (sizeof(double) * D * M);
	double *a2 = malloc (sizeof(double) * D);
	double *sm = malloc (sizeof(double) * D);
	double tot = 0, totmx = 0;

	int d = 0;
	for (int k = 0; k < r->lay.n_sym; k++) {
	  if (r->lay.is_sync[k]) continue;
	  double mx = 0;
	  for (int m = 0; m < M; m++) {
	    ed[d * M + m] = E[k * M + m];
	    tot += E[k * M + m];
	    if (E[k * M + m] > mx) mx = E[k * M + m];
	  }
	  a2[d] = mx;
	  totmx += mx;
	  d++;
	}
	double sig2 = fmax((tot - totmx) / (D * (M - 1.0)), 1e-30);
	double mean_a2 = 0;
	for (d = 0; d < D; d++) {
	  a2[d] = fmax(a2[d] - sig2, 0.0);
	  mean_a2 += a2[d] / D;
	}
	int w = r->p->amp_window;
	if (w > 0 && w < D) {
	  /* moving average, edges padded with the end values (as numpy 'edge') */
	  int lpad = w / 2;
	  for (d = 0; d < D; d++) {
	    double s = 0;
	    for (int j = 0; j < w; j++) {
	      int i = d - lpad + j;
	      i = i < 0 ? 0 : i >= D ? D - 1 : i;
	      s += a2[i];
	    }
	    sm[d] = s / w;
	  }
	}
	else {
	  for (d = 0; d < D; d++) sm[d] = mean_a2;
	}
	for (d = 0; d < D; d++) {
	  double a = sqrt(fmax(sm[d], 0.01 * sig2));
	  double ll[WAPR_MAX_TONES];
	  for (int m = 0; m < M; m++) ll[m] = log_i0(2.0 * a * sqrt(ed[d * M + m]) / sig2);
	  for (int b = 0; b < bps; b++) {
	    double m0 = -1e300, m1 = -1e300;
	    for (int m = 0; m < M; m++) {
	      int g = m ^ (m >> 1);
	      if ((g >> (bps - 1 - b)) & 1) m1 = fmax(m1, ll[m]); else m0 = fmax(m0, ll[m]);
	    }
	    double s0 = 0, s1 = 0;
	    for (int m = 0; m < M; m++) {
	      int g = m ^ (m >> 1);
	      if ((g >> (bps - 1 - b)) & 1) s1 += exp(ll[m] - m1); else s0 += exp(ll[m] - m0);
	    }
	    int i = d * bps + b;
	    if (i < WAPR_CODE_BITS) llr[i] = (float)((m0 + log(s0)) - (m1 + log(s1)));
	  }
	}
	free (ed);
	free (a2);
	free (sm);
	return ((float)(10 * log10(fmax(mean_a2 / sig2, 1e-10) * r->p->baud / 2500.0)));
}


typedef struct { int t; int ob; double metric; } cand_t;

static int cmp_cand (const void *a, const void *b)
{
	double x = ((const cand_t *)a)->metric, y = ((const cand_t *)b)->metric;
	return (x < y) - (x > y);
}


/*------------------------------------------------------------------
 *
 * Name:	wapr_receive
 *
 * Purpose:	Find and decode WAPR frames in a buffer of audio.
 *
 * Inputs:	p	- profile.
 *		x, n	- audio samples (any scale), sample rate fs.
 *
 * Outputs:	res	- decoded frames, at most max_res.
 *		tried	- number of candidates that went through the decoder.
 *
 * Returns:	Number of frames decoded.  Frames that fail the CRC or the
 *		frame format checks are never returned.
 *
 *---------------------------------------------------------------*/

int wapr_receive (const wapr_profile_t *p, const float *x, int n, int fs, wapr_rx_result_t *res, int max_res, int *tried)
{
	rx_t r;
	int found = 0;
	int ntried = 0;

	memset (&r, 0, sizeof(r));
	r.p = p;
	r.spacing = p->baud;
	wapr_layout (p, &r.lay);
	for (int m = 0; m < p->tones; m++) r.toff[m] = (m - (p->tones - 1) / 2.0) * r.spacing;
	int D = baseband (&r, x, n, fs);
	double fsd = (double)fs / D;
	int M = p->tones;

	/* Coarse grid. */
	double hop = r.sps / TIME_STEPS;
	double dfb = p->baud / FREQ_OVERSAMPLE;
	int sb = (int)floor(p->freq_search / dfb + 0.5);
	int tb[WAPR_MAX_TONES];
	for (int m = 0; m < M; m++) tb[m] = (int)round(r.toff[m] / dfb);
	int b0 = tb[0] - sb, nb = tb[M-1] + sb - b0 + 1;
	int nh = (int)floor((r.n - r.win) / hop) + 1;
	int span = (r.lay.n_sym - 1) * TIME_STEPS;
	int nt = nh - span;
	int nob = 2 * sb + 1;

	if (nt <= 0) {
	  free (r.zr); free (r.zi);
	  if (tried) *tried = 0;
	  return (0);
	}
	float *P = malloc (sizeof(float) * nh * nb);
	for (int h = 0; h < nh; h++) {
	  int s = (int)round(h * hop);
	  for (int b = 0; b < nb; b++) {
	    P[h * nb + b] = (float)corr_power (&r, s, (b0 + b) * dfb, fsd);
	  }
	}
	cand_t *all = malloc (sizeof(cand_t) * nt * nob);
	int na = 0;
	for (int t = 0; t < nt; t++) {
	  for (int ob = 0; ob < nob; ob++) {
	    double num = 0, den = 0;
	    for (int k = 0; k < r.lay.n_sym; k++) {
	      if (! r.lay.is_sync[k]) continue;
	      const float *row = P + (t + k * TIME_STEPS) * nb + (ob - sb) - b0;
	      num += row[tb[r.lay.sync_tone[k]]];
	      for (int m = 0; m < M; m++) den += row[tb[m]];
	    }
	    double metric = M * num / fmax(den, 1e-30);
	    if (metric >= SYNC_MIN) {
	      all[na].t = t; all[na].ob = ob; all[na].metric = metric;
	      na++;
	    }
	  }
	}
	qsort (all, na, sizeof(cand_t), cmp_cand);
	cand_t pick[MAX_CANDIDATES];
	int npick = 0;
	for (int i = 0; i < na && npick < MAX_CANDIDATES; i++) {
	  int close_by = 0;	/* not "near": a macro in the Windows headers */
	  for (int j = 0; j < npick; j++) {
	    if (abs(all[i].t - pick[j].t) <= TIME_STEPS && abs(all[i].ob - pick[j].ob) <= FREQ_OVERSAMPLE) close_by = 1;
	  }
	  if (! close_by) pick[npick++] = all[i];
	}
	free (all);
	free (P);

	/* Fine search and decode. */
	double *E = malloc (sizeof(double) * r.lay.n_sym * M);
	double *Eb = malloc (sizeof(double) * r.lay.n_sym * M);
	float llr[WAPR_CODE_BITS];
	double half = fmin(dfb / 2.0, p->freq_search);
	int ihop = (int)floor(hop);
	int tstep = ihop / FINE_STEPS > 1 ? ihop / FINE_STEPS : 1;

	for (int c = 0; c < npick && found < max_res; c++) {
	  double t0 = pick[c].t * hop;
	  double fo = (pick[c].ob - sb) * dfb;
	  double best = -1, bt = t0, bf = fo;

	  /* skip candidates inside a frame already decoded */
	  int dup = 0;
	  for (int j = 0; j < found; j++) {
	    if (fabs(res[j].start / D - t0) < r.sps * r.lay.n_sym / 2) dup = 1;
	  }
	  if (dup) continue;

	  for (int dt = -ihop / 2; dt <= ihop / 2; dt += tstep) {
	    for (int k = -FINE_STEPS; k <= FINE_STEPS; k++) {
	      double f = fo + half * k / FINE_STEPS;
	      if (f > p->freq_search) f = p->freq_search;
	      if (f < -p->freq_search) f = -p->freq_search;
	      if (energies(&r, t0 + dt, f, fsd, Eb) != 0) continue;
	      double mtr = frame_metric (&r, Eb);
	      if (mtr > best) {
	        best = mtr; bt = t0 + dt; bf = f;
	        memcpy (E, Eb, sizeof(double) * r.lay.n_sym * M);
	      }
	    }
	  }
	  if (best < 0) continue;
	  ntried++;

	  float snr = soft_bits (&r, E, llr);

	  unsigned char info[WAPR_INFO_BYTES];
	  wapr_frame_t f;
	  if (wapr_decode(llr, info, NULL) == WAPR_OK && wapr_frame_unpack(info, &f) == WAPR_OK) {
	    res[found].frame = f;
	    res[found].start = bt * D;
	    res[found].freq_offset = bf;
	    res[found].sync_metric = best;
	    res[found].snr_est = snr;
	    found++;
	  }
	}
	free (E);
	free (Eb);
	free (r.zr);
	free (r.zi);
	if (tried) *tried = ntried;
	return (found);
}
