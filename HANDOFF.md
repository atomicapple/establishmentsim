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

# Smoke test — 216 checks, the main safety net
./Godot_v4.7.1-stable_win64_console.exe --headless --path . smoke_test.tscn

# Balance harness — 20 simulated nights + a nine-point verdict
./Godot_v4.7.1-stable_win64_console.exe --headless --path . balance.tscn

# The same, over 50 nights — where slow drifts become visible
./Godot_v4.7.1-stable_win64_console.exe --headless --path . balance_long.tscn

# 50 nights answering every crisis as cheaply as possible — the other bracket
./Godot_v4.7.1-stable_win64_console.exe --headless --path . balance_thrifty.tscn
```

**A verdict that fails for reasons it does not name is worse than no
verdict.** The crisis-count check originally asserted at least one crisis in
twenty nights. Twenty nights yields exactly one, so unrelated changes flipped
it to zero and reported a crisis failure for an economic experiment. Its
lower bound now applies only to runs of forty nights or more.

**`SeedEstablishedHouse` matters.** A new campaign opens with a reception, a
bar, and nothing else — the player builds every suite. The smoke test and all
three balance scenes set `SeedEstablishedHouse = true`, because neither ever
builds a room and both would otherwise measure a house with nothing to sell.
Capture scenes that photograph furnished rooms set it too; `main.tscn` does
not. If a test suddenly reports no revenue, check that flag first.

**Always run both before committing.** The smoke test catches wiring; the
balance harness catches economic regressions the smoke test cannot see.

### Diagnostic scenes

| Scene | What it answers |
|---|---|
| `character_probe.tscn` | Do the character `.glb` files load? What clips? |
| `room_probe.tscn` | What meshes does a furnished room actually build, and where? |
| `suite_probe.tscn` | A tight camera on one furnished suite, doors shut. For judging placement and scale. |
| `newgame_probe.tscn` | What a brand-new campaign opens with — the entrance and nothing else. |

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
`union_strike_capture` · `crackdown_capture` · `crisis_capture` ·
`help_capture` · `negotiation_capture` · `intro_capture`

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

4. **`CenterContainer` sizes its child to the child's *minimum*.** It will
   never let a panel grow into available space, however much there is. The
   Ledger was centred with one, so its scroll region stayed at its 460px
   minimum at every resolution and the Standing section sat permanently below
   the fold. Centre with an `HBoxContainer` and two `ExpandFill` spacers
   instead, and set `SizeFlagsVertical = ExpandFill` on the panel.

5. **`.glb` files here have no usable `.import` sidecars.** `GD.Load` cannot
   see them. Use `GltfDocument` + `AppendFromBuffer`. Parse once and
   `Duplicate()` — these are 50–60 MB files.

6. **A `const` cannot be a ceiling.** `FurnitureSlotsPerTile` had to become a
   static property for licences to raise it.

---

## Assets

See `ASSETS.md` — it documents the drop-in conventions in full.

Short version: **`Assets/` is tracked in full** — 246 MB, nothing excluded,
no LFS. It was 2.1 GB with four files over GitHub's 100 MiB hard limit until
`tools/` was written; see below.
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

### Crises

Six trigger sources, three polled on the daily tick and two event-driven:

| Trigger | Fires on | Route |
|---|---|---|
| `PoliceRaid` | heat > 35 | poll |
| `WorkerWalkout` | a strike is running | poll |
| `PublicScandal` | sentiment < 35 | poll |
| `ReputationCollapse` | reputation < 30 | poll |
| `FinancialCollapse` | cash < 0 | poll |
| `StaffBreakdown` | `OnPsychologicalBreak` | signal |
| `RivalAttack` | `OnRivalAction`, severity ≥ 7 | signal |

The two signal-driven ones **cannot** be polled: a break and a rival's move
are moments, not states, and by the next daily tick the emitting system has
applied its consequences and moved on.

`RaiseCrisis` is the single gate all seven pass through, so a new source
cannot bypass the cooldown or the one-at-a-time rule.

Heat alone gave two crises per fifty nights and **no threshold could improve
that** — each raid takes 25 heat off and heat accrues 1–2 a night, so the
system suppressed itself for fifteen nights afterwards. More sources was the
fix, not a lower number. A 50-night run now faces five.

Adding them exposed that the crisis costs were authored for a
once-a-campaign event: at five per fifty nights the house went bankrupt at
−$3,668. Every "pay it away" cash line was scaled to roughly 55%.

### Onboarding

`IntroScreen` shows four cards on a new campaign — what the house is, the
shape of a night, what Appointment actually sells, and what it costs in
people and heat. Skippable, and suppressed on capture runs and on the
established house.

The step-by-step guidance is **derived, not scripted**. `Onboarding.Next`
reads the world every time it is asked and returns the first thing genuinely
still undone, which surfaces as a chip in the top bar. A player who furnishes
before posting staff, or builds two suites before opening, is never told to
undo anything — the prompt just moves on. There is no sequence to persist, no
state to reset, and no way for the guidance to disagree with the game.

### Two modal screens above the HUD, and a third

`NightLedgerScreen`, `CrisisScreen` and `NegotiationScreen` are all
`CanvasLayer`s that take a real pause. Each shadows `Hide()` deliberately,
because a plain visibility toggle would leave the game frozen behind an
invisible window.

`NegotiationScreen` parks the night: `NightDirector` holds the encounter in
`_pending` and does nothing until `ResolveNegotiation` or `DeclineClient`
answers. **`NightDirector.NegotiationUiPresent` is what decides whether a VIP
parks at all**, and it is set explicitly by `GameScene`. It was originally
inferred from `GetSignalConnectionList`, which does *not* filter to the
signal you name — so the headless harness, which connects other signals on
that node, read as "a UI is present", parked every VIP forever and dropped
them. All nine balance verdicts moved and nothing in the logs explained it.
**Do not infer the presence of a listener from Godot's connection list.**

### The three one-way stats

Three times now the same bug has appeared in a different stat: a value that
only ever moves in one direction, so the system built on it is a countdown
rather than a resource.

| Stat | Symptom | Fix |
|---|---|---|
| Stress | rose from shifts, never fell; everyone eventually broke | `BaseDailyStressRecovery`, tuned to 15 |
| Loyalty | eroded on `Adequate`, the modal outcome; roster collapsed by night 20 | `Adequate` made neutral |
| Reputation | fell on bad nights, and arrivals scale with it, so a slump removed the chances to recover | `ReputationRecoveryRate`, asymmetric |

Reputation's fix is deliberately **asymmetric** — it pulls up toward the
baseline and does not pull down. A symmetric version was tried first and
flattened the game: the ceiling fell from 84 to 60 while the floor rose,
compressing fifty nights into a narrow band and making good nights stop
mattering. Bad news fades; an earned reputation should be taken away by bad
nights, not by time. `ReputationDecayRate` exists and is zero if that ever
needs revisiting.

**When a stat here misbehaves over a long run, check whether anything moves
it the other way before touching its magnitudes.**

### Every major constant, swept

The standing advice used to be that no constant could be trusted, because
all of them were tuned against a harness with 55% run-to-run variance. All
five of the biggest have now been re-derived against the deterministic one.
**None of them needed changing** — but each is now a measured choice rather
than a remembered one, and each is right for a *different* reason.

```
commission        0.30  cash 7607  costs 48%  8/9
                  0.36       6619        54%  8/9
                  0.42       5630        60%  9/9   <- current
                  0.48       4642        66%  9/9
                  0.54       3654        72%  9/9

room price         $62  4371  8/9     $78  5630  9/9  <- current
                   $70  5351  8/9     $86  6603  9/9
                                      $94  7563  9/9

arrivals/night       6  cash  -986  turned away 38  8/9
                     7         137              28  8/9
                     8        1657              21  9/9  <- current
                     9        1653              31  9/9
                    10        1805              30  9/9

maintenance repair   0  appointment 70.3 -> 41.7  7/9
                     1              70.7 -> 54.3  8/9
                     2              71.1 -> 68.0  9/9   <- current
                     3              71.4 -> 71.4  9/9
                     4              71.4 -> 71.4  9/9

stress recovery      9  peak 59  mean 19.9  8 breaks  7/9
                    12       46       20.9  5 breaks  7/9
                    15       29        9.0  1 break   9/9  <- current
                    18        7        0.8  0 breaks  8/9
                    21        1        0.0  0 breaks  8/9
```

Read the shapes, not just the ticks:

- **Commission** sits in the lower third of the passing band, not on its edge.
- **Room price** has no economic boundary in this range at all.
- **Arrivals at 8 is a knee.** Below it the house is insolvent; above it cash
  plateaus and turn-aways climb, because three staff become the binding
  constraint instead of demand. More customers stop helping.
- **Maintenance at 2 is the last value where the mechanic is alive.** At 3
  and above, repair fully cancels wear (71.4 → 71.4) and the entire furniture
  degradation system becomes decorative.
- **Stress recovery at 15 is the only 9/9**, sitting between "everyone breaks"
  and "stress pins at zero and the system is inert" — which is exactly what
  its original commit message claimed, now with evidence.

Two of these — maintenance and stress recovery — fail *upward* as well as
downward. A generous-looking value silently switches the mechanic off. When
tuning here, check the top of the range as well as the bottom.

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

## Asset optimisation

`tools/` decimates the Meshy exports. Run it after dropping new models in:

```bash
npm install --no-save @gltf-transform/cli   # once
node tools/decimate-all.mjs                 # Assets/ -> Assets_optimised/
```

It never writes over an input. Copy the results in yourself once you have
looked at them.

```
1,525 MB of .glb  ->  253 MB      Assets/ total 2.1 GB -> 804 MB
largest bed        138.4 -> 5.9 MB     1,729,170 -> 86,404 tris
purple runner rug   31.2 -> 1.0 MB        (all texture, barely any geometry)
```

**Four things about this were not obvious and cost hours:**

1. **`weld` merges only bitwise-identical vertices.** These meshes are
   faceted, so every triangle carries its own normals and nothing matches —
   welding removed 32 triangles out of 1.7 million. Without shared edges
   `simplify` has nothing to collapse and does nothing at all. Strip NORMAL
   first, weld on position, decimate, regenerate normals.

2. **Importing `@gltf-transform/functions` breaks `sharp` in the same
   process.** Every texture resize then fails with `colourspace: parameter
   space not set`. That reads like a colour-management problem and is a
   module-loading one. This is why textures and geometry run as two separate
   processes — `shrink-textures.mjs` must never import `functions`.

3. **`NodeIO` silently drops extensions it was not told to register.** A
   first pass discarded `KHR_materials_specular` and `KHR_materials_ior` from
   every character, changing how they shade for no reason connected to size.
   `registerExtensions(ALL_EXTENSIONS)`.

4. **Which half dominates is not guessable from the file size.** A 138 MB bed
   was pure geometry with no textures at all; a 31 MB rug was 35k triangles
   and four 4096² JPEGs. Both need handling.

Originals of the four beds — the only files git never had — are at
`C:\whorehouse_asset_backup`.

### Nothing here is source material any more

The three Meshy `.zip` rig archives and the unpacked
`Meshy_AI_Wealthy_Gentleman_Rig_biped/` folder are gone — about 280 MB of
master material for the three characters the game uses. **If a character ever
needs re-rigging or re-exporting, it comes back out of git history, or out of
Meshy.** Nothing in the working tree is a master any more; everything left is
a runtime asset.

One orphan remains and is deliberate: `wealthy_gentleman_texture_0.png`,
26.4 MB. Every used `.glb` embeds its own textures — verified, zero external
URI references — so nothing loads it. It is left only because a 26 MB
hand-editable texture is the kind of thing an artist reaches for, and it is
one file.

## What's left

1. **The unused character models above.**

2. **Balance constants predate the working harness** for everything except
   the five swept in this file. Re-derive rather than trust.

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
