# Handoff

Written at the end of a long session, for whoever picks this up next —
possibly a fresh agent with no memory of any of it. Read this before
touching anything.

---

## What this is

**Establishment Simulator** — a Godot 4.7.1 / C# (.NET 8) management sim.
You run a brothel: furnish rooms, staff them, serve clients through a nightly
loop, and manage the heat, politics and human cost that follow. Mature themes
handled seriously; nothing explicit is depicted. Encounters are a black box —
a conversation beat, then an abstract cloud, then a computed outcome.

Closest reference: **Two Point Hospital**. Cutaway building, orthographic
camera, little people in rooms.

The full design rationale lives in the approved plan at
`C:\Users\tobia\.claude\plans\here-are-some-reference-temporal-comet.md`.
It predates the 3D migration (see below) but the *design* is still current.

---

## Build, run, test

Everything runs from `C:\whorehouse`. The .NET SDK and the Godot editor are
**vendored in the repo** and gitignored.

```bash
# Build (output is German: "0 Fehler" means success)
./dotnet_sdk/dotnet.exe build EstablishmentSimulator.csproj -v quiet -nologo

# The game, windowed
./Godot_v4.7.1-stable_win64_console.exe --path . --resolution 1600x900 main.tscn

# Smoke test — 184 checks, the main safety net
./Godot_v4.7.1-stable_win64_console.exe --headless --path . smoke_test.tscn

# Balance harness — 20 simulated nights + a six-point verdict
./Godot_v4.7.1-stable_win64_console.exe --headless --path . balance.tscn

# The same, over 50 nights — where slow drifts become visible
./Godot_v4.7.1-stable_win64_console.exe --headless --path . balance_long.tscn
```

**Always run both before committing.** The smoke test catches wiring; the
balance harness catches economic regressions the smoke test cannot see.

### Diagnostic scenes

| Scene | What it answers |
|---|---|
| `character_probe.tscn` | Do the character `.glb` files load? What clips? |
| `room_probe.tscn` | What meshes does a furnished room actually build, and where? |

`room_probe` exists because three screenshots in a row *appeared* to show
empty rooms and I nearly rewrote the furniture builders. The probe proved the
furniture was correct and the camera was framing between floors. **A
screenshot cannot distinguish "not built" from "built where you aren't
looking."**

### Capture scenes

Self-driving screenshot runs. Each sets flags on `GameScene` and quits.

`main_capture` · `closeup_capture` · `ledger_capture` · `staff_capture` ·
`hiring_capture` · `influence_capture` · `policy_capture` ·
`patrons_capture` · `licences_capture` · `union_capture` ·
`union_strike_capture` · `crackdown_capture` · `crisis_capture`

Output lands in
`C:\Users\tobia\AppData\Roaming\Godot\app_userdata\Establishment Simulator\screenshots\`.

`patrons_capture` plays 12 compressed nights before shooting, because the
book is empty on night one by definition. `union_strike_capture` calls
`UnionizationManager.ForceStrike()` for the same reason — a strike needs
weeks of mistreatment to arrive on its own, so there is no other way to
photograph (or test) the resolutions.

Screenshots only work **windowed**. A headless run reports "Viewport
produced no image" and writes nothing.

---

## The one pattern that matters most

**This codebase is full of well-written systems that nothing ever calls.**
It began as ~90 files that had never been assembled — no instances, no
wiring. Most of the session's real bugs were of exactly one kind: a system
that looks finished, reads correctly, and has zero callers.

Found and fixed this session:

- `StaffRoster` didn't exist; seven files each read
  `PsychologicalBreakSystem.GetStaffAtRisk()` (which filters to Stress ≥ 80)
  and treated it as the full roster.
- `RecordTraumaEvent` — no callers, so the displaced-aggression design never
  reached play.
- `BlackmailNetwork.AttemptIntelGathering` — no callers.
- `PoliticalInfluenceSystem` permits — nothing consumed them.
- `PolicyTreeManager.EnactPolicy` — no callers, hiding *two* fatal bugs.
- `RealEstateMarket.RegisterDistrict` — no callers; market permanently empty.
- `ResearchTreeUI` — never instantiated, effects empty. Deleted.
- `UnionizationManager`'s three strike resolutions — no callers, so a strike
  could fire at the player with no way to answer it.
- `MacroEconomyEngine.OnPhaseChanged` / `OnPhaseWarning` — no listeners, so
  the largest single force on revenue was invisible.
- `BribeCostMultiplier`, `PropertyValueMultiplier` — computed every day,
  read by nobody. Both now wired.
- `CrisisNarrativeDirector` — never instantiated, and would have deadlocked
  on its first crisis if it had been.

A closed island of 2,694 lines was deleted outright: `ClientQueueManager`,
`EventDialogUI`, `TutorialManager`, `TutorialSequenceManager`,
`OnboardingTestSuite`, `SimulationTestRunner`, `SpatialHeatmapLogger`,
`EngineMetricsOverlay`, `TaskPoolDispatcher`. They referenced each other and
nothing else referenced them.

**Before trusting any system here, grep for callers of its main entry
point.** If there are none, assume it has never run and budget for bugs.

---

## Architecture

### Shared contracts — do not duplicate these

| File | Owns |
|---|---|
| `VenueSpace.cs` | The **only** grid → world mapping. Geometry, pawns, effects and the click raycast all go through it. |
| `IsoTheme.cs` | The **only** palette. Its projection half is dead 2D legacy — ignore it. |
| `ISaveableSystem.cs` | Persistence contract. Implement it and `SaveLoadSystem` finds you automatically. |

A second copy of the projection maths is how layers drift apart. This bit us
in 2D and the same discipline carried into 3D.

### Wiring

`GameBootstrap.cs` constructs every system as a **named** child.
**The node names are load-bearing** — systems find each other with
`GetTree().Root.FindChild("SomeName", true, false)`. Rename a node and things
silently stop resolving.

`GameScene.cs` is the playable root: builds the world, the 3D view, the HUD
and six side panels, and wires the signals.

### Rendering — 3D since the migration

The project **started 2D isometric and migrated to real 3D.** The reason was
the asset pipeline, not the graphics: Meshy produces 3D, no 2D art existed,
and the 2D plan needed hundreds of hand-drawn sprites nothing could make.

The bet was that the simulation is renderer-agnostic. It held — only three
files changed hands and every test passed unchanged.

- `VenueView3D.cs` — orthographic camera at a fixed isometric angle, floor
  cutaway, picking, pan/zoom.
- `VenueRoomBuilder.cs` / `VenueFurnitureBuilder.cs` — room shells and
  furniture; instances real `.glb` models where they exist, procedural
  shapes otherwise.
- `VenuePawns3D.cs` — people, via `CharacterLibrary`.
- `EncounterCloud3D.cs` — the abstract encounter effect.

Deleted: `IsometricDollhouseView`, `VenuePawnLayer`, `EncounterCloudVfx`.

### Side panels

Seven, all sharing the left column through `GameScene.ShowOnly()`:

| Key | Panel |
|---|---|
| click a room | `DecoratePanel` — the furniture shop |
| `S` | `StaffPanel` — roster + hiring |
| `I` | `InfluencePanel` — bribes |
| `P` | `PolicyPanel` — the Panderer's Code |
| `B` | `PatronsPanel` — the book |
| `L` | `LicencesPanel` — ceilings |
| `U` | `UnionPanel` — labour disputes |

All six of the keyed panels also have a labelled button in the top bar
(`GameHud.BuildPanelRail`), and the tooltip names the shortcut. The keys were
the *only* route until then, which meant a player who never guessed `L` could
finish a campaign without learning licences existed. `GameHud.PanelKey` is
the single definition of each shortcut — the tooltip that teaches it and the
handler in `GameScene` both read it, so they cannot disagree.

`UnionPanel` is the exception that also opens itself: `OnStrikeTriggered`
raises the alert chip and brings the panel up, because a strike is the one
event that acts on the player unbidden.

Two modal screens sit above everything, both `CanvasLayer` and both holding
a real pause: `NightLedgerScreen` and `CrisisScreen`. A crisis takes
precedence — the Ledger waits behind it. Both shadow `Hide()` deliberately,
because a plain visibility toggle would leave the game frozen behind an
invisible window.

---

## Godot gotchas that cost real time

1. **`SetAnchorsPreset` leaves stale offsets.** A `PanelContainer` inside
   then collapses to its minimum size and the panel renders as a bare header
   strip. Use `SetAnchorsAndOffsetsPreset`, and only **after** `AddChild`.
   This bit three separate panels.

2. **Panels anchor themselves in `_Ready`,** overriding offsets set from
   outside. `GameScene.MakeSidePanelHost` wraps each in a correctly-sized
   host they fill instead.

3. **`ProcessMode` defaults to inherit,** so anything under a paused tree
   stops ticking. `ScreenshotCapture` silently never fired on modal screens
   until it was set to `Always`.

4. **`.glb` files here have no usable `.import` sidecars.** `GD.Load` cannot
   see them. Use `GltfDocument` + `AppendFromBuffer`. Parse once and
   `Duplicate()` — these are 50–60 MB files.

5. **A `const` cannot be a ceiling.** `FurnitureSlotsPerTile` had to become a
   static property for licences to raise it.

---

## Assets

See `ASSETS.md` — it documents the drop-in conventions in full.

Short version: **`Assets/` is gitignored** (~1.2 GB, exceeds free LFS quota).
Furniture auto-discovers from folder names under `Assets/Furniture/`; the
folder picks the category, the filename can name a style. The scan walks *up*
the path, so `Beds/GLB/x.glb` works.

Characters are listed explicitly in `CharacterLibrary.Models` /
`ClientModels`, but every extra `.glb` in a character's folder is merged for
its animations.

**Open request to the user:** idle and talk animations for the two female
rigs. They currently ship only `Running` and `Walking`, so idle staff freeze
a walk pose. The gentleman has 21 clips and visibly reads better.

Also wanted: **Bar** and **Bath** models — both are *required* categories
(Bar room, VIP Suite) and still render procedurally.

---

## Balance

Two harnesses: `balance.tscn` (20 nights, the gate) and `balance_long.tscn`
(50 nights, the diagnostic). Both report six verdicts and all six pass.

```
revenue        ~$800/night early, decaying toward ~$150 by night 50
direct costs    70% of revenue
cash            1000 → ~6300 at 20 nights, ~4700 at 50
outcomes        ~48% Adequate, ~26% Good+Exceptional, ~24% Poor, ~2% Disastrous
appointment    71.1 → 68.3 over 50 unattended nights
```

**The harness is deterministic — as of the run that made it so.** It always
had a `Seed` export, and that export seeded exactly one generator: the
bootstrap's own. Every other system called `_rng.Randomize()` in its own
`_Ready`, `GenerateRandomClient` built a freshly randomized generator per
client, and the night loop advanced on the real frame delta. Three identical
20-night runs finished on +3537, +5497 and +5281 — a 55% spread. **Every
balance decision recorded in this repo's history was measured against that
noise.** Re-derive rather than trust an old number.

The fix is `WorldRandom.cs` (one seed, named independent streams) plus
`NightDirector.FixedStepSeconds` (the harness advances the night in a fixed
number of beats instead of a fixed number of seconds). Three runs now agree
to the dollar. If that ever stops being true, suspect a new unseeded
generator or a new `string.GetHashCode()` — .NET randomizes string hashing
per process, and that bug got reintroduced *inside* `WorldRandom` on the
first attempt.

**Change numbers against the harness, not intuition.** The first balance pass
produced a −$10,308 death spiral traceable to a single ratio (stress gained
per night vs. recovered), and two of my "obvious" corrections overshot in
opposite directions.

The harness plays **naively** — auto-assigns, never hires, never buys, never
bribes. It measures the floor, not skilled play. What the 50-night run shows
is that the floor **stagnates rather than dies**, and the cause is the macro
phase, not furniture and not heat:

```
night 32   the city flips to PoliceCrackdown
           footfall ×0.4, client spend ×0.7, for 32 days
night 33+  served falls 7-8 → 2-3, revenue $850 → $150
           reputation 83 → 25, and it has no passive recovery
```

Furniture wear moves Appointment 2.8 points over 50 nights — nothing. No
raid ever fires. The roster never shrinks. It is one macro phase, and it was
invisible until the city chip was built.

Reputation having no recovery term is the shape of a real problem: arrivals
scale with reputation, so a shock that lowers it lowers the number of chances
to earn it back. Nothing has been changed about it — it is a design call, not
a bug, and it belongs to the person who owns the design.

---

## What works and is reachable

The night loop (Preparation → Service → Closing → Ledger), furniture and the
appointment/coherence economy, hiring across five channels, staff wellbeing
and ambitions, the policy tree, influence and permit-gated expansion,
regulars and patrons, licences, save/load, and the 3D view with real
characters.

---

## What's left, in the order I'd do it

The first two are **design calls, not bugs.** They are the largest open
questions in the game and neither has a right answer I can derive from the
code, so the numbers are untouched.

1. **Crises never fire.** The director is live, the screen works, four
   scenarios are written — and a 50-night naive run faces **zero** of them.
   The triggers sit at catastrophe level: heat above 85 (it peaks around
   70), sentiment below 15, an active strike, or the house $500 in debt. The
   design calls for the Ledger to end with one to three decisions as the
   pacing heartbeat; at these thresholds it beats zero times. How often
   should the game interrupt the player?

2. **Reputation has no recovery term.** Arrivals scale with it, so a shock
   that lowers reputation lowers the number of chances to earn it back. Over
   50 nights it falls 83 → 25 and never returns. Death spiral, or the
   ratchet working as intended?

3. **Balance constants predate the working harness.** Anything justified by
   a harness run before `WorldRandom` was measured against ±55% noise.
   Re-derive rather than trust — including numbers whose commit messages
   sound confident.

4. **Asset decimation.** Thirteen files drew GitHub's "larger than the
   recommended 50 MB" warning, and four beds are excluded from the repo
   entirely for breaking the 100 MiB hard limit. A 70 MB rug and a 91 MB rig
   archive are Meshy defaults, not game assets. This has to happen before
   release regardless of where the files are stored.

5. **`RollReturningClient` iterates a `Dictionary<string, Patron>`** keyed by
   Guid. Iteration order is stable within a process but is not part of the
   seed, so it is the one part of a "deterministic" run that could still
   drift if the dictionary were ever rebuilt in a different order. Not
   currently observed; an ordered list would settle it.

6. **The three `HudTool` chips** (Cleaning, Alert, Info) emit
   `OnToolRequested` and nothing consumes it. The Alert chip is written to,
   but pressing any of the three does nothing.

---

## Repo

**`https://github.com/atomicapple/establishmentsim`, private.** Pushed and in
sync. 31 commits; `.git` ~1.3 GB, because `Assets/` is tracked.

Assets went up as ordinary git objects — 157 files, 1.55 GiB, no LFS and
nothing paid for. Four ornate bed exports (103–138 MiB each) are excluded by
a narrow ignore pattern: they exceed GitHub's 100 MiB per-file hard limit, so
a push containing any of them is *rejected*, not warned about. They are being
replaced with smaller textured models. The 300 KB Regal Purple Velvet bed in
the same folder is tracked, so Beds is not an empty category.

Thirteen other files drew "larger than the recommended 50 MB" warnings.
Advisory, not enforced — but they are the same problem as the beds under the
threshold, and all of them need decimating before release anyway. A 70 MB rug
is a Meshy default, not a game asset.

### A warning about this file

`Assets/` was untracked for 30 commits on the strength of a `.gitignore`
comment explaining that the project had moved to 2D illustrated isometric and
the 3D models were therefore surplus. That stopped being true at the 3D
migration. Nobody questioned the exclusion because the comment sounded like a
reason.

**Prose rots the same way code does, and it rots more quietly** — a stale
comment is indistinguishable from a current one, where a stale function at
least fails a test. This file is prose. When something here contradicts what
you observe, the file is what is wrong.

Commit messages in this repo are long and explain *why*, including bugs found
and rejected approaches. Worth continuing — several of them are the only
record of why a number is what it is.
