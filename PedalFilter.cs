// Pedal Filter – ReBuzz managed effect machine  (v7)
//
// Multi-mode filter with:
//   • Low Pass / High Pass / Band Pass / Band Reject (Notch) modes
//   • 12 dB/oct (2-pole) and 24 dB/oct (4-pole, cascaded TPT SVF) slopes
//   • Drive  – tanh() soft-clipper pre-filter (normalised, level-independent)
//   • LFO    – 5 waveforms, free-running or tempo-synced (14 divisions)
//   • Stereo LFO phase offset
//   • Wet/dry mix
//   • CPU silence detection (v7 fix — see notes below)
//
// Silence detection — v7 corrections
// ────────────────────────────────────
// Three bugs in the v6 silence path:
//
// 1. WM_READ is unreliable.  ReBuzz may not clear WM_READ on the
//    EffectBlock mode flags when the upstream machine returns false.
//    Relying on it meant the silence gate never fired.
//    Fix: scan the input buffer directly for non-zero samples.
//
// 2. Denormal-number CPU spike.  SVF integrator states decaying toward
//    zero on silent input enter the IEEE 754 denormal range.  On x64 .NET
//    without DAZ/FTZ, every arithmetic op on a denormal triggers a
//    microcode trap that is ~100× slower than normal FP.  This keeps CPU
//    elevated long after the output is inaudible.
//    Fix: flush states to 0 after every integrator update whenever they
//    drop below 1e-25 — well above the denormal zone, well below audibility.
//
// 3. Silence threshold was calibrated for ±1 signals.  ReBuzz effect
//    machines operate at ±32768 (16-bit integer scale).  A threshold of
//    0.001 is ~−150 dBFS at that scale, which the states never naturally
//    reach at high resonance / low cutoff within any reasonable time.
//    Fix: raise threshold to 1.0 (≈ −90 dBFS relative to 32768).
//
// TPT SVF (Zavalishin) is unconditionally stable at all cutoff frequencies.
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
        double s1_L1, s2_L1;   // Left  stage 1
        double s1_R1, s2_R1;   // Right stage 1
        double s1_L2, s2_L2;   // Left  stage 2  (4-pole only)
        double s1_R2, s2_R2;   // Right stage 2  (4-pole only)

        // ── LFO ──────────────────────────────────────────────────────────────
        double lfoPhaseL;

        // ── Drive peak tracker ────────────────────────────────────────────────
        double _drivePeakL = 1e-6;
        double _drivePeakR = 1e-6;

        // ── Silence detection ─────────────────────────────────────────────────
        // Threshold at which an integrator state is considered inaudible.
        // ReBuzz effect machines run at ±32768 (16-bit integer scale).
        // 1.0 ≈ −90 dBFS relative to 32768 full-scale.
        const double SILENCE_THRESHOLD = 1.0;

        // ── Denormal flush threshold ──────────────────────────────────────────
        // IEEE 754 double denormals begin around 2.2e−308.  Values below
        // ~1e−25 are inaudible at any realistic audio scale and must be
        // clamped to 0 to avoid the microcode-trap CPU penalty.
        const double DENORMAL_THRESHOLD = 1e-25;

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
            Description = "Filter mode: Low Pass · High Pass · Band Pass · Band Reject",
            MinValue    = 0,
            MaxValue    = 3,
            DefValue    = 0,
            ValueDescriptions = new[] { "Low Pass", "High Pass", "Band Pass", "Band Reject" })]
        public int Mode { get; set; } = 0;

        [ParameterDecl(
            Name        = "Slope",
            Description = "Filter slope: 12 dB/oct (2-pole) or 24 dB/oct (4-pole)",
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
            // ── Silence gate ──────────────────────────────────────────────────
            //
            // Step 1: check whether the input buffer actually contains signal.
            // We scan the buffer directly rather than relying on WM_READ —
            // ReBuzz does not reliably clear WM_READ on the EffectBlock mode
            // flags when the upstream machine returns false.
            //
            // Step 2: if input is silent AND all integrator states have decayed
            // below the inaudible threshold, return false immediately.
            // ReBuzz interprets false as "output is silence" and will stop
            // calling Work() until a non-silent upstream buffer arrives.
            //
            // If input is silent but states still hold energy (resonant tail),
            // we fall through to the normal processing loop with a zeroed input
            // buffer.  The integrators drain naturally, helped by the denormal
            // flush inside TptSvfStep that prevents the slow-path FP trap.

            bool inputActive = false;
            for (int i = 0; i < n; i++)
            {
                if (input[i].L != 0f || input[i].R != 0f)
                {
                    inputActive = true;
                    break;
                }
            }

            if (!inputActive && StatesAreSilent())
                return false;

            // ------------------------------------------------------------------
            // Block-level constants
            // ------------------------------------------------------------------
            double sr  = host.MasterInfo.SamplesPerSec;
            double wet = Mix  / 100.0;
            double dry = 1.0 - wet;

            double driveGain = 1.0 + (Drive / 100.0) * 9.0;

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

            double lfoInc       = (lfoHz > 0.0) ? lfoHz / sr : 0.0;
            double baseCutoff   = Math.Clamp(CutoffHz, 20.0, sr * 0.499);
            double depthOct     = LfoDepth / 100.0 * 4.0;
            double R            = 1.0 - (Resonance / 100.0) * 0.995;
            double phaseOffNorm = PhaseOffset / 360.0;

            var  filterMode = (FilterMode)Math.Clamp(Mode, 0, 3);
            bool fourPole   = (Slope == 1);

            // ------------------------------------------------------------------
            // Sample loop
            // ------------------------------------------------------------------
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

                double gL = Math.Tan(Math.PI * fcL / sr);
                double gR = Math.Tan(Math.PI * fcR / sr);

                // Drive
                // Drive = 0: exact bypass, no gain change.
                // Drive > 0: normalised tanh saturator — signal is divided by
                // a running peak before tanh and multiplied back after, making
                // the saturation character independent of input amplitude.
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
                    inL = input[i].L;
                    inR = input[i].R;
                }

                // TPT SVF stage 1
                // TptSvfStep flushes integrator states to 0 when they fall
                // below DENORMAL_THRESHOLD, preventing the FP-denormal CPU spike.
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
                    double f1L = SelectMode(filterMode, lp1L, hp1L, bp1L, no1L);
                    double f1R = SelectMode(filterMode, lp1R, hp1R, bp1R, no1R);

                    TptSvfStep(f1L, gL, R, ref s1_L2, ref s2_L2,
                               out double hp2L, out double bp2L, out double lp2L, out double no2L);
                    TptSvfStep(f1R, gR, R, ref s1_R2, ref s2_R2,
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
            && Math.Abs(s2_R2) < SILENCE_THRESHOLD;

        // =========================================================================
        // TPT SVF (Zavalishin)
        // =========================================================================

        /// <summary>
        /// One sample of a Topology-Preserving Transform State Variable Filter.
        ///
        /// Derivation:
        ///   hp·(1 + 2R·g + g²) = x − (2R + g)·s1 − s2
        ///
        /// After updating s1 and s2, both are flushed to 0 if they fall below
        /// DENORMAL_THRESHOLD.  Without this flush, decaying integrator states
        /// enter the IEEE 754 denormal range and trigger a ~100× CPU penalty
        /// on x64 .NET (which does not set DAZ/FTZ by default).
        /// </summary>
        void TptSvfStep(
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

            s1 = 2.0 * bp - s1;
            s2 = 2.0 * lp - s2;

            // Denormal flush — runs every sample.
            // Threshold 1e-25 is ~13 orders above the denormal zone
            // and ~500 dBFS below any audible signal at ±32768 scale.
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
