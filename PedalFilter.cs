// Pedal Filter – ReBuzz managed effect machine  (v8)
//
// Multi-mode filter:
//   • Low Pass / High Pass / Band Pass / Band Reject  (modes 0-3, TPT SVF)
//   • Comb  (mode 4, FIR or IIR selectable via Slope)
//   • 12 dB/oct  (2-pole SVF / FIR comb)
//   • 24 dB/oct  (4-pole cascaded SVF / IIR comb)
//
// v8 additions:
//   • S&H LFO waveform  (independent per-channel wrap detection)
//   • Coefficient smoothing  (~20 Hz LP on Cutoff + Resonance params)
//   • Resonance compensation  (min(1, 2R) on SVF wet signal)
//   • DC blocking  (~3.5 Hz HP on filter output)
//   • Drive 2× oversampling  (linear interp midpoint, average decimate)
//   • Comb filter mode  (delay = sr/cutoff; LFO = vibrato/flanger/chorus)
//
// Build:
//   dotnet build PedalFilter.csproj -c Release
// Output:
//   C:\Program Files\ReBuzz\Gear\Effects\Pedal Filter.NET.dll

using System;
using Buzz.MachineInterface;

namespace WDE.PedalFilter
{
    // =========================================================================
    // Enumerations
    // =========================================================================

    public enum FilterMode
    {
        LowPass    = 0,
        HighPass   = 1,
        BandPass   = 2,
        BandReject = 3,
        Comb       = 4,
    }

    public enum LfoWaveform
    {
        Sine          = 0,
        Triangle      = 1,
        Square        = 2,
        SawUp         = 3,
        SawDown       = 4,
        SampleAndHold = 5,
    }

    // =========================================================================
    // Machine declaration
    // =========================================================================

    [MachineDecl(
        Name        = "Pedal Filter",
        ShortName   = "PdlFlt",
        Author      = "WDE",
        MaxTracks   = 0,
        InputCount  = 1,
        OutputCount = 1)]
    public class PedalFilterMachine : IBuzzMachine
    {
        // ── Host ──────────────────────────────────────────────────────────────
        readonly IBuzzMachineHost host;

        // ── TPT SVF integrator states ─────────────────────────────────────────
        double s1_L1, s2_L1, s1_R1, s2_R1;   // stage 1
        double s1_L2, s2_L2, s1_R2, s2_R2;   // stage 2  (4-pole only)

        // ── Comb filter ───────────────────────────────────────────────────────
        // 16 384 samples covers sr/20 Hz at up to ~328 kHz sample rate.
        const int COMB_BUF = 16384;
        readonly float[] _combBufL = new float[COMB_BUF];
        readonly float[] _combBufR = new float[COMB_BUF];
        int    _combWritePos;
        double _combEnergyL, _combEnergyR;   // running peak → silence gate
        FilterMode _prevMode = FilterMode.LowPass;

        // ── LFO ───────────────────────────────────────────────────────────────
        double lfoPhaseL;
        readonly Random _rng = new Random();
        double _shValueL, _shValueR;   // S&H held values

        // ── Drive ─────────────────────────────────────────────────────────────
        double _drivePeakL = 1e-6, _drivePeakR = 1e-6;
        double _drivePrevL, _drivePrevR;   // previous input for 2× OS midpoint

        // ── Parameter smoothing ───────────────────────────────────────────────
        // Applied at ~20 Hz to Cutoff and Resonance to eliminate zipper noise
        // when parameters jump between Work() calls.  LFO modulation runs
        // per-sample on top of the smoothed base so sweep quality is unaffected.
        double _smoothCutoff    = 2000.0;
        double _smoothResonance = 20.0;
        bool   _firstWork       = true;

        // ── DC blocking ───────────────────────────────────────────────────────
        // Difference equation: y[n] = x[n] − x[n-1] + c·y[n-1], c = 0.9995
        // fc ≈ 3.5 Hz at 44.1 kHz.  Applied to filter output only.
        double _dcInL, _dcOutL, _dcInR, _dcOutR;

        // ── Constants ─────────────────────────────────────────────────────────
        // ReBuzz effect machines run at ±32768 (16-bit integer scale).
        const double SILENCE_THRESHOLD  = 1.0;     //  ≈ −90 dBFS at 32768
        const double DENORMAL_THRESHOLD = 1e-25;   //  double denormal guard
        const double DC_COEFF           = 0.9995;  //  DC-blocker pole
        const double PARAM_SMOOTH_A     = 0.003;   //  ≈ 20 Hz 1-pole LP
        const double COMB_ENERGY_DECAY  = 0.9995;  //  comb energy LP

        // ── Tempo-sync division table ─────────────────────────────────────────
        static readonly double[] DivBeats =
        {
            16.0,        //  0 – 4 Bars
             8.0,        //  1 – 2 Bars
             4.0,        //  2 – 1 Bar
             2.0,        //  3 – 1/2
             1.0,        //  4 – 1/4
             0.5,        //  5 – 1/8
             0.25,       //  6 – 1/16
             0.125,      //  7 – 1/32
             3.0,        //  8 – 1/2 Dotted
             1.5,        //  9 – 1/4 Dotted
             0.75,       // 10 – 1/8 Dotted
             2.0 / 3.0,  // 11 – 1/4 Triplet
             1.0 / 3.0,  // 12 – 1/8 Triplet
             1.0 / 6.0,  // 13 – 1/16 Triplet
        };

        public PedalFilterMachine(IBuzzMachineHost host) => this.host = host;

        // =========================================================================
        // Parameters
        // =========================================================================

        [ParameterDecl(
            Name        = "Mode",
            Description = "Filter mode.  Comb: Cutoff = fundamental freq, Slope = FIR/IIR.",
            MinValue    = 0,
            MaxValue    = 4,
            DefValue    = 0,
            ValueDescriptions = new[] { "Low Pass", "High Pass", "Band Pass", "Band Reject", "Comb" })]
        public int Mode { get; set; } = 0;

        [ParameterDecl(
            Name        = "Slope",
            Description = "SVF: 12 dB/oct (2-pole) or 24 dB/oct (4-pole).  "
                        + "Comb: FIR = flanging, IIR = resonator/string.",
            MinValue    = 0,
            MaxValue    = 1,
            DefValue    = 0,
            ValueDescriptions = new[] { "12 dB / FIR", "24 dB / IIR" })]
        public int Slope { get; set; } = 0;

        [ParameterDecl(
            Name        = "Cutoff",
            Description = "Filter cutoff Hz.  Comb: comb fundamental frequency.",
            MinValue    = 20,
            MaxValue    = 18000,
            DefValue    = 2000)]
        public int CutoffHz { get; set; } = 2000;

        [ParameterDecl(
            Name        = "Resonance",
            Description = "Filter Q.  Comb: feedback amount (0 = none, 100 = near-infinite ring).",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 20)]
        public int Resonance { get; set; } = 20;

        [ParameterDecl(
            Name        = "Drive",
            Description = "Pre-filter tanh() saturation with 2x oversampling.  0 = clean.",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 0)]
        public int Drive { get; set; } = 0;

        [ParameterDecl(
            Name        = "LFO Wave",
            Description = "LFO waveform.  S&H: random step, L/R update independently.",
            MinValue    = 0,
            MaxValue    = 5,
            DefValue    = 0,
            ValueDescriptions = new[] { "Sine", "Triangle", "Square", "Saw Up", "Saw Down", "S & H" })]
        public int LfoWave { get; set; } = 0;

        [ParameterDecl(
            Name        = "LFO Rate",
            Description = "LFO speed (free mode): 0 = stopped, 100 ≈ 10 Hz, 200 ≈ 20 Hz.",
            MinValue    = 0,
            MaxValue    = 200,
            DefValue    = 20)]
        public int LfoRate { get; set; } = 20;

        [ParameterDecl(
            Name        = "Tempo Sync",
            Description = "Lock LFO to host tempo (uses LFO Div).",
            MinValue    = 0,
            MaxValue    = 1,
            DefValue    = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int TempoSync { get; set; } = 0;

        [ParameterDecl(
            Name        = "LFO Div",
            Description = "Note division when Tempo Sync = On.",
            MinValue    = 0,
            MaxValue    = 13,
            DefValue    = 4,
            ValueDescriptions = new[]
            {
                "4 Bars",   "2 Bars",   "1 Bar",
                "1/2",      "1/4",      "1/8",      "1/16",     "1/32",
                "1/2 Dot",  "1/4 Dot",  "1/8 Dot",
                "1/4 Trip", "1/8 Trip", "1/16 Trip",
            })]
        public int LfoDiv { get; set; } = 4;

        [ParameterDecl(
            Name        = "LFO Depth",
            Description = "Sweep width: 0 = none, 100 = ±4 octaves.",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 0)]
        public int LfoDepth { get; set; } = 0;

        [ParameterDecl(
            Name        = "Phase Offset",
            Description = "L/R LFO phase difference.  "
                        + "90 = quadrature, 180 = opposed.  Comb: drives stereo chorus/flanger.",
            MinValue    = 0,
            MaxValue    = 180,
            DefValue    = 0)]
        public int PhaseOffset { get; set; } = 0;

        [ParameterDecl(
            Name        = "Mix",
            Description = "Wet/dry blend: 0 = dry, 100 = fully filtered.",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 100)]
        public int Mix { get; set; } = 100;

        // =========================================================================
        // Audio processing
        // =========================================================================

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // ── Silence gate ──────────────────────────────────────────────────
            // Scan buffer directly — WM_READ is not reliably cleared when
            // upstream returns false on the EffectBlock work mode.
            bool inputActive = false;
            for (int i = 0; i < n; i++)
                if (input[i].L != 0f || input[i].R != 0f) { inputActive = true; break; }

            if (!inputActive && StatesAreSilent()) return false;

            // ── Block setup ───────────────────────────────────────────────────
            double sr        = host.MasterInfo.SamplesPerSec;
            double maxCutoff = sr * 0.499;
            double wet       = Mix  / 100.0;
            double dry       = 1.0 - wet;
            double driveGain = 1.0 + (Drive / 100.0) * 9.0;

            double lfoHz;
            if (TempoSync == 1)
            {
                double bpm = Math.Max(host.MasterInfo.BeatsPerMin, 1.0);
                int    div = Math.Clamp(LfoDiv, 0, DivBeats.Length - 1);
                lfoHz      = 1.0 / (DivBeats[div] * (60.0 / bpm));
            }
            else
                lfoHz = LfoRate / 10.0;

            double lfoInc       = lfoHz > 0.0 ? lfoHz / sr : 0.0;
            double depthOct     = LfoDepth   / 100.0 * 4.0;
            double phaseOffNorm = PhaseOffset / 360.0;

            var  wave       = (LfoWaveform)LfoWave;
            var  filterMode = (FilterMode)Math.Clamp(Mode, 0, 4);
            bool fourPole   = (Slope == 1);

            // Smoothed parameter targets (block-constant)
            double targetCutoff    = Math.Clamp(CutoffHz, 20.0, maxCutoff);
            double targetResonance = Resonance;
            if (_firstWork)
            {
                _smoothCutoff    = targetCutoff;
                _smoothResonance = targetResonance;
                _firstWork       = false;
            }

            // Clear comb buffer when entering comb mode to avoid clicks
            // from stale data being read as delayed signal.
            if (filterMode == FilterMode.Comb && _prevMode != FilterMode.Comb)
            {
                Array.Clear(_combBufL, 0, COMB_BUF);
                Array.Clear(_combBufR, 0, COMB_BUF);
                _combEnergyL = _combEnergyR = 0.0;
            }
            _prevMode = filterMode;

            // ── Sample loop ───────────────────────────────────────────────────
            for (int i = 0; i < n; i++)
            {
                // Per-sample parameter smoothing (~20 Hz bandwidth).
                // Prevents zipper noise on discrete Cutoff / Resonance jumps.
                // LFO modulation is applied AFTER smoothing at full resolution.
                _smoothCutoff    += PARAM_SMOOTH_A * (targetCutoff    - _smoothCutoff);
                _smoothResonance += PARAM_SMOOTH_A * (targetResonance - _smoothResonance);
                double R            = 1.0 - (_smoothResonance / 100.0) * 0.995;
                double combFeedback = (_smoothResonance / 100.0) * 0.99;

                // LFO — capture pre-advance phases, advance, detect wraps.
                // Wrap detection is independent per channel so S&H sequences
                // diverge when Phase Offset > 0.
                double phL = lfoPhaseL;
                lfoPhaseL += lfoInc;
                bool wrapL = lfoPhaseL >= 1.0;
                if (wrapL) lfoPhaseL -= 1.0;

                double phR     = phL       + phaseOffNorm; if (phR     >= 1.0) phR     -= 1.0;
                double phR_new = lfoPhaseL + phaseOffNorm; if (phR_new >= 1.0) phR_new -= 1.0;
                bool   wrapR   = (phR_new < phR) && (lfoInc > 0.0);

                double lfoL, lfoR;
                if (wave == LfoWaveform.SampleAndHold)
                {
                    // Update held values at their own cycle boundaries.
                    // L and R pick independent randoms → genuinely different
                    // sequences rather than a time-shifted copy.
                    if (wrapL) _shValueL = _rng.NextDouble() * 2.0 - 1.0;
                    if (wrapR) _shValueR = _rng.NextDouble() * 2.0 - 1.0;
                    lfoL = _shValueL;
                    lfoR = _shValueR;
                }
                else
                {
                    lfoL = EvalLfo(wave, phL);
                    lfoR = EvalLfo(wave, phR);
                }

                // LFO-modulated cutoff in octave domain (perceptually even sweep).
                double fcL = Math.Clamp(_smoothCutoff * Math.Pow(2.0, lfoL * depthOct), 20.0, maxCutoff);
                double fcR = Math.Clamp(_smoothCutoff * Math.Pow(2.0, lfoR * depthOct), 20.0, maxCutoff);

                // Drive with 2× oversampling.
                // tanh at audio rate folds harmonics back into the audible band.
                // Linear interpolation gives a midpoint between the previous and
                // current sample.  Saturating both and averaging is equivalent to
                // a 2-tap FIR decimation filter [0.5, 0.5], halving alias products.
                double inL, inR;
                if (Drive > 0)
                {
                    _drivePeakL = Math.Max(Math.Abs(input[i].L), _drivePeakL * 0.9999);
                    _drivePeakR = Math.Max(Math.Abs(input[i].R), _drivePeakR * 0.9999);

                    double midL = 0.5 * (_drivePrevL + input[i].L);
                    double midR = 0.5 * (_drivePrevR + input[i].R);

                    double pL = Math.Max(_drivePeakL, 1e-10);
                    double pR = Math.Max(_drivePeakR, 1e-10);

                    double tMidL = Math.Tanh(midL       / pL * driveGain) * pL;
                    double tCurL = Math.Tanh(input[i].L / pL * driveGain) * pL;
                    double tMidR = Math.Tanh(midR       / pR * driveGain) * pR;
                    double tCurR = Math.Tanh(input[i].R / pR * driveGain) * pR;

                    inL = 0.5 * (tMidL + tCurL);
                    inR = 0.5 * (tMidR + tCurR);
                }
                else
                {
                    inL = input[i].L;
                    inR = input[i].R;
                }
                _drivePrevL = input[i].L;
                _drivePrevR = input[i].R;

                // Filter
                double filtL, filtR;

                if (filterMode == FilterMode.Comb)
                {
                    // Delay time = sr / fundamental_frequency.
                    // The LFO-modulated fcL/fcR is the fundamental, so LFO
                    // naturally produces vibrato, flanging, and chorus sweeps.
                    // Phase Offset between L and R creates stereo width.
                    //
                    // Slope = 0 (FIR):  write INPUT.  y = x + fb·x[n-N].
                    //   Notches/peaks at multiples of the fundamental — flanging.
                    //
                    // Slope = 1 (IIR):  write OUTPUT.  y = x + fb·y[n-N].
                    //   Sharp resonant peaks — string/resonator character.
                    double delL = Math.Clamp(sr / fcL, 1.0, COMB_BUF - 2.0);
                    double delR = Math.Clamp(sr / fcR, 1.0, COMB_BUF - 2.0);

                    filtL = CombStep(inL, delL, combFeedback, fourPole, _combBufL, _combWritePos);
                    filtR = CombStep(inR, delR, combFeedback, fourPole, _combBufR, _combWritePos);

                    // Advance shared write pointer after both channels have used it
                    _combWritePos = (_combWritePos + 1) % COMB_BUF;

                    // Running energy for silence gate
                    _combEnergyL *= COMB_ENERGY_DECAY;
                    _combEnergyR *= COMB_ENERGY_DECAY;
                    double absFL = Math.Abs(filtL), absFR = Math.Abs(filtR);
                    if (absFL > _combEnergyL) _combEnergyL = absFL;
                    if (absFR > _combEnergyR) _combEnergyR = absFR;
                }
                else
                {
                    double gL = Math.Tan(Math.PI * fcL / sr);
                    double gR = Math.Tan(Math.PI * fcR / sr);

                    TptSvfStep(inL, gL, R, ref s1_L1, ref s2_L1,
                               out double hp1L, out double bp1L,
                               out double lp1L, out double no1L);
                    TptSvfStep(inR, gR, R, ref s1_R1, ref s2_R1,
                               out double hp1R, out double bp1R,
                               out double lp1R, out double no1R);

                    if (!fourPole)
                    {
                        filtL = SelectMode(filterMode, lp1L, hp1L, bp1L, no1L);
                        filtR = SelectMode(filterMode, lp1R, hp1R, bp1R, no1R);
                    }
                    else
                    {
                        double f1L = SelectMode(filterMode, lp1L, hp1L, bp1L, no1L);
                        double f1R = SelectMode(filterMode, lp1R, hp1R, bp1R, no1R);

                        TptSvfStep(f1L, gL, R, ref s1_L2, ref s2_L2,
                                   out double hp2L, out double bp2L,
                                   out double lp2L, out double no2L);
                        TptSvfStep(f1R, gR, R, ref s1_R2, ref s2_R2,
                                   out double hp2R, out double bp2R,
                                   out double lp2R, out double no2R);

                        filtL = SelectMode(filterMode, lp2L, hp2L, bp2L, no2L);
                        filtR = SelectMode(filterMode, lp2R, hp2R, bp2R, no2R);
                    }

                    // Resonance compensation (SVF modes only).
                    // SVF peak gain at cutoff = Q = 1/(2R).
                    // Multiplying by min(1, 2R) exactly cancels that peak,
                    // keeping perceived loudness stable as Resonance is swept.
                    // Activates at Resonance > ~50 (R < 0.5); no effect below.
                    double resComp = Math.Min(1.0, 2.0 * R);
                    filtL *= resComp;
                    filtR *= resComp;
                }

                // DC blocking — removes offset introduced by Drive or filter build-up.
                // Applied to filter output only; dry path is unmodified.
                double dcL = filtL - _dcInL + DC_COEFF * _dcOutL;
                _dcInL  = filtL;
                _dcOutL = dcL;
                if (dcL > -DENORMAL_THRESHOLD && dcL < DENORMAL_THRESHOLD) dcL = 0.0;

                double dcR = filtR - _dcInR + DC_COEFF * _dcOutR;
                _dcInR  = filtR;
                _dcOutR = dcR;
                if (dcR > -DENORMAL_THRESHOLD && dcR < DENORMAL_THRESHOLD) dcR = 0.0;

                output[i].L = (float)(dry * input[i].L + wet * dcL);
                output[i].R = (float)(dry * input[i].R + wet * dcR);
            }

            return true;
        }

        // =========================================================================
        // Silence check
        // =========================================================================

        bool StatesAreSilent()
            => Math.Abs(s1_L1) < SILENCE_THRESHOLD
            && Math.Abs(s2_L1) < SILENCE_THRESHOLD
            && Math.Abs(s1_R1) < SILENCE_THRESHOLD
            && Math.Abs(s2_R1) < SILENCE_THRESHOLD
            && Math.Abs(s1_L2) < SILENCE_THRESHOLD
            && Math.Abs(s2_L2) < SILENCE_THRESHOLD
            && Math.Abs(s1_R2) < SILENCE_THRESHOLD
            && Math.Abs(s2_R2) < SILENCE_THRESHOLD
            && _combEnergyL    < SILENCE_THRESHOLD
            && _combEnergyR    < SILENCE_THRESHOLD;

        // =========================================================================
        // Comb filter step
        // =========================================================================

        /// <summary>
        /// One sample of the comb filter.  Uses fractional-sample linear
        /// interpolation for smooth delay modulation (no stepping artefacts).
        ///
        /// FIR (iir=false):  y[n] = x[n] + fb·x[n-N]   stores input
        ///   Notches/peaks at f, 2f, 3f … — classic flanger/phaser character.
        ///
        /// IIR (iir=true):   y[n] = x[n] + fb·y[n-N]   stores output
        ///   Sharp resonant peaks — string / Karplus-Strong resonator character.
        ///
        /// Denormal flush prevents the FP slow-path CPU penalty on drain.
        /// </summary>
        static double CombStep(
            double x, double delaySamples, double feedback,
            bool iir, float[] buf, int writePos)
        {
            double readF = writePos - delaySamples;
            if (readF < 0.0) readF += buf.Length;

            int    r0   = (int)readF;
            int    r1   = (r0 + 1) % buf.Length;
            double frac = readF - r0;

            double delayed = buf[r0] * (1.0 - frac) + buf[r1] * frac;
            double y       = x + feedback * delayed;

            if (y > -1e-25 && y < 1e-25) y = 0.0;

            float stored = iir ? (float)y : (float)x;
            if (stored > -1e-20f && stored < 1e-20f) stored = 0f;
            buf[writePos] = stored;

            return y;
        }

        // =========================================================================
        // TPT SVF (Zavalishin) — unconditionally stable at all cutoff frequencies
        // =========================================================================

        /// <summary>
        /// One sample of a Topology-Preserving Transform State Variable Filter.
        /// Derivation: hp·(1 + 2R·g + g²) = x − (2R + g)·s1 − s2
        /// Denormal flush applied to integrator states after every update.
        /// </summary>
        static void TptSvfStep(
            double x, double g, double R,
            ref double s1,  ref double s2,
            out double hp,  out double bp,
            out double lp,  out double notch)
        {
            double denom = 1.0 + 2.0 * R * g + g * g;
            hp    = (x - (2.0 * R + g) * s1 - s2) / denom;
            bp    = g * hp + s1;
            lp    = g * bp + s2;
            notch = lp + hp;
            s1    = 2.0 * bp - s1;
            s2    = 2.0 * lp - s2;
            if (s1 > -DENORMAL_THRESHOLD && s1 < DENORMAL_THRESHOLD) s1 = 0.0;
            if (s2 > -DENORMAL_THRESHOLD && s2 < DENORMAL_THRESHOLD) s2 = 0.0;
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        static double SelectMode(
            FilterMode m,
            double lp, double hp, double bp, double notch)
            => m switch
            {
                FilterMode.LowPass    => lp,
                FilterMode.HighPass   => hp,
                FilterMode.BandPass   => bp,
                FilterMode.BandReject => notch,
                _                     => lp,
            };

        /// <summary>
        /// Returns LFO value in −1..+1 for normalised phase 0..1.
        /// SampleAndHold is handled inline in Work() and never reaches here.
        /// </summary>
        static double EvalLfo(LfoWaveform w, double ph)
            => w switch
            {
                LfoWaveform.Sine     => Math.Sin(ph * Math.PI * 2.0),
                LfoWaveform.Triangle =>
                    ph < 0.25 ?  4.0 * ph :
                    ph < 0.75 ?  2.0 - 4.0 * ph :
                                -4.0 + 4.0 * ph,
                LfoWaveform.Square   => ph < 0.5 ? 1.0 : -1.0,
                LfoWaveform.SawUp    => 2.0 * ph - 1.0,
                LfoWaveform.SawDown  => 1.0 - 2.0 * ph,
                _                    => 0.0,
            };

        // =========================================================================
        // IBuzzMachine boilerplate
        // =========================================================================

        public void Stop() { }
        public void MidiNote(int channel, int value, int velocity) { }
        public void MidiControlChange(int ctrl, int channel, int value) { }
    }
}
