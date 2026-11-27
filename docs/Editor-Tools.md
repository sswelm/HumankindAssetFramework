# Editor / Authoring Tools reference

The **HAF Authoring Tools** are a suite of Unity editor windows under **`Tools ▸ HAF`**. They are the *bake* half of HAF —
you author custom content here, and the runtime plugin injects it in-game; the two halves talk only through the JSON pack
registry. (The editor source lives in this repo under [`editor/`](https://github.com/sswelm/HumankindAssetFramework/tree/master/editor)
— the only copy. It moved out of the ENCReload Unity project on 2026-08-24 so the framework owns both its halves;
ENCReload now consumes it as a package like anyone else.)

This page is the **map** — every tool, its exact menu path, what it does, and what it writes. For the deep, field-by-field
workflows, follow the *Deep dive* links; this reference deliberately doesn't duplicate them.

## Installing them

`Window ▸ Package Manager` → `+` → `Add package from git URL…`:

```
https://github.com/sswelm/HumankindAssetFramework.git?path=/editor
```

The install is inert by design: automatic backups, the asset-delete guard and the console filter all default **off**
in an installed package and on in the tools' own development checkout (`HafPackageContext` decides by *how* the
package is installed — git/registry is a consumer install, `file:`/embedded is the working copy). An installed
package owns its **own pack source** (`Assets/Pack/<YourPackName>/pack.json`, starting empty) and deploys a derived copy
under `BepInEx/config/haf_packs/`. It also ships the Blender helper scripts and the `glbconv` importer inside (`Tools~`),
so a static `.glb` bake needs nothing installed beyond the package.
See [Getting-Started.md](Getting-Started.md) for the ordered path and [Installation.md](Installation.md) for
install, verification and updating.

### Check for Updates — `Tools ▸ HAF ▸ Check for Updates…`
Unity never reports updates for a git package, so the tools do it themselves: this asks the package's own
repository and either answers *"up to date"* or offers **Update now**, which hands the fetch to Package Manager
(the same operation as its Update button). An installed package also checks once a day and prints one console line
when a release exists — read-only, nothing sent, off via EditorPrefs `HAF.UpdateCheck = false`. Release notes are
behind the package's *View changelog* link. Details: [Installation.md](Installation.md#updating-the-tools).

## Where the tools write — the registries

Every registry is **ONE file**: the git-tracked project file is the source the editor reads and writes; the copy in the
game's `BepInEx/config` (auto-detected by `ModelRegistry.ConfigDir`) is a **build artifact** regenerated on every Save
(units since 2026-08-19, districts and formations since 2026-08-20 via the shared `SingleSourceRegistry` engine — with
pinpointed corruption and one-click recovery in each window). The runtime plugin reads the deployed copies on launch.

| Registry | Live path | Repo backup | Written by |
|---|---|---|---|
| **Model registry** (`pack.json`) | `config/haf_packs/<PackName>/pack.json` | `Assets/Pack/<PackName>/pack.json` | Model Factory, Animation Lab, Resize Lab, Unit Retexture, Sound Studio, Global Era Lab |
| **Districts** (`haf_districts.json`) | `config/haf_districts.json` (artifact) | `Assets/Databases/haf_districts.backup.json` (THE source — historical name) | District Factory |
| **Formations** (`haf_formations.json`) | `config/haf_formations.json` (artifact) | `Assets/Databases/haf_formations.backup.json` (THE source — historical name) | Formation Override |
| **Sound overrides** (`haf_sounds.json`) | `config/haf_sounds.json` | project backup | Game Sound Lab |
| **Technologies that require all prerequisites** (`haf_tech_requires_all.json`) | `config/haf_tech_requires_all.json` (artifact) | `Assets/Databases/haf_tech_requires_all.backup.json` (THE source) | Tech Requires All |
| **Props** (`haf_props.json`) | `Assets/Databases/haf_props.json` | — | Prop Lab (editor-side recipe store; the runtime reads the baked GUIDs, not this file) |

> `<PackName>` is `ENCReload` in the home project and is derived from the host project by `HafPackageContext` in a
> guest package install. `pack.json` is that project's own **pack** (see [Multi-Mod.md](Multi-Mod.md)). Older docs/config call the base model file
> `haf_models.json` colloquially — the real shipped filename is `pack.json` in the pack folder. Baked assets
> (`<name>_ModelMesh` / `_Skeleton` / `_Atlas`) go under `Assets/Resources`.

---

## Core model authoring

### Model Factory — `Tools ▸ HAF ▸ Model Factory`
Author a static model (or the *model half* of an animated one): pick/create a resource, a target pawn, and a model file;
tune geometry and shading; **Bake** → produces a `Skeleton` + `Atlas` and a registry entry. *Key controls:* target pawn,
model file, Size, Strip parts, reduce-to-tris, height-UVs, winding fix, albedo brightness/saturation,
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
> all — an untextured wheel looks identical spinning or still. Checker is a deliberate material override: this preview
> validates moving parts, pivots, and axes, **not** the final atlas or in-game texture.

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
> **THE THREE GUN ROLES — one elevation assembly, three meanings (2026-08-22).** With recoil off, **Gun**, **Cradle**
> and **Muzzle** all weld to the single `Gun` bone because they elevate together about the trunnions. With recoil on,
> the generator adds a `Barrel` child: Gun + Muzzle ride it while Cradle stays on `Gun`. None of the three roles creates
> an independent bone by itself. What separates them is what else they mean:
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
> | **Animation Lab** — flags | Convert raw rig ✓ · Keep bone translations ✓ · Fix 100× ✗ · `gunElevMax` **0** |
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

> **Checking the axis without a relaunch (2026-09-26):** with an elevation dialled, the Animation Lab's preview row
> gains an **Elevation** slider that raises the gun from resting to the max on the bone and axis the game will use
> (Turret bone if set, else Gun bone; the runtime's negated angle), composed onto the playing clip. Sideways swing =
> wrong axis; a dip = flip the sign of the max. It loads the first baked role when no clip is in the preview. A
> **Pivot** slider under the *Elevation axis* setting previews the elevation about any point of the breech→muzzle
> span and names the number to dial into the Vehicle Lab's *Gun pivot* — the game turns the bone about its origin,
> which only Generate places, so the preview is the finder and Generate + Bake is the fix. The span is the one the
> **rig measured** at Generate (`VEHICLE GUNSPAN`, kept beside the output GLB as `<glb>.gun.txt`): the tube alone,
> brake-pinned at the tip, cradle excluded, so the fraction shown is the fraction the dial takes. Without that file
> — an older rig, or a hand-made GLB — the slider says so instead of guessing. With a Turret bone set it says so
> too: the elevation turns the turret, and *Gun pivot* cannot move it.

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
next-step recipe to the rotorcraft bake (**continuous** spin — State-driven OFF). Preview aids
for dialing it in: **Pause**, **◀/▶ frame-step**, and a **Level line** (horizontal reference at rotor height).
Rotor bones are authored as **axle frames** (main: local Y = mast; tail fan: local X = the canted fan axle) so the
donor's own clip can drive them — see [Donor-Clip-Flight.md](Donor-Clip-Flight.md). Two workflow notes: Orientation
composes **yaw-first** (Pitch/Roll act on the grid-aligned model as you see it), and the sliders only take effect on
the next **Generate rig** run — which is also a *mandatory separate step* before an Animation Lab rebake (the Lab
reuses the last rig GLB; skipping Generate rig bakes the OLD rig).

**Materials survive here, but are proven later.** Generate rig joins the classified parts into a skinned mesh while
preserving the source's material slots. The HAF atlas is not created until Model Factory/Animation Lab bakes that GLB.
For multi-material vehicles, start that bake with **Material mode Auto/Multi** and **Reduce to ~tris = 0**; only lower
the triangle ceiling after the post-Bake mapping is correct. See [Textures.md](Textures.md) for the failure catalog.

New to the workflow? Follow [Vehicle Lab quickstart](Vehicle-Lab-Quickstart.md) for the complete raw-model → rig →
animated bake → game sequence.

---

### 3ds Max files (`.max`) — read natively, converted at the door

The **Model Cutter**, the **Model Fuser**, the **Vehicle Lab** (both source fields) and the **Model Factory** take a
`.max` file in their file pickers. Picking one converts it: the reader (`editor/MaxDocument.cs`, `MaxScene.cs`,
`MaxModel.cs` — C#, no 3ds Max and no Blender involved) writes `name_max.glb` beside the file, and the tool works on
that copy from then on. A report says what was read and what was not: the dialog shows it cut to a length (1,800
characters, the first twelve unread parts), the Console holds it whole.

Why a copy: nothing else in the pipeline can open a `.max` (stock Blender and Unity cannot), so every later step —
including the Blender fallbacks of the in-process routes — needs an ordinary GLB.

- **A `.max` path typed into the field** shows a **Convert .max** button under it, when the file exists. Until it is
  pressed the tool has no model: the Vehicle Lab's Probe and Generate say so by name; the Cutter and the Fuser answer
  "Input is not a GLB 2.0 file", the Factory "unsupported model format: .max".
- **The copy is always written beside the `.max`.** A folder that cannot be written to (a read-only share) cannot
  hold it: the conversion fails, and the dialog names where the copy would go. Copy the model's folder somewhere
  writable first.
- **Picking the same `.max` again** converts it again and compares: when the copy already holds exactly those bytes it
  is left untouched and no report is shown. Another tool that has the copy open sees no change. The window you pick
  in treats it as a new pick: the Cutter and the Fuser clear their list (and their unsaved marks), the Vehicle Lab
  keeps its marked parts until the next Probe.
- **An existing `name_max.glb`** asks before replacement whenever the conversion would change its bytes. Choose
  **Cancel** to keep the existing file and its edits.
- **A file with no part the reader can read** is refused with the reasons, and no copy is written.

**What is read** (the four sample models — two armoured cars, a torpedo boat, a stealth ship: 257 parts, 244,337
visible triangles — hold most of it; what only files built by the tests hold is said):

- **Parts that store their mesh** — Editable Mesh, and the older MeshObject (all 152 parts of the torpedo boat):
  vertices, triangles, smoothing groups (written as normals), material ids, one UV channel.
- **UVs** are written on the faces that show a picture, and on the faces whose picture is missing or unreadable, so
  that it can be put on later (a plain repeating picture then needs no second conversion; a decal does, its UVs are
  fitted to its canvas only when the picture is there). Faces that show no picture — a material without a diffuse
  picture, faces beside a decal — are written without UVs, and the report names their parts. The reason is the
  Model Factory's converter, which shifts every UV of a model by one whole-tile offset taken from all of them: the
  stealth ship's decals use 0.1 to 1.0, and its plain hull's unused UVs run from −102 to 127.
- **Names** are written without the spaces some files have at their ends: the tools key their marks on names and trim
  them.
- **Each part's place:** position, rotation and scale, parents, the object offset, and the scale's axis system (a part
  scaled along its parent's axes after it was turned). A mirrored part is re-wound so its faces still face out. Axes
  are converted from 3ds Max's Z-up to a GLB's Y-up; units stay as the file has them.
- **Standard and Multi/Sub-Object materials:** the diffuse colour, the two-sided switch, and the diffuse slot's
  picture: JPEG and TGA in the samples, PNG and BMP in built files only. A material id counts the Multi/Sub-Object's
  slots, the empty ones too. Every material is written matte (metallic 0, roughness 0.5): shininess, specular colour
  and self-illumination are not read.
- **A picture's own alpha** works like a mask: where the picture is not opaque the material's colour shows. A PNG's
  alpha counts; a 32-bit TGA's counts when its header declares alpha bits and it is not zero everywhere; a BMP has
  none. That is taken to be 3ds Max's default for a bitmap ("Image Alpha"), and it is what the torpedo boat's hull
  needs — its picture is a render of the hull's side with everything around it transparent and black, and 64 % of the
  surface that uses the material lands on those pixels. It was **not checked against 3ds Max**; the report names
  every picture it applies to, with the share of the picture that is not opaque.
- **Where pictures are looked for:** beside the `.max`, then in every folder directly under it, then one folder up. A
  picture taken from another folder than the file's own is named in the report. A file is a picture by its bytes,
  not by its name: one named `.png` that is none is left out by name.
- **Decals:** a picture behind a mask, or one with tiling switched off, is shown once in 3ds Max with the plain colour
  around it. The reader composes it onto a larger canvas of that colour and scales the UVs to match, so nothing
  downstream has to honour a sampler setting. The padding is at most four pictures to each side and 8,192 pixels a
  side, and the canvas 67 megapixels in all; faces that reach further are held at its edge, which draws the decal
  larger on them, and the report says so. The faces beside the picture get a second material, `<name>_plain`; the
  composed pictures are named `<file>_decal` or `<file>_mixed`.
- **A map's amount, offset and tiling:** a map below 100 % is mixed with the colour. Offset and tiling are folded into
  the UVs by the documented rule, which **no sample confirms** (one material of the Rolls-Royce uses it); the report
  asks you to check each such material. A map's angle is not applied, and a mirrored map is written as plain tiling
  (the mirror switches are read where 3ds Max's own names put them; no sample sets one); both are named.

**What is not read.**

- **Named in the report, part by part or material by material:**
  - **Modifiers** (the count and the parts). A `.max` stores the base object and each modifier's settings, not the
    result; only 3ds Max can evaluate them. The part is read as it was before the modifier. A Skin is a modifier and
    is counted with the others.
  - **Parametric objects** (Box, Cylinder, …): stored as their parameters, not as a mesh. **Editable Poly:** no
    sample file yet to hold a reader to.
  - **Parts that cannot be written:** no visible faces, a transform that flattens them or is no number, a place past
    what a GLB's 32-bit float holds (their own or a node's above them), more than 16 modifier stacks on each other.
  - **Parts that may stand in the wrong place:** a part whose own controller, or that of the nearest node above it,
    is not read or names nothing. The report opens with a WARNING that counts them. A list controller gives its
    first entry; a list of more than one entry is counted here too.
  - **Maps in every slot but diffuse,** by slot. An opacity map is the one that changes the shape the eye sees — it
    cuts holes in 3ds Max, here the faces are written whole — and is named in those words. Two materials of the
    Rolls-Royce have one: its "LC 1507" lettering and a second plate come out as solid slabs.
  - **Procedural maps; a transparent material** (written opaque); **parts without a material; UVs that could not be
    read or are not written; a picture that could not be found, read or decoded** (a TGA or BMP past 64 megapixels
    among them: the plain colour instead).
- **Counted only:** hidden faces (left out); references to objects the file does not have (read as empty); and
  lights, cameras and helpers by the name of their class (the samples' camera is of a class named "Target", so it is
  counted among the targets).
- **Not detected, so the report cannot say it:**
  - **Animation.** A part stands where the value stored in each of its controllers puts it; whether a controller also
    holds keys is not looked at.
  - **Whether an object is hidden.** Hidden faces are left out; a hidden object is converted like any other.
  - **UV channels beyond the first, the third UV coordinate, and vertex colours.**
- A node keeps its name, its parent and the **place** of its pivot, not its rotation or scale: a part's vertices are
  written in world axes relative to that pivot.

**Which controllers are read:** a Position/Rotation/Scale controller whose three parts store their value in the chunk
the samples use (Bezier position, TCB rotation and Bezier scale there). A controller of another class is expected to end
in the WARNING above; no sample part has one (each sample's lights and camera use Look At, and carry no mesh).

**Limits.** A file past one of these is refused, with the limit named: the file 1 GB; a stream 2,000,000 chunks, or
chunks nested deeper than 64; a packed stream that unpacks past 200 times its packed size (always allowed 64 MB, never
more than 1 GB); one object 1,000,000 references; the model 10,000,000 triangles, every instance of a mesh counted.

**Tested on:** all four files have one layout: compound-file version 3, 512-byte sectors, a `ClassDirectory2` stream,
nothing packed — by the public descriptions, the layout of the 3ds Max 2 and 3 era. The reader does not read a
version number. A file of a newer version may differ in four ways, none of them seen in a real file here:

- **Packed streams** (gzip): unpacked. Exercised by files the tests build, and in the review of 2026-11-21 by two of
  the sample files rewritten with Windows' own compound-file writer: packed, with 4,096-byte sectors, and — in
  512-byte sectors — with an allocation table past the header's 109 entries.
- **Other controller classes** (Position XYZ, Euler XYZ): expected to end in the WARNING, with the parts unplaced.
- **Editable Poly** instead of Editable Mesh: not read. If no part of the file holds a stored mesh the file is
  refused; the refusal names the first six parts.
- **A Multi/Sub-Object that keeps a parameter block in its first slot** (later versions are said to): every material
  id would then pick its neighbour's material, and the first would be named as "a material of class … is not read".
  Likewise, an id is passed on to a Multi/Sub-Object inside another as 0; whether 3ds Max does so was not checked.

Collapse the model to Editable Mesh in 3ds Max before saving when you can.

**Checks:** `tools/max_drill.sh` and, inside a Unity editor, `tools/max_unity_drill.sh` (see [Testing.md](Testing.md)).
**Format sources:** the files themselves, read with a Python probe first, and the public descriptions of the format; two
open-source readers served as references for the chunk ids — `io_scene_max`, an importer for Blender (GPL), and Ryzom
Core's `pipeline_max` (AGPL). Neither was run and neither's code was copied; HAF is MIT.

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
searchable catalog pick list. *Writes:* the git-tracked `Assets/Databases/haf_sounds.backup.json` (the source) and
deploys `haf_sounds.json` for the game — only over the version the Lab loaded (`SoundOverrideRegistry.Save`, on the
shared `SingleSourceRegistry` engine). A broken source gets the same one-click recovery as the District and Formation
windows. **Deep dive:**
[Game-Sound-Lab.md](Game-Sound-Lab.md).

---

## Utilities, diagnostics & safety

- **Split disconnected GLB parts** — `Tools ▸ HAF ▸ Model Tools ▸ Split disconnected GLB parts…`. Pick a `.glb`
  and write a new `_split_parts.glb` in which every unattached geometry island is a separately selectable child
  node. The source is protected from overwrite, triangle totals are verified, and existing materials, transforms,
  skins, animations, textures and vertex data are retained. Duplicate UV/normal seam vertices remain attached.
- **Compact a GLB** — `Tools ▸ HAF ▸ Model Tools ▸ Compact a GLB (leave out what no part uses)…`. Writes a copy of a
  `.glb` without the geometry no part can reach any more: what Splits, Cuts, Fuses and Deletes took off the nodes of a
  file written before the Workshop compacted its own outputs (a fused ship can be three quarters dead weight). Every
  part reads exactly as before; nodes keep their indices; the source is not changed. Every Cutter/Fuser output is
  compacted as it is written, so this is only for files made earlier.
- **Model Cutter and Model Fuser** — `Tools ▸ HAF ▸ Model Cutter` and `Tools ▸ HAF ▸ Model Fuser`, two windows on one
  implementation (0.5.7: the one Model Workshop had grown cluttered). Both share the file pickers, the probe, the filtered
  part list, the preview, the mirror finder and the keyboard sweep; the Cutter shows the Split checkboxes, the deletion marks and the plane cut,
  the Fuser the ⊕ letters, the weld and Fuse. The Cutter is the *selective* version of the batch splitter above: **Probe parts**
  lists every mesh-carrying node with its triangle and disconnected-island counts; check just the parts whose
  islands you need to separate (floating junk welded into a hull part, say) and **Split** writes a new GLB where
  only those gain `_Part_NNN` children — same lossless method, so the rest of the model is byte-identical. Feed
  the output to the Vehicle Lab and mark the junk islands Ignore. Splitting a many-island part (ropes, rigging)
  explodes the part list; usually leave those whole — or raise the **Merge closer than (%)** slider, which counts
  near islands as one part (rotation-invariant since 0.5.6: diagonal dashed lines gate correctly too). It takes
  decimals: type 0.3 (or 0,3) in the box and press Enter, or drag — the slider is square-root scaled, so 0–1 % fills
  about a third of its track, and it snaps to 0.01 %. The
  **Output GLB** auto-follows the source file until you edit it, and changing the source — typed or browsed —
  resets the probe so a stale part list can never split the wrong file. The preview is built straight from the GLB
  (0.5.7: no Blender export, no FBX import — a 214 MB ship probes in about ten seconds), one object per node, so a
  row lights exactly its own part even in a file whose nodes all share one name. The preview is a MIRROR image of the
  model (glTF and Unity disagree on handedness, and correcting it would invert every face), so screen-left is the
  file's starboard — tick **Un-mirror** above the preview to see it the right way round (the geometry is flipped back,
  so nothing turns see-through); the side sliders and the mirror finder work in file coordinates and are unaffected
  either way. A model whose
  meshes would need more than 2 GB is listed and filtered as usual but not drawn.
  **Find the mirror of …** (0.5.7): with a row highlighted, the button finds the part on the other side of the
  centreline whose bounding box is this one's reflected (within 3 % of the part's size — triangle counts may differ,
  the two sides of a ship are often remodelled rather than instanced), highlights it and scrolls to it; press a letter
  to put it in the same fuse group. A part sitting on the centreline reports itself as its own mirror.
  **Plane cut** (0.5.7) handles the part island splitting *can't* touch: a CONNECTED mesh that needs two roles —
  the ocean liner's hull welded to its deck. Select the part's row, press **Plane cut**: the preview swaps to
  just that part, two-colored **from the source file's own bytes** — the yellow triangles are exactly what
  becomes `_CutA`, grey becomes `_CutB`. Two **cut rules**: *Flat plane* (pick the axis — Y is "up" in most
  GLBs, so Y = a horizontal deck-off-hull slice; X/Z are vertical — and slide the position) and *Horizontal
  surfaces* (a triangle goes to `_CutA` when its FACE lies flatter than the **Max tilt** dial — the deck
  separates from the bow plating by orientation, where no flat plane can trace the boundary; the **Only above**
  floor keeps the equally-horizontal hull BOTTOM out of the deck piece). Whole triangles either way: nothing is
  sliced, vertex data stays byte-identical, the boundary follows the existing triangulation (invisible once the
  two halves get their own reduce dials or roles). **Cut** writes the output GLB; for a second cut (the bow off
  the deck piece, say) point Source GLB at that output and re-Probe.
  **Fuse** (0.5.7) is the opposite of Split, for the opposite disease: a hull authored as dozens of separate
  plates (the Teutonic — 861 islands over four parts) renders see-through with a hole in its side, because no
  per-plate facing test can see that a 1,609-face region is glued on the wrong way round along a 26-edge seam,
  and it cannot be reduced either: plates share no vertices, so any collapse opens gaps. Give the plates the same **⊕ letter** (the popup on each row, or keys **A–Z** on the highlighted row;
  ↑/↓ move the highlight, 0 clears; several letters = several shells), leave
  **Fuse — weld seams closer than** at **0** (coincident positions, within float rounding — the Teutonic's plates already touch,
  and that alone found its hole; raise it only for plates that leave gaps, knowing that every triangle smaller
  than the distance collapses: at 0.5‰ the Teutonic lost 1,564 rivet-sized faces and its hull island broke apart.
  The distance is a share of each **group's** own length, the dial is below the preview and keeps its value between
  ships; above 0 a warning box under it says so, and after a fuse the status leads with the groups that lost faces
  or surface compared with weld 0 (the six worst; each group has its own warning in the console and the report) —
  at 2.288‰ four of the RSS Centurion's 23 groups, the anchor chains among them, kept none of their surface),
  tick **Check mirrored parts** if one side of a symmetric hull comes out see-through (0.5.7: some files store their
  mirrored parts already facing outward, and the glTF reversal then turns them inward; the fuse checks them against
  the plain parts they touch and acts only on a clear verdict; with the box off, the status after a fuse leads with
  the groups whose mirrored parts are still reversed in the output and how many faces, and the Fuser's own preview will not show it,
  because it draws the source as stored),
  tick **Leave out faces no camera can see** to drop what lies inside the model (0.5.7, off by default): after the fuse
  and the deletions, every face that no camera of the game sees from **either** side is left out — gun bodies under the
  deck, the inner skin of a hull, undersides below it. The cameras are the game's: its camera tilts from 28 degrees
  below the horizon fully zoomed in to 80 zoomed out (`DefaultCameraProfile` in the game's data), so the pass looks
  from straight above and at 65, 40 and 28 degrees (larger faces from more directions and more of their points). The
  camera has perspective, so the top edge of a fully zoomed-in screen looks along about 10 degrees; that edge is the
  one place a removed face can show. A face stops a camera's view only
  where it shows its front, as under back-face culling; glass and cut-outs hide nothing and are kept. A face whose
  back shows stays, so the Lab's inside-out fix, Flip, TwoSided and double-sided output turn a removed face into a
  hole no more often than the front rule misses one (rendered with both sides drawn, the first cut showed no sky). Rays from a few points along a few directions miss what a camera sees through a slit or past a lip, so
  two rings of faces around everything that shows stay as well (the rim). The Fuser reads the Vehicle Lab's saved records of the output — every
  recipe that loads this file as its model or as its second model (under the names without the Lab's `B_`), whatever
  the recipe is named, and the role lists that recipe's Generate wrote; a part with no record takes a marked role of
  its parent name as the Lab's Probe does (`Gun_Part_003` takes `Gun`'s), else the role the Lab would guess from its
  name (a "Propeller" is a rotor). It does not run, and says why,
  when such a recipe cannot be read, rigs the model on its own skeleton (the fast path), or turns the model
  (Orientation, or the second model's Rotation): a part the Lab ignores hides nothing; a part that moves or changes
  facing (wheels, turrets, guns, rotors, trails, muzzles, cradles, oars, sails, flags, rudders, chains, caterpillars,
  Flip, TwoSided, Edgecase), a placed part, a skinned or morphing mesh hides nothing and loses nothing; where the
  recipe and the lists disagree the pass takes the careful reading; a node no scene reaches is not drawn. The Factory bakes the unit to a triangle budget, so what goes here is detail the visible ship gets back:
  on the Saleg's Revenge 1,369,211 of the 2,400,377 faces of the parts that stand still (57 %), in about 100 s in the
  editor (Unity's Mono; 43 s on .NET Framework, the progress bar waits at the end meanwhile), and the fused file from
  97.6 to 60.1 MB. Rendered before and after from 144 directions the pass does not ask (16 round at nine heights from
  10 to 85 degrees, 640 x 640, back faces culled, 4.25 million ship pixels): from 28 degrees up 343 pixels changed;
  at 20 degrees 163 and at 10 degrees 993 — the top of a fully zoomed-in screen — and 106 of all the changed pixels
  show sky. The rest show a face a little behind the one left out (median 4.5 % of the model's radius). No sampled
  rule reaches zero. Every part that could hide a face (each still, drawn part with an opaque face) is listed beside the
  output (`<output>.unseen.txt`); when one of them is ignored, moving or placed in the Lab later, or the model is
  turned, Generate says so and asks before it runs (save the recipe and fuse again). The pose is the file's: the water line is not
  modelled, and a part the Lab moves without a role that says so is not known to move,
  leave **Fuse — groups at once** at **0** (auto: half the logical processors; measured on a 22-group ship, 8 at once is
  the sweet spot on 16 threads and more is no faster, since the largest group's weld slows as the others compete for
  memory bandwidth — lower it when the editor must stay responsive during a fuse or memory is short; the output never
  depends on it),
  press **Fuse … into one shell each**: every group becomes ONE mesh in the output GLB, their seam vertices
  welded (a UV seam or a hard edge keeps its own vertex; connectivity is by position regardless), the winding
  made **consistent by majority**
  (and a LEVEL sheet above the hull's floor — a deck — is taken to face up rather than judged against the hull's
  belly line, which a sailing rig pushes above the decks; 0.5.7)
  across each welded *sheet* — the faces reachable through two-face edges; an edge shared by three faces, a deck
  meeting the hull side mid-plate, is a junction no two faces own, so a hull-and-decks island holds several sheets
  and each is judged on its own (a sheet whose winding cannot be made consistent at all — a propeller blade with
  fins, a twisted rim — is left as authored and named in the report, with the part pair it fails at), and direction judged once per sheet where it can be — a closed shell, a thin solid or a convex
  plating region by its signed volume about its own centroid (wound inward = reversed whole; trusted only where the
  per-face volumes agree and there is real thickness — and never a *deck*: a level sheet already facing up above the
  hull's floor, or facing up on balance above the belly line, is a deck however negative its volume reads, because a
  deck with a rim is a tray and a tray seen from above is the same surface as a box wound inside out), a *flat* sheet
  (deck, bulwark) by the inside-out score against the hull's belly axis — except an *underside*: a down-facing
  sheet above the belly line whose faces have something close above them and, at least three times as far below,
  either nothing at all or a surface that is itself above the belly line (HMS Svea's fighting top, whose flared
  underside the score called inside-out and turned up, see-through from below; a deck wound the wrong way has the
  sky above it, and the room under a deck ends on the hull's bottom, which is low by construction). Only distances
  are read, never which way the thing above or below faces: a group's own faces carry whatever the passes have
  already done to them, and the source's own winding is what is in question. A part *double-sided by duplication* (every
  face twice, wound both ways) is kept as two copies through the weld, so each is judged and emitted whole — welded
  together they made four-face edges everywhere and an importer then dropped one copy at random. **Hidden twins are left out** (0.5.7, at weld 0 only; with a non-zero weld the report says the pass was not run): where a face lies
  exactly on a face wound the other way — a ceiling under the deck laid on it, the inner copy of a doubled mast — and no
  camera above the horizon can see its front while the other side is seen, the fused shell carries the visible face only.
  The game drew the hidden one through the visible one (the RSS Centurion's upper deck, in grey patches). A pair that
  shows on both sides (a flag, a boat's shell with a painted inside) keeps both, and so does a pair nothing sees.
  The pass preserves blended and alpha-masked geometry and looks through it when checking visibility; an opaque face
  lying back to back on a pane of glass is still left out when no camera sees it.
  The fused part then has FEWER triangles than its sources: the status line ends with
  `N hidden twin face(s) left out`, and the report's `hidden twins:` line names the parts they came from and what was kept.
  It also follows that the underside of a level sheet doubled in the open (an awning) is left out. Faces lying on each
  other and wound the SAME way are a different defect and are not touched. Every sheet's report line now carries its
  levelness (`level +1.00` = every face up) and height, which is what makes a wrong verdict traceable. Welded vertices share one position even where a UV seam or hard edge keeps them separate;
  vertex colours, further UV sets and custom attributes ride along and keep their own seams too (tangents are the
  one thing dropped — the importer recomputes them from the new winding); and a mirrored instance (a node with negative scale, the usual "other half" of a symmetric hull) is read with the
  winding it renders with, its authored normals taking the sign of the final winding (a mirrored port side ships
  normals pointing up over a winding that renders down). A report beside the output (`<output>.glb.fuse-report.txt`) lists, per group, every
  sheet's verdict, every warning, every stitched candidate's numbers and the faces rewound per part, one item per line; the status box keeps
  one line per group. A Vehicle Lab recipe marked on the unfused source follows the fuse through `tools/recipe_migrate.py`
  (the report in, the recipe out: unfused parts keep their entries, each group becomes one entry with its members' role). The part list carries the Lab's filters — hide under a vertex count or a size, hide OVER a size (logarithmic, with an
  exact entry box: drag it down and only the small clutter remains, ready to mark for deletion), a height
  band, a side band, **Only flat parts (≥ % level)** — the Lab's deck finder, measured on the preview meshes, so
  the deck plate a fuse group is still missing shows up among a dozen rows — and a **Show only** popup by mark or
  island count — so a 1,400-row liner can be worked one deck or one side at a time; marks on hidden rows are kept. A file's
  first Probe starts with every filter off (the sliders are in model units, so a previous model's settings would hide
  everything on one at another scale), and **Show all** resets them by hand. To cut AND fuse, chain the two windows: the ⊕
  letters travel with a cut or split output, and a fused output's sidecar names each shell under its group's letter, so
  either order works (the Cutter re-reads the sidecar on every Probe, so after changing groups in the Fuser,
  re-Probe in the Cutter before cutting; the Fuser keeps your edits across a re-Probe and reloads through Load groups).
  **Splitting again over an output you already grouped** keeps that work by GEOMETRY, not by name (2026-11-26): the
  pieces are numbered in island order, so another merge distance — or one more part checked — hands the names
  `_Part_001…` to different geometry (on a cruiser re-split at 0.5 % after 1 %, 255 of the 424 reused names). Before
  the old output is replaced, each new piece is traced to the old piece its triangles came from and takes that piece's
  group letter and Delete mark: a part that broke into smaller pieces keeps all of them in its group; a piece that now
  joins old pieces marked differently (or holds geometry the old output did not have beside a marked one) does not
  inherit, gets only what the source hands down, and is named in the console. When several old parts share the same
  triangle, all of them must agree before the new part inherits that value. Split and Tear checks always come from
  the source, so a part you unchecked stays unchecked. Parts that are not triangles (lines, points) keep theirs by
  index and name, and a saved value whose geometry is gone is counted in the status line. If NONE of the saved work
  can be traced to the new output (a model moved or re-exported, or a sidecar whose rows refer to different nodes),
  the Keep/Replace question shows the name matches and lets you cancel before overwriting. If the old output is
  missing or unreadable, the source fingerprint and part names are used. A **Fuse** rewrites the groups beside its output from its
  own run. While the merge box holds a typed value you did not apply, Split, Cut and Fuse refuse: press Enter to
  apply it (or Esc to discard it).
  **How long a fuse takes, and the native plugin** (2026-10): Fuse's weld, exposure views from above and from the side,
  and twin census run as C++ from the package's `Plugins/x86_64/hafkernels.dll`, alongside kernels used by the Vehicle
  Lab's C# probe and the Factory's reduce. The Workshop's **Probe** (island analysis and preview) stays managed C#.
  The DLL supports the Windows x86_64 editor and ships with the package. Its C# twins provide the same output when
  native kernels are unavailable or disabled; the speed difference depends on the operation. Across successive fuse
  optimizations — shared reads, scheduling, allocation reductions and native kernels — the Saleg's Revenge (22 groups,
  about 705 parts, 3.9 million triangles) fell from 148.7 s to 37 s with four group workers; the galley's 15 groups fell
  from 24.6 s to 9.6 s. On the Saleg's, eight workers later measured 34.2–35.5 s (the settings and method are in
  [Testing → Native kernels](Testing.md#native-kernels)). **After an update that changes the DLL, restart Unity**:
  the editor keeps the loaded plugin. An ABI-version mismatch uses managed fallback; an unchanged ABI version can
  keep the old native implementation running. To check the backend, run a **C# Vehicle Lab probe in the same editor
  session** and inspect its Console line containing `kernels native (…)` or `kernels managed (…)`. The Workshop prints
  no backend line; elapsed time alone does not identify which path ran.
  Fuse plans groups a few at a time, largest first (**Fuse — groups at once**, above). Each group in
  `<output GLB>.fuse-report.txt` has a separate stage timing line (`timing: parse … gather … weld … sheets …`). Groups
  overlap, so their elapsed times must not be summed to get wall time. The largest group's chain dominated the ship
  measurement above; other models and worker settings can have a different balance. **Probe parts** analyses the file's
  meshes on every core (the Saleg's 1,868 meshes: 12 s on one thread before 2026-10-27, 4.5 s now). A Probe that fails
  logs its exception with the stack trace to the Console, not only its message.
  **Delete** (0.5.7): the Delete key, or the row popup, marks a part for deletion in either window — it loses its mesh
  in the output, whatever writes it (Split, Plane cut or Fuse); Insert marks the highlighted row for split, – / 0 /
  Backspace clear every mark. The Split checks and the deletion marks persist in `<source>.marks.txt` (S = split,
  X = delete, the fuse sidecar's format): **Save marks** / **Load marks** in the Cutter, Save/Load groups in the Fuser,
  and every Split, Plane cut and Fuse writes it; the first Probe of a file reads it. Both windows share it.
  Triangles are preserved exactly, hidden twins and (when ticked) unseen faces apart (above: they are left out); the source parts keep their transforms and children and lose only their mesh;
  the fused part is a new root node named `Fused_<letter>_<first row of the group>`. The output can never be the source file.
  The ⊕ letters persist beside the source as `<source>.glb.fuse.txt` (a `#fuse-groups v2` header, then one
  `letter|node index|name` line per marked part — the name last, so it may contain anything; written by every Fuse
  and by **Save groups**, read when a file is first probed into the window and by
  **Load groups**) — a line applies to the part at its node index, or by name where the name is unique; a name
  shared by several parts is refused and named in the console rather than guessed. **Save groups** with no letters
  marked removes the file, and a re-Probe never re-applies a sidecar over letters you cleared. Re-Probe the output to see it as one island, then rig it in the Vehicle Lab
  like any other part.
- **Backup & Restore** — `Tools ▸ HAF ▸ Backup and Restore`. A safety net for everything git doesn't track (editor
  scripts, `FactorySource`, baked Resources, ENC databases, `Tools/`, live `BepInEx/config`). Timestamped, additive,
  guarded restore (auto-snapshots current state first). **Deep dive:** [Backup.md](Backup.md).
- **Database Browser** — `Tools ▸ HAF ▸ Database Browser`. Browse/search the game's definition assets (vanilla + mod) with
  an embedded inspector. Read-only viewer; persists UI prefs in EditorPrefs.
- **Tech Tree** — `Tools ▸ HAF ▸ Tech Tree ▸ Open Viewer` (+ `Diagnose Mod Split`, `Dump Data`). View and edit the tech
  tree: drag a card or type its position, add or remove prerequisites, switch its rule and whether it is in the game.
  Every edit is **staged** and written on **Save** (in place when the asset is under `Databases/`, else
  copy-on-write into the mod path). New-tech creation is noted not-yet-implemented.
  - **Editing** is switched by the toolbar's first button (`● Edit: ON` / `Edit: OFF`); it is ON when the window opens
    and the choice is remembered. With it OFF the side strip's controls are greyed out and a line there says why.
  - **Staged is not saved.** `Save (N)` is amber while edits are staged; they reach the assets when it is pressed.
    Staged edits are kept across a script reload (a recompile, entering Play mode) with the selection and the view,
    and across a reload of the tree that does not find their technology (the button then reads `Save (N) · M
    missing`). They are dropped, with a Console line, after an editor restart. Save removes each part from the
    staged set when that part is written, so an edit that could not be written stays and says why in the Console.
  - **A Save is not part of Undo.** Ctrl+Z does not take a Save back, and does not take back a staged edit either;
    **Revert** discards the staged edits. An undo from elsewhere that changes a technology in memory (an Inspector
    edit) is shown, and said in the Console and in the window, because the project's next save writes that state.
  - **OR / AND.** The heading over the selected technology's prerequisites names its rule: `Prerequisites (OR)` is the
    game's own (any one unlocks it), `Prerequisites (AND)` means it is in the Tech Requires All list (all are needed).
    The two buttons under it switch the rule. Save adds the technology to that list or removes it, and deploys
    `haf_tech_requires_all.json` (relaunch the game to apply). Only that one name is changed in the list, so entries
    made in the list's own window meanwhile are kept. A card under the AND rule with two or more prerequisites
    carries `AND` under its title. When the list cannot be read the heading names no rule and the buttons are
    replaced by a warning; the list's own window says why, and **Reload** reads it again.
  - **Position X / Y.** The selected card's position in grid cells, as two number fields (type, or drag the `X` / `Y`
    label). Same staged values as a drag on the canvas. A coordinate stops at 0, or as low as it already is.
  - **Shift.** A number of columns (6 to start with, one card's step in the tree) and a button under the position: every card whose column is right of the
    selected card's moves by that many columns (negative = to the left), to make room in the tree or to close a
    gap. The selected card and the cards in its own column stay. Each moved card is a staged position like a
    drag's, so Save writes them and the opposite number takes the shift back. A shift to the left stops where the
    nearest card would reach the selected card's column; the line under the button says what was moved.
  - **Title, Description, Image.** At the top of the side strip, staged like a position; the card shows the staged
    title and image at once. Save writes them into the technology's UI mapper: the Title and the Description as the
    text the game shows, the image into the card's and the notification's entries (`TechnologyScreenItemBackground`,
    `TechnologyPopupBackground`). The small `Picto` image is not changed.
    - Many titles and descriptions are localization keys (`%...`, the game's own text): the fields show the text.
      Typing it back is no change; a different text replaces the key, the same in every language. A text that
      starts with `%` is a key to the game: the side strip names its text, or warns when the key is unknown.
    - The image: pick a picture of the project, or **Same as…** another technology's image (which can be one of
      the game's own); **Own** (or None in the field) goes back to the saved one. A picture reaches the game only
      through the mod's asset bundle: the side strip warns when the project's build settings do not take the
      picked one (ENC's take pictures from `Assets/Resources` only). A texture inside another file is refused.
  - **Era.** A choice under the position: the project's eras by index, each with its Roman numeral (`V  ·  Era5`;
    the first era has none, as in the game). Staged like a drag; Save writes it to the technology's definition
    (`EraReference`). In the game a technology's era decides when an empire can research it (not before the
    empire's technological era reaches it; an era-exclusive technology also not after), its research cost (the
    era's cost for its tier, unless the technology has a cost of its own), and which side of the era separators
    its card is on. The technologies of the eras before the one a game starts in are completed at the start.
    - A warning shows when the chosen era names no cost for the technology's tier and the technology has no cost
      of its own: in the game it would cost nothing. In ENC almost every technology has a cost of its own.
    - An era-exclusive technology says until when it can be researched.
    - An era that is not one of the project's (or none) stays in the list as its first entry, also after another
      era was staged, and a note says so.
  - **Era separators.** Between two eras that both have a card on the game's tech screen the canvas draws the
    game's separator: a slanted line with the two Roman numerals beside it. The numerals are pinned to the top of
    the view, so they stay in sight while you scroll (in the game they sit at half its screen's height). It
    stands where the game puts it: halfway between the right end of the earlier era's rightmost card and the left
    end of the later era's leftmost card. Where a card of the later era sits left of a card of the earlier one,
    that is inside the overlap, and the line runs behind those cards. It follows staged eras and positions. A
    technology that is not on the tech screen (switched off, or without the TechnologyScreen visibility bit) or
    has no UI mapper has no card in the game and does not count.
  - The side strip scrolls up and down only; its rows are laid out to its width.
  - **In the game / Switched off** (`TechnologyDefinition.Visibility`). `Never` does more than hide a technology: the
    game creates a technology for an empire only when its Visibility is not `Never`, so by the game's own rules a
    `Never` technology is not on the tech screen and cannot be researched, traded or granted, and a technology that
    names only it as a prerequisite never unlocks (read from the game's code, not run in a game). **Switched off**
    writes `Never`; **In the game** turns `Never` into `Always` and leaves any other value as it is. For a
    technology that is not simply `Always`, a note says what state it is in, and for a switched-off one which
    technologies name it as a prerequisite. On the canvas a switched-off technology's card and links are faint and
    the card says `OFF`; one that is in the game without a card on the tech screen says `HIDDEN`. In ENC one
    technology is switched off (Future Technology).
  - **Scroll bars** under and to the right of the canvas. They move the same pan as the middle-mouse drag; their range
    is the tree's extent, widened to wherever the view is, so a view panned past the tree is not pulled back.
  - **The canvas looks like the game's tech screen.** Cards and spacing use the game's own numbers, read from its
    prefabs (a card 310 x 116, a column step 74, a row step 22), so cards that overlap or line up here are placed the
    same way in the game. Links are routed by the game's own rule: two cards that share some height get one
    horizontal line; otherwise the link leaves the first card's top or bottom edge, turns one rounded elbow and
    enters the second card's left edge. A card shows the title in upper case, then era, tier and the marks, then
    the internal name. The view opens on the tree's full height at its left end; **Frame All** shows the whole tree.
  - **Artwork.** A card shows its image as the game does, standing out of its right end. A mod's own image is the
    project file the mapper's `TechnologyScreenItemBackground` reference resolves to. One of the game's own is
    taken from the game's UI bundle (`AssetBundles/Mercury.Data.UI`); nothing is copied into the project. Of ENC's
    technologies on 2026-11-21, 124 showed a project image, 88 one of the game's and 4 none (they had no mapper). The side
    strip's `Image:` line names the file or the atlas, or says why there is no image; **Reload** tries a failed
    read again.
  - **Reference folder and mod path** (the two fields at the right of the toolbar). A technology whose mapper or
    definition exists only in the reference folder is copied into a collection under the mod path when it is first
    saved; without a matching collection there the Save is refused with a Console line and the edit stays staged.
    In ENC the reference copy is `Assets/~References` and the collections are in `Assets/Databases/Technologies`.
  - **What a technology unlocks.** A row of icons along the bottom of its card, where the game's card has it, and
    the same as a list in the side strip. The entries are the technology definition's own effects that are not
    hidden, turned into a row the way the game does it: one per unlocked constructible (unit, district,
    infrastructure), bonus, resource, action, cost change and so on, in the game's order (bonuses, resources,
    actions, then constructibles by their UI mapper's `SortPriority`).
    - **Whose row it is.** The game builds the row for the player's culture; the viewer shows the one a culture
      without anything of its own gets. Where an unlock names a common unit and emblematic ones, the card shows
      the common one and the side strip lists the others under it with their cultures (`Era1_Phoenicia get Bireme`).
      An unlock that only other cultures get comes after the row, faint with an amber edge, and its line says
      `ONLY for` and the culture.
    - **The icons** come from each entry's UI mapper, by the one image key the game asks that kind of entry for:
      a file of the project, or one of the game's own from its UI bundle (the pictograms of bonuses, resources and
      actions are drawn white, as in the game). Frame colours: blue a unit, green a district or starting package,
      brown an infrastructure, violet anything else. A card has room for six; with more, the sixth box says how
      many are left out (`+3`).
    - **`?` in a box** means the game would show a blank icon there, or the image could not be read here. The
      side strip says which, per entry: no UI mapper of that name, a mapper without that image, or the game's
      files not readable (Reload tries again). An entry the game draws nothing for (an action without its
      `ActionType...` mapper, a name that is no constructible / resource / cost modifier definition of the
      project) has no box; it is listed in the side strip with the reason, and the heading counts it
      (`Unlocks (3) + 3 for other cultures + 1 without an icon in the game`).
    - **Not shown:** whether the player owns the DLC an unlock needs (the line says `needs` and the DLC), the
      striped overlay of some unit classes, and the emblematic decoration.
    - Unlocks are not edited here. The internal name of a technology sits behind era and tier on its card,
      without the `Technology_` prefix, from zoom 0.6 up.
  - A card's artwork is drawn on top of every card, so nothing of any card crosses it (the user's choice; in the
    game a later card can cover an earlier card's artwork). A long title runs on under it, as in the game. A
    click where an artwork stands over another card selects the artwork's card; the selected card's edge is drawn
    over the artworks.
  - Not shown: the researched / locked states, the era buttons above the game's tree, and the large faint numeral
    the game puts in the middle of each era.
- **Tech Requires All** — `Tools ▸ HAF ▸ Tech Tree ▸ Requires All Prerequisites`. A list of technologies, nothing else.
  The game unlocks a technology when **any one** of its prerequisites is researched, and its data has no field to say
  otherwise; a technology in this list unlocks only when **all** of them are. **+ Add technology** picks from the
  technologies that have two or more prerequisites; each row shows what "all" means for it (`Needs all of: A + B`) and
  warns when an entry can have no effect (unknown name, fewer than two prerequisites). The prerequisites themselves are
  not edited here — that stays in the Tech Tree viewer, which can also switch one technology between the two rules
  (its OR / AND buttons write this same list; a window of this list that is open without unsaved changes then shows
  the new list at once, and one with unsaved changes has its next Save refused and says to reload). **Save** writes the project's
  `haf_tech_requires_all.backup.json` and deploys `haf_tech_requires_all.json`; relaunch the game to apply. The file is
  one list of `TechnologyDefinition` names, `{"technologies": ["Technology_Era4_LandClearance", …]}`. The plugin
  (`Patches/TechRequireAllPatch.cs`) then keeps a listed technology locked until every prerequisite is completed, and
  when a locked one is queued (by the player or the AI) it queues **every** missing prerequisite in front of it, where
  the game queues one chain. It changes the simulation, so in multiplayer every player needs the same list;
  `[Technologies] TechRequireAll = false` in the plugin's `.cfg` ignores the file. Not covered: a technology that is
  already in a saved research queue behind one prerequisite keeps that place, and anything that completes a technology
  outside the research queue.
  **On the tech screen (2026-11-21)** a listed technology carries a mark in front of its title and its tooltip ends
  with a line in italics, behind the same mark, that names the prerequisites it waits for: *Requires both Multimedia
  Computer and Ethernet*, or *Requires all of A, B and C* for three or more. All of it is display only and set in the plugin's `.cfg`:
  `[Technologies] TechRequireAllMark` (default `[SharedProject]`, the game's own branching symbol; plain text or the
  game's own text markup, an inline symbol such as `[Technology]` or a colour as `<c=FFCC66>&</c>`; empty = no mark),
  `TechRequireAllTooltip` for a technology with two prerequisites (default `{2} <i>Requires both {0} and {1}</i>`) and
  `TechRequireAllTooltipMany` for three or more (default `{2} <i>Requires all of {0} and {1}</i>`; `{0}` = every title
  but the last, comma-separated, `{1}` = the last, `{2}` = the mark, so one setting changes the icon in both places;
  without `{1}` the whole list goes into `{0}`; empty = no line). A listed
  technology with fewer than two prerequisites gets neither. Only the tech screen's nodes are marked, not the search results or notifications.
  **Saves:** a research queue planned under a list is saved with its ordering constraints. The plugin keeps such a save
  safe whether or not the list is still there; loaded with the plugin **uninstalled**, removing a queued prerequisite
  that two queued branches share can throw in the game's own code. Clear the research queue before uninstalling.
- **Pawn Rig Dump** — `Tools ▸ HAF ▸ Diagnostics ▸ Pawn Rig Dump`. Dump every matching `PresentationPawnDefinition`'s full
  serialized graph (skeleton, clip collections, assets) to `rig_dump.txt` for rig investigation.
- **Export selected atlas to PNG** — `Tools ▸ HAF ▸ Export selected atlas to PNG`. Blit a selected compressed atlas through a
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

**Kernels dropdown** (2026-10-28): which code the rows exercise - *C++ kernels (native)*, *C# only (managed)*, or
*Both - kernel rows twice*. Every native kernel keeps a C# twin and the callers take one path whole on a process-wide
switch (`HafNativeKernels.Available`); the window used to run whatever the switch held and the report never said which,
so a stale or missing DLL made every row a managed run that still passed. Now the first row, *Do the C++ kernels load?*,
fails when the DLL did not load (SKIP only for a deliberate `HAF_NATIVE_KERNELS=0`), the rows whose pipeline can reach a
kernel are marked "C++ kernels" in their cost line, under *Both* each of them runs twice under distinct titles
(`[C++ kernels]`, then `[C# only]`) against the same goldens and Blender comparisons, and the report header names the
mode and the loaded status. The choice persists (EditorPrefs `HAF.BakeTests.Kernels`). The two Vehicle Lab golden
rows always use Blender and run once; select **Vehicle Generate backend acceptance** to exercise the C# Generate path.
The kernel mode retains each row's Blender/C# backend choice; the dedicated in-process and Generate acceptance rows
provide explicit C# coverage. Each pass retains its checks (including cached-asset checks or named fallbacks in its body);
summary and tolerance matches establish those checks rather than output-byte equality.

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
- **Model Workshop** (every recipe's source) — on every GLB a Vehicle Lab recipe names as its source, in memory:
  splits every multi-island part, tears the smallest multi-island part, fuses the largest parts (up to 150k
  triangles) into one shell; no triangle lost by a split or a tear, the fuse's adding up (shell + hidden twins left out), every split child one island, unique names, the fused shell present
  with its reported count; then a golden diff of the counts (`Tools/workshop_golden/<file>.txt`, captured on the
  first run, deleted to re-bless). About six minutes on the current recipes.
- **Vehicle Lab** (representative recipes / every recipe) — runs the Lab's Generate headlessly (the same code as the
  button; everything under `Logs/bake_tests/lab`, nothing under `Assets/`) and diffs the rig script's summary lines
  (bones, clusters, clip frames, reduce tiers) against `Tools/lab_golden/<recipe>.txt`, captured on the first run.

Run before committing baker/pipeline changes. See [Testing.md](Testing.md) and [Factory-Manual.md](Factory-Manual.md) §11.

## Under the hood (non-window)

- **`UniversalBaker.cs`** — the bake core: model file + `BakeConfig` knobs → Amplitude `Skeleton` (+ atlas), static or
  animated. Called by the Factory, Animation Lab, and the tests (via `ConfigFor`, the one shared config path).
- **`DistrictBaker.cs`** — wraps a baked mesh as a bone-free district `FxMesh` (`BakeFxMesh`; also used by Prop Lab). One
  hand-driven menu: `Tools ▸ HAF ▸ District ▸ 1. Bake District FxMesh` (superseded by District Factory).
- **Registries** — `ModelRegistry` (master; `pack.json` + static `UnitScales`/`EraGrid`/`FormationThresholds`;
  corruption-guarded — won't overwrite an unparsable file), `DistrictRegistry`, `FormationRegistry`, `SoundOverrideRegistry`.
- **Dialogs** — `ClipRangeDialog` (scrub a clip → `clip[start..end/N]`; a `.glb`/`.gltf` plays as a live rig built in-process by `HafModelRig`, an FBX/`.blend` through Blender's inspection FBXs), `SocketBonesDialog` (donor hardpoint → your bone
  map → `socketBones`), `StripPartsDialog` (pick parts → `deployStripExtra` CSV).
