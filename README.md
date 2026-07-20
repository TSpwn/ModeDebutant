# Mode Débutant — a N.I.N.A. plugin for beginners

> 🇫🇷 [Version française](README.fr.md)

**Jargon-free astrophotography.** A plugin for [N.I.N.A.](https://nighttime-imaging.eu/)
(Nighttime Imaging 'N' Astronomy) that adds two beginner-friendly panels on top of existing
features:

- 🎯 **Simplified Polar Alignment** — which knob to turn, which way, shown with big arrows,
  until you reach "✓ Target reached" (built on top of the TPPA plugin).
- 📷 **Simplified Sequencer** — type "M31", point the scope, set 3 numbers, press GO.
  Includes tonight's target suggestions, an hourly cloud forecast, phone notifications,
  automatic darks and an HTML night report.

Written by an amateur astrophotographer with the assistance of Claude (Anthropic).
License: MPL-2.0.

> ⚠️ **The plugin UI is currently French-only.** This page documents everything in English so
> you can decide whether it's for you (and contribute translations if you like!).

---

## 📦 Installation

1. Download `ModeDebutant.dll` from the **[Releases](../../releases)** page.
2. Create the folder `%LOCALAPPDATA%\NINA\Plugins\3.0.0\ModeDebutant\` and copy the DLL there.
   (Paste that path straight into the Windows Explorer address bar.)
3. Restart N.I.N.A. → **Imaging** tab → both panels are in the tool icon bar, top right
   (a target icon and a camera icon).

**Requirements**
- N.I.N.A. **3.2** (.NET 8), Windows.
- For the polar alignment panel: the **Three Point Polar Alignment (TPPA)** plugin installed
  (N.I.N.A. Plugins tab) and working plate-solving (ASTAP).
- The sequencer panel works without TPPA.

Prefer building from source? `dotnet build -c Release` (.NET 8 SDK, N.I.N.A. closed) — the DLL
deploys itself to the right folder.

---

## 🎯 Guide: Simplified Polar Alignment

The panel translates TPPA's measurements into physical gestures. Three screens flow into each
other:

**📍 Your observing location** (top of the waiting screen)
| Element | What it's for |
|---|---|
| Location line | Latitude, longitude, elevation and hemisphere read from the N.I.N.A. profile — **everything depends on this**, make sure it's your place |
| City name | Looked up online for a quick sanity check (nothing shows without internet; never blocking) |
| ✏ Edit manually | Latitude/longitude in decimal degrees (comma accepted) + elevation in meters. Tip: right-click your house in Google Maps to read the values |
| 🌍 Locate me via internet | Sets everything automatically (city-level accuracy — plenty for polar alignment); elevation is derived from the terrain |
| Red warning (0°, 0°) | Location never set = wrong instructions. Fix it first |

**Start options**
| Option | Default | Meaning |
|---|---|---|
| My mount moves by itself (GoTo) | ON | OFF = you rotate the mount by hand between shots |
| Start from current position | OFF | ON = measure where the scope points; OFF = TPPA slews to its start point first |
| Target accuracy | 1.0′ | Below this threshold the alignment is declared done and stops by itself (1′ is great for beginners) |
| 🔧 All settings | empty | Exposure, gain, offset, binning, filter, search radius, rotation, move rate, East/West — empty = keep TPPA's own setting |

**During adjustment**: a big colored dial (total error + a plain scale: Perfect / Almost / A bit /
A lot / Way off), and two screw cards — only one arrow active at a time:
- "Bottom screws (left-right)" = the azimuth knobs on the base;
- "Top screw (up-down)" = the latitude adjustment bolt.

Turn gently; numbers refresh after every plate-solve. Thumbnail + star count + sharpness (HFR)
keep an eye on image quality. Guards: camera/mount connected, mount unparked, watchdog if TPPA
stops responding.

---

## 📷 Guide: Simplified Sequencer

The preparation screen follows the order of an actual evening:

**🧹 New evening** — clears last session's data (target, verdicts, report…) while keeping your
settings. **📊 Reopen last night's report** right below it.

**☁️ Tonight's weather** (info banner) — hourly cloud cover for your location: "Clear sky from
22 h to 2 h, cloudy after" + a strip like `21h☀ 22h🌤 23h⛅ 0h☁`. ⟳ refreshes. (No internet →
the banner just disappears.)

**1 · The target** *(optional: with no target, you shoot wherever the scope points)*
| Element | Meaning |
|---|---|
| Search box + 🔍 | Type "M31", "NGC 7000", "Andromeda"… The list shows magnitude and visibility (✅ 32° up / 🚫 below the horizon right now) |
| 🌌 Suggest tonight's targets | The 5 best targets right now **for your location**: bright and large enough for beginners, high for several hours, away from a bright Moon. "🌟 best 62° around 23h" |
| 🔭 Point the telescope | GoTo to the selected target (checks: mount connected, unparked, target above the horizon) |

**2 · The imaging run**
| Setting | Default | Meaning |
|---|---|---|
| Number of shots | 30 | More shots = cleaner stacked image |
| Exposure time | 30 s | A good starting point with decent tracking |
| Gain / ISO | empty | Empty = keep the camera's current setting |
| Dithering | ON | Small offset between shots (better stacking). Needs guiding — **silently skipped otherwise** |
| Automatic meridian flip | ON | The mount flips by itself when the target crosses the meridian (skipped if no mount connected) |
| Darks at the end | OFF | See below |
| 🔧 All settings | — | Offset, binning, filter (exact name), dither every X shots, number of darks |

The **estimated total duration** updates live ("about 1 h 05, ends around 23 h 40").

**📱 Phone notifications** *(optional, set up once)*
1. Install the free **ntfy** app (Android/iPhone).
2. In the app: "Add subscription" → copy the channel name shown in the panel (keep it private —
   it's your personal "frequency").
3. Flip the switch, then "📨 Send a test notification".

You'll receive: run started (with a "📱 alert sent ✔" receipt in the panel), suspicious frame
(only on **changes** — no spam), back to normal, "cover the telescope" for darks, final report.
Clouds rolling in or focus drifting: you'll know from the couch.

**▶ START THE RUN** — the plugin builds a **real sequence in N.I.N.A.'s advanced sequencer**
and starts it (a "see the details" button shows how it's built — a nice way to learn).
Pre-flight guards: camera connected, mount unparked, no sequence already running, weather
(warning if ≥ 70 % clouds are forecast before the estimated end — a second click overrides).

**During the run**: "Photo 12 of 30", progress bar, time remaining and end time, latest frame
thumbnail, "⭐ 543 stars · HFR 2.1" and the **automatic verdict**:
- ✅ Frame validated — sharpness stable, stars present;
- ⚠ Check focus — HFR is 30 % above the best of this run;
- ⚠ Fewer stars — clouds or dew likely;
- ❌ No stars at all — cap on? big problem?

The verdict calibrates itself on **your own run** (no universal thresholds). Buttons: ■ STOP,
📊 interim report (opens in your browser), open the advanced sequencer.

**🌡️ Darks** (if enabled) — when the lights finish, the panel asks you to **cover the
telescope** (+ phone alert), explains what darks are for, then chains an identical run (same
exposure/gain/offset/binning, DARK image type). 15 darks are enough regardless of how many
lights you took. "No thanks" to skip.

**📊 The night report** — written automatically next to your images
(`Bilan 2026-07-19 2130 M 31.html`): the harvest (shots × exposure = total integration, darks,
gain), quality (validated / to watch / starless, best-average-worst HFR), the **sharpness curve
of the night**, and a 4-step Siril stacking guide.

---

## ❓ Quick troubleshooting

| Symptom | Check |
|---|---|
| Panels don't show up | DLL in the right folder? N.I.N.A. restarted? N.I.N.A. 3.2? |
| Red "TPPA missing" banner | Install Three Point Polar Alignment from the Plugins tab |
| "Location not set (0°, 0°)" | 📍 card → "Locate me via internet" or "Edit manually" |
| No test notification | Same channel name on both sides? Internet? Notifications allowed for the ntfy app? |
| No weather / no city | No internet — everything else keeps working |
| "⚠ TPPA not responding" | Check the error toasts at the bottom of N.I.N.A. (hardware disconnected?) |

---

## 🔧 Technical notes (developers)

Full technical documentation (architecture, TPPA message-broker contract, sequencer object
model, decompilation notes) lives in the [French README](README.fr.md#-documentation-technique-développeurs).
The short version:

- **No reference to TPPA's DLL.** The polar alignment panel talks to TPPA exclusively through
  N.I.N.A.'s public inter-plugin message broker (`IMessageBroker`): it subscribes to
  `PolarAlignmentPlugin_PolarAlignment_AlignmentError` / `_Progress` and publishes
  `PolarAlignmentPlugin_DockablePolarAlignmentVM_StartAlignment` / `_StopAlignment`.
- **The sequencer panel only uses public NuGet interfaces** (`NINA.Plugin` 3.2.0.9001):
  it builds a real `SequenceRootContainer` (start/target/end areas + `DeepSkyObjectContainer`
  + `SmartExposure`, plus `MeridianFlipTrigger`) and drives it through `ISequenceMediator`
  (`SetAdvancedSequence`, `StartAdvancedSequence`, `CancelAdvancedSequence`). Progress is read
  from `LoopCondition.CompletedIterations`; per-frame quality from `ImagePrepared` +
  `StarDetectionAnalysis`.
- Target search: N.I.N.A.'s local sky database (`NINA.Astrometry.DatabaseInteraction`).
  Suggestions: local altitude simulation + NOVAS Moon position/illumination.
  Weather & terrain elevation: open-meteo (free, no key). Geolocation: ipapi.co.
  City lookup: BigDataCloud. Phone alerts: ntfy.sh. All web calls fail silently — nothing is
  ever blocking.
- Build: `dotnet build -c Release` → auto-deploys to
  `%LOCALAPPDATA%\NINA\Plugins\3.0.0\ModeDebutant\`. WPF panels are discovered by N.I.N.A.'s
  DataTemplate naming convention (`<Namespace.VMClass>_Dockable`).

Contributions welcome — an English (or any other language) translation of the UI would be a
great first issue.
