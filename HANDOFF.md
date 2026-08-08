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

# Smoke test — 132 checks, the main safety net
./Godot_v4.7.1-stable_win64_console.exe --headless --path . smoke_test.tscn

# Balance harness — 20 simulated nights + a five-point verdict
./Godot_v4.7.1-stable_win64_console.exe --headless --path . balance.tscn
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
`union_strike_capture`

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

Only room-click has a real affordance; the rest are keys. **Giving these
discoverable buttons is worthwhile UI work.**

`UnionPanel` is the exception that also opens itself: `OnStrikeTriggered`
raises the alert chip and brings the panel up, because a strike is the one
event that acts on the player unbidden.

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

Current 20-night baseline, all five verdicts passing:

```
revenue        ~$800/night
direct costs    60% of revenue
cash            1000 → ~6000 over 20 nights
outcomes        ~60% Adequate, ~30% Good, <10% Poor, ~0% Disastrous
```

**Change numbers against the harness, not intuition.** The first balance pass
produced a −$10,308 death spiral traceable to a single ratio (stress gained
per night vs. recovered), and two of my "obvious" corrections overshot in
opposite directions.

The harness plays **naively** — auto-assigns, never hires, never buys, never
bribes. It measures the floor, not skilled play.

---

## What works and is reachable

The night loop (Preparation → Service → Closing → Ledger), furniture and the
appointment/coherence economy, hiring across five channels, staff wellbeing
and ambitions, the policy tree, influence and permit-gated expansion,
regulars and patrons, licences, save/load, and the 3D view with real
characters.

---

## What's left, in the order I'd do it

1. **`CrisisNarrativeDirector` can deadlock.** `_crisisActive` latches true on
   trigger and only clears in `ExecuteChoice`. If no scenario ever arrives —
   it expects an out-of-process LLM to read `<<<MCP_CRISIS_PAYLOAD>>>` from
   stdout — it hangs permanently. Needs the fallback path to be the primary
   one. It is currently **not instantiated**, so it is latent, not live.

2. **`GenerateRandomClient` has 8 names.** The patrons book is premised on
   knowing people; the same name already appears as two distinct patrons.

3. **Discoverable buttons for the side panels.** Six of seven are key-only.

4. **`StrikingStaffCount` is frozen at trigger time** and never re-derived, so
   staff who leave mid-strike still count as walked out. The panel clamps the
   display; the number itself is still wrong.

5. **Furniture wear vs. maintenance** is tuned so Appointment drifts down
   slowly. Worth re-checking over a 50-night run rather than 20.

---

## Repo

Not pushed anywhere yet. Remote is set to
`https://github.com/atomicapple/whorehouse` — `git push -u origin main` when
wanted. 25 commits, `.git` ~1.9 MB because all the heavy assets are ignored.

Commit messages in this repo are long and explain *why*, including bugs found
and rejected approaches. Worth continuing — several of them are the only
record of why a number is what it is.
