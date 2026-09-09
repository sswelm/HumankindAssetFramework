# Vehicle Lab quickstart — static model to animated unit

Use this page when you have a **static vehicle model** and want HAF to generate the rig and motion: rolling wheels,
tracks, a turret/gun, split trails, helicopter rotors, or wave rock. It is the shortest complete route from a raw model
to the game. The deeper references remain [Editor Tools](Editor-Tools.md),
[Animated Models](Animated-Models.md), and the [Factory Manual](Factory-Manual.md).

Vehicle Lab does **one** job: it turns the raw model into a rigged, animated GLB. It does not add a unit entry, build
the HAF atlas, build the Humankind mod, or deploy anything to the game. Those happen afterward.

## The complete route

| Stage | Tool | Output / proof |
|---|---|---|
| 1. Classify and rig parts | `Tools ▸ HAF ▸ Vehicle Lab` | `<name>_Spin.glb` plus an optional recipe JSON |
| 2. Configure animation | `Tools ▸ HAF ▸ Animation Lab` | Animation settings on the model entry |
| 3. Bake model and atlas | `Tools ▸ HAF ▸ Model Factory` | Skeleton, clips, mesh, material, and atlas assets |
| 4. Package and deploy | Humankind Mod Editor or `haf build` | Updated mod in Humankind's `Community` folder |
| 5. Verify | Humankind + F8 | The real runtime model, animation, and shared mesh-budget cost |

Blender must be installed for Vehicle Lab and animated bakes. HAF auto-detects it; use the override in the HAF
settings only when detection fails.

## 1. Prepare the source

Keep an untouched copy of the original model. GLB is the easiest input because it keeps mesh, material, and hierarchy
together.

Every independently moving object must be separable geometry. **Probe parts** can split a combined mesh into disconnected
loose pieces, but it cannot infer a boundary through connected topology. If a rotor blade, rotor shaft, and internal
motor are one connected piece, split them in a modelling tool first; otherwise they can only receive one role and one
bone. The same rule applies to wheels fused into an axle or a gun barrel fused into its carriage.

Do not remove material slots to make rigging easier. Vehicle Lab preserves them; Model Factory needs those slots later
to build the atlas.

## 2. Probe and orient

1. Open **`Tools ▸ HAF ▸ Vehicle Lab`**.
2. Set **Raw model**. Leave **Output GLB** at the suggested `<source>_Spin.glb`, or choose another file.
3. Press **Probe parts**.
4. Open **Orientation — straighten the model** before tuning axes or tracks. Vehicle Lab expects the vehicle's length
   along X. Orientation is baked into the generated rig; Model Factory's Rotation is a later whole-model adjustment.
5. Use the height/side filters and click a row to zoom and highlight the corresponding part.

If the source is already skinned and at least 90% of its vertices are weighted, Vehicle Lab offers **Use source
skeleton (fast path)**. In that mode each row is a bone, not an individual mesh shard. Leave it enabled to preserve the
artist's pivots and weights; disable it when those weights are the problem or you need shard-level control.

**The fast path spins Wheel-marked bones only.** Mark any bone that should spin — a helicopter rotor included — as
**Wheel** (fast-path bones spin about their own axis, which is exactly what a rotor bone wants). Rotor / Tail rotor
roles, Oar recovery, and Wave rock all need the mesh rig: Generate refuses them on the fast path with the workaround
in the warning (since 0.5.6 — before that the rig generated and the marked parts silently didn't move).

## 3. Assign roles

Resolve every **Default** row before generating the rig. **Verify** reports undecided parts, unexpected wheel clusters,
axle disagreement, unpaired wheels, turret outliers, and visible interior geometry.

| Role | Meaning |
|---|---|
| **Body** (`B`) | Reviewed, static geometry; weighted to Root. |
| **Wheel** (`W`) | Spins about its inferred or selected axle; nearby wheel shards form one hub. Optional **Wheel reduce (%)** dial (CAD rims are dense for what reads as a spinning disc; runs before clustering). Rolling-contact speed scaling applies only to wheels that reach the ground — a propeller marked Wheel keeps the dialed speed. |
| **Turret** (`T`) | Joins the shared Turret bone. |
| **Rotor** | Main rotor group; fused to one hub and spun about the mast axis. |
| **Tail rotor** (`L`) | Tail fan group; fused to one hub with its own lateral axle and trim controls. |
| **Caterpillar** (`C`) | Tread loop; enables the path-instanced rigid-link controls. |
| **Gun** (`G`) | Barrel assembly on the Gun bone; rides the Turret when one exists. |
| **Cradle** | Gun support that elevates with the tube but remains fixed during recoil. |
| **Muzzle** | Muzzle brake/flash-hider; refines the measured muzzle end and follows the tube (`Gun`, or `Barrel` when recoil creates that split). |
| **Trail** | Split-trail arm; receives a body-end hinge and the generated `Deploy` action. |
| **Oar** (`O`) | A galley oar bank — one merged mesh of poles/blades spanning **both** sides. Split into one bone per oar with a baked rowing stroke. Optional **Oar reduce (%)** dial (runs before clustering, so bones land on the slim mesh). |
| **Sail** | Marked canvas. Always exported double-sided, kept out of the inside-out flip, and struck/raised by its own generated `Furl` clip — hidden at idle, up while moving. Optional **Sail reduce (%)** dial — every vertex kept ships twice (double-sided), but the first non-zero step already cuts hard on flat canvas, so go gently. |
| **Rigging** (`R`) | Rope/line geometry — dense but barely visible at game distance. Reduced at Generate by the **Rigging reduce (%)** dial, at the source. When sails are marked, rigging **rides the Sail bone**: struck below the keel with the canvas at idle, raised underway — but single-sided (its own mesh, never doubled with the canvas). Without sails it welds to the hull as before. |
| **Structure** (`S`) | Dense detail geometry (railings, a carved bow) — more visible than rigging, so its own usually-gentler **Structure reduce (%)** dial. |
| **Flag** | Banners/pennants — the **opposite of sails**: they fly at anchor and are struck below the keel while the ship moves (one Flag bone, held flipped through `Spin`). Double-sided. |
| **Rudder** | Double-sided and **always visible**, winding kept — for slabs the inside-out test cannot decide (a half-inverted rudder scores ~0; no flip can repair it). No bone, no clip. |
| **Preserve** | Shipped as authored: never winding-flipped, never doubled (not even under the global Double-sided switch). Its **Preserve reduce (%)** dial (default 0 = byte-identical, the original promise) can opt reduction in. For parts every automatic pass keeps getting wrong. |
| **Flip** (`F`) | Winding reversed **once** at export, applied on top of the inside-out fix — an XOR **per island**: islands the fix flipped land back on their authored winding, and with the fix off (or on islands the fix left alone) the mark alone reverses them. A part whose islands got *mixed* fix verdicts can't be fully repaired by Flip — split it in the Workshop, or use Rudder (double-sided) there. Own **Flip reduce (%)** dial. Mesh rig only (the fast path refuses it loudly). |
| **Detail** | A plain reduction tier of its own (**Detail reduce (%)**) for ornament/trim geometry that wants a dial between Structure and Body. No exemptions — the winding fix and doubling treat it like Body. |
| **Ignore** (`I`) | Deleted from the generated GLB. Use for genuinely invisible internals or unwanted variants. |
| **Default / Edgecase** (`D` / `E`) | Root-weighted review markers: undecided, or deliberately parked for another pass. |

The **Visibility** filter can isolate parts that escape-ray probing found fully enclosed. It is conservative: anything
visible through an opening counts as external. Review interior parts before marking them Ignore.

## 4. Tune and generate

For ordinary wheels, leave **Axle axis = Auto**, **Spin frames = 15**, and start with one full turn. If the wheels roll
backward, reverse the sign of **Spin degrees**. Tracks, trails, gun deployment/recoil, rotors, and wave rock reveal their
own controls only when the corresponding roles are present.

For a helicopter:

- assign the blade disc and its moving hub/shaft to **Rotor**, but keep a stationary mast or engine housing as Body;
- assign the tail blades and their moving hub to **Tail rotor**;
- use **Tail-rotor axle** and yaw/pitch trim only when Auto does not keep the fan flat in its ring;
- judge the rotation plane with Pause and frame-step, not from one still frame.

**Double-sided (fix see-through parts).** The game culls backfaces, so a single-sided / CAD-style source (thin
wheel spokes, flat plates, an open frame) renders see-through from the wrong angle. Tick **Double-sided** and the
rig export appends a reversed copy of every face to the Spin GLB — genuinely two-sided geometry, nudged slightly
inward so it never reads as ~50% transparent, with the skin weights carried onto the new faces. Because the fix
is in the exported GLB, it just works in every preview (this turntable, the Model Factory, the Animation Lab) and
in-game — no Model Factory option is involved (that checkbox was removed). It **doubles the triangle count**; the
Model Factory's **Reduce to ~tris** still caps the shipped mesh, so lower that if you are near the vertex budget.
Leave it **off** for models that are already solid.

**Fix inside-out faces.** Some sources ship with part of their winding **inverted** — from outside you see through
the near hull wall while the far wall's *interior* renders. Tick this and at export the islands that provably face
the hull's interior (inverted side planking, judged against an axis through the hull belly) are **reversed** — the
cheap, no-extra-triangles fix. Everything the test cannot call decisively keeps the artist's winding, as do marked
**Sail** and **Oar** meshes. Do not expect it to fix sails or flags: no flip can show both sides of a sheet — mark
those **Sail** instead. (A blunt whole-model recalc, and then a sheet-detection heuristic, were both tried and
rejected: each flipped or missed authored surfaces; explicit marking wins.) Global **Double-sided** remains for
models that need both sides everywhere; when combined, this fix runs first.

**Sails.** Mark the canvas **Sail** (dropdown; `S` marks Structure). All sail parts weld to one `Sail` bone and are **always exported
double-sided** — canvas must read from both tacks — with the artist's winding untouched. The rig also authors a
separate **`Furl` clip** whose frame 1 **flips the canvas 180° below the keel** (rotation-only — the same
Deploy-proven stance mechanism the trails use; an earlier translation-based strike fought the converter's
rest-fold and location-strip and shipped misplaced). Use it as a **stance, never as an animation to play**: the
clip format has no visibility or alpha, so out-of-sight *is* the disappear, and the clean on/off comes from never
playing the move. Assign after baking: Idle/reference = `Spin[0..0]` (this defines the model's REST — never put
`Furl` in the reference field, or the conversion adopts the struck pose as the bind) · Idle stance (override) =
`Furl[1..1]` (a ship under oars, no canvas) · Movement = `Spin` (sails up) · **After-move and Pre-move empty** —
the state change swaps the pose in one tick. **Keep bone translations** can stay **OFF**: the strike is pure
rotation.

**Fold sail at idle** (checkbox under the sails notice) swaps the strike for the vanilla ships' look: instead of
vanishing below the keel, the idle canvas **curls up to the yard** — a hand-close roll on a generated
`Sail → SailF1 → SailF2 → SailF3` fold chain, the canvas band-skinned in height **quarters** and every fold
joint bending the **same way**, like fingers closing onto a palm (the top band). At the default 270° total each
joint bends 90°: the canvas's **foot lands at the beam**, tucked against the yard in a C-shaped roll — the way
a brailed sail actually gathers, and the gather *moves* like a hand closing. Still **pure rotation** (per-bone
*scale* is the pipeline's known trap — deploy_convert strips it for a reason). Rigging keeps standing — it
rides the root `Sail` bone, which holds. Three controls: **Fold frames** (default 12) spans the gather over
real frames, **Curl** (default 270° total) sets how far the roll closes — less = a looser, more open curl —
and **Reverse curl direction** mirrors the roll to the other side of the sail plane (which way is "backwards"
depends on the source model's facing; the reverse direction curls against the billow camber and bundles a
little looser). A fourth control, **Sag (gravity)**, presses the folded roll flat: 0 = the open zero-g C-curl,
1 = the layers fold toward 170° into a thin bundle hugging the yard — thinner in height; the bands' authored
billow camber survives (rotations can't flatten a curved sheet), so fore-aft depth is set by the source canvas.

With frames, the fold is the **deployment mechanic** applied to canvas — assign like the split-trail gun:
Idle/reference = `Spin[0..0]` · Idle stance (override) = `Furl[N..N]` (held folded) · Movement = `Spin` (sails
up) · **Pre-move = `Furl[N..0]`** (the canvas lets out as the ship gets under way) · **After-move =
`Furl[0..N]`** (it gathers on arrival) — or leave Pre/After empty for a one-tick swap. Not available on the
source-skeleton fast path (the fold needs generated bones and band skinning); regenerate and rebake to apply.

**Oars (galley rowing).** A galley's oars usually arrive as a **few merged meshes** — all the poles in one, all the
blades in another (often split front/back) — each mesh holding *every* oar across *both* banks. Mark those meshes
**Oar** (`O`). Unlike any other role, one marked mesh becomes **many** bones: the rig recovers each individual oar
(by projecting the geometry onto the plane perpendicular to the common pole direction, where each oar separates
cleanly), gives it a bone at its oarlock, and bakes a unison rowing stroke into `Spin` — a fore-aft **Sweep** about
the oarlock plus a phase-locked **Dip** (blades drop into the water on the aft drive, lift clear on the recovery). It
adds one bone per oar (~60 on a full galley), well within the skeleton budget. Tune **Sweep**, **Dip**, and **Stroke
frames** while watching the preview loop — the believable-from-a-distance amplitudes are a judgement made on the
moving turntable, not a still frame. If the ship **rows backwards** (blades push water toward the bow while in the
water), make **Sweep negative** — the same sign convention as Spin degrees for wheels that roll the wrong way. If the blades knife through the water edge-on instead of scooping, the source
models them *feathered* — set **Blade roll (deg)** (typically 90) to spin each oar about its own long axis in the
rest geometry; the cylindrical pole shows no change, only the blade face squares to the water. The oars row
whenever the movement clip plays; no Model Factory option is
involved. Marked oar meshes **keep their authored winding**: blades usually ship as front/back sheet pairs (already
two-sided by construction), so the **Fix inside-out faces** recalc skips them — recalculating an open sheet picks
an arbitrary side and culls half the blades. The rest of the model follows the Double-sided / inside-out checkboxes. For a rigged source, turn off **Use source skeleton (fast path)** first: oar recovery needs the merged mesh
geometry, not the source skeleton's bone rows. If wheel spin or wave rock requests a longer `Spin` clip, Stroke frames
is treated as the preferred period and the nearest whole number of strokes is fitted across the shared clip so it
loops without a pause or snap.

Press **Verify**, resolve meaningful warnings, optionally **Save recipe**, then press **Generate rig**. The output path is
copied to the clipboard and the generated animation appears in the preview. Re-run **Generate rig** after changing any
role, orientation, axle, or motion control; Animation Lab otherwise keeps using the older GLB on disk.

## 5. Know what each preview proves

| Surface | Trust it for | Do not trust it for |
|---|---|---|
| **Vehicle Lab preview** | Part grouping, pivots, axes, rotation direction, generated clips | Final HAF atlas or in-game material appearance. **Checker** deliberately replaces materials. |
| **Model Factory post-Bake preview** | Baked geometry and atlas mapping/material boundaries | Final lighting, gloss, or exact in-game colour. It uses editor lighting. |
| **Exported `<name>_Atlas` PNG** | The actual packed pixels | Whether the runtime shader and donor presentation look right. |
| **Humankind** | Final model, materials, animation, scale, donor effects, and performance | Nothing downstream remains; this is authoritative. |

## 6. Configure the generated GLB

In **Model Factory**, select or create the unit entry, set **Model file** to the generated GLB, and open
**Animation Lab**. Press **Auto-detect settings from the model**, then review what it chose.

For wheels/tracks, the expected recipe is:

- **State-driven ON**
- Idle/reference: `Spin[0..0]`
- Movement: `Spin`
- **Convert raw rig ON**
- **Fix 100× OFF**
- **Auto-ground ON**
- **Keep bone translations ON**

For rotorcraft, override the generic Spin detection with the recipe Vehicle Lab prints:

- **State-driven OFF** — rotors spin continuously
- Clip/reference: full `Spin`
- **Convert raw rig ON**
- **Fix 100× OFF**
- **Auto-ground OFF** — it is a flyer
- **Keep bone translations ON**

Set Size and the target pawn in Model Factory. A donor with no unwanted animated parts is simplest.

## 7. Prove materials before reducing geometry

For an animated multi-material vehicle, make the first bake a control:

- **Material mode = Auto** (or Multi)
- **Reduce to ~tris = 0**
- **Keep black ON** when black cockpit, glass, tyre, or shadow materials are intentional

If that is correct, lower the triangle ceiling gradually and re-bake after each change. `Reduce to ~tris` is a triangle
ceiling, not a vertex target. Animated decimation changes topology before atlas remapping, so a small threshold change
can alter the result; one rotorcraft mapped incorrectly at 20,000 and correctly at 24,000. Use the lowest value you have
actually verified, then check the bake's `verts=` count and F8's shared pawn-buffer readout.

## 8. Build, deploy, and verify

After Bake, **rebuild and deploy the Humankind mod**. A correct editor preview does not update the bundle already loaded
by the game. Launch Humankind, enable the mod, load the target unit, and use F8 plus
`BepInEx/LogOutput.log` when the runtime result differs.

## 9. Large models — fitting the engine's draw ceiling

The engine draws **at most 16,320 quads per baked unit mesh** (255 sub-particles × 64 primitives, a hard 8-bit
field) and the overrun is **silent**: the mesh stores fully, but whatever baked last — masts, rigging, sails —
simply never renders in-game, with no error anywhere. Every preview shows the full model; only the game clips.
The tooling now surfaces this at both ends: the Factory prints a per-mesh
`BAKED MESH … fits (N to spare)` / `OVER by N` line after each bake (over-ceiling also raises a dialog), and the
plugin logs a `[Uni][BUDGET]` audit line per injected unit at load, catching units baked before the check
existed.

A 395k-vertex source (a fully rigged galley: 64+ oars, sails, flags, rigging) fits under that ceiling at full
visual quality with this workflow — **delete and cut per role at the source, so the Factory's blind global
reduction never has to choose what survives**:

1. **Amputate before you diet.** Open the source in the **Model Workshop** (`Tools ▸ HAF ▸ Model Workshop`):
   Probe lists every part's disconnected-island count; split the parts hiding floating junk (the merge-distance
   slider keeps segmented ropes and trim lines whole — only genuinely distant debris separates); then, in the
   Vehicle Lab, mark the junk **Ignore**. Deleting invisible geometry is free quality — on the galley this
   removed three quarters of the raw source before any reduction ran.
2. **Cut where nobody looks, spare the silhouette.** In **Vertices control**, set the per-role reduce dials by
   visibility, not uniformly: Rigging 85–90 (ropes read as lines at game distance), Structure ~80, Body to
   taste — but keep **Oar around 40 and Sail at or below 50**: blades and canvas *are* the unit's identity, and
   thin sheets are what decimation destroys first (half-blades and tattered sails read worse than fewer ropes).
   Cutting a rope past ~90 leaves floating dash fragments — lower the dial or Ignore the part outright.
3. **Read the projection before generating.** **Verify** now ends with per-role vertex statistics and the
   post-dial projection. Aim the generated GLB below roughly **30k triangles**: then the Factory bakes with
   **Reduce to ~tris = 0** — no global decimation at all — and still fits the ceiling.
4. **Trust the bake line, not the previews.** After Bake, the console's `BAKED MESH` line is the verdict. If it
   says OVER, lower dials or Ignore more; do not ship it — the missing geometry will be exactly the parts you
   care about, and the game will not tell you.
5. **The escape hatches for stubborn parts.** A surface see-through from one side (mirrored halves import with
   inverted winding; bow/stern-facing surfaces sit in the inside-out fix's deliberate blind spot; artists leave
   backfaces behind occluders you may Ignore away) → mark it **Rudder** (always double-sided, winding-proof).
   A part every automatic pass keeps damaging → **Preserve** (shipped byte-identical). Sail-attached fittings
   must be marked **Sail** or they hang in mid-air when the canvas strikes; mast fittings stay **Structure**.
6. **Re-point, don't re-classify.** When a Workshop split (or any re-export) produces a new GLB: load the
   recipe, **Browse** to the new file (marked roles are kept), **Probe** (roles re-apply by part name — only the
   new `_Part_NNN` rows need marking), Save. Every Save keeps a `.bak~` of what it overwrites.

Beyond the single-mesh ceiling, the engine-native path is multiple meshes per unit (each gets its own 16,320
budget, as vanilla's detailed units do) — a planned framework feature, not yet available.

## Fast symptom map

| Symptom | First check |
|---|---|
| Rotor/shaft cannot be assigned separately | They are connected topology. Split the moving piece in the source, then Probe again. |
| Generated animation still uses old roles or axes | Press **Generate rig** again before re-baking. |
| Checker/missing texture in Vehicle Lab | Expected rigging preview; inspect the post-Bake Factory preview and atlas. |
| Whole model takes one material or becomes uniformly dark | Use **Material mode Auto/Multi**; Single collapses everything to slot 0. Control-bake with reduction 0. |
| Black cockpit becomes grey | Turn on **Keep black** and re-bake. |
| Materials work at reduction 0 but break when reduced | Raise the triangle ceiling until mapping is stable. |
| Tail rotor spins in the wrong plane | Adjust Tail-rotor axle/trim, then **Generate rig** again. |
| Wheels spin while parked or stay still while moving | Review the state-driven wheel recipe and `Spin[0..0]`/`Spin` roles. |
| A flat animated donor rotor remains over your real rotor | In Model Factory's Runtime section enable **Silence donor VFX (flashes)**, Save settings, and relaunch. This suppresses donor VFX; it does not remove donor mesh geometry. |
| First borrowed donor rotor sits too low | **Respawn after load** is only for models borrowing a donor's animated rotor, not models using their own generated rotor bones. |

For deeper diagnosis, continue with [Textures](Textures.md),
[Animation Pitfalls](Animation-Pitfalls.md), or [Donor Clip Flight](Donor-Clip-Flight.md).
