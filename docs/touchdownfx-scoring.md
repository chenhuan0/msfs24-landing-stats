# TouchdownFX landing score

The landing score in this application (`src/LandingStats.App/Presentation/LandingScore.cs`, rules
version 4) reproduces the score of TouchdownFX 2.1.7. This document records TouchdownFX's rules as
captured on 2026-10-29, so that they stay available if its website changes.

## Sources

- **Website bundle.** `https://touchdownfx.com/guide` is rendered by JavaScript. Both the guide text
  and a full re-implementation of the scorecard are in the bundle `/assets/index-DzVVC0x5.js`. The
  site recomputes the score from the replay to draw the scorecard, and shows that total instead of
  the stored one when replay data is present. In the minified bundle the scorer follows the category
  function; search for `idealFpm`.
- **Public API** (no authentication):
  - `GET /api/landings?page=N&limit=50` returns `{landings, total, offset, limit}` with summary
    fields. On 2026-10-29 `total` was 30,851.
  - `GET /api/landings/{id}` adds `replayData` (see [Replay data](#replay-data)).
- **Verification.** A Python port of the scorer reproduced the stored `score` exactly for 40 of 40
  recent public landings. In that sample:
  - 3 landings were sensitive to the sign of the bank angle, and all 3 confirm the raw MSFS sign.
  - Only 1 landing triggered a severity penalty.
  - No landing was in the heavy category, so those branches are verified least.
- **Local evidence.** `docs/touchdownfx-2.1.7/` is not committed: it holds third-party code and
  other pilots' public data, and it is kept in the local Dropbox copy. It contains:
  - `index-DzVVC0x5.js`: the full bundle.
  - `scorecard-excerpt.js`: the scorer, replay decoder and helper functions, verbatim.
  - `guide-text.txt`: the guide's text strings.
  - `landings/`: the 40 sample landings and one list page.
  - `tfxscore.py`: the port and its comparison against stored scores.
  - `banksign.py`: the bank-sign check.

Where the guide text and the code differ, the code is authoritative, because it reproduces the
stored scores. The guide says the category is detected from approach speed and that the formal
touchdown zone ends at 914 m. The code uses the airspeed at touchdown, and ends the touchdown zone
at the zero point given below.

## Inputs

| Field | Meaning |
|---|---|
| `fpmAtTouchdown` | Vertical speed at touchdown, fpm; its magnitude is scored |
| `peakGForce` | Peak G; replaced by the replay maximum when replay data exists |
| `airspeedKnots` | Airspeed at touchdown, kt |
| `bankAngleDeg` | Raw MSFS bank: a lowered **left** wing is positive |
| `pitchAngleDeg` | Raw MSFS pitch: nose **up** is negative |
| `crabAngleDeg` | Nose against track at touchdown, degrees |
| `windDirectionDeg`, `windSpeedKnots` | Wind at touchdown |
| `runwayLat`, `runwayLon` | Runway centre |
| `runwayHeadingDeg` | Heading of the landing runway end |
| `runwayLengthMeters`, `displacedThresholdMeters` | Physical length and displaced threshold of the landing end |
| `centerlineDeviationM` | Lateral offset, used only without replay data |
| `touchdownFrameIndex` | Index of the touchdown frame in the replay |

A missing value falls back to a default (`x || default` in JavaScript):

| Missing reading | Default | Effect |
|---|---|---|
| Airspeed | 120 kt | Airliner category |
| Peak G | 1 G | Full marks |
| Crab | 0° | Full marks |
| Bank | 0° | Full marks |
| Pitch | 0° | 6 points |
| Displaced threshold | 0 m | — |
| Vertical speed | none | The total becomes NaN; no score |

## Category

The category comes from airspeed at touchdown only. "Large" means an ideal vertical speed of at
least 180 fpm, that is airliner and heavy.

| Category | Airspeed | V/S full marks up to | V/S zero at |
|---|---|---|---|
| LIGHT | < 70 kt | 80 fpm | 400 fpm |
| TURBOPROP | 70 – < 110 kt | 120 fpm | 500 fpm |
| AIRLINER | 110 – < 155 kt | 180 fpm | 600 fpm |
| HEAVY | ≥ 155 kt | 240 fpm | 600 fpm |

## Criteria

Each criterion is computed in points, then **rounded down** to a whole number. The base score is
the sum of the seven rounded values.

**Vertical speed (30).** With `v = |fpm|`:

```
30                                        if v <= full
30 * max(0, 1 - (v - full) / (zero - full))   otherwise
```

**Peak G (15).** With replay data, `g` is the largest frame `g` with `0 < g < 10` from 5 frames
before to 20 frames after the touchdown frame. At 10 Hz that is 0.5 s before to 2 s after. If no
frame qualifies, `g` is the touchdown frame's `g`.

```
15                                 if g <= 1.2
15 * max(0, 1 - (g - 1.2) / 0.3)   otherwise
```

**Touchdown zone (15).** This needs replay data and a runway with a length. The touchdown frame is
projected onto the runway with a flat-earth approximation:

```
east  = (lon - runwayLon) * 111320 * cos(runwayLat)
north = (lat - runwayLat) * 111320
d     = east * sin(hdg) + north * cos(hdg) + L / 2    // metres from the physical landing end
frac  = d / L
```

If `frac` is outside 0–1, the touchdown zone scores 0 and there is no late-touchdown penalty.
Otherwise:

```
disp      = displacedThresholdMeters
lda       = max(1, L - disp)
aim       = disp + (lda <= 800 ? 150 : lda <= 1200 ? 250 : lda <= 2400 ? 300 : 400)   // ICAO Annex 14 aiming point
tol       = large ? 100 : 150
decay     = large ? 500 : 400
zero      = min(0.5 * L, aim + decay)
fullEnd   = min(aim + tol, zero)
fullStart = max(disp, min(aim - tol, fullEnd))

points = d < disp      ? 0
       : d < fullStart ? 15 * (d - disp) / max(1, fullStart - disp)
       : d <= fullEnd  ? 15
       : d < zero      ? 15 * (1 - (d - fullEnd) / max(1, zero - fullEnd))
       : 0
```

Without replay or runway data this criterion scores 0.

Example: an airliner on a 3,000 m runway with no displaced threshold. The aiming point is 400 m.
The score rises from 0 at the threshold to full marks from 300 m to 500 m, then falls to 0 at
900 m.

**Centreline (10).** The deviation `dev` is the lateral offset of the touchdown frame:

```
dev = |east * cos(hdg) - north * sin(hdg)|
```

Without replay data it is `|centerlineDeviationM|`, or 0 when that is missing.

```
10                                 if dev <= 1
10 * max(0, 1 - (dev - 1) / 19)    otherwise
```

**Crab (10).** With `m = |crabAngleDeg|`:

```
10                                   if m <= 0.5
10 * max(0, 1 - (m - 0.5) / 4.5)     otherwise
```

**Bank (10).** With `b = bankAngleDeg`, the curve normally gives full marks up to 1° and reaches 0
at 5°. A crosswind allowance changes this:

```
t  = windDirectionDeg - runwayHeadingDeg, normalised to [-180, 180]   // positive: wind from the right
xw = |windSpeedKnots * sin(|t|)|
```

If `xw >= 5` and `b * t < 0`, the pilot is banking into the wind (a right bank is negative in
MSFS). Full marks then extend to 3° and the score reaches 0 at 6°.

```
10                                         if |b| <= full
10 * max(0, 1 - (|b| - full) / (zero - full))  otherwise
```

**Pitch (10).** With `x = -pitchAngleDeg`, so that nose up is positive, and `top = large ? 7 : 5`:

```
c = 3 <= x <= top ? 1
  : x < 3         ? max(0, 1 - (3 - x) / 8)     // zero at -5°
  :                 max(0, 1 - (x - top) / 6)   // zero at top + 6°
points = 10 * c
```

## Severity penalties

Each applicable multiplier is applied, and they multiply together.

| Penalty | Condition | Multiplier |
|---|---|---|
| Crab | m > 5° | max(0.4, 1 − (m − 5) / 10 × 0.6) |
| Centreline | dev > 20 m | max(0.5, 1 − (dev − 20) / 20 × 0.5) |
| Bank | \|b\| > bank zero (5°, or 6° with the crosswind allowance) | max(0.5, 1 − (\|b\| − zero) / 5 × 0.5) |
| G-force | g > 1.5 | max(0.4, 1 − (g − 1.5) × 0.6) |
| Late touchdown | 0.6 < frac ≤ 1 | max(0.4, 1 − (frac − 0.6) / 0.2 × 0.6) |

## Total and grades

```
total = clamp(floor(base * product of multipliers), 0, 100)
```

| Total | Grade |
|---|---|
| 90–100 | Excellent |
| 75–89 | Good |
| 60–74 | Acceptable |
| 40–59 | Below average |
| 20–39 | Poor |
| 0–19 | Dangerous |

Bounces are not scored.

The **wind bonus** (0–15) is not part of the score. It is shown as "+X" and breaks ties on the
leaderboards. It requires a base score of at least 60. Its formula is not in the website bundle;
the guide describes only:

- crosswind component of 5–30 kt: 0–10 points, on an exponential curve;
- tailwind above 3 kt: 0–3 points;
- total wind of 25 kt or more: 0–2 points.

This application does not compute the wind bonus.

## Replay data

`replayData` is base64 of a zlib/gzip stream. The stream is a sequence of little-endian float64
frames recorded at 10 Hz. The frame size depends on the file:

| Frame size | Fields |
|---|---|
| 80 bytes | `lat, lon, agl, vs, ias, g, pitch, bank, hdgMag, hdgTrue` |
| 96 bytes | the 80-byte fields, then `windDir, windSpd` |
| 120 bytes | the 96-byte fields, then `elevator, aileron, rudder` |

The decoder chooses 120 if the length is divisible by 120, otherwise 96 if divisible by 96,
otherwise 80.

## How this application applies the rules

The rules are applied exactly: categories, curves, the rounding of each criterion, the penalties,
the defaults for missing readings and the grade bands. The application uses its own measurements
as inputs, so the same landing can score slightly differently in the two tools:

| Input | This application |
|---|---|
| Vertical speed | Inertial vertical rate at the analysed contact (`InertialFpm`, see `methodology.md`), not the simulator's vertical speed |
| Peak G | Validated impact peak G (`ValidImpactPeakG`), not the largest G over −0.5 to +2 s |
| Crab | Nose heading against the ground track over the last 0.5 s of the recorded track |
| Touchdown position and centreline | `LandingDistanceAnalysis`, which uses the aircraft reference point in the runway frame of the replay projection, not the flat-earth approximation; the threshold offset is the displaced threshold |
| Crosswind | Wind speed and direction recorded at contact, against the true heading of the landing direction |
| Bank, pitch, airspeed | Raw MSFS values at contact, with the same sign conventions as TouchdownFX |

Defaults for missing readings follow TouchdownFX. Without runway geometry, the touchdown zone scores
0 and the centreline scores as 0 m. A missing peak G counts as 1 G; a missing crab or bank counts as
0°; a missing pitch counts as 0°. A missing vertical speed gives no score.

Only the first contact of a landing is scored. Later contacts are recorded as bounces and not
scored. The grade colours are this application's own.

## Re-checking against a newer TouchdownFX

1. Download `https://touchdownfx.com/guide` and take the `/assets/index-*.js` path from it.
2. In that bundle, search for `idealFpm` and compare the category table, the scorer and the
   penalties with this document.
3. Fetch a few landings from `/api/landings/{id}`, adapt `tfxscore.py` to any change, and confirm
   that it reproduces the stored `score` for every landing.
4. Update `LandingScore`, bump `RulesVersion`, and update this document and the tests in
   `tests/LandingStats.App.RegressionTests/LandingScoreRegressionTests.cs`.
