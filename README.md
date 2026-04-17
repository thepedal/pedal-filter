# Pedal Filter  (v2)

A managed effect machine for [ReBuzz](https://github.com/wasteddesign/ReBuzz).
Multi-mode filter with drive, tempo-synced LFO, and stereo phase offset.

---

## Requirements

- [ReBuzz](https://github.com/wasteddesign/ReBuzz) (1812-preview or later)
- [.NET 10.0 Desktop Runtime (Windows x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) — to build from source

---

## Installation

1. Copy `Pedal Filter.NET.dll` to:
   ```
   C:\Program Files\ReBuzz\Gear\Effects\
   ```
2. Restart ReBuzz. **Pedal Filter** appears under Effects.

## Building from source

```powershell
.\build.ps1
```

Or manually:

```powershell
dotnet build PedalFilter.csproj -c Release
# Custom ReBuzz path:
dotnet build PedalFilter.csproj -c Release /p:BuzzDir="D:\ReBuzz"
```

---

## Parameters

| Parameter    | Range       | Default | Description |
|--------------|-------------|---------|-------------|
| Mode         | 0 – 3       | 0       | **0** Low Pass · **1** High Pass · **2** Band Pass · **3** Band Reject (Notch) |
| Slope        | 0 – 1       | 0       | **0** 12 dB/oct (2-pole) · **1** 24 dB/oct (4-pole, cascaded SVF) |
| Cutoff       | 20 – 18 000 | 2000    | Filter cutoff frequency in Hz |
| Resonance    | 0 – 100     | 20      | Q / resonance. 0 = wide, 100 = near self-oscillation |
| Drive        | 0 – 100     | 0       | Pre-filter tanh() saturation. 0 = clean, 100 = heavy overdrive |
| LFO Wave     | 0 – 4       | 0       | **0** Sine · **1** Triangle · **2** Square · **3** Saw Up · **4** Saw Down |
| LFO Rate     | 0 – 200     | 20      | Free-running speed: 0 = stopped, 100 ≈ 10 Hz, 200 ≈ 20 Hz |
| Tempo Sync   | 0 – 1       | 0       | **Off** / **On** — locks LFO to host BPM using LFO Div |
| LFO Div      | 0 – 13      | 4 (1/4) | Note division when Tempo Sync is On (see table below) |
| LFO Depth    | 0 – 100     | 0       | Sweep width: 0 = none, 100 = ±4 octaves around Cutoff |
| Phase Offset | 0 – 180     | 0       | LFO phase difference between L and R. 0 = mono, 180 = fully opposed |
| Mix          | 0 – 100     | 100     | Wet/dry blend |

### LFO Division values (Tempo Sync = On)

| Value | Division    | Value | Division     |
|-------|-------------|-------|--------------|
| 0     | 4 Bars      | 8     | 1/2 Dotted   |
| 1     | 2 Bars      | 9     | 1/4 Dotted   |
| 2     | 1 Bar       | 10    | 1/8 Dotted   |
| 3     | 1/2         | 11    | 1/4 Triplet  |
| 4     | 1/4         | 12    | 1/8 Triplet  |
| 5     | 1/8         | 13    | 1/16 Triplet |
| 6     | 1/16        |       |              |
| 7     | 1/32        |       |              |

---

## Design notes

### State Variable Filter

The SVF is a two-integrator loop that produces all four outputs simultaneously
every sample:

```
high  =  input − low − q × band
band  +=  f × high       (f = 2·sin(π·fc/sr))
low   +=  f × band
notch =  high + low
```

The coefficient `f` is recalculated every sample, so LFO modulation is
perfectly smooth with no stepping or clicking.

### 4-pole / 24 dB/oct

Two SVF stages are cascaded.  The selected mode output from stage 1 is fed
directly into stage 2 as its input, and the same mode is taken from stage 2 as
the final output.  This doubles the roll-off slope and sharpens the resonance
peak — particularly effective in Low Pass and High Pass modes, giving a
character close to a 4-pole Moog ladder.

> **Tip:** In 4-pole mode, Resonance sounds more pronounced than in 2-pole at
> the same knob position.  Start with Resonance around 30–40 and work up.

### Drive

`tanh()` is applied pre-filter with 1×–10× gain (Drive 0–100).  At quiet
signal levels `tanh(x) ≈ x`, so low Drive settings are nearly transparent.
As Drive increases the signal is compressed into the tanh knee, adding warm
odd-order harmonics.  These then pass through the filter, which shapes the
harmonic colour — high Drive + resonant Band Pass = very vocal, Moog-style
growl.

### Stereo Phase Offset

The R-channel LFO runs at `lfoPhaseL + PhaseOffset/360`.  At 90° the two
channels are in quadrature — while L opens, R closes — creating a wide,
auto-panning filter sweep.  At 180° the channels are fully opposed.

### LFO modulation domain

Cutoff is modulated in the **octave (pitch) domain**:
`fc = Cutoff × 2^(lfoValue × depthOct)`.
This makes sweeps perceptually even across the full frequency range — a sweep
from 200 Hz to 800 Hz covers the same apparent "distance" as 800 Hz to 3200 Hz.

---

## Licence

MIT
