# Editor / Authoring Tools reference

The **HAF Authoring Tools** are a suite of Unity editor windows under **`Tools ▸ HAF`**. They are the *bake* half of HAF —
you author custom content here, and the runtime plugin injects it in-game; the two halves talk only through the JSON pack
registry. (The editor scripts live in the [ENCReload](https://github.com/sswelm/ENCReload) repo, under
`Assets/Scripts/Editor/` — the only copy; the stale mirror that used to sit in this repo's `baker/` was deleted 2026-08-21.)

This page is the **map** — every tool, its exact menu path, what it does, and what it writes. For the deep, field-by-field
workflows, follow the *Deep dive* links; this reference deliberately doesn't duplicate them.

## Installing them

`Window ▸ Package Manager` → `+` → `Add package from git URL…`:

```
https://github.com/sswelm/ENCReload.git?path=/Assets/Scripts/Editor
```

The install is inert by design: automatic backups, the asset-delete guard and the console filter all default **off**
in an installed package and on in ENCReload itself (`HafPackageContext` reads the context from
`UnityEditor.PackageManager`). See [Getting-Started.md](Getting-Started.md) for the ordered path and the two current
limits — `Tools/` doesn't ship in the package yet, and pack identity is still fixed to ENC's.

## Where the tools write — the registries

Every registry is **ONE file**: the git-tracked project file is the source the editor reads and writes; the copy in the
game's `BepInEx/config` (auto-detected by `ModelRegistry.ConfigDir`) is a **build artifact** regenerated on every Save
(units since 2026-08-19, districts and formations since 2026-08-20 via the shared `SingleSourceRegistry` engine — with
pinpointed corruption and one-click recovery in each window). The runtime plugin reads the deployed copies on launch.

| Registry | Live path | Repo backup | Written by |
|---|---|---|---|
| **Model registry** (`pack.json`) | `config/haf_packs/ENCReload/pack.json` | `Assets/Pack/ENCReload/pack.json` | Model Factory, Animation Lab, Resize Lab, Unit Retexture, Sound Studio, Global Era Lab |
| **Districts** (`haf_districts.json`) | `config/haf_districts.json` (artifact) | `Assets/Databases/haf_districts.backup.json` (THE source — historical name) | District Factory |
| **Formations** (`haf_formations.json`) | `config/haf_formations.json` (artifact) | `Assets/Databases/haf_formations.backup.json` (THE source — historical name) | Formation Override |
| **Sound overrides** (`haf_sounds.json`) | `config/haf_sounds.json` | project backup | Game Sound Lab |
| **Props** (`haf_props.json`) | `Assets/Databases/haf_props.json` | — | Prop Lab (editor-side recipe store; the runtime reads the baked GUIDs, not this file) |

> `pack.json` is ENC's own **pack** (see [Multi-Mod.md](Multi-Mod.md)). Older docs/config call the base model file
> `haf_models.json` colloquially — the real shipped filename is `pack.json` in the pack folder. Baked assets
> (`<name>_ModelMesh` / `_Skeleton` / `_Atlas`) go under `Assets/Resources`.

---

## Core model authoring

### Model Factory — `Tools ▸ HAF ▸ Model Factory`
Author a static model (or the *model half* of an animated one): pick/create a resource, a target pawn, and a model file;
tune geometry and shading; **Bake** → produces a `Skeleton` + `Atlas` and a registry entry. *Key controls:* target pawn,
model file, Size, Strip parts, reduce-to-tris, height-UVs, winding fix, double-sided, albedo brightness/saturation,
keep-black, atlas size (256–4096), material mode, hide-donor, freeze-donor, re-spawn-after-load, embedded 3D preview.
The preview stands the model on a true-size tile hex at the true in-game surface level — sunk or floating bakes
preview that way (static entries show the shipped mesh; animated entries the rest-pose rig, the same faithful view as
the Animation Lab). Boats stand on a **water-blue** hex instead of grass — detected from the pawn's own Boat
capability profile, never the name — so a waterline offset (hull below the blue) reads naturally. The **forward arrow**
on the hex marks the in-game facing: dial the model's nose/bow/barrel along it and the unit moves and fights the
right way round. Orbit/pan/deep-zoom controls with a **Center** button that re-frames a lost view.
*Writes:* `pack.json` (via `ModelRegistry.Upsert`) + baked assets, through `ConfigFor → UniversalBaker`.
**Deep dive:** [Factory-Manual.md](Factory-Manual.md).

### Animation Lab — `Tools ▸ HAF ▸ Animation Lab`
The animation-only companion (docks beside the Factory): configures *which clip plays and how* for one model entry —
state-driven idle/move/after/attack, deploy conversion, recoil/slam, turret & muzzle bones, hand props, donor sockets.
*Key controls:* Clip field + range picker (▶) + Pick, state-driven toggle (idle-alt interval, attack repeats), deploy
block (frames, strip-parts, recoil frames/step/return, slam), turret bone + aim axis, muzzle bone + offset, hand prop
(bone/material/live rotation), animate-only bones, convert-raw-rig, fix-100×. The rest-pose preview stands the rig on a
true-size tile hex at the in-game surface level (water-blue for Boat-profile pawns, forward arrow = in-game
facing) — the faithful upright/grounded view for animated models — with orbit/pan/deep-zoom and a **Center** re-frame
button, and a **Play clip** row that plays any *baked* role clip (Idle / Movement / After-move / Pre-move / Attack)
**textured and skinned** right there — Pause, scrub, speed. That is the only view in which a subtle motion (a
rolling wheel) is actually visible: the rest pose has no motion at all, and the raw-model ▶ picker is untextured.
The deploy block's **Wheel bones (roll while moving)** + axle axis / loop frames / degrees key that roll into the
`folded` travel stance at conversion time (Movement clip = `folded[1..N]`).
*Writes:* `pack.json`, same `UniversalBaker.BuildAnimated` pipeline. **Deep dive:** [Animated-Models.md](Animated-Models.md), [Factory-Manual.md](Factory-Manual.md) §16.

### Vehicle Lab — `Tools ▸ HAF ▸ Vehicle Lab`

> **TRAILS — a split-trail gun's deploy (2026-08-22, verified in-game).** Mark a gun's arms **Trail** in the part
> dropdown and the rigger gives each a bone **hinged at its body end** plus a second action, **`Deploy`**, that
> swings them open about the vertical — mirrored per side, with the direction *chosen* by testing which way moves
> the spade away from the centreline (so left and right open together whatever way the source faces). Dials:
> **Spread (deg)** and **Deploy frames**. That one rig then feeds the whole state machine: Idle stance
> `Deploy[N..N]` (parked, deployed), Movement `Spin` (wheels rolling, trails at their folded rest), After-move
> `Deploy` (opens on arrival), Pre-move `Deploy[N..0]` (folds before travelling). *Trail* is the artillery term —
> these are the arms of a split-trail carriage, each ending in a spade; **Leg** is deliberately left free for a
> walking mech limb. The **preview picks the clip** when a rig has more than one, so `Spin` and `Deploy` can each
> be judged on the turntable before a bake, and **Checker** paints a high-contrast skin so rotation is visible at
> all — an untextured wheel looks identical spinning or still.

> **THE GUN COMES UP WITH THE TRAILS (2026-08-22).** Two dials in that same section, both only live when parts are
> marked **Gun**:
>
> * **Gun pivot (breech→muzzle)** — where the `Gun` bone's head sits along the assembly, `0` = breech, `1` = muzzle,
>   `0.5` = the bbox centre. That head **is the trunnion**: the bone rotates about its own origin, so at the centre a
>   tube see-saws about its middle and the breech swings down through the carriage. Measured on the M114 (76-unit
>   tube): at `0.4` the muzzle rises 15.5 and the breech drops 10.3, clearing the ground; at the `0.5` default it
>   would be a symmetric ±15.5. `0.5` stays the default so rigs baked before this dial existed regenerate identical.
> * **Gun raise on deploy (deg)** — degrees the gun elevates *inside the `Deploy` clip*, on the same frames as the
>   spread. A towed gun travels with the tube clamped level over its closed trails and only comes up once they are
>   planted, so the raise belongs in the same clip; every use the state machine already makes of `Deploy` then
>   carries it free — unfold raises, `Deploy[N..0]` lowers it back onto the travel lock before the unit rolls,
>   `Deploy[N..N]` holds it up. Axis is the world horizontal perpendicular to the tube and the **sign is chosen**, as
>   the trails' is, by testing which way actually lifts the muzzle.
>
> **THE THREE GUN ROLES — one bone, three meanings (2026-08-22).** **Gun**, **Cradle** and **Muzzle** all weld to the
> single `Gun` bone, because all three elevate together about the trunnions. None of them gets a bone of its own.
> What separates them is what else they mean:
>
> | role | is | in the breech→muzzle span? | when recoil lands |
> |---|---|---|---|
> | **Gun** | the tube itself | **yes** — it *defines* the span | the part that **kicks back** |
> | **Cradle** | the frame holding the tube — trunnions, recoil cylinders, the trough it slides in | **no** | the part that **stays** |
> | **Muzzle** | a separately-modelled brake / flash hider | yes, and it **pins the tip exactly** | rides the tube |
>
> The span exclusion is the point of **Cradle**: a cradle stops well short of the muzzle (26 units short on the
> M114), so folding it in would shrink the span and make *Gun pivot*'s fraction lie about where along the barrel the
> trunnion sits. On a model whose barrel already outreaches its cradle both ways, marking the cradle `Gun` gives a
> byte-identical rig — but the role is still the right home, because it is the split recoil will need.
>
> **Muzzle** buys an **exact tip**: without it the muzzle end is the gun bbox's far extreme, which a wide brake or a
> front bracket skews. That tip is the fire origin, and the run reports it **gun-bone-local, in source units** (scale
> by the bake's `size`) in its DONE status — the value the Animation Lab's *Muzzle offset* dial otherwise costs an
> iterate-and-relaunch loop to find. If the brake is modelled **into** the barrel mesh — as on the M114, where the
> tube tapers to 3.84 wide and then flares back to 5.22 over its last 6 units — there is nothing to mark; skip it.
> Marking the *cradle* as Muzzle is the trap: it pins the tip 26 units short and silently rescales the pivot slider.

> **RECOIL — the one motion that needs the barrel on its OWN bone (2026-08-22).** **Recoil (fraction of tube)** is
> how far the tube kicks back when the gun fires, as a fraction of its own breech→muzzle length — a fraction, so the
> dial means the same distance-relative-to-the-gun on any model at any scale, and measured breech-to-muzzle rather
> than trunnion-to-muzzle so that moving *Gun pivot* doesn't silently change what the number means. **0 = off**, and
> off means the `Barrel` bone is never created: a gun that never recoils costs no bone and regenerates byte-identical
> to before the feature existed (verified — bone lists match the shipped M114 rig exactly).
>
> With it on, the **Gun**-marked parts move onto a new `Barrel` bone, a child of `Gun`, and the **Cradle**-marked
> parts stay behind. *That* is the split the Cradle role was created for. Mark no cradle and the whole assembly
> slides back together, mount and all — the Lab warns about it.
>
> Two design points worth keeping:
>
> * **THE SLIDE IS AN ARC, because a bone's own translation does not render.** This is the whole reason recoil is
>   hard, and it cost a full debugging cycle to re-learn. The clip can bake a perfect translation and the engine's
>   **own** `GetPoseTRS` can decode it perfectly — measured in-game, `SLID 0,3 (0,0,-0.001)->(-0.001,-0.013,-0.301)`,
>   matching the authored frame to three decimals — and **the barrel still will not move a pixel**. Law 5: bone
>   positions are held at BIND and only orientations propagate. But *a position derived from an ANCESTOR's rotation
>   does bake*, by forward kinematics. So the rigger builds a hidden **`RecoilArm`** whose pivot sits far off the
>   bore, hangs `Barrel` under it, and **rotates the arm** by `θ = slide/R`: the tube swings `R·θ` along its own
>   bore — a near-straight slide — while tilting only `θ`. Chain `Root → Gun → RecoilArm → Barrel`, so it still
>   rides the elevation. Measured at peak: −11.87 along the bore (asked 11.96), 3.4 off-bore, 3.0° tilt.
>   **The far-pivot trick is not a legacy workaround for missing translation support — it IS the mechanism.**
>   Residual tilt is the price of faking a slide with an arc, and `R` must stay finite: a pivot at infinity collapses
>   bone chains through float32 cancellation (the old `1e9` sentinel bug).
> * **Recoil lead-in (frames)** holds the gun still before the kick. **Normally leave this at 0.** It exists from
>   the days when the recoil could start before the gun had finished slewing; that turned out to be a race in the
>   plugin — the fire was released 20 ms *before* the strike even set an aim to be aligned with — and is fixed, so
>   the hold is now real. Use a handful of frames only if you deliberately want a beat between the gun settling and
>   the kick. 24 fps, so 24 = one second.
> * **The asymmetry is what sells it, not the distance.** The kick takes the first ~15% of the clip and the ride
>   forward gets the rest, derived from **Recoil frames** rather than given its own dial, so it cannot be set to a
>   shape that reads wrong. At 16 frames that is 2 back, 14 forward.
>
> * **The clip HOLDS the deployed pose — and that is correct.** A role clip poses the whole skeleton, so bones it
>   does not key sit at the reference pose; keying only the barrel fires the gun from its *travel* pose (level tube,
>   folded trails). So the clip carries the end-of-`Deploy` pose flat across its own length. This looks like it
>   should violate *"BIND must equal animation frame 0"* from **The engine contract**, and it does not: that contract
>   governs the **primary (Idle/reference)** clip, which is what defines the reference pose. A **role** clip
>   legitimately encodes a non-identity pose against it — Law 2 is the same rule from the other side (a stance
>   belongs in a role clip, never the primary). The proven M114 confirms it: `deploy_convert`'s `make_role` writes
>   **absolute poses, no delta-rebasing**, and its `recoil` role is authored from `m_home` captured at `deploy_end`.
>
> **Recoil is a TRANSLATION**, and the clip bake is rotation-only by default — tick **Keep bone translations** on the
> entry or the bake discards it and the gun does not move at all. That flag is the whole reason this can be an honest
> slide rather than the far-pivot `RecoilArm` rotation trick `deploy_convert` was forced into. Assign `Recoil` to the
> **Attack clip**.

> **THE FIRING CYCLE — proven numbers (2026-08-22, verified in-game).** *"They allow a reasonable time to aim,
> fire, recover and reload."* The elevation timing that came out of tuning the M114 is now the **shipped default**,
> so a new gun starts here rather than from scratch:
>
> | Animation Lab | value | what it buys |
> |---|---|---|
> | Raise over | **1 s** | the gun is laid onto the target — and the shot WAITS for it |
> | Hold after firing | **1 s** | stays up through the recoil, then settles |
> | Lower over | **1 s** | eases back to the resting angle |
>
> Read as a rhythm: one second up, one second holding the shot, one second down — about three seconds from aim to
> stood-down, which leaves room for the recoil (0.67 s) inside it and reads as a crew working the gun.
> The angles stay per-model, because a howitzer and a tank destroyer want different envelopes: the M114 runs a
> **10°** baked resting angle plus **35°** of runtime lift, topping out at the 45° max-range pose on an 8-tile shot.
>
> `Raise over = 0` remains available and means *track the turn* — the elevation finishes exactly as the slew does,
> and the shot is not delayed. That was the old default; 1 s replaced it because a fixed, deliberate lay reads
> better than one whose speed changes with how far the unit happened to turn.

> **THE M114, END TO END — the worked example (2026-08-22, verified in-game).** A towed howitzer rebuilt from a raw
> Sketchfab GLB on a Vehicle Lab rig, wheels + trails + gun, no converter. The shipped settings:
>
> | where | setting |
> |---|---|
> | **Vehicle Lab** — parts | `l_wheel`, `r_wheel` = **Wheel** · `l_leg`, `r_leg` = **Trail** · `barrel1` (+ breech door, handle, lanyard) = **Gun** · `cannon2` = **Cradle** · `main` = **Body** |
> | **Vehicle Lab** — Spin | 30 frames · −360° · axle **AUTO** |
> | **Vehicle Lab** — Deploy | Spread **28°** · **20** frames · Gun pivot **0.25** · Gun raise on deploy **45°** |
> | **Animation Lab** — clips | Idle/reference `Spin` · Idle stance `Deploy[20..20]` · Movement `Spin` · After-move `Deploy[0..20]` · Pre-move `Deploy[20..0]` |
> | **Animation Lab** — flags | Convert raw rig ✓ · Auto-ground ✓ · Keep bone translations ✓ · Fix 100× ✗ · `gunElevMax` **0** |
>
> The resulting 6-bone rig (`Root, Wheel_00, Wheel_01, Gun, Trail_00, Trail_01`) separates cleanly: `Spin` moves
> **only** the wheels, `Deploy` moves **only** the gun (45°) and the trails (28°). So it drives with the gun clamped
> and the trails folded, folds before it turns, and opens again on arrival.
>
> Two dial choices worth knowing were **not** the measured-accurate ones. The real trunnion is pivot **0.4** — it
> lands within 0.8 units of the cradle's centre — but 45° there drops the breech to `Z 1.6`, on the ground; **0.25**
> keeps the full 45° with ~11 units of breech clearance. And 45° itself is well above an M114's parked elevation.
> Both were chosen by eye, and correctly: *"when you see it you should think, ah that looks like a howitzer."* The bar
> is recognition at map zoom, and it can want a pose exaggerated past the accurate one. Measure to catch what is
> **broken** — geometry through the ground, a bone that never moves, a slice that holds the wrong frame — then let
> the eye pick the look.

> **This does not replace HAF's runtime `gunElevMax`** (Animation Lab ▸ *Gun elevation — max*) — that writes a
> `BoneRotation` slot, a channel the clip pose never touches, so the two **compose**: the clip sets the base firing
> elevation, the runtime adds the per-shot, distance-proportional lift on top of it. Dial `gunElevMax` against the
> raised base, not against level. The hand-converted M114 baked its elevation into the deploy clip too
> (`deployReadyFrame`), but out of necessity — a deploy-converted rig cannot carry authored bone motion at all
> ([Animation-Pitfalls.md](Animation-Pitfalls.md)). Here it is a deliberate two-key authoring on a clean rig.

> The Spin section leads with **Enable spin animation** — the master switch: off, the rig generates with zero
> wheel/rotor rotation and static tracks, keeping every bone, marking and dial for re-enabling (no more
> unmarking every wheel to still a vehicle). With no wheel/rotor/turret marked at all the section reads
> **inert** and grays out — except that *Spin frames* still floors the generated clip length (the one
> spin↔wave coupling, stated in the UI). Tracked vehicles then get **Static tracks (no movement)** — tread
> loops rigid, wheels still spin, far fewer bones — gating the tread speed/detail dials. Recipes that predate
> newer fields load them at defaults (absent JSON = the field's default — the Lab never guesses a config the
> recipe doesn't state) **and say so**: since 2026-08-20 the load status names the features the recipe
> predates ("recipe predates: wave rock, spin switch — loaded as safe defaults; Save to modernize"). The
> **Edit existing** dropdown leads each entry with its last-modified stamp, newest first, so the one you worked on yesterday is
> obvious. The
> recipe round-trip is also gated: `Tools/check_handlists.sh` fails the push if any Recipe field isn't both
> written by Save and restored by Load (the canoe wave-config loss, made structurally impossible). Rig
> options apply through **Generate rig → Bake → mod build**.
Turn a **raw static** vehicle model into a rigged, animated GLB (Root + per-wheel/turret bones, a procedural *Spin*
action, optional rolling tracks and wave-rock) that the animated bake path then consumes — no Blender knowledge needed.
*Key controls:* raw model + output GLB, **Probe parts**, per-part role assignment, use-source-skeleton (for pre-rigged
`SKM_` rips), part-hiding sliders, model roll/pitch/yaw, axle axis, Spin frames/degrees, tread speed/detail, wave-rock
block, **Generate rig**, save/load **recipes**, **Verify** report. *Writes:* the rigged GLB + a recipe JSON (no unit
registry write here — the GLB is then baked via Model Factory/Animation Lab).

**Interior-part detection (strip what's never seen).** The probe classifies every part by **escape-ray sampling**:
a part is *external* if any sampled surface point has a straight, unblocked line to infinity; a part blocked in every
direction from every sample is **interior** — provably invisible (cockpit instruments, engine internals) and safe to
strip. The **Visibility** switch (All / External only / **Interior only**) filters the part list; set it to *Interior
only* and sweep the rows with `I` (**Ignore** = deleted from the output GLB) to reclaim the vertex budget for visible
surfaces — on the RAH-66 that was 47 parts and **28% of the model's vertices**. The verdict is deliberately
conservative: anything that peeks through an opening (canopy, gun bay) counts as external, so "inside-ish" parts
remain a manual judgment call. Classification happens at probe time — re-Probe an older session to populate it.
**Verify** also warns about interior parts *not yet Ignored* (name, role, vert count — clickable), so wasted budget
can't slip through unnoticed.

**Rotorcraft roles (helicopters).** Besides Wheel/Turret/Body there are **Rotor** (`R`) and **Tail rotor** (`L`) roles.
Each rotor group fuses into **one hub bone** (unlike wheels' proximity clusters, so a wide blade disc spins as one):
the **main rotor** pivots on its central hub part and spins about that hub's own *pole-to-pole* axis; the **tail fan**
pivots on the blades' centroid and spins about the axis *perpendicular to the duct ring* (lateral to the boom), with
**Tail-rotor axle** X/Y/Z override plus **yaw/pitch trim sliders** for the final degrees. Rotors are excluded from the
wheels' rolling-contact speed scaling, so main + tail spin at the same rate, and a rotor marking switches the printed
next-step recipe to the rotorcraft bake (**continuous** spin — State-driven OFF — and Auto-ground OFF). Preview aids
for dialing it in: **Pause**, **◀/▶ frame-step**, and a **Level line** (horizontal reference at rotor height).
Rotor bones are authored as **axle frames** (main: local Y = mast; tail fan: local X = the canted fan axle) so the
donor's own clip can drive them — see [Donor-Clip-Flight.md](Donor-Clip-Flight.md). Two workflow notes: Orientation
composes **yaw-first** (Pitch/Roll act on the grid-aligned model as you see it), and the sliders only take effect on
the next **Vehicleize** run — which is also a *mandatory separate step* before an Animation Lab rebake (the Lab
reuses the last rig GLB; skipping Vehicleize bakes the OLD rig).

---

## The injection-axis tools

### District Factory — `Tools ▸ HAF ▸ District Factory`
Bake a custom static district building — imports a model, bakes a **bone-free, auto-leveled FxMesh**, writes the
district registry entry (incl. the baked **albedo GUID** the plugin's texture injection binds) the district repoint
reads. An **embedded preview pane** shows the baked mesh, textured, on a true-size tile hex at the true in-game
surface level — Facing and Position offset preview live; orbit/pan/deep-zoom + a **Center** re-frame button. *Key controls:* district
name + Pick, model file, Size, Rotation offset (stand it up), Facing on tile (turn it), Position offset (place it),
target tris, normals, strip parts, isolate. *Writes:*
`haf_districts.json` (via `DistrictRegistry.Upsert`; mesh via `DistrictBaker.BakeFxMesh`). **Deep dive:**
[District-Visuals.md](District-Visuals.md).

### Prop Lab (attachments) — `Tools ▸ HAF ▸ Prop Lab (attachments)` · *experimental*
Author custom pawn attachments (weapons/gear): static bake → bone-free FxMesh → MeshCollection → FragmentMesh. Includes a
**dump** tool for vanilla fragment GUIDs. *Key controls:* fragment GUID (dump), Size, rotation/position offset, target
tris, borrowed material GUID. *Writes:* `Assets/Databases/haf_props.json` (editor recipe store; runtime reads the baked
GUIDs). **Deep dive:** [Pawn-Props.md](Pawn-Props.md).

### Projectile Lab (munitions) — `Tools ▸ HAF ▸ Projectile Lab (munitions)` · *experimental*
The projectile injection axis — today mostly a **dump/discovery** tool: paste a vanilla projectile GUID to log its FX
GUIDs/speed and walk each `FxEvolverMaterial` for mesh-typed fields, plus baking knobs toward a mesh-particle munition.
The final mesh-swap output field is still being discovered, so its registry/asset output isn't fully wired.
**Deep dive:** [Projectiles.md](Projectiles.md).

### Formation Override — `Tools ▸ HAF ▸ Formation Override`
Link a unit to a custom/vanilla formation (changing displayed pawn count) by serializing the formation's full layout into
config; supports a single-unit link or a full formation replacement (macro). *Key controls:* mode toggle, unit + Pick,
formation + Pick, re-read layout, packing jitter, formation scale + mode, footprint override. *Writes:*
`haf_formations.json` (dummy positions + 6 orientation grids, via `FormationRegistry.Upsert`). **Deep dive:** [Formations.md](Formations.md).

### Resize Lab — `Tools ▸ HAF ▸ Resize Lab`
Runtime per-unit rescaling, **no bake** — rules `{match, scale, era, trueSize, note}` the plugin applies to any unit whose
presentation name contains `match`. *Key controls:* the rule rows (match string, scale, era, note). *Writes:* the
`unitScales` array in `pack.json` (via `ModelRegistry.Save`). **Deep dive:** [Unit-Size.md](Unit-Size.md).

### Global Era Lab — `Tools ▸ HAF ▸ Global Era Lab`
Author how already-resized units rescale as the world ages — a **5×5 grid** of (unit era × current era) multipliers
(defaults 1.0), plus a "formation by size" threshold table. *Key controls:* the era-scale grid cells, formation-threshold
rows (threshold + formation Pick). *Writes:* era/threshold statics into `pack.json` (via `ModelRegistry.SaveStatics`,
preserving on-disk models). **Deep dive:** [Unit-Size.md](Unit-Size.md).

---

## Textures & audio

### Unit Retexture — `Tools ▸ HAF ▸ Unit Retexture`
Reskin an existing unit at runtime **without baking a model** — download its atlas to paint, replace with a PNG, or just
grey/tint it. *Key controls:* pawn description, replacement PNG, brightness (gamma), desaturate, RGB ±255 tint,
download-skin / Apply / Remove. *Writes:* texture-only entries in `pack.json`; skins under the pack's `skins/`.
**Deep dive:** [Textures.md](Textures.md).

### Sound Studio — `Tools ▸ HAF ▸ Sound Studio`
Configure one unit's whole audio profile onto its existing registry entry — **no bake**: silence donor sound, idle growl,
attack roar, death/battle cries, movement WAVs (spool-up → loop → spool-down), and the Wwise engine event. *Key
controls:* pawn Pick, silence-donor, per-clip fields with ▶ preview / ■ stop, start offsets, one-voice radius, engine-event
toggle. *Writes:* the unit's entry in `pack.json`; sound files under the pack's `sounds/`. **Deep dive:**
[Factory-Manual.md](Factory-Manual.md) §13–14.

### Game Sound Lab — `Tools ▸ HAF ▸ Game Sound Lab`
Author **global** audio overrides — silence any vanilla Wwise *event* by name-substring (units / ambient / music / UI).
Distinct from Sound Studio (which is per-model). *Key controls:* override rows (silence substring), category tabs, a
searchable catalog pick list. *Writes:* `haf_sounds.json` (via `SoundOverrideRegistry.Save`). **Deep dive:**
[Game-Sound-Lab.md](Game-Sound-Lab.md).

---

## Utilities, diagnostics & safety

- **Backup & Restore** — `Tools ▸ HAF ▸ Backup and Restore`. A safety net for everything git doesn't track (editor
  scripts, `FactorySource`, baked Resources, ENC databases, `Tools/`, live `BepInEx/config`). Timestamped, additive,
  guarded restore (auto-snapshots current state first). **Deep dive:** [Backup.md](Backup.md).
- **Database Browser** — `Tools ▸ HAF ▸ Database Browser`. Browse/search the game's definition assets (vanilla + mod) with
  an embedded inspector. Read-only viewer; persists UI prefs in EditorPrefs.
- **Tech Tree** — `Tools ▸ HAF ▸ Tech Tree ▸ Open Viewer` (+ `Diagnose Mod Split`, `Dump Data`). View/edit the tech tree;
  in edit mode drag node positions and remove prereqs, committed on Save (edits in place under `Databases/`, else
  copy-on-write into "New Additions"). New-tech creation / +prereq are noted not-yet-implemented.
- **Pawn Rig Dump** — `Tools ▸ HAF ▸ Diagnostics ▸ Pawn Rig Dump`. Dump every matching `PresentationPawnDefinition`'s full
  serialized graph (skeleton, clip collections, assets) to `rig_dump.txt` for rig investigation.
- **Export selected atlas to PNG** — `Tools ▸ HAF ▸ Export selected atlas to PNG`. Blit a selected (DXT1) atlas through a
  linear RT to a readable PNG in `C:/tmp` + log average RGB. (`AtlasDebug.cs`.)
- **Suppressed Console Noise** — `Tools ▸ HAF ▸ Suppressed Console Noise — open/clear log`. An `[InitializeOnLoad]` filter
  that hides known-harmless vanilla SDK console spam and re-logs matches to `Logs/SuppressedConsoleNoise.log`.

### In-game F8 window (runtime — part of the plugin, not `Tools ▸ HAF`)

Press **F8** in a loaded game for HAF's runtime panel. It's trimmed to what a mod author needs while testing a build —
"is HAF working, did it pick up *my* mod, do my models fit, and the live audio/texture/footprint authoring aids":

- **Game-binding health banner** — a red banner at the top naming any reflected game type/member that didn't resolve on
  your game version, so a compatibility break is loud and specific instead of a silent misbehave (from the `GameBinding`
  startup report). **Nothing shown = all bindings resolved OK.**
- **Smoke Test** — one-click **PASS/FAIL**: are the game bindings resolved, did your registry load models, and did
  injection run without errors? The fastest *"why isn't my asset showing?"* check. For districts it reports live
  tiles on **both** render paths (`[1 tile(s) live, 1 scoped]`) and whether your albedo actually landed
  (`1/1 textured`; a give-up after 3 apply errors is a named FAIL, a not-yet-applied atlas a NOTE to re-run shortly).
  Since 2026-08-21 it also reads **what the engine is rendering**, not just what the registry says: every live pawn
  of yours must sit on *your* skeleton (`N live pawn(s) on our skeletons` — a unit wearing its donor's skin is a named
  FAIL), the pose hook must have touched every entry with live pawns within 5 s (`[pose hook fresh]`), and the
  sub-pawn walk that feeds engine audio + sub-pawn visuals is re-audited against a full scene scan on the spot
  (`sub-pawn walk 6/6`; a miss names the sub-pawn). Nothing runs per frame.
  **Two tiers:** the **load tier** runs by itself once per session, on the first frame after the loading screen
  hides (`SmokeOnLoad = true`, the default — bindings, registry, roles, assets, sounds, files, GPU budget, district
  tiles; a few ms at a moment you're already waiting), tagged `[load]` in the log, the F8 panel and
  `haf_smoke_report.txt`. The **full tier** is the button, now labelled **Smoke Test (full — adds the live-pawn checks)**: load + the live-pawn checks, tagged `[full]`. The panel shows whichever ran last.
- **GPU mesh buffer (live)** — per-layer vertex/index/mesh fill, so you can see whether your models fit the shared budget
  ([Vertex-Budget.md](Vertex-Budget.md)); **Shift+F8** also logs it.
- **HAF per-frame cost** — what the plugin itself costs each frame, averaged over 5 s: total µs/frame and percent of the
  frame, the pose hook split into vanilla pawns and yours (ns per pawn-add), and the six most expensive internal
  buckets. The same line is written to the log once a minute as `[FrameCost]`. If a number here grows after you add
  units or districts, the bucket names where. (`DistrictDebug = true` in the config's `[Debug]` section adds ~40 ms/frame for the first
  seconds of every load — a diagnostic, keep it off for play.)
- **Dump Atlases** — dump a unit's atlas to paint (the Unit Retexture workflow; [Textures.md](Textures.md)).
- **Game Sound Lab** — **Audio Trace** (live Wwise event trace), **Dump Sound Catalog** (list every event name), and
  **Play Event / Stop** (audition an event before you override it; [Game-Sound-Lab.md](Game-Sound-Lab.md)).
- **Live readouts** — **Unit resize** (Resize Lab × Global Era grid) and the **strategic-footprint flatten-height**
  override ([Unit-Size.md](Unit-Size.md), [District-Dedicated-Visual.md](District-Dedicated-Visual.md)).

## Tests — `Tools ▸ HAF ▸ Bake Tests…`

One window is the whole in-editor test suite (it replaced seven bare menu items on 2026-08-20). Every bake
integration test is a row with a plain-language explanation of what it tests and what it costs, a checkbox, and
Quick/Everything presets; one Run button executes the selection and each run writes a durable report to
`Logs/haf_bake_tests_report.txt`, with per-row PASS/FAIL in the window (failures unfold their detail). All tests are
non-destructive (throwaway resource names — your assets and registry are untouched).

**Fire and forget** (since 2026-08-22): a run finishes on its own — alt-tab away, minimise Unity, walk off. It used to
be driven by editor ticks, which Unity stops delivering when its window loses OS focus, so a 28-minute suite silently
stalled the moment you looked at something else. The run is now one synchronous pass behind a cancellable progress
bar, and the report is **rewritten after every test**, so even a cancelled or interrupted run leaves a record of
everything that finished. The rows:

- **Smoke** (one per bake path / ALL models) — re-bakes models and asserts the output assets exist and aren't stubs.
- **Features** (synthetic / Blender + animated) — bakes fixtures toggling one baker option at a time and asserts a
  per-option invariant.
- **Conversion** (litmus rig / real registry rigs / deploy golden diff) — asserts raw-rig conversion invariants
  (scale==1, parent<child index, rotation-only curves) and the deploy-convert golden bone snapshots.

Run before committing baker/pipeline changes. See [Testing.md](Testing.md) and [Factory-Manual.md](Factory-Manual.md) §11.

## Under the hood (non-window)

- **`UniversalBaker.cs`** — the bake core: model file + `BakeConfig` knobs → Amplitude `Skeleton` (+ atlas), static or
  animated. Called by the Factory, Animation Lab, and the tests (via `ConfigFor`, the one shared config path).
- **`DistrictBaker.cs`** — wraps a baked mesh as a bone-free district `FxMesh` (`BakeFxMesh`; also used by Prop Lab). One
  hand-driven menu: `Tools ▸ HAF ▸ District ▸ 1. Bake District FxMesh` (superseded by District Factory).
- **Registries** — `ModelRegistry` (master; `pack.json` + static `UnitScales`/`EraGrid`/`FormationThresholds`;
  corruption-guarded — won't overwrite an unparsable file), `DistrictRegistry`, `FormationRegistry`, `SoundOverrideRegistry`.
- **Dialogs** — `ClipRangeDialog` (scrub a clip → `clip[start..end/N]`), `SocketBonesDialog` (donor hardpoint → your bone
  map → `socketBones`), `StripPartsDialog` (pick parts → `deployStripExtra` CSV).
