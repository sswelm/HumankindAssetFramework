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

## 3. Assign roles

Resolve every **Default** row before generating the rig. **Verify** reports undecided parts, unexpected wheel clusters,
axle disagreement, unpaired wheels, turret outliers, and visible interior geometry.

| Role | Meaning |
|---|---|
| **Body** (`B`) | Reviewed, static geometry; weighted to Root. |
| **Wheel** (`W`) | Spins about its inferred or selected axle; nearby wheel shards form one hub. |
| **Turret** (`T`) | Joins the shared Turret bone. |
| **Rotor** (`R`) | Main rotor group; fused to one hub and spun about the mast axis. |
| **Tail rotor** (`L`) | Tail fan group; fused to one hub with its own lateral axle and trim controls. |
| **Caterpillar** (`C`) | Tread loop; enables the path-instanced rigid-link controls. |
| **Gun** (`G`) | Barrel assembly on the Gun bone; rides the Turret when one exists. |
| **Cradle** | Gun support that elevates with the tube but remains fixed during recoil. |
| **Muzzle** | Muzzle brake/flash-hider; refines the measured muzzle end and follows the tube (`Gun`, or `Barrel` when recoil creates that split). |
| **Trail** | Split-trail arm; receives a body-end hinge and the generated `Deploy` action. |
| **Oar** (`O`) | A galley oar bank — one merged mesh of poles/blades spanning **both** sides. Split into one bone per oar with a baked rowing stroke. |
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

**Fix inside-out faces (recalc outward).** Some sources ship with their winding consistently **inverted** — from
outside you see through the near hull wall while the far wall's *interior* renders. Tick this and the rig
recalculates every face normal to point outward (Blender's Shift+N) at export: the cheap, single-sided fix — no
extra triangles. It orients per connected shell, so a closed hull corrects robustly; zero-thickness sheets (sails,
flags) still show only one side — reach for **Double-sided** when both sides of a sheet must render. The two can
be combined: the recalc runs first, so a doubled shell insets its back copy the right way.

**Oars (galley rowing).** A galley's oars usually arrive as a **few merged meshes** — all the poles in one, all the
blades in another (often split front/back) — each mesh holding *every* oar across *both* banks. Mark those meshes
**Oar** (`O`). Unlike any other role, one marked mesh becomes **many** bones: the rig recovers each individual oar
(by projecting the geometry onto the plane perpendicular to the common pole direction, where each oar separates
cleanly), gives it a bone at its oarlock, and bakes a unison rowing stroke into `Spin` — a fore-aft **Sweep** about
the oarlock plus a phase-locked **Dip** (blades drop into the water on the aft drive, lift clear on the recovery). It
adds one bone per oar (~60 on a full galley), well within the skeleton budget. Tune **Sweep**, **Dip**, and **Stroke
frames** while watching the preview loop — the believable-from-a-distance amplitudes are a judgement made on the
moving turntable, not a still frame. The oars row whenever the movement clip plays; no Model Factory option is
involved. Marked oar meshes are **always double-sided automatically** — a blade is a zero-thickness sheet that
rotates through the stroke, so one side alone vanishes for half the sweep no matter how it is wound; only the oars
pay that doubling, the rest of the model follows the Double-sided / inside-out checkboxes. For a rigged source, turn off **Use source skeleton (fast path)** first: oar recovery needs the merged mesh
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
