# Art assets

Everything here is discovered at runtime. Dropping a file in the right place
is the whole integration step — no code changes, no Godot import step, no
registry to edit.

Models are parsed with `GltfDocument.AppendFromBuffer`, not `GD.Load`, so
`.import` sidecars are irrelevant. `.glb` only; `.gltf` with side files is not
supported.

`Assets/` is **gitignored**. It is roughly 1.2 GB and exceeds GitHub's free
LFS quota, so the art lives on disk and in your own backups, not in the repo.

---

## Furniture

```
Assets/Furniture/<Folder>/<style>_<name>.glb
```

The **folder** decides the category. The **filename** may name a style, and
that model is then preferred for pieces of that style; models with no style
word are used for anything.

### Folder → category

| Category | Accepted folder names |
|---|---|
| Bed | `Bed`, `Beds` |
| Seating | `Seating`, `Seats`, `Chairs`, `Sofas`, `Couches` |
| Lighting | `Lamps`, `Lamp`, `Lighting`, `Lights`, `Chandeliers` |
| Rug | `Rugs`, `Rug`, `Carpets` |
| Decor | `Decor`, `Decoration`, `Mirrors`, `Art`, `Paintings` |
| Vanity | `Vanity`, `Vanities`, `Tables`, `Desks`, `Dressers` |
| Screen | `Screens`, `Screen`, `Dividers` |
| Bath | `Baths`, `Bath`, `Tubs` |
| Bar | `Bar`, `Bars`, `Counters`, `Cabinets` |

A folder that matches nothing is reported once at startup rather than
silently ignored — check the log if a model does not appear.

### Filename → style

| Style | Words recognised in the filename |
|---|---|
| Baroque | `baroque`, `rococo`, `gilded`, `victorian` |
| ArtDeco | `deco` |
| Oriental | `oriental`, `lacquer`, `shoji` |
| Bohemian | `bohemian`, `boho` |
| Modern | `modern`, `minimal` |
| Spartan | `spartan`, `plain` |

Style matters in play: a room where ≥70% of pieces share a style scores a
large **Coherence** bonus, which is 20% of its Appointment score. Coverage of
the *categories* a room requires is worth more (40%), so breadth beats depth
until every required slot is filled.

### Modelling notes

- **Any scale is fine.** Each model's bounding box is measured and normalised
  to a target height, because Meshy's units are inconsistent. Targets: bed
  0.62 m, seating 0.85 m, lamp 0.55 m, rug 0.03 m, decor 1.10 m, vanity
  0.78 m, screen 1.65 m, bath 0.65 m, bar 1.10 m — against a 2 m tile.
- **Face +Z** if the piece has a front (headboard, counter service side).
- **No baked base plate or floor.** Pieces sit on the room's own slab.
- **Single mesh, modest poly count.** Models are instanced many times per
  floor and every floor of the building is drawn.

### What each room requires

Missing a required category collapses a room's Coverage score, so these are
worth having a model for first:

| Room | Required categories |
|---|---|
| VIP Suite | Bed, Seating, Lighting, Decor, Bath |
| Private Suite | Bed, Lighting, Vanity |
| Lounge | Seating, Lighting, Rug, Decor |
| Bar | Bar, Seating, Lighting |
| VIP Entrance | Lighting, Decor, Seating |
| Medical | Bed, Lighting |

Any category with no model falls back to a procedural shape. Those are built
to be recognisable in silhouette, not to look good — they are a placeholder
and a permanent safety net, not a target.

---

## Characters

```
Assets/Characters/<Name>/<anything>.glb
```

Unlike furniture, characters are **not** auto-discovered — they are listed
explicitly in `CharacterLibrary.Models`, because each one needs a scale
correction and an optional separate animation file. Adding one is a few
lines there.

Requirements:

- **Rigged, roughly 1.7 m tall.** The probe reports measured height.
- **Animations in the same file** (a "merged animations" export) or in a
  second `.glb` pointed at by `AnimationPath`.

### Animation naming

Clips are matched by substring, case-insensitively, first match winning:

| State | Words looked for |
|---|---|
| Idle | `idle`, `stand`, `breath` |
| Walk | `walk`, `stroll` |
| Talk | `talk`, `gesture`, `agree`, `wave` |
| Sit | `sit`, `chair` |
| Dance | `dance`, `groove`, `hop` |

**The current exports only contain `Running` and `Walking`.** With no idle
clip, staff standing in a room freeze a walk cycle at frame zero rather than
sprinting on the spot — workable, but the house looks static. An **idle** and
a **talk** gesture would do more for how alive it reads than any other two
assets.

### Checking a character

```bash
Godot_v4.7.1-stable_win64_console.exe --headless --path . character_probe.tscn
```

Reports mesh count, skeleton, measured height and every clip found, plus how
each logical state resolved. When a character does not show up in game, this
says whether the fault is the file or the game.
