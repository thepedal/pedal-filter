// Pedal Filter – ReBuzz managed effect machine  (v3)
//
// Multi-mode filter with:
//   • Low Pass / High Pass / Band Pass / Band Reject (Notch) modes
//   • 12 dB/oct (2-pole) and 24 dB/oct (4-pole, cascaded TPT SVF) slopes
//   • Drive  – tanh() soft-clipper pre-filter
//   • LFO    – 5 waveforms, free-running or tempo-synced (14 divisions)
//   • Stereo LFO phase offset
//   • Wet/dry mix
//
// v3 change: Chamberlin SVF replaced with TPT (Topology-Preserving Transform)
// SVF (Zavalishin).  The TPT SVF solves the two integrator equations
// algebraically per sample, making it unconditionally stable at ALL cutoff
// frequencies — including close to Nyquist — where the old Chamberlin SVF
// would diverge and produce an extremely loud volume spike.
//
// Build:
//   dotnet build PedalFilter.csproj -c Release
//
// Output: C:\Program Files\ReBuzz\Gear\Effects\Pedal Filter.NET.dll

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
    }

    public enum LfoWaveform
    {
        Sine     = 0,
        Triangle = 1,
        Square   = 2,
        SawUp    = 3,
        SawDown  = 4,
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
        // ── Host reference ────────────────────────────────────────────────────
        readonly IBuzzMachineHost host;

        // ── TPT SVF integrator states ─────────────────────────────────────────
        // s1 = first integrator output (band-pass state)
        // s2 = second integrator output (low-pass state)
        // Stage 1 (both poles / modes)
        double s1_L1, s2_L1;   // Left  stage 1
        double s1_R1, s2_R1;   // Right stage 1
        // Stage 2 (4-pole cascade only)
        double s1_L2, s2_L2;   // Left  stage 2
        double s1_R2, s2_R2;   // Right stage 2

        // ── LFO ──────────────────────────────────────────────────────────────
        double lfoPhaseL;   // normalised 0..1

        // ── Drive peak tracker ────────────────────────────────────────────────
        // Tracks the running peak amplitude per channel so that the tanh
        // saturator is normalised to ±1 before clipping and then scaled back
        // out.  This makes Drive level-independent: it behaves the same whether
        // the upstream chain runs at ±1 or ±32 768.
        // Initialised to a small non-zero value to avoid divide-by-zero.
        double _drivePeakL = 1e-6;
        double _drivePeakR = 1e-6;

        // ── Tempo-sync division table ─────────────────────────────────────────
        // Values are note lengths in quarter-note beats.
        // cycle length in seconds = beats × (60 / BPM)
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

        // ── Constructor ───────────────────────────────────────────────────────
        public PedalFilterMachine(IBuzzMachineHost host) => this.host = host;

        // =========================================================================
        // Parameters
        // =========================================================================

        [ParameterDecl(
            Name        = "Mode",
            Description = "Filter mode: Low Pass · High Pass · Band Pass · Band Reject",
            MinValue    = 0,
            MaxValue    = 3,
            DefValue    = 0,
            ValueDescriptions = new[] { "Low Pass", "High Pass", "Band Pass", "Band Reject" })]
        public int Mode { get; set; } = 0;

        [ParameterDecl(
            Name        = "Slope",
            Description = "Filter slope: 12 dB/oct (2-pole) or 24 dB/oct (4-pole, cascaded TPT SVF)",
            MinValue    = 0,
            MaxValue    = 1,
            DefValue    = 0,
            ValueDescriptions = new[] { "12 dB/oct", "24 dB/oct" })]
        public int Slope { get; set; } = 0;

        [ParameterDecl(
            Name        = "Cutoff",
            Description = "Filter cutoff frequency in Hz",
            MinValue    = 20,
            MaxValue    = 18000,
            DefValue    = 2000)]
        public int CutoffHz { get; set; } = 2000;

        [ParameterDecl(
            Name        = "Resonance",
            Description = "Filter resonance (Q).  0 = wide open, 100 = near self-oscillation",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 20)]
        public int Resonance { get; set; } = 20;

        [ParameterDecl(
            Name        = "Drive",
            Description = "Pre-filter tanh() saturation.  0 = clean, 100 = heavy overdrive",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 0)]
        public int Drive { get; set; } = 0;

        [ParameterDecl(
            Name        = "LFO Wave",
            Description = "LFO waveform",
            MinValue    = 0,
            MaxValue    = 4,
            DefValue    = 0,
            ValueDescriptions = new[] { "Sine", "Triangle", "Square", "Saw Up", "Saw Down" })]
        public int LfoWave { get; set; } = 0;

        [ParameterDecl(
            Name        = "LFO Rate",
            Description = "LFO speed (free mode): 0 = stopped, 100 ≈ 10 Hz, 200 ≈ 20 Hz",
            MinValue    = 0,
            MaxValue    = 200,
            DefValue    = 20)]
        public int LfoRate { get; set; } = 20;

        [ParameterDecl(
            Name        = "Tempo Sync",
            Description = "Lock LFO speed to host tempo using the LFO Div setting",
            MinValue    = 0,
            MaxValue    = 1,
            DefValue    = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int TempoSync { get; set; } = 0;

        [ParameterDecl(
            Name        = "LFO Div",
            Description = "LFO note division (active only when Tempo Sync = On)",
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
            Description = "LFO cutoff sweep width.  0 = none, 100 = ±4 octaves",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 0)]
        public int LfoDepth { get; set; } = 0;

        [ParameterDecl(
            Name        = "Phase Offset",
            Description = "LFO phase difference between L and R.  0 = mono sweep, 180 = fully opposed",
            MinValue    = 0,
            MaxValue    = 180,
            DefValue    = 0)]
        public int PhaseOffset { get; set; } = 0;

        [ParameterDecl(
            Name        = "Mix",
            Description = "Wet/dry blend: 0 = dry, 100 = fully filtered",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 100)]
        public int Mix { get; set; } = 100;

        // =========================================================================
        // Audio processing
        // =========================================================================

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            double sr  = host.MasterInfo.SamplesPerSec;
            double wet = Mix  / 100.0;
            double dry = 1.0 - wet;

            // Drive: 0 % → gain 1×  /  100 % → gain 10×  (into tanh)
            double driveGain = 1.0 + (Drive / 100.0) * 9.0;

            // LFO rate
            double lfoHz;
            if (TempoSync == 1)
            {
                double bpm      = Math.Max(host.MasterInfo.BeatsPerMin, 1.0);
                int    divIdx   = Math.Clamp(LfoDiv, 0, DivBeats.Length - 1);
                double cycleSec = DivBeats[divIdx] * (60.0 / bpm);
                lfoHz           = 1.0 / cycleSec;
            }
            else
            {
                lfoHz = LfoRate / 10.0;
            }

            double lfoInc = (lfoHz > 0.0) ? lfoHz / sr : 0.0;

            // Cutoff & modulation
            // Hard ceiling at sr*0.499 — tan(π*0.499) ≈ 314, finite and safe.
            double baseCutoff   = Math.Clamp(CutoffHz, 20.0, sr * 0.499);
            double depthOct     = LfoDepth / 100.0 * 4.0;

            // TPT SVF damping coefficient R.
            // R = 1.0 → flat (Butterworth), R → 0 → self-oscillation.
            // We keep R ≥ 0.005 so it never quite reaches true self-oscillation.
            double R = 1.0 - (Resonance / 100.0) * 0.995;   // 1.0 → 0.005

            double phaseOffNorm = PhaseOffset / 360.0;
            var    filterMode   = (FilterMode)Math.Clamp(Mode, 0, 3);
            bool   fourPole     = (Slope == 1);

            // ── Sample loop ───────────────────────────────────────────────────
            for (int i = 0; i < n; i++)
            {
                // LFO
                double lfoL = EvalLfo((LfoWaveform)LfoWave, lfoPhaseL);
                double phR  = lfoPhaseL + phaseOffNorm;
                if (phR >= 1.0) phR -= 1.0;
                double lfoR = EvalLfo((LfoWaveform)LfoWave, phR);

                lfoPhaseL += lfoInc;
                if (lfoPhaseL >= 1.0) lfoPhaseL -= 1.0;

                // Per-channel modulated cutoff (octave domain)
                double fcL = Math.Clamp(baseCutoff * Math.Pow(2.0, lfoL * depthOct), 20.0, sr * 0.499);
                double fcR = Math.Clamp(baseCutoff * Math.Pow(2.0, lfoR * depthOct), 20.0, sr * 0.499);

                // TPT g coefficient: g = tan(π·fc/sr)
                // As fc → Nyquist, g → ∞ BUT the denominator (1+2Rg+g²) → ∞
                // faster, so hp → 0.  No instability at any frequency.
                double gL = Math.Tan(Math.PI * fcL / sr);
                double gR = Math.Tan(Math.PI * fcR / sr);

                // Drive (pre-filter soft saturation)
                //
                // IMPORTANT: when Drive = 0 the signal must pass through
                // completely unmodified.  Even with gain = 1, Math.Tanh()
                // compresses large-amplitude signals to the ±1 output range
                // of tanh — causing drastic level loss at any signal amplitude
                // larger than a few units.
                //
                // When Drive > 0 we use a self-scaling normalised tanh:
                //   1. Track the running peak per channel (instant attack,
                //      ~160 ms half-life release at 44 100 Hz).
                //   2. Divide by peak  →  input is normalised to ±1.
                //   3. Apply tanh with drive gain  →  saturation at ±1.
                //   4. Multiply by peak again  →  restore original amplitude scale.
                //
                // This makes Drive work identically at any signal amplitude.
                double inL, inR;
                if (Drive > 0)
                {
                    const double peakDecay = 0.9999;
                    _drivePeakL = Math.Max(Math.Abs(input[i].L), _drivePeakL * peakDecay);
                    _drivePeakR = Math.Max(Math.Abs(input[i].R), _drivePeakR * peakDecay);
                    inL = Math.Tanh(input[i].L / _drivePeakL * driveGain) * _drivePeakL;
                    inR = Math.Tanh(input[i].R / _drivePeakR * driveGain) * _drivePeakR;
                }
                else
                {
                    // Exact bypass — zero processing, zero level change.
                    inL = input[i].L;
                    inR = input[i].R;
                }

                // TPT SVF stage 1
                TptSvfStep(inL, gL, R, ref s1_L1, ref s2_L1,
                           out double hp1L, out double bp1L, out double lp1L, out double no1L);
                TptSvfStep(inR, gR, R, ref s1_R1, ref s2_R1,
                           out double hp1R, out double bp1R, out double lp1R, out double no1R);

                double filtL, filtR;

                if (!fourPole)
                {
                    filtL = SelectMode(filterMode, lp1L, hp1L, bp1L, no1L);
                    filtR = SelectMode(filterMode, lp1R, hp1R, bp1R, no1R);
                }
                else
                {
                    // 4-pole: feed stage-1 selected output into stage 2.
                    // Both stages run at the same g and R so the combined
                    // roll-off is 24 dB/oct with a consistent resonance peak.
                    double feed1L = SelectMode(filterMode, lp1L, hp1L, bp1L, no1L);
                    double feed1R = SelectMode(filterMode, lp1R, hp1R, bp1R, no1R);

                    TptSvfStep(feed1L, gL, R, ref s1_L2, ref s2_L2,
                               out double hp2L, out double bp2L, out double lp2L, out double no2L);
                    TptSvfStep(feed1R, gR, R, ref s1_R2, ref s2_R2,
                               out double hp2R, out double bp2R, out double lp2R, out double no2R);

                    filtL = SelectMode(filterMode, lp2L, hp2L, bp2L, no2L);
                    filtR = SelectMode(filterMode, lp2R, hp2R, bp2R, no2R);
                }

                output[i].L = (float)(dry * input[i].L + wet * filtL);
                output[i].R = (float)(dry * input[i].R + wet * filtR);
            }

            return true;
        }

        // =========================================================================
        // TPT SVF (Zavalishin)
        // =========================================================================

        /// <summary>
        /// One sample of a Topology-Preserving Transform State Variable Filter.
        ///
        /// Unconditionally stable for all fc in (0, sr/2) at any resonance.
        /// Solves the two integrator feedback equations algebraically before
        /// updating state, so there is no frequency at which the filter diverges.
        ///
        /// Parameters:
        ///   x        – input sample
        ///   g        – frequency coefficient: tan(π·fc/sr)
        ///   R        – damping: 1.0 = flat/Butterworth, ~0 = near self-oscillation
        ///   s1, s2   – integrator states (modified in place)
        ///
        /// Outputs:
        ///   hp       – high-pass
        ///   bp       – band-pass
        ///   lp       – low-pass  (also readable as s2 after the call)
        ///   notch    – notch / band-reject (lp + hp)
        /// </summary>
        static void TptSvfStep(
            double x, double g, double R,
            ref double s1,   ref double s2,
            out double hp,   out double bp,
            out double lp,   out double notch)
        {
            // Full derivation:
            //   hp  = x  −  2R·v1  −  v2             (SVF definition)
            //   v1  = g·hp + s1                       (trapezoidal integrator 1)
            //   v2  = g·v1 + s2                       (trapezoidal integrator 2)
            //
            // Substitute v1 and v2 into the hp equation and collect hp terms:
            //   hp + 2R·(g·hp + s1) + (g²·hp + g·s1 + s2) = x
            //   hp·(1 + 2R·g + g²)                         = x − (2R + g)·s1 − s2
            //
            // Note: the coefficient on s1 is (2R + g), NOT just 2R.
            // The g·s1 term is small at low frequencies but grows with g,
            // so omitting it causes a progressive drop in LP output and
            // instability as the cutoff approaches Nyquist.
            double denom = 1.0 + 2.0 * R * g + g * g;
            hp    = (x - (2.0 * R + g) * s1 - s2) / denom;
            bp    = g * hp + s1;
            lp    = g * bp + s2;
            notch = lp + hp;

            // Advance trapezoidal integrator states
            s1 = 2.0 * bp - s1;
            s2 = 2.0 * lp - s2;
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

        /// <summary>Returns LFO value in −1..+1 for normalised phase 0..1.</summary>
        static double EvalLfo(LfoWaveform w, double ph)
            => w switch
            {
                LfoWaveform.Sine =>
                    Math.Sin(ph * Math.PI * 2.0),

                LfoWaveform.Triangle =>
                    ph < 0.25 ?  4.0 * ph :
                    ph < 0.75 ?  2.0 - 4.0 * ph :
                                -4.0 + 4.0 * ph,

                LfoWaveform.Square =>
                    ph < 0.5 ? 1.0 : -1.0,

                LfoWaveform.SawUp =>
                    2.0 * ph - 1.0,

                LfoWaveform.SawDown =>
                    1.0 - 2.0 * ph,

                _ => 0.0,
            };

        // =========================================================================
        // IBuzzMachine boilerplate
        // =========================================================================

        public void Stop() { }
        public void MidiNote(int channel, int value, int velocity) { }
        public void MidiControlChange(int ctrl, int channel, int value) { }
    }
}
