# Testing

HAF is verified in **three tiers**, each a machine, each at the level where its bug class actually lives:

| Tier | Runs | Guards |
|---|---|---|
| **Unit tests** — **801 as of 2026-09-14** | `dotnet test`, the pre-push gate, CI | the pure logic: registry/parse/era, pack resolution + merge + tuning tables, pose math, dial config, the session-state rule, the smoke **verdict and classifiers**, and since 09-14 the kernels behind the game-touching code: the descriptor repoint, the multi-mesh quad estimator + partition, the district density boost, the Vehicle Lab's probe-row parser |
| **Headless game checks** | `tools/check-catalog.sh` in the push gate; `tools/check-bindings.sh` on demand / after a game update | **two halves of one claim**: `check-catalog.sh` proves the catalog **covers the code** (every by-name literal at a reflection site is catalogued or allowlisted with a reason), `bindcheck` proves it **resolves** against the real DLLs; `typeprobe --find` / `--exact` locate a seam or a member's owner before a binding is written |
| **In-game smoke test** — `[load]` automatic, `[full]` on the F8 button | every load (a few ms, once), and on request | the injecting half, read from the **engine**: bindings, registry, roles, assets, sounds, files, GPU budget, district tiles and textures, patched seams — and, on the button, every live pawn on *our* skeleton, pose-hook liveness, the sub-pawn walk vs a scene scan, the write-back self-test |

> **This page owns the test count.** Nowhere else states it. On 2026-08-23 this file said **589** in the table above
> and **681** twelve lines further down, while `dotnet test` reported **705** — the same drift
> [Shared-Schema.md](Shared-Schema.md) already fences off for the field count, caught here by a review rather than by a
> guard. Re-check with `dotnet test Tests/HumankindAssetFramework.Tests.csproj -c Release` and read the `Total:`.
> The number counts expanded `[Theory]` cases, so it moves faster than the count of test *methods*.

The unit suite is a deliberate, bounded suite, not a coverage target: it guards the functions where bugs have actually
hidden, and stops there on purpose. What it cannot reach — the ~18k lines that reflect into a running game — is not
left to eyes: the smoke test reads the engine's own state, and its load tier needs no one to press anything.

```
dotnet test Tests/HumankindAssetFramework.Tests.csproj -c Release
```

## The fast gates — two lanes, `check.sh` and CI

The fast guards used to be separate scripts you had to remember to run. They're now one in-repo command
(`tools/check.sh`), wired as a **pre-push hook** — and, since 2026-08-23, the guards that need nothing but source also run
in **GitHub Actions**. Both lanes matter, and for different reasons:

- **The hook** runs *everything*, including the guards that need a licensed Unity install or the game. It is the
  complete gate, and it is the only place some checks can run at all.
- **CI** runs the source-only subset. A hook is **per-clone config** (`git config core.hooksPath …`) that a
  contributor may never have set, and `git push --no-verify` — or a GitHub web edit, which runs no hook whatsoever —
  walks straight past it. CI is the lane that survives all three.

| Surface | `tools/check.sh` (pre-push hook) | also in CI | ~time |
|---|---|---|---|
| **Runtime + shared contract** | `dotnet build` · `dotnet test` · docs guard · binding-catalog surface · hot path · parse shape · member shape · schema parity | all source-only checks | seconds |
| **Editor package** | Roslyn editor compile-check · registry engine drill · backup dedup drill · blender exit drill · GLB reader drill · GLB writer drill · vehicle probe drill · Workshop compaction drill · schema parity · hand-list gate | parity + hand-list; compile check and drills stay local | ~60 s |

The one guard CI cannot run is **`tools/editor_compile_check.sh`**: it needs a licensed Unity 2021.3.1f1 install
(`UnityEditor.dll`, the MonoBleedingEdge 4.7.1 profile, every `UnityEngine` module), none of which is
redistributable or present on a hosted runner. It stays in the hook, where the Unity install already is. So the
editor's compile check is the one check a `--no-verify` still gets past — worth knowing before you use one.

**`tools/registry_engine_drill.sh`** needs the same install. It runs the real `editor/SingleSourceRegistry.cs` (the
district, formation and sound registry engine) against real files on Unity's Mono, with tiny stand-ins for the few
Unity APIs it touches (`tools/registry-engine-drill/Stubs.cs`): one scenario per row of the engine's exit table — `{}`
and 0-byte sources, a hand-emptied source beside a full deploy, a lock versus corruption, a concurrent edit, a stale
snapshot, a failed deploy finished later, git recovery that must not touch the working copy.

**`tools/backup_dedup_drill.sh`** runs the real `editor/BackupDedup.cs` the same way over a scratch tree: identical bytes
hard-link, a file rewritten with new bytes under the same size *and* the same last-write time is copied ("unchanged" is
decided by content — PR #105), two snapshots of the same bytes sign the same, and a previous snapshot without a
content index links nothing.

**`tools/blender_exit_drill.sh`** runs the real Blender (newest install under Program Files; `BLENDER=<exe>` overrides; SKIP
when absent) with the head `BakerRules.BlenderScript` builds: a script that raises exits 1, without the flag it exits 0 (the
hole), a clean script exits 0, and the real `rig_anim.py` clears the previous run's role clip before touching the model
and fails the process on a missing input.

**`tools/glb_reader_drill.sh`** (2026-09-30, step 1 of replacing Blender) reads every `.glb` the modding project's registry
names with the real `editor/GlbReader.cs` on Unity's Mono, checks what a file cannot say about itself (skinned vertices weigh
to 1 over the skin's joints), and compares each file with Blender's *evaluated* import of the same file: the counts
(triangles, materials, images, joints) and, order-independent so vertex merging cannot move them, the world-space
bounding box, the total triangle area, the area-weighted centroid, the area-weighted sum of face normals (winding and
mirrored nodes), the bone names and each animation's span (earliest first key to latest last key over every channel —
what a Blender 5.1 slotted action spans) — through both transform chains, in Blender's Z-up frame.

**What else goes through both GLB drills** (the round-trip diversity PR, 2026-10-02 — every registry file is a Blender
or Sketchfab export, so a reader that only ever saw those had not met the rest of the specification):

- **The fixture library**, `tools/glb-reader-drill/fixtures.py`: twelve small, deterministic files, one per shape the
  registry lacks — normalized integer attributes (ushort UVs, ubyte colours and joints, ushort weights), an
  interleaved buffer view with accessors at byte offsets and `uint` indices sharing one view, every primitive mode
  (non-indexed triangles, strip, fan, lines, points), a `.gltf` with its `.bin`, a `.png` beside it and a data-URI
  image, CUBICSPLINE/STEP/LINEAR keys with three animations on one node (one unnamed), two scenes with the default
  the second plus an orphan node and a mirrored matrix child, material variety (MASK/BLEND, double-sided, textures on
  `TEXCOORD_1`, occlusion strength, normal scale, `KHR_materials_emissive_strength` / `specular` / `unlit`, a sampler
  with all four settings, a texture without one, two textures sharing an image, extras of every JSON type), eight
  influences over a joint chain with no inverse bind matrices and a skinned node under a translated parent, a
  72,541-vertex grid (`uint` indices), names (empty, duplicate, unicode, JSON escapes, a material no primitive
  uses), and two scenes with no default (the specification's "show nothing at load", kept absent on the round trip). Each goes through the reader against Blender, the writer round trip field by field, and Blender again.
- **The Khronos glTF-Sample-Assets** named in `samples.txt` — other exporters' output (Box, Duck, CesiumMan,
  BrainStem, InterpolationTest, NegativeScaleTest, Unicode❤♻Test, …) — once fetched with
  `python tools/glb-reader-drill/fetch_samples.py References/gltf-samples` (12 MB, git-ignored; the drills say when
  they are absent and run without them). A line may carry an **expectation**: a stage that must refuse the file by
  name — `SimpleSparseAccessor` (the reader: sparse), the two morph-target samples (the writer), `TextureTransformTest`
  (the source guard: a texture-info extension the model does not carry). The "no" cases drilled like the "yes" cases.
- What the diversity found, fixed on the way: the drill measured area for TRIANGLES only (strips and fans now unroll
  through `HafPrimitive.Triangles()`, one definition for the drill and the preview); a joint with no name is
  `Node_<index>` in Blender; Blender creates a material only when a primitive uses it and **invents one** for a
  `COLOR_0` primitive that has none (the C# line states that count as `blendermaterials`); Blender imports every
  node, other scenes' and orphans' too; Mono printed an emoji file name as `??` under the console code page.

The Blender comparison in the gate covers every fixture and sample (small) plus the registry's largest, smallest and
four animated files: 43 files; `FULL=1` for every file.

**`tools/vehicle_probe_drill.sh`** (2026-10-02, step 3: the Vehicle Lab's probe in C#) compiles
`editor/VehicleProbe.cs` and `editor/BlenderNames.cs` with Unity's Roslyn and probes, on Unity's Mono, every fixture,
every registry source, the Khronos samples **and the sources of the Lab's saved recipes** — the probe's real inputs,
the unreduced originals (925 MB, one of 398 MB). Then Blender runs the REAL probe (`vehicle_rig.py probe`, in one
process, posed at the first clip's start by its opt-in `posestart=1`) on a sample and the rows are compared per file:
the part **names** — the key every saved recipe holds — and their order, vertex counts, world boxes (tolerance
0.0002 + 2e-6 of the model's extent), **visibility verdicts** (since step 3b, 2026-10-02: exact — 10,498 parts, 1,171
of them interior, agree; two fixtures hold a part inside a closed box, a 1,681-vertex grid Blender separates from a
single mesh, and a triangle whose only escape is its file normal's direction), **inside-out verdicts** (since step 3c,
2026-10-02: exact — 14,020 of 14,023 parts on 119 files agree; the three that differ are skinned parts with zero-area triangles
whose normal is decided by the float32 ulp Blender's importer adds when it skins them, named in the Review Backlog;
the `insideout` fixture holds strips facing the axis and away, the same under a mirrored node and under a turned one, two
islands in one mesh, faces sharing only a vertex (two islands), a nonmanifold edge joining three faces (one island), a
collinear triangle and a skinned strip), dominant bones, the RIGBONE rows, and — since 2026-10-03 — each part's
**matrix_world bit for bit** (MATRIX rows: 14,003 of 14,023 on 119 files; the 20 meshes Blender parents to a bone are
counted in the PASS line, not held, until the bone chain is ported) and **vertex 0 of every part's position bit for
bit** (VERTEX rows: `matrix_world @ co` — the float32 matrix and the importer's numpy float32 skinning in one number;
the row's normalized world normal is counted, not held: Blender's two-short custom-normal pipeline is reproduced to
about 1e-5, not to the bit). The sample positions, the normal ray and the
ray cast are Blender's float32 arithmetic (`VehicleProbe.BlenderWorld.cs`, `VehicleProbe.BlenderSkin.cs`,
`VehicleProbe.CustomNormals.cs`, the ray test in `VehicleProbe.Visibility.cs`), proved by the Dragon's grazing rays: 12
verdicts off with the double chain, 0 with this; every inside-out verdict agrees (14,023); the Ehrhardt's spin output
keeps 6 visibility verdicts where Blender's binary contradicts its own box test (Review Backlog). The `custom_normals`
fixture holds a leaning and a flat quad whose vertex normals are read off Blender to the bit.
**Since 2026-10-03 (step 3d) the drill also runs JOBS** — probes with the Lab's other inputs, a second model (`merge2=`),
per-part placements (`parttx=`) and an orientation (`proberot=`), from one JSON both sides read
(`tools/vehicle-probe-drill/probe_jobs.py`, `ProbeDrill.cs @jobs.json`, `blender_probe_many.py @jobs.json`): ten on
the `second_model_a/b/c`, `placement_nested`, `placement_split`, `placement_shear` and `insideout` fixtures (a turned second-model root
with a per-axis scale, a mirrored node, leaning normals, a skinned part, a bone shape, a clashing name, a single-mesh
second model's split, a first model's split beside a second model and both splitting at once, placements on a parent with children, on a loose
part, with a per-axis scale on a turned part, on a missing name, with a zero scale; the orientation about Z and about X),
and one per saved recipe that sets any of these, with the recipe's exact arguments formatted as the Lab formats them
(`Tests/test_probe_jobs.py` holds the formatting to the Lab's). Every job's MATRIX and VERTEX rows are held bit for bit
like a file's; `recipe_check.py` judges a recipe with inputs on its job's rows — its B_ parts and placed parts included.
**`tools/decimate_drill.sh`** (step 5 milestone a, 2026-10-03: the exact port of Blender's Decimate begins with the ORDER its
heap sees) compiles `editor/BlenderMesh.cs` with Unity's Roslyn and lays out every fixture, registry and recipe mesh as
Blender's importer and `mesh_calc_edges` do - each primitive's used indices ascending, the line primitives' pairs first in their
own orientation, then per face per corner the (previous, current) pair as (low, high), deduplicated in insertion order inside
1 bucket (under 1,000 faces) or 8 (the lower vertex index masked; the machine's thread count as a power of two, at most 8),
then validate's removals - and Blender imports a sample in one process and dumps `mesh.edges`; the lists are compared per
object, exact (hash of the whole list, counts, the first edges on a mismatch). `FULL=1` on 2026-10-03: 137 files, 6,912 mesh
objects, 21,137,582 edges equal. `BlenderMeshTests` hold each rule on a hand-made case.
Milestone (b), the same day, adds `editor/BMesh.cs` - Blender's BMesh kernel as far as the collapse walks it: the disk
cycle (a new edge is appended before the head; a removed head passes to its next), the radial cycle (the LATEST face's
loop is the head), LOOPS_OF_VERT (edge by edge around the disk from the head edge's loop at the vertex - or that loop's
NEXT, on another edge, when the head's loop sits at the far end - then along each radial cycle), the kills and splices
(the head, repeatedly), element numbers = creation order = `BM_ITER_MESH` order (creation after a kill is refused rather
than modelling the pool's free list). The same drill builds the BMesh of every object and holds every link list - per
vertex its edges and its loops, per edge its ends and its loops, per face its loops and vertices - to `bmesh.from_mesh`'s,
then on objects of at most 3,000 edges runs a scripted sequence of vertex kills, edge kills and vertex splices (a fixed
generator seeded per object, the same sequence on both sides; Blender's Python refuses a splice across a shared edge or
face, so the sequence kills the edge first, as the collapse does) and holds the lists after every step. Gate sample:
2,284 objects' links and 7,059 steps equal; `FULL=1` on 2026-10-05 (the population as it stood then): 110 files, 4,312
objects, 19,453,528 edges and 38,510 steps equal. `BMeshTests` walk each rule by hand.
Milestone (c) adds the collapse: `editor/BlenderDecimate.cs` (`BM_mesh_decimate_collapse` without symmetry, weights or
triangulation - the double quadrics, `BLI_heap` whose equal values pop last-inserted first, the costs and the topology
fallback, the degenerate checks, the collapse with its UV / colour / vertex-group blending), `editor/BlenderReduce.cs` (the
mesh as the importer stores it - the bind pose, the custom normals, sharp faces, material slots per material AND vertex
colour, UV layers with gaps filled (0, 1), colour layers per domain, vertex groups with the zero-sum rule - then the
modifier, then `modifier_apply`'s `merge_customdata`, which snaps a vertex's UVs within 12 ulps together) and
`editor/BlenderColor.cs` (Blender's SSE linear-to-sRGB with the `rsqrtps` table read off this machine's Zen 4; another
CPU keeps Blender for coloured meshes) and `editor/BlenderTrig.cs` (`cosf`/`sinf` as the 64-bit Windows C runtime computes
them - Blender's, and an ulp off the rounded double cosine on 1 float in 2,000, enough to reorder the heap among equal
costs). The same drill collapses every object of more than 3 faces at three ratios (1, a half, prep_model's ratio for a
third of the file) and compares positions, faces, UVs, custom normals, material and sharp flags, vertex groups and colour
bytes with Blender's own, exact. Its C# side runs as a 64-BIT process (the exe directly, on the .NET Framework: a 32-bit
process has no `cosf` to call), then once more on Unity's 32-bit Mono over the meshes without normals, because Mono
evaluates an uncast float product in double and only that run shows it. Gate sample: 141 reductions (42,208 faces)
equal, 102 of them under Mono too, in 15 s; `FULL=1` on 2026-10-05: 6,099 reductions of 19,194,169 faces on 110 files
equal, none declined, in 15 minutes (C# under 5 of them). The vehicle probe drill runs 64-bit for the same reason
(`PROBE_MONO=1` for the old way).
`BlenderDecimateTests` hold the heap order, the quadric, the stop count, the UV merge and the colour path (values from
real SSE on the same CPU); the `decimate_attrs` fixture carries the attribute combinations no population file has.
**`tools/prep_drill.sh`** (step 5 milestone d, 2026-10-05: the mesh as Blender's glTF EXPORTER writes it after the reduce,
`editor/BlenderExport.cs`) lets Blender run the REAL `editor/Tools~/prep_model.py` - by `runpy`, with the Factory's command
line - twice per file: with a target of a third of the file's triangles, and with a target of all of them (nothing
collapses; importer, apply and exporter still run). The C# side reduces every mesh object of the source at prep_model's
ratio (`BlenderReduce`), lays it out as the exporter does and holds each primitive to the one Blender wrote for the node of
the same name, bit for bit: the vertex count, positions, normals, every UV set, every colour set, the indices. The rules,
each read from `io_scene_gltf2/blender/exp/primitive_extract.py`: the exporter first runs `mesh.validate()`, which
removes the later of two faces on the same three vertices - the collapse leaves such twins where a small closed solid (a
bolt, a rivet) closes onto itself, and the normals of the faces that stay change with them; one "dot" per corner (vertex
number, normal, UVs, colours); the normal is `Mesh.corner_normals` (`VehicleProbe.BlenderCornerNormals`: by the mesh's normal domain) rounded
to 4 decimals in float32, renormalized, a zero made "up"; every -0.0 becomes 0.0; triangles are bucketed per material
slot and each bucket's unique dots are SORTED as raw 32-bit words (numpy sorts the records as strings: a negative float
comes after a positive one); the colour sets follow the first material slot in use - a material built with the vertex
colour gives COLOR_0 as linear RGB floats, a face without material ahead of it gives COLOR_0 with alpha, materials
without either give a forced COLOR_0 of 255s - and every further layer follows with alpha as normalized shorts. The C#
side prints which rules its compared objects exercised (`COVER` rows) and the script FAILS on a row at zero: three were
at zero on the first run (the forced set, alpha first, the zero normal) and the `export_layout` fixture now carries them.
The validate step was MISSING until the first `FULL=1` run: the gate sample was equal, five fused ships were not (a few
triangles too many, one normal made "up"), and the fixture's closed solids now hold it in the gate. The fixture's
triangle of no area could not tell "a zero normal is made up" from "an invalid space keeps the fan's normal" - its fan
points up anyway - so a fan whose angle-weighted normal runs along its own edge, +X, stands beside it (its own `COVER`
row): Blender's corner normal there is zero, and the rival rule, planted, fails the drill on that object alone.
It runs 64-bit like the decimate drill. A file `prep_model.py` itself fails on (several scenes: "not in View Layer")
passes only as that known failure. A SKINNED object (second part, 2026-10-06) hangs from its armature without a
transform of its own: its positions go through the armature's `matrix_world`, its normals through the armature's 3x3 times
the inverse transpose of `armature^-1 @ object` (the identity plus float noise - an ulp or two that shows), both by
numpy's float32 `matmul` (numpy reads a mathutils matrix as FLOAT32; the first version computed in double and the
turned, unevenly scaled armature of the `export_skin` fixture showed an ulp where the simpler rigs of the sample had
agreed); per vertex the groups over 0.0001 by weight (a stable sort), four kept, divided by their float32 sum; the joints
are the armature's bones depth-first (`BlenderNames.BoneNodesInOrder`), and a vertex left without a bone gets the
exporter's "neutral bone" - only a vertex whose weights do not sum to 1 reaches that, so the fixture for it,
`export_skin_badweights`, says so in its name and the reader drill's weight contract lets that name through. The neutral
bone is the ARMATURE's: Blender appends it to the shared skin when any mesh of that armature needs it, so a fully
weighted mesh beside the boneless one lists it too (review of PR #127; that fixture's second mesh, listed first). A coloured
material WITH alpha (third part, 2026-10-06): its set is written with alpha when the importer wired the vertex colour's
alpha into the material (`pbrMetallicRoughness.py` `base_color`), which it does for every alpha mode and cutoff except
OPAQUE (or no mode) and MASK at a cutoff of 0 or over 1 - so BLEND, MASK in (0, 1] and below 0, and a mode string the spec
does not know are wired (`BlenderExport.VertexAlphaWired`). The population has 48 such primitives, all BLEND and one of
them textured; the `export_layout` fixture carries the rest: MASK at 0.5, 0, 1.5, 1.0, without a cutoff and at -0.5, "Blend",
the wiring through a base colour texture, unlit BLEND and MASK, and a BLEND solid that loses a twin face (its slots' alpha
modes must survive validate's rebuild - they did not, in the first version: the review of PR #128 found it, and the rule
above, which the first version had as "BLEND, or MASK in (0, 1]"). The cutoff is kept as the JSON DOUBLE through the model (the review's
fixtures: MASK at 1.00000001 and at 1e-50, which float32 would put on the wrong side), so a boundary is decided as the
importer decides it. Base colour factors also retain JSON double precision: a non-unit alpha such as `0.99999999`
creates a factor node whose float32 value rounds to 1, so the exporter detects OPAQUE and writes RGB. Exactly 1
creates no factor node and keeps RGBA; MASK's clip nodes also keep RGBA. The regression fixture exercises plain,
textured, unlit and specular-glossiness materials, with coverage required for both sides of the near-one decision.
Specular-glossiness uses its diffuse alpha factor; unlit takes precedence and uses the core base colour factor.
Not probed: `KHR_animation_pointer` (an animated cutoff keeps the socket). Nothing is
declined any more. Gate sample: 50 runs on 25 files, 154 object runs (219 primitives, 31,884 vertices) equal, in 10 s;
`FULL=1` on 2026-10-06 (late, with the Espana just fused and rigged - 1.1 million triangles over 3,500 objects): 154 runs on
77 files, 11,344 object runs (12,505 primitives, 17,449,348 vertices) equal, 0 declined - 534 of them skinned, 166 under an
armature not at the identity, 132 with a wired alpha, 29 with twin faces removed, 42 with a zero normal on a fan that points
elsewhere; Blender needed an hour for it, the C# side a few minutes. `BlenderExportTests` hold each rule alone, the rounding and the short quantizing against
values numpy gave, the skinned arithmetic against matrices and results read off Blender; thirty planted defects, one
per rule, each failed them but one that changes nothing (`<` for `<=` at the weight threshold: no float32 equals 0.0001);
sixteen planted under the drill each failed it on the fixture built for that rule. Three planted defects cannot fail the
drill, because no imported file reaches them, and are held by the unit tests alone: that equivalent threshold, validate's
clamp of a weight outside 0..1, and a vertex group whose bone is no joint.
**The written file's structure** (milestone d, part 4a, 2026-10-07: `editor/BlenderExportTree.cs`, judged by a structure
stage of the same drill): which nodes Blender's exporter writes, in which order, under which parent, with which transform,
and which materials in which order - what the Factory's converter walks. Measured first (an `order_try` file of roots and
children named to tell the rules apart, the fixtures' outputs dumped), then read in `tree.py`, `nodes.py` and
`exporter.py`: the scene's root objects in creation order; an object's children by name as `bpy.data.objects` sorts them
(`BLI_strcasecmp`: signed bytes after an ASCII tolower - "a10" before "a9", a non-ASCII byte before every letter); an
armature's object children before its root bones, bones' children in creation order; the index order is the
serializer's walk - a node's members alphabetically (children, then mesh, then skin), the node appended AFTER them: every
child before its parent, a joint reached through a skin before the mesh that uses it, a root last; the dummy root's
armature holds every top-level object. A transform is mathutils' float32 decomposition of `parent.matrix_world.
inverted_safe() @ matrix_world` (the matrix alone for a root), the quaternion normalized, turned Y up, each component
within 2e-6 of its identity snapped to it, an identity property left out (the names fixture's 0.99999994 and
0.707106829). A skinned mesh hangs from its armature with the identity; a mesh of lines or points alone is a node
without a mesh; a mesh needing the neutral bone adds the joint "neutral_bone" after the armature's bones. Materials: the
importer makes one per (glTF material, has COLOR_0) variant on first use, in mesh creation order, named after the glTF
material - "Material_<index>" when the file has none, Blender's default "Material" when it is there but empty -, a
"DefaultMaterial" per mesh for a coloured primitive without one; the exporter lists them at first use along its walk.
`BlenderNames.Objects` and `MaterialOf` carry what the tree needs. **The bones** (part 4b, the same day;
`VehicleProbe.BlenderArmature.cs`, `BlenderEigen.cs`): a joint's written transform and a skin's inverse bind matrices
come out of a chain the importer and Blender run on every rig - the bind translation and rotation per bone; the
BLENDER bone heuristic (every edit bone turned by a quarter about X, its children turned back - mathutils' mul_qt_qtqt
and mul_qt_v3 -, a bone length from the nearest bone child over 0.004, else the parent's, else its own, else 1 - a
double square root); the edit bone's head, tail (mathutils' double sums), length (divided by the armature's largest
scale) and roll (ED_armature_ebone_roll_to_vector: angle_v3v3 by asinf); then leaving edit mode -
armature_finalize_restpose: head and tail relative to the parent's tail through Eigen's 4x4 inverse of the parent's
`arm_mat` (`BlenderEigen.InvertM4`: Intel's SSE block algorithm with a true division, since Blender's build has no FMA)
and mul_mat3_m4_v3; where_is_bone with roll 0 (vec_roll_to_mat3, the parent's length added along Y, mul_m4_m4m4's SSE
association); the roll that brings that matrix onto the edit bone's (invert_m3_m3, mul_m3_m3m3, -atan2f) and
where_is_bone once more: `bone.matrix_local`. The exporter then takes armature.matrix_world @ matrix_local @ the
basis change as the joint's world, decomposes it against the parent joint's or the armature's WITHOUT the
normalization and the snapping object nodes get (joints.py), writes a -0.0 as 0 (__fix_json), and the inverse bind
matrix (basis @ armature.matrix_world @ matrix_local).inverted_safe(); the neutral bone is the basis change decomposed
with (basis @ armature.matrix_world).inverted_safe(). Found on the way: `mathutils.Matrix.inverted()` is NOT
Blender's C invert_m4_m4 (it is the adjugate), so measuring Eigen through it misled the port for an hour - the C call
is reached through `bpy.ops.object.parent_set`, which stores it in `matrix_parent_inverse`; five matrices measured
that way, bit for bit. A tilted joint chain in the skin8 fixture tells Eigen's inverse from the adjugate; eight planted
defects in the chain each fail the drill on it. NOT compared: an object parented to a bone (its transform needs
Blender's pose evaluation; the dug-out canoe's eleven parts, the `export_skin_bonechild` fixture) and an animated
file's object transforms (Blender's posed import state; the Factory preps static entries). Gate sample: 48 node
lists, 176 object and 46 joint transforms, 26 skins' inverse bind matrices, 2 neutral bones and 24 material lists
equal; `FULL=1` on 2026-10-07 (after the review below): 160 runs on 80 files, 158 node lists, 50,872 object and 990 joint
transforms, 538 skins' inverse bind matrices, 2 neutral bones and 128 material lists equal; an earlier run found the walk
appending an armature before its bones when no mesh uses the skin (the dug-out canoe; fixed, unit-tested) and the one file
still named is the canoe, whose eight joint transforms differ by ulps because the probe's world matrix for the armature's ancestor `Canoe` (a quaternion 2.9e-8
off unit, components of 1e-33) is not Blender's - an existing gap of `BlenderWorldMatrices`, now in the backlog;
the canoe's edit bones and `matrix_local` are equal. `BlenderExportTreeTests`, `BlenderArmatureTests` and
`BlenderEigenTests` hold each rule on the measured values.
The review of PR #129 (2026-10-07) added three checks. The prep-only `export_skin_lines` fixture (`fixtures.py --prep`;
the reader and writer drills do not see it) is a lines-only skinned mesh under a turned Holder with a transform of its own:
the exporter writes its node without a mesh and without a skin, still under the ARMATURE with the identity, and indexed
before the joints because the serializer does not reach them through it (measured; a first fixture with Body already
under the armature and without a transform could not tell that rule from "it stays with its parent"). A negative row
removes a skin's non-identity `inverseBindMatrices` accessor from one of Blender's outputs (`tools/prep-drill/missing_ibm.py`)
and requires the structure stage to fail on the implicit identity (the stage had skipped such a skin). And the writer keeps
a material's empty name (`"name": ""`, which the importer names `Material`; an absent one is `Material_<index>`), with
`HafModelDiff` telling the two apart (`GlbWriterTests`).
The skin-wiring regression uses the prep-only `export_skin_twins` fixture: two armatures have
identical joint names and inverse bind matrices. The structure stage derives each mesh's ordered
joint indices from its source armature, including the neutral bone when needed, before comparing
matrices. `tools/prep-drill/wrong_skin.py` constructs two negative exports: a mesh redirected to
the other skin, and a skin with the other armature's joint indices. Both must fail the prep drill.
A review of that fix found the same hole one field over: nothing compared WHICH material a written primitive uses (the
list held names and order, the mesh stage the vertices). The structure stage now compares each primitive's material
index, with a negative row of its own (`tools/prep-drill/wrong_material.py` swaps two primitives' materials in one of
Blender's outputs). The joints a skin lists are the tree's now (`BlenderExportTree.Node.SkinJoints`, the rule the writer
will use - the drill had its own copy), and the stage holds the exporter's one skin per armature: nodes of one armature
share a skin index, nodes of two never do. Each node's mesh INDEX is compared too (every object's mesh written once, in
the order the serializer reaches them: `Result.MeshVisitOrder`, which nothing had consumed). `FULL=1` after it (188 runs on 94 files): 5,885 mesh indices, 5,861 material assignments and 558 skins' joint indices equal, 474 nodes sharing their armature's skin.
A second review the same day (two independent readers against Blender's and Eigen's sources; every finding run through
Blender before it was believed) found four shapes the 80 real files do not have and the tree got wrong, and a branch of
the bone chain that was not ported. Fixed and held by prep-only fixtures: a camera is not exported (`export_camera`; the
filter of `tree.py`), an object parented to a bone hangs from the first node that BEARS THE BONE'S NAME in a depth-first
search - an object hung earlier can come before the joint (`export_bone_name_clash`), a material named JSON `null` has no
name (`export_material_null`), and an edit bone no longer than 1e-6 is elongated on leaving edit mode - along itself, or
along Z when it has no length (`export_bones_tiny`, `export_bones_nolength`). Left to Blender, each NAMED by the tree
(`BlenderExportTree.Result.Problems`; the drill prints a NOTE and does not compare that file's structure): a camera with
children (their matrices carry the importer's camera correction, which the world matrices do not model),
`KHR_lights_punctual`, a skin on a mesh without weights (Blender writes its positions through the armature and joints of a
bone it never adds; `BlenderReduce.FallbackReason` declines the object too), and a material read with two different
sets of UV indices (the exporter writes it once per set). A file with a declined object is left to Blender as a whole.
The bone chain's branches the sample did not take now have a fixture and a `bones:` COVER row each, counted only when
the file that took the branch compared equal: a bone of length 1, a bone child nearer than 0.004, a bone along -Y (the
mirrored matrix) and almost along it (the series), two root bones, a skeleton that is not a joint on a skin with inverse
bind matrices. Ten planted defects each fail; two were NOT caught at first and changed their fixtures - a lone bone's
length shows only in a child's matrices, and 200 km out the 2e-6 elongation is itself lost. The structure stage now also
compares every node's children ARRAY in order and requires a skin without a `skeleton`; its COVER rows count after the
comparison, not before. `FULL=1` after it: 186 runs on 93 files, 176 node lists, 50,960 object and 1,026 joint transforms, 554 skins' inverse bind matrices and 130 material lists equal; no real file is left to Blender by the new rules, and real files do take the -Y and length-1 branches; the canoe remains the one file named.

**The prepared file, written** (step 5 d, part 4c, 2026-10-08): `editor/BlenderPrep.cs` assembles the ports into the file
`prep_model.py` writes and `GlbWriter` writes it. The prep drill calls that production code for everything it compares
(`BlenderPrep.Prepare(diagnose: true)`; it no longer has an orchestration of its own) and gained a WRITTEN stage: the
file is written, read back, and held to Blender's three ways - the assembled model field by field (scene roots; every
node's name, children, mesh, skin and transform as the JSON doubles; every skin's joints and inverse bind matrices), each
material (the base colour factor as doubles, the base colour image's bytes, metallic and roughness, and whether its JSON
has a `pbrMetallicRoughness` object at all - the converter makes a white swatch of a flat material with one and a grey
one without), and the Factory's converter itself: `glbconv` is run on this file and on Blender's, at grid 0 (every
vertex, an `.mtl` and an albedo per material) and at the default grid, and its two output folders must be equal file by
file, byte for byte. A file the prep cannot make as Blender does is NAMED and left to Blender (`Result.Fallback`); the
drill prints why per run, counts it, and requires the runs to add up (failed + known prep failures + written + left).
What came back out of Blender, measured before it was written down: images are passed through byte for byte EXCEPT a
JPEG whose alpha is read (written again as PNG - the Espana; left to Blender); `baseColorFactor` is the factor in
float32 with an alpha that depends on the mode, the cutoff and whether the material has a texture or vertex colours
(`export_layout`, `export_flat_alpha`); a specular-glossiness material comes back metallic-roughness with the diffuse
colour and texture, metallic 0 and roughness 1 - glossiness (22 materials of the Teutonic); an unlit one with metallic 0
and roughness 0.9; an alpha mode the schema does not know makes the converter refuse the file. An independent reader
went through it against the exporter's source before the PR and found the unlit rule, `prep_model.py`'s purge of every
unskinned object named Icosphere, and the shapes now left to Blender by name (material variants, a factor outside 0..1 -
see the self-review below -,
an image that is not a PNG or a JPEG or whose file name is not its format's). 25 planted defects in the assembly each
fail the drill; three did not at first and changed it (the scene's roots, which the converter does not walk; a factor
left in double, which a float compare hid; a MASK at a cutoff of 0, whose fixture had an alpha of 1). `FULL=1` (192 runs on 96 files; the two files the parser's ulp had failed re-judged after the fix): 114 runs written and read by the converter as it reads Blender's, 74 left to Blender by name (58 animated, 6 under a bone, 4 a JPEG whose alpha is read, the rest fixtures of the named shapes), 2 known prep failures, and the canoe's 2 (the probe's world matrix, in the backlog); 484 base colour images byte for byte.
`BlenderPrepTests` hold the assembly's shape, every reason to fall back and the material rules. Not Blender's, and not
read by the converter: mesh names, COLOR_n as float RGBA, material properties beyond the base colour, metallic and
roughness (copied from the source; the alpha mode made one the schema knows), unused textures and images (kept).
Nothing calls `BlenderPrep` yet: strip and the wiring behind `PrepViaBlender` are part 4d.
A self-review after the PR (2026-10-08, the follow-up to PR #130) ran what the first version had only read. Wrong, and
corrected: Blender clamps a base colour's RGB and NOTHING else - metallic 2, roughness -1 and an alpha of 1.5 come back as
they went in, an unlit colour loses only what is under 0 - so the "Blender clamps it" decline was false. What is true,
and found by the drill on Blender's own output: the converter REFUSES a file with such a factor, so that source does not
bake today either; the prep names it and leaves it, and the drill runs the converter on Blender's file for those runs
and requires it to fail (`export_factor_range`). The reason given for a file of several scenes was false too
(`prep_model.py` fails on some, not on all): reworded. The Icosphere purge is measured now (the part goes, its child
becomes a root with its local transform) and reaches an object without vertices. Seven more plain materials hold the
alpha rule where only vertex-colour variants had (unlit under MASK and BLEND, a mode the specification does not know,
specular-glossiness under MASK and BLEND). And the drill's log now NAMES every file left to Blender with its reason
(`LEFT <file>: <why>`): the script had been printing FAIL, NOTE and COVER lines only, so the per-run outcome added
for the review never reached anyone. One clean `FULL=1` run on that state (194 runs on 97 files; the figures above were a run plus two files judged again): 114 written and read by the converter as it reads Blender's, 76 left to Blender by name, 2 known prep failures, the canoe's 2. Of the real files, 23 are left: 22 animated ones and the Espana (a JPEG whose alpha is read).

**The prep in the Factory: strip, and the wiring** (step 5 d, part 4d, 2026-10-08). `BlenderPrep.Prepare` takes
`prep_model.py`'s strip list: every object whose name contains one of the substrings, ignoring case, goes with all its
descendants (those hanging from an armature's bones included), and the reduce's ratio is taken from what is left
(`BlenderPrep.Stripped`). The prep drill's Blender side now makes STRIP runs: a fixture names its own in a file beside
it (`export_strip.glb.strip`, one list per line: a subtree under an empty, a match by case only, several substrings
with spaces, a skinned mesh whose armature stays, the armature itself, a list that matches nothing, one that leaves no
mesh - `prep_model.py` stops there, and the prep must name it), every other file gets one (the name of an object a
third of the way down its sorted names). The strip runs found a rule of the exporter no file had shown: an armature
that NO kept mesh is skinned to still gets its skin written, after the skins nodes use, in the order the armatures
enter the exporter's tree (`tree.py` `get_unused_skins`; `BlenderExportTree.Result.UnusedSkins`) - a source skin no
node uses makes one without any strip (`export_skin_unused`, and `export_skin_unused_order` for the order).
`UniversalBaker.PrepInProcess` is the Factory's call: a .glb/.gltf with a reduce target is prepared in process, and
whatever `BlenderPrep` names - or the C# reader refuses, or goes wrong there - is logged with its reason and prepared
by Blender's `prep_model.py` as before; a failure of the C# path never fails a bake. An independent reader went
through strip, the unused skins and the wiring against Blender's source BEFORE the PR: no difference in what is
written; its findings were the drill's (a strip that leaves nothing on an animated file would have failed falsely;
the strip COVER rows counted before the comparison; the Mono pass said more than it shows) and are fixed. Twelve
planted defects: eleven fail the drill, the twelfth changes no output (bone matrices computed for a stripped
armature are never used). The drill also runs its C# side under the Mono that ships with Unity - which is 32-bit and
NOT the editor's runtime, so meshes with normals are declined there; it holds the node list, the bone chain, the
assembly and the writer on the normal-less files under a second runtime, and nothing about the decimate. The answer
for the editor itself is the Bake Tests row *Does the in-process model prep match Blender's?*
(`BlenderPrepHeadlessTest`): every static registry entry that reduces is prepared in process AND by Blender, the
converter runs on both at the entry's grid, and its output folders must be equal byte for byte; an entry left to
Blender is a SKIP that says why. It needs Blender and is not in the push gate. `FULL=1` (293 runs on 99 files, 74 of them strip runs): 163 written and read by the converter as it reads Blender's - 43 of them stripped -, 106 left to Blender by name, 21 known prep failures (18 strips that leave no mesh), the canoe's 3. The drill's two sides now run as six processes each (`PREP_JOBS`; `tools/prep-drill/merge_shards.py` adds their rows up, and a shard that did not finish is a FAIL): the gate sample in 43 s where it took minutes, that FULL run in 27 minutes.
Not covered: district entries in that row (they prep through the same `UniversalBaker.Build`); a strip list that was
already broken on Blender's command line (a trailing backslash, a double quote) now strips what it says.
The review of PR #132 (2026-10-09). ChatGPT: the strip list was trimmed with .NET's `Trim()`, which keeps the
information separators U+001C..U+001F that Python's `str.strip()` takes - so a name between two of them stayed in the
model; the list is trimmed with Python's own white space now (29 characters, compared with `str.isspace` over all of
Unicode in Blender's Python 3.13: the same set), a line of `export_strip.glb.strip` holds it, and .NET's `Trim()`
planted in its place fails the drill. And `PrepInProcess` polls a 180-second deadline and the Bake Tests' cancel at the
prep's stages and inside the collapse loop (a time-out falls back to Blender like any other failure of the C# path;
a cancel passes through). That deadline asked for a measurement nobody had made - how long the prep takes: 18 s in
process against Blender's 29 s on a 3.6-million-triangle ship, 2 s against 7 s on one of 410,000 (the default target,
wall time of the whole prep). The same measurement showed the log line off by a few: `PREP reduce: tris a -> b`
counted the WRITTEN triangles, a twin face or two fewer than the modifier leaves, where `prep_model.py` counts before
the export (28,356 for its 28,365; the files were equal); it counts as Blender does now. `FULL=1` on that state: 294 runs, 164 written and read by the converter as it reads Blender's (44 stripped), 106 left by name, 21 known prep failures, the canoe's 3.

**Replacing `deploy_convert.py`, part 1: Blender's posed state** (2026-10-09). `deploy_convert.py` is HAF's own
algorithm on Blender's animation machinery: everything it decides and bakes starts from what `scene.frame_set(f)` makes
of the imported file. `tools/deploy_drill.sh` holds that: Blender dumps every object's `matrix_world` and its evaluated
`location`, `rotation_quaternion` and `scale` at up to thirteen frames of each file
(`tools/deploy-drill/blender_posed_dump.py`: before the first key, on it, after it, eight across the range, the last,
beyond it - fewer where those coincide), and the C# side rebuilds the importer's action (`BlenderPosedState.Import`),
sets the frames in the same order (`BlenderPosedState.Pose`), composes the matrices as Blender does
(`VehicleProbe.BlenderWorldMatrices`) and compares the BITS of all sixteen floats of each matrix and of every animated
property - with the action's frame range and WHICH objects the animation touches (the script takes those for its
parts). What it took, read from the importer, `fcurve.cc` and `anim_sys.cc` and measured: one fcurve per component, a
key at its time x 24 (in double, stored float32); quaternions that point away from the one before negated, then
interpolated component by component; `change * time / duration + begin` in float32 between keys, the end keys held
outside; a key within 0.0001 frame of the frame gives its value; and `fcurve.update()` MERGES keys closer than 0.01
frame, the last one winning at the earlier key's frame unless it sits on a whole frame - the T-62 has 332 such pairs
in each curve (times written both exact and rounded to the millisecond), and 9,376 of its 15,314 matrices were wrong
until that was ported. The first animation's action is the active one, found by its track name (`bpy.data.actions` is
sorted by name: comparing with its first row had a three-animation fixture's range wrong). Morph-weight curves sit in
the same action and count for its frame range.

The comparator validates complete matrix and property rows for every dumped object at every declared frame, with a
nonempty frame list. The gate removes one matrix, one property, every property row, or the frame list, and truncates
a matrix/property record; each plant must fail. `posed_pointer_cubic.glb` combines a pointer channel with CUBICSPLINE:
its unread pointer extends the action range, so the entire file is left to Blender without range or action checks
(`Action.RangeAndTouchedKnown` says so to a caller: the pointer's reason stands over the cubic sampler's, which alone
leaves the range known).

*The sign of a zero.* The first version compared values (`-0 == +0`) and said "bit for bit"; an independent review
measured `-0` here where Blender has `+0`. Compared as bits, four rules decide it, each read from the source and each
with a planted defect that fails: (1) `BKE_object_to_mat4` multiplies the rotation by the delta rotation and by the
scale matrix and adds the delta location - identities that change no value and turn a lone `-0` into `+0`
(`ObjectMatrix` writes the products out); (2) the importer puts every key through `rotation_after @ loc`,
`rotation_after @ rot @ rotation_before` and a matrix `@ scale` - identities again for anything but a camera or a
light, so glTF's `z = 0`, negated into Blender's `y`, is `+0` in the curve (the curves are now kept in Blender's
components; evaluating in glTF's axes and negating afterwards had 554 of the howitzer's 1,664 evaluated sets wrong);
(3) the short-way negation comes AFTER that, so a negated key's zeros are `-0`; (4) `BKE_animsys_write_to_rna_path`
does not write a value equal to the one held - and `-0` equals `+0` - so a property keeps the sign of the zero it
had, back to the last value that was not zero or to the import: the posed state depends on the frames set before
(`Pose` keeps that state; `TrsAt` alone gives the curves' values).

The gate sample: the fixtures with `posed.glb`, `posed_camera.glb`, `posed_pointer.glb` and `posed_pointer_cubic.glb`
(`fixtures.py --posed`: STEP keys, a matrix node, a doubled channel, a second animation, key merges on and off a whole frame, two keys at one
time, a curve of one key, the near-key rule, an animated morph weight that stretches the range, the four zero rules on
sampled keys) and the deploy sources under 40 MB; `FULL=1` adds the T-62, every registry model and recipe source:
121,055 object matrices and 15,703 evaluated property sets of 85 files equal. A COVER row at zero fails, and a file's
rows count only once the file holds. Twenty planted defects, all caught. LEFT to Blender by name
(`Action.NotModelled`): a CUBICSPLINE sampler (Bezier keys with automatic handles; its frame range and touched objects
are still held, its values are not) and `KHR_animation_pointer` (the reader carries no such channel; without the rule
the fixture reads as a file where nothing moves). Not compared, counted: a camera and what hangs below it (the
importer's camera correction is not in `BlenderWorldMatrices`; without the skip 24 of the fixture's 36 matrices
differ), an object under a bone and what hangs below it (it follows the armature's pose), a skinned mesh. NOT held, to
know: the properties of an object the animation does not touch (their matrices are, bit for bit; the importer's
corrections are not applied to the static values `BlenderWorldMatrices` starts from, and no matrix told the
difference); a scale key of zero (no file and no fixture has one); the frames in another order than ascending.
`BlenderPosedStateTests` hold each rule, the merge on the T-62's measured value. For part 2, named by the review:
`matrix_local` is the parent's inverse times the world (Blender's Eigen inverse), `matrix_world = m` goes through
`BKE_object_apply_mat4`, the frame range is per ACTION (a stripped object's slot, bones and weights count) and is cut
by `int()`. Next: the conversion's decisions against the script's own `DEPLOY` log lines.

**Replacing `deploy_convert.py`, part 2: the decisions** (2026-10-10). Everything the script decides before it builds
the armature: what the strip leaves, the frame range, the unit normalization, which objects are parts, the bone
slimming, the legacy or contract path, the degenerate cull, the bone-budget pair-merge, the stop when no part is left
(`BlenderDeploy.Decide`). The oracle is THE SCRIPT ITSELF: `tools/deploy-drill/blender_decisions_dump.py` executes
`deploy_convert.py` cut at the line where it starts on the armature, so no rule is restated on Blender's side, and
writes what it printed, what its variables hold and the scene it left. The second half of `tools/deploy_drill.sh`
compares, per job: the log lines to the letter (Python's `%.3f` is an exact half-to-even rounding - `PyFormat.Fixed`;
`%-40s` pads by code point; the culled names are a Python `repr`), the frame range, the normalization's five numbers
as the bits of each double, the path, the parts with their parents in the script's order, the culled parts, the
merges, and EVERY object left - order, type, parent, datablock name, action - with its `matrix_world`, its location,
rotation and scale and a mesh object's `bound_box`, as bits. The jobs: the project's recorded conversions
(`Assets/FactorySource/*/deploy_converted.args.txt`: the source and the arguments the Factory gave) and 43 fixture
jobs (`deploy_fixtures.py`, `deploy_fixtures_review.py`). 47 jobs, 1,037 log lines, 1,997 objects equal; the
helicopter, the towed howitzer and the T-62 (1,179 objects, slimmed from 1,032 parts to 139 and merged to 124)
matched on the first run.

What the scene does, measured and read from Blender's source: `bpy.data.objects` is ordered by name, the UTF-8 bytes
as unsigned values with upper-case ASCII folded - the order of the survivors, the parts, the roots;
`bpy.data.objects.remove` leaves a removed object's children as ROOTS with their own transform, and its name free;
the importer's bone shape (`Icosphere`, radius 1 at the origin) is a mesh object like any other - the canoe's strip
list does not name it, and its vertical offset of 1.00 is that radius; `o.matrix_world = keep` is
`BKE_object_apply_mat4` - the parent's inverse, `mat4_to_loc_rot_size`, `mat3_normalized_to_quat`: the object's
transform is REPLACED by a decomposition (a mirrored root comes out with three negative sizes, a sheared one
changed); mathutils sums a `Matrix @ Vector` row in a double over float32 products, and `Vector.length` is the double
root of a double sum taken from the last component.

*Review before the PR* (an independent source-reading agent, 31 fixture jobs of its own): eight defects, each
executed. Two reach a plausible model. A mesh with MORPH TARGETS: `bound_box` is the evaluated mesh, shape keys
applied, and the port read the base positions (`dim 4.000` here, `31.000` in Blender) - now left to Blender. Names
past ASCII: the order came from `BlenderExportTree.StrCaseCmp`, which reads the bytes as SIGNED and put `Émile`
first where Blender has it last - `BlenderDeploy.IdNameCmp` now (the exporter's child order uses the signed one and
was never measured on such names: open, in the backlog). Then: a node outside the scene the file names is in an
excluded collection and frozen where the import left it (left to Blender); `EXT_mesh_gpu_instancing` makes an object
per instance (left); the normalization root's name after a strip took an object of that name; `%-40s` by code point;
the culled list's order by code point and its `repr`. Its fixtures are in the gate.

The no-parts abort must have exactly one `EXIT` record with code `1`; a successful exit, a truncated record and a
duplicate record each fail a regression check. Missing-record regressions use matching one-job lists, first prove
their intact controls pass, and then require the failure for the specific removed record. This prevents unrelated
missing jobs from satisfying a negative check.

A job may fall back to Blender only where the jobs file marks it `LEFT:` - the first plant run had SEVERAL wrong strip
rules end in a fallback and pass. Marked: the dugout canoe (ten objects hang from animated bones: the armature's pose
is not modelled), a skinned survivor, the morph, scene and instancing fixtures. 54 of 55 planted defects fail; with
the fallbacks planted away, each of those jobs fails too, so every fallback is needed. NOT caught: whether the pose
hears of the transform the script writes (`Pose.Write`) - it can differ only in the sign of a zero and no fixture
tells the two apart. Not judged: `SiegeHowitzersCar` (its source, `D:/Downloads/m114_howitzer_in_action.glb`, is
gone; the drill names it - the same model at another path is `TowedGunHowitzers`). Not held, to know: a name with a
TAB (the dump is tab-separated; `BlenderDeployTests` holds the `repr`); a zero-key sampler (invalid glTF: Blender
still gives the object an action); the cull's parent inverse asks a double determinant "is it singular" where
Eigen's is float32; lights and cameras that survive are left to Blender unjudged. The gate removes a log line, a
matrix, a transform, a box, an object, a part, the range or the end row from the real dump: each must fail. Next:
the armature's pose (the canoe), then part 3, the armature's rest and the binding.

**Replacing `deploy_convert.py`, part 2b: the armature's pose** (2026-10-10). The dugout canoe was left to Blender:
ten of its objects hang from bones the animation moves. `VehicleProbe.BlenderPose.cs` now gives an imported
armature's pose and what hangs from it. The importer: `prettify_bones` turns every bone's edit rotation by a quarter
turn and cancels it in the node's own transform (the bone's vnode gets the turn as `rotation_before`, every child of
it - bone or object - the conjugate as `rotation_after`; a bone's scale has Y and Z swapped); a pose bone holds the
transform RELATIVE to its edit bone (`er^-1 @ (t - et)`, `er^-1 @ r`); an object under a bone is parented to the bone
and moved back by `bone_length` (the vnode's Python value, not the scaled Blender one); every key goes the same way.
Blender: `BKE_pchan_to_mat4`, `BKE_armature_mat_bone_to_pose` (a child on its parent's POSE matrix times the bone's
offset matrix, the location column through the same matrix apart), `ob_parbone` (the pose matrix moved to the bone's
tail). The posed drill now dumps and compares every bone at rest (parent, length, `matrix_local`), every pose bone
at every sampled frame (location, quaternion, scale, pose matrix) and no longer skips an object under a bone: 88
files, 121,774 object matrices, 9,153 pose bones equal. The canoe's 21 bones and ten bone-parented objects matched on
the first comparison, and its decisions job passes without the `LEFT:` mark - 43 log lines, 37 parts, 124 objects:
every recorded conversion whose source exists is now decided in C#.

Rest records must identify every imported bone exactly once, with all 29 numeric values. Pose records must identify
every bone at every declared frame exactly once, with all 26 values. The drill validates this evidence before choosing
which animations it can compare. `missing_bones.py` constructs ten negative cases from the real `posed_bones` dump:
missing, duplicate, truncated and oversized rest/pose records, an unknown pose bone, and all poses removed. An intact
control must first pass, and each mutation must fail for its specific defect.

Found on the way, each with a fixture: an armature that is STRIPPED leaves what hung from its bones as roots (the
bone link goes with the parent); the importer's bone shape is evaluated only while its armature is there (its
collection is hidden) - with the armature stripped its `matrix_world` stays the identity though it is parented to
the normalization root. *Review before the PR* (an independent agent, 252 files through the posed drill and 322
decision jobs, 200 of them fuzzed): (1) a property's history does not start at the importer's value - when the
import returns, every animated property already holds the action at frame 1, so the chain a zero's sign runs
through is static, frame 1, then the frames set (`Pose` evaluates frame 1 when it is made; part 1 had this wrong for
plain objects too, a pose bone's rest location of `er^-1 @ 0` with its `-0` made it show); (2) an armature under
another armature's bone read its unposed matrix when its node came later in the file (`BlenderWorldMatricesPosed`
resolves what a node hangs from first); (3) an armature with a zero scale component: Blender's importer divides by
it and fails - left to Blender, including scales decomposed from a matrix and TRS values that underflow float32
(three regression cases); (4) OPEN, older code: `GlbReader` reads the JSON token `-0.0` as `+0` (.NET
Framework's `double.Parse`), Python keeps the sign - it shows in a location's zero only, real sources carry such
tokens and pass because the matrix's `loc + 0` and the corrections mask it; in the backlog. 24 of 26 planted defects
fail; not caught, both a zero's sign only: a pose bone's matrix built with the object's delta product, and a bone's
history started from the node's own transform. Not held: a camera or a light under a bone. Next: part 3, the
armature's rest and the binding.

**Replacing `deploy_convert.py`, part 3: the armature and its anchors** (2026-10-10). What the script BUILDS before
it bakes, from its `# --- 4. armature` to the call of `bpy.ops.nla.bake`: the armature (`DeployArm` on the legacy
path, `DeployArmV2` on the contract one - object and datablock names made unique as Blender makes them), an edit
bone per part at the part's world translation with its tail 0.1 up Z, the parts' DIRECT parents mirrored onto the
bones, `bone_of` (a pair-merged part rides its neighbour's bone - an entry that joins only AFTER the parents are
mirrored, so a part whose parent was merged away has a root bone), the `StaticRoot` bone and what it is constrained
to (the first mesh no bone carries: its parent, or the mesh itself), and the root-motion anchor (the bone-carrying
node of the mesh with the biggest `dimensions` volume; its travel over every seventh frame and the last; the
armature parented to it with `matrix_parent_inverse = matrix_world.inverted()` when it travels more than a tenth of
the model). The oracle's cut moved from the armature step to the bake (`blender_decisions_dump.py`), so ONE run of
the real script covers parts 2 and 3; the drill compares, besides everything of part 2, the armature's name, the
bone of each part, every bone at rest (parent, `head_local`, `tail_local`, length, `matrix_local`), both anchors,
the travel and the model size as the bits of their doubles, the parent inverse - and the armature object's own row
among the objects. 63 jobs, 1,218 bones at rest, 2,612 objects equal; the T-62's anchor (`T_62_body.003`, 12.57
units against a model of 9.61) and its parent inverse to the bit. Leaving edit mode is the importer's code path:
`VehicleProbe.RestFromEditBones` is now shared with it (the probe, prep and posed drills hold it as before).

What the first comparison showed: `head_local` and `tail_local` are the EDIT bone's own values, never recomputed,
while `matrix_local` goes through the parent's inverse and back - a child bone's matrix may sit an ulp beside its
own head (two bones of 843, one in the canoe). *Review before the PR* (an independent agent, about 6,000 generated
rigs through the real script): (1) the travel gate's model size is `max(mx - mn)` WITHOUT the guard the
normalization's size has (`mx.x > mn.x`) - a model flat in X has size 0 there and its Y/Z extent here, the port
reused the guarded one and anchored on any travel; (2) `Matrix.inverted()` RAISES on a float32 determinant of zero
- the script dies, the port now leaves such a job to Blender (not drillable: Blender cannot run it; the reviewer's
probe showed it); (3) two wrong ports passed every job: the merged part's bone known before the parents are
mirrored, and the `dimensions` axis length summed in another float order - fixtures `chain` (140 links, each the
child of the one before) and `ties` (forty parts of one mesh; four of eighty seeds pick another anchor under that
mutant) are in the gate. 27 planted defects, all caught. The gate also removes a bone, a part's bone, either anchor
or the parent inverse from the real dump: each must fail. Not in this part: binding the meshes (the script does it
after the bake and the options); the order of `arm.data.bones` (compared by name; the order is the export's, part
5). Next: part 4, the bake.

**The Clip Range picker's in-process rig** (step 4, 2026-10-03): `HafUnityFrameTests` hold the preview frame's arithmetic
without Unity — the X mirror's conjugation of rotations, matrices and matrix nodes; Unity's skinning formula fed the rig's
matrices (the joints' world matrices, the inverse bind matrices, the vertex, all mirrored) against the reader's posed vertex;
Blender's track names. The Bake Tests row *Does the glTF clip player match the reader?* (`HafModelRigHeadlessTest`, also in the
headless lane) builds the rig for every registry model and fixture in Unity, samples each clip at its first and middle frame,
bakes every skinned mesh through Unity's own skinning and sets every vertex beside the reader's pose within
`1e-4 * max(1e-3, largest absolute coordinate) + 1e-4`, calculated separately for each pose.
The `clip_switch` fixture holds restoration of both other nodes and other properties
when switching clips; `path_clip` holds animation binding for node names containing `/`; `unicode_clip` holds the actual
Unity clip names against Blender's whole-character, 63-byte truncation; `far_clip` holds independent tolerances for a
distant pose and a later pose at the origin. Unit tests also cover multibyte track names and collision suffixes.
What no drill outside Unity can hold is the PREVIEW the Lab builds from the probe's parts: the Bake Tests row *Does the
in-process probe preview match the Blender preview?* (`VehicleProbePreviewGateTest`, no Blender needed) sets it beside
Unity's import of the preview FBX the last Blender probe left for the same source, part for part by name — bounds centre
and size within 1e-3 of the extent, the facing of the faces — and fails on a leaked Unity mesh. A single-part FBX
root is matched by its mesh's original part name, because Unity renames that root to the file name. The allowance
for stale previews is at most five percent of distinct matched parts; a mismatch on a one-part model fails.
The sheared-child job holds the bounds of children and descendants after detachment. The Lab routes geometry the
kernel does not yet evaluate (morph targets, bone-parented meshes, animated second models and animated/placed
first-model skinned meshes) through Blender; `VehicleProbeMergeTests` holds this routing policy.

**`VehicleProbePreviewHeadlessTest.Run`** runs in Unity with `-batchmode -nographics -executeMethod
VehicleProbePreviewHeadlessTest.Run`. It checks the preview frame against coordinates measured from Blender's FBX
export imported in Unity, normals perpendicular to a nonuniformly scaled panel, and destruction of both complete
and partially built previews. It also checks material colours against the imported FBX's sRGB values (Unity's FBX
importer converts Blender's linear colours on import: measured in the project's import cache on the Salegs Revenge's
probe FBX, 0.137255 -> 0.4062), BLEND/MASK/OPAQUE
alpha handling, brightness preserving alpha, imported root names, and rejection of reversed winding on a single
part while allowing five percent of stale parts in larger models. Run it in a Unity project with the HAF editor
package installed; it exits nonzero
if an invariant fails. The probe comparator's normal rays retain Blender's forward-matrix arithmetic; rendered
surface normals use the inverse transpose.

A file too large for Unity's 32-bit standalone Mono is probed on the 64-bit .NET runtime instead, and the drill says so. The names come from a port of the
importer's own tree construction (`compute_vnodes`: creation depth-first from the parentless nodes in index order,
armatures at the joints' deepest common ancestor, skinned meshes moved or split off under them, meshes on bones moved
to children named after the mesh, cameras taking names first) and of Blender's two unique-name rules (a datablock
takes its base's smallest free number, a bone counts up from its own tail — `main_namemap.cc` and
`BLI_uniquename_cb`, read from Blender 5.1's source); twenty-eight **naming fixtures** (files, as the drill counts them)
(`tools/vehicle-probe-drill/naming_fixtures.py`) hold one rule each, and Blender confirms every one. Seven of them
hold the unique-name rules, a case per branch of the source (non-ASCII and overflowing tails, 255- and 63-byte
limits, past 1,023 duplicates, numbers used up, a purged object freeing its number): the first port was one rule for
both, right on every real file and wrong for every duplicate with a numeric tail of its own. Five exist
because no real file had the shape: a self-review found that not one of 105 files carried a second skin, a camera whose
name clashes, a non-unit rotation or a taken loose-part name — and four of the five fixtures built for those failed
against Blender before the code was fixed. A PASS on every real file proves the rules the files exercise, no more.

**The recipes are the product's own oracle**: each stores the parts Blender's probe listed when it was saved (5,214
parts across 22 recipes). `recipe_check.py` sets every stored part beside the C# probe of its source. A recipe is user
data and can be STALE (saved before its source was re-cut or re-fused), so a difference is not a verdict by itself:
the source of every differing recipe is put to Blender as it is today, and only a C# row that differs from
**Blender's** fails.

Full run (`FULL=1`): **105 of 105 files, row for row** — 31 registry sources, 20 recipe sources, 31 Khronos samples,
23 fixtures; 9,793 rows; C# 9.8 s (reading 1.7 GB included), Blender's probe 91 s in one process without the preview
export. Recipes: 17 of 20 the same; the other three differ from today's Blender exactly as they differ from the C#
probe (stale). The visibility (3b) and inside-out (3c) verdicts are compared since 2026-10-02, as above; the
inside-out field is the one with three parts said and not solved.

What the comparison taught, kept as rules in the code: Blender keeps only the vertices a primitive USES; its import
state is a blend of every clip one frame in (hence `posestart=1` — on the registry's rigged files a sail's box read
77 units off, at the folded pose of another clip); a part's box is its LOCAL box's corners through the world matrix;
a node given by a matrix is that matrix decomposed and recomposed (a Lab source's sheared matrix moved four boxes by
0.15 %); a skinned part's box is posed, a rig bone's is at the importer's guessed bind pose, through the armature.
And what the Lab's real sources taught the READER, which the registry never had: 2,133 primitives sharing 162 vertex
accessors (decoded per primitive: 4 GB — one array per accessor now, and the writer writes it once), and a source that
*requires* `KHR_materials_pbrSpecularGlossiness` (read and carried now: its effect is a material's payload).

**`Tests/GlbRobustnessTests.cs`** (2026-10-02): the reader and writer beyond the happy round trip — a **corruption
sweep** (the full fixture truncated at every 4-byte boundary, every byte of its JSON chunk replaced four ways, every
byte of its BIN chunk set to 0xFF, ten broken containers, a `.gltf` with missing or malformed sidecars) where every
outcome must be a read or a refusal by name, never a crash-type exception, and whatever the reader accepts the writer
writes or refuses by name (planting the removal of one accessor bounds check: the sweep fails on `ArgumentOutOfRange`
at JSON byte 1595); the written bytes are the same under the invariant, Dutch and Turkish cultures and a Dutch read
gives the same model; eight threads reading and writing at once get the sequential bytes.

**`tools/workshop_compact_drill.sh`** (2026-10-02) is the proof that the Workshop's compaction leaves out only what
no node can reach. The real `GlbDisconnectedParts.Compact` runs over every `.glb` in the folders of the Lab's recipe
sources — the Workshop's outputs as the operations wrote them before they compacted, orphans and all — and over the
fixture library; and through a real operation (the first mesh node removed, which orphans its mesh and compacts on the
way out) over the fixtures and the Khronos samples: other exporters' layouts — interleaved, sparse, skinned,
animated — that no Workshop file has. For every file something was left out of, the **GLB reader**, which shares no
code with the Workshop, reads the file as it was and as it is: the first, less the meshes no node uses, must equal the
second field by field (`HafModelDiff`); a second compaction must find nothing more; and **Blender** imports original
and copy of a sample (every pair under `FULL=1`) and must report the same for both *as printed* — the same importer on
the same live data, so "close" is not accepted. A refused compaction (an extension the tool does not follow, or
nothing would remain) is listed, not failed. First full run (2026-10-02): 28 of the 91 Workshop files had something
to leave out, 1,322 → 757 MB; 24 of the 35 fixtures and samples compacted after a part was removed; Blender agreed on
all 29 pairs. Unity ships its standalone Mono as a 32-bit process, so the two largest sources are verified on the
64-bit .NET runtime instead, and the drill says so. `Tests/GlbCompactTests.cs` holds one
hand-built fixture per branch of the compaction (a view two meshes share, an interleaved view, a shared vertex
accessor, skin/animation/image data, a sparse accessor, unused bytes with every mesh live, the refusals), each judged
by the same reader comparison.

**`tools/check-exit-status.sh`** (2026-10-02) exists because a drill's verdict was twice read from the END of a
pipeline — `CMP=$(python compare.py … | tr -d '\r'); crc=$?` — where `$?` is `tr`'s and always 0. The vehicle probe
drill carried that line from PR #112: for a day the gate that says the C# probe's rows equal Blender's printed the
comparator's FAIL lines and then PASS (it had no failure to hide in that time: 47 compared, 0 failed, once it could
fail). The compaction drill was written with a copy of it, which an external review caught. The guard refuses the
shape — a `|` and then `; name=$?` on one line — in every gate script, and checks its own pattern against the line
that cost us first. It does not see a status read on the next line or a verdict never read at all; it is a net for
one shape, not a proof.

**`tools/glb_writer_drill.sh`** (2026-10-01, step 2) first checks every **source**: each `extensions`/`extras`
object must sit at a path the model carries (`tools/glb-reader-drill/carried_paths.py`; the reader does not model the
rest, so the writer would drop it and a reader-vs-reader compare could not tell) — else FAIL by file and path. Then it
writes every registry model and the fixture to disk with the real `editor/GlbWriter.cs`, reads each written file back
and compares it with its source **field by field** inside the drill (every vertex of every attribute, every index,
material field, texture, sampler, image byte, skin matrix, animation key, the asset's extras and the scene's name; the
first difference is named: `DIFF <file> <field>`), writes the read-back model again and compares the bytes as they
stream (deterministic on real files), then reads the written files with the reader drill (every value must equal the
original's) and has Blender import the written sample (every value must equal what the C# side read from the
original; `FULL=1` for all). Full run: 32 of 32 both ways, 726 MB written to disk in 0.8 s. It found that material
extension payloads (`KHR_materials_specular`, clearcoat) carry texture references Blender needs — six files lost
images until the model carried a material's `extensions` verbatim; the field compare then found 12 files whose
sampler settings (filters, wrap) came back as glTF defaults, and the source check found 6 files whose `asset.extras`
(Sketchfab author and licence) were dropped — both carried verbatim now (the value summaries could see none of it).
Each guard was proved by planting the drop it names and watching it fire.
Counting alone could not tell a wrong matrix chain. The pose both sides evaluate is **animation 0 at time 0**
(`HafTransforms.PoseAt`; Blender with its NLA tracks dropped and the active clip at frame 0 — its untouched import
blends every clip through the NLA, a pose no file defines), and a skinned vertex goes through the spec's weighted joint
blend over both influence sets on both sides (`HafTransforms.WorldPositions` / `WorldNormals`; Blender's armature
modifier). Measured on scp-682: Blender's untouched import is the blend (area 384.6); the undeformed mesh (394.6)
appears only once the pose is reset — a reset the drill once did itself, which had made a wrong rule look right. A sample by default (the largest, the smallest, the
animated ones), every file with `FULL=1`; the comparison and its tolerances are `tools/glb-reader-drill/compare.py`. SKIP
without the project (hosted CI) or without Blender. Full run 2026-10-01: 31 of 31 unique files agree on everything; the
reader took 1.7 s for 784 MB, Blender's importer 14.4 s plus its boot.

### Schema parity is in-repo and mandatory

The editor package, shared `Haf.Schema`, runtime `ModelEntry`, and regex fallback now live together. Therefore
`tools/check_schema_parity.sh` has no sibling checkout and no best-effort `[SKIP]` path: every push compares the
writer, generic reader, regex fallback, GUID hand-lists, and shared types from the same revision. A missing fallback
key is still dangerous because malformed JSON can silently lose it, but the guard is now symmetric by construction.

The parity and hand-list guards were **fault-injected before being trusted**: a UI-edited field with no ownership-list
entry and a deleted `Regex.Matches` line each turned the matching step red and named the offending field.

### The docs guard (`tools/check-docs.sh`)

The docs publish **three** ways — the repo, the [Pages site](https://sswelm.github.io/HumankindAssetFramework/)
(which rewrites relative `.md` links via `jekyll-relative-links`), and the wiki (`tools/sync_wiki.sh`) — and all
three resolve *relative* links. So one moved page breaks three surfaces at once, silently. The guard checks:

1. **every relative Markdown link and `#anchor` resolves** — anchors are derived from the target's headings using
   GitHub's lowercase/punctuation/whitespace and duplicate-suffix rules;
2. **every page in `docs/notes/` opens with the `ARCHIVED NOTE` banner** — the convention that makes the
   maintained-vs-archived split mean something rather than being a folder name;
3. **no basename collides across `docs/` and `docs/notes/`** — the wiki page namespace is flat, so a collision
   would have one page silently overwrite the other;
4. **schema version and the shared-field count agree with code**, rather than with another prose copy;
5. **maintained Pages docs do not use `../` links** that Jekyll would publish outside the project site;
6. **a fresh wiki generation succeeds**, contains no empty/missing internal targets, and its tracked sidebar includes
   every maintained page;
7. **retired pre-package claims stay out of current guides** (cross-repo editor paths, missing helpers, hardcoded
   guest identity). Historical review pages remain intentionally untouched.

Fault-injected: a dead file link, a valid file with a nonexistent heading anchor, a banner-less note, and a planted
`docs/notes/Textures.md` collision were each caught with a named failure, and the baseline returned to green.

Run it any time by hand: `bash tools/check.sh`. **Enable the hook once per clone:**

```
git config core.hooksPath tools/git-hooks
```

(Casing matters on case-sensitive filesystems: this repo's folder is lowercase `tools/`; ENCReload's is `Tools/` —
the mismatch has already eaten files once, commit `db40e73`.) The hook (`tools/git-hooks/pre-push`,
version-controlled) then blocks a failing push; bypass only in a real emergency
with `git push --no-verify`. Deliberately **not** in the gate (too slow / need Unity, Blender, or the game): the Blender
golden-master `deploy_regression.sh`, the in-editor bake tests, and the in-game binding report — those stay manual.
So does `python tools/drill-merge2.py` (six Blender launches, 1–2 min): it generates its own fixtures and drills the
Vehicle Lab's second-model merge through the real `vehicle_rig.py` — a glTF morph target keeping its position,
per-axis scale under a rotated root, a mirrored source facing outward. Run it after touching the merge block.

### Wiki publication after CI

The public GitHub Wiki is a separate Git repository, so merging Markdown into `master` does not update it by itself.
The CI workflow's `publish-wiki` job closes that gap: after `build-test` succeeds on a **push to `master`**, it clones
the wiki into the runner's temporary directory, runs `tools/sync_wiki.sh`, stages the generated result, and pushes only
when the staged tree changed. A non-documentation merge is therefore a no-op.

The write boundary is intentional: the workflow defaults to `contents: read`; only this job receives
`contents: write`, and its event condition excludes pull requests. The sync generator still owns stale-page cleanup and
link validation (sidebar coverage is enforced by `check-docs.sh` in `build-test`), so a broken generation fails before
the wiki commit is created.

The in-editor tests all run from **one window** — `Tools ▸ HAF ▸ Bake Tests…` (Smoke / Features / Conversion rows,
each with a plain-language explanation, live per-row PASS/FAIL, and a durable `Logs/haf_bake_tests_report.txt` per
run; see [Factory-Manual.md](Factory-Manual.md) §11 — including what SKIPPED means, what a fresh
package install reports, and which rows need Blender). The
gate earned its keep on day one: standing it up surfaced three latent schema drifts (a wrapper field the plugin read but
the baker never wrote, two runtime-only keys, and a `float?`-cast the parity script mis-classified), all fixed to green.

### The hand-list gate (`tools/check_handlists.sh`) — four blocks

This project's signature bug class is a **hand-maintained list of fields that must stay in step with a type**. Each
instance shipped a real bug before it was gated, and each gate compares the list against the type mechanically, so
the next added field fails the push instead of being silently dropped:

| Block | The list | The bug it shipped |
|---|---|---|
| Factory ownership rebase | every UI-edited field re-applied on Save | `combatZ` silently reset to 0 the day it landed |
| Animation Lab ownership rebase | same shape, the Lab's 63 fields | same class |
| Vehicle Lab recipe round-trip | every `Recipe` DTO field written **and** restored | the canoe's wave config vanished; took GLB forensics to recover |
| Clone GUID reset (2026-08-22) | every `int[4]` GUID cleared on a copy | `clipIdleAlt2` inherited, pointing the clone at the **source's** ClipCollection |

Each block is drilled the same way: plant the omission, watch the gate name it, restore. A gate nobody has seen
fail is not yet a gate.

### The dead-sentinel gate (`tools/check-member-shape.sh`)

Sibling of the dead-default `TryParse` gate, one layer down. The banned shape:

```csharp
bool loaded = true; try { loaded = Convert.ToBoolean(GetMember(unit, "IsLoaded")); } catch { }
if (!loaded) continue;
```

`GetMember` swallows its own exception and returns **null** for a missing or renamed member — and
`Convert.ToBoolean(null)` is `false`, `Convert.ToInt32(null)` is `0`. **They do not throw.** So the `catch` never
runs, the initializer is dead, and the variable takes the converted-null value instead of the default written
beside it. Two live sites had exactly this and then skipped their work on it: on a game rename the respawn pass
and the vanilla re-scale would each have stopped running, silently and permanently.

The fix is the one the `ParseFloat` policy already states — **the fallback is a return value, never a variable the
call can overwrite** — plus a `Try*` pair for the sites whose intent is *"if I cannot read this, leave the thing
alone"* (they used `catch { continue; }`, which never fired either):

```csharp
if (!MemberBool(unit, "IsLoaded", true)) continue;              // fallback returned
if (!TryMemberLong(br, "AxisIndex", out long axis)) continue;   // absence is a state you can branch on
```

**This does not replace the binding catalog.** The catalog is what makes a rename *loud* at startup; this stops a
call site from advertising a local defence it never had, so the two are not mistaken for one another.

Drilled on the day it was written (2026-08-23), and the drill paid immediately: the first version of the gate
caught the `catch { continue; }` form but **missed the headline dead-initializer form** — its regex could not
cross the `;` inside `GetMember(…);`. A planted violation exposed that in one run. The corrected gate then found
**ten more sites the hand review had missed**, all two-line declarations that a single-line grep never saw, and
one **false positive** — `Convert.ToInt32(GetMember(o, "Count") ?? -1)`, where the `??` supplies the fallback
before the convert, so that sentinel really is reachable. Excluded by name, because a gate that cries wolf on
correct code is a gate people learn to bypass. Tests: `Tests/MemberReadTests.cs`, including an **oracle** test
asserting `Convert.ToBoolean(null) == false` — the premise the whole bug class rests on.

### The binding-catalog surface guard (`tools/check-catalog.sh`)

`bindcheck` (below) validates every binding **in** the catalog, so its green light is a statement about the catalog —
not about the code. On 2026-08-21 a review measured the difference and found **84 member names read by name at
reflection call sites that were not catalogued**, several on functional paths behind silent catches
(`FacingAngleOffset`, `IdleAudioEvent`, `CurrentTechnologicalEraIndex`, `BonesCount`) — the CHANGELOG had claimed full
coverage on the strength of a *hand* sweep. This guard makes the claim mechanical: it extracts every string literal
passed to a by-name reflection accessor, subtracts the catalog, subtracts an allowlist where **every entry states its
reason** (Unity/BCL names; a handful of *tolerant probes* that try several names and cope with all absent), and fails
on the rest. Pure source analysis, so it runs in the fast gate. Fault-injected on the day it was written: dropping
`BonesCount` from the catalog, and adding a new uncatalogued site, were each caught by name.

Together the two are the whole claim: **covers the code** (this) **and resolves against the game** (bindcheck).

**Its blind spots are its real failure mode, and it has had three (2026-08-22, 08-23, 08-23).** This guard is a set of
regexes over source, so a call shape it does not match is not reported as unchecked — it is silently *not counted*, and
the pass line still says "all N catalogued" with a smaller N. Each time, the gate went green while the thing it exists
to catch sat in plain sight:

| found | shape it could not see | scale |
|---|---|---|
| 08-22 | `GetMember(GetMember(x, "Inner"), "Outer")` — pass 1 stopped at the first `)` | `TagAsAbilities`, read that way and only that way |
| 08-23 | the `CachedField` / `GFA` accessor family, newly added | 16 sites |
| 08-23 | `AccessTools.Field(x.GetType(), "name")` — pass 1's accessor list stopped at HAF's own helpers, and pass 2 gives up at the first `)` | **146 sites, 70 names** |

The third was not found by the gate, by `bindcheck`, or by review. It was found by a **log line**: `allMeshNames` missed
36 times in one session, and `typeprobe --exact` then said no assembly in the game declares that field at all — a dead
probe whose fallback branch rebuilt an array by reflection and discarded it. The gate had been reporting "all 331
catalogued" while never looking at the site. Drilled by putting a bogus member name in that shape: the shipped gate said
`OK — all 331`, the widened one failed.

The lesson generalises past this script: **a guard that filters before it counts cannot report its own blindness.** When
adding an accessor helper or a new call shape, add it to `extract()` in the same commit — and prefer a drill that injects
a bad name *in the new shape* over one that just re-runs the gate.

## Headless binding drift check (`Tools/check-bindings.sh` — for game updates)

A **different trigger** from the push gate: that guards HAF *code* changes; this guards *game* changes. After a Humankind
update, run:

> **How you learn a game update happened at all (2026-08-23).** Two signals, and the second closed a real blind spot.
> An update that **breaks** a binding was always loud — `HealthMissing > 0` puts a red banner at the top of the F8
> window naming exactly what broke. An update where every binding still **resolves** used to be *silent in-game*:
> `HealthSummary` was nulled, the banner is gated on `HealthMissing`, and the only trace was one log line and
> `haf_bindings_report.txt`. But resolution succeeding proves the *names* survived the update, not the *behaviour* —
> which is precisely the state where an update-caused oddity gets blamed on HAF. So an amber advisory now shows
> whenever `Application.version` differs from `GameBinding.VerifiedGameVersion`, naming both builds and pointing here.
> It is advisory by design (fail-soft) and silent on a verified build. The decision is a pure function
> (`VersionAdvisoryFor`) so it is unit-tested without a game, including the cases where it must say **nothing**: an
> unreadable version or an unpinned catalog are "no information", and a `?` in an advisory trains the reader to ignore
> the line. **`VerifiedGameVersion` is hand-updated** — bump it in the same commit as a re-verification, never before.

```
bash tools/check-bindings.sh [<…/Humankind_Data/Managed>]
```

The `bindcheck` tool (net8, `System.Reflection.MetadataLoadContext`) validates **every `GameBinding` catalog binding
against the build's assemblies without launching the game** — it reads `Patches/GameBinding.cs` directly (always in sync,
no manifest to stale) and inspects the game DLLs reflection-only (Unity's native deps don't matter). It prints
`bindcheck: N/N types | M member(s) missing` and exits non-zero on any drift, so a game patch's binding breakage is named
**headlessly** (CI-able on a version bump) instead of found by launching and reading `haf_bindings_report.txt`. It
evaluates the **derived** accessors too (`CachedDerived(... ElementType / FieldOrPropType / MethodParamType ...)`) along
the same chain the runtime walks — since 2026-08-21; before that it fell back to a bare-name lookup for them and
false-positived 7 of 12 on a clean build. Verified both ways: `91/91` clean on the pinned build, and it correctly flags
an injected fake binding — and, closing the catalog on 08-21, it caught five mis-attributions of mine before any launch
(`importAngles` on the wrong type, a member that exists on no assembly, three that live on runtime subclasses the
declared field type can't see). Its sibling **`tools/typeprobe`** (`dotnet typeprobe.dll <Managed> <Type>…`, or `--find <substring>` to list every
type/method/field/event whose name contains it — how the end-of-loading seam `LoadingScreen.VisibilityChanged` was
located for the load-tier smoke) dumps a game type's real field/property layout from the DLLs — it answered "why did `PawnFast` stay on reflection?"
(`HideFactor` is a packed property) and "where do a squadron's pawns live?" (`PresentationAirPatrolController`)
without a launch. It's the headless twin of
the in-game report — same catalog, no game needed.

## What it covers

| Function | Lives in | What's asserted |
|---|---|---|
| `ParseModels` | `UniversalInjectPatch.cs` | JSON→`ModelEntry` mapping via the generic `ToObject<ModelEntry>()`; omitted keys fall to the **shared `HafModelSchema` initializers** (`idleAltInterval` 25, `turretAxis` -1, `scale`/`brightness` 1); the **`position` Vector3** parses (Newtonsoft chokes on raw Vector3 — the strip-then-repin path is what's under test); signed GUID components; **per-object isolation** (an omitted field doesn't shift onto another model); **robustness** — garbage/empty input → empty *without throwing*; the regex fallback recovery when `JObject.Parse` rejects the document (keys entry count on `Min(pawnDescription, skel, atlas)`) |
| `ResolvePacks` | `UniversalInjectPatch.cs` | duplicate-modId reject (first file kept); `dependsOn`/`loadAfter` ordering; missing-dep skip + **transitive strand** (fixpoint); cycle → file-order + note; soft `loadAfter` to an absent modId; **stable seed order** (the invariant that keeps today's single-pack setup byte-identical) |
| `LongestMatch` | `UniversalInjectPatch.cs` | most-specific substring wins (not first-in-order); single-match fallback; no-match → null |
| `RegexStrArray` | `UniversalInjectPatch.cs` | wrapper string-array extraction; empty-item filtering; missing field → empty |
| `CoreDesc` | `UniversalInject.Combat.cs` | trailing `_NN` variant-suffix strip |
| `GuidToLong` | `UniversalInject.Combat.cs` | null / non-numeric → 0; numeric string parses |
| `EraFromName` | `UniversalInject.ScaleEra.cs` | extract `EraN` (case-insensitive, multi-digit); none/null → −1 |
| `EraAnchorFor` | `UniversalInject.ScaleEra.cs` | the Global Era Lab anchor rule — **a unit stays at 1.0 unless an authored grid cell says otherwise** (own-age-or-earlier → 1.0; later-but-unauthored → 1.0; non-positive eras clamp cleanly) |
| `GameBinding.Validate` / `Cached` | `Patches/GameBinding.cs` | the startup **reflection compatibility report** — resolves the catalog (~124 type + member bindings across the load-bearing injection path) incl. the simple-name (`Type.Name`) fallback scan, and writes a diffable `haf_bindings_report.txt` every launch; a game-update rename is *reported* (one `[MISSING]` line, headless-checkable), not silently absorbed. The report is self-validating: an added binding that isn't a real game member shows `[MISSING]` on the known-good build. |
| The four **live dials** | `Patches/DialConfig.cs` | `haf_rotortrim` / `haf_turnease` / `haf_hugterrain` / `haf_battleturn` — every known key, the shipped defaults (`lookahead` 3, `ease` 4, `cliff` 1 — not zero), the `air`→`hover` legacy alias, the **order-independent `hoverbank`→`bank` fallback**, the CSV name filters read *before* any numeric parse, CRLF, and one bad line never costing the rest of the file. Plus the reason the parse was extracted: every unrecognised line now yields a **named problem** (line number, the offending token, and the valid keys) instead of being silently dropped. See below. |
| The **per-frame pose decisions** | `Patches/PoseMath.cs` | which clip a pawn plays and where in it — the thing the player actually sees. The **proximity-weighted state vote** (`PickState`) and why it is not a headcount or a nearest-pick; the representative coming from the *winning* side; the attack window (first match, not nearest) and its unclamped `repeats` passes; the after-move / pre-move one-shots and the **never-quite-1.0 clamp** that stops a held frame wrapping to the folded pose; the nearest-fire match; the deploy ramp; the recoil sweep. And the invariant a tidy-up would break: **the three match radii differ** (state 4u, fire 4u, deploy 3u). |
| `MergeModels` | `UniversalInjectPatch.cs` | the pack **merge policy**: first-loaded keeps an undeclared clash (conflict), a declared override replaces in place, and `disabled` is honoured on every path — a disabled declared override leaves the owner in place with a named note |
| `PackTuning.Parse` | `Patches/UniversalInject.PackTuning.cs` | the three pack tuning tables (`unitScales` / `eraGrid` / `formationThresholds`) parsed from the **resolved** packs in mod order — a skipped pack contributes nothing, later-in-mod-order (not alphabetical) wins a row, and every cross-pack interaction is a named note |
| **Thread-discipline rule** | `Patches/ThreadDiscipline.cs` | **a structural test**: every mutable field on `ModelEntry` must declare `[MainThread]` / `[Locked]` / `[Concurrent]`, `[Concurrent]` is machine-checked against the field's real type (both directions), the four Architecture §2 locked fields are pinned against silent demotion, and the inherited `Haf.Schema` half must stay free of mutable collections so "config is immutable" holds by construction. Replaces the memorised four-name table; found 17 of 23 mutable collections declaring nothing |
| **Session-state rule** | `Patches/SessionState.cs` | **a structural test**: reflects over every static collection field in the plugin and fails unless each declares `[SessionScoped]` / `[SessionScoped(Manual=…)]` / `[ProcessLived(…)]`; plus the registry really clears only the registered fields of the asked scope (a fixture holder in the test assembly). This is the "every session-keyed static gets cleared on re-arm" invariant of Architecture.md §3 as code — the bug class behind the Oracle incident, the `_DRILL` pack-data bug and the tank-destroyer donor skin |
| `SmokeVerdict` | `Patches/UniversalInject.SmokeTest.cs` | the **in-game smoke harness's** PASS/FAIL rule (injection errors = a per-session ledger of named sites, once each — `500 frames of one throwing model = 1 error, named`). **Live-pawn truth** (2026-08-21): `GatherLivePawnFacts` is pure over `(descId, skeletonId)` slots + entries + the clock — a live pawn on a foreign skeleton (rendering the donor), an entry with live pawns the pose hook hasn't touched in 5 s, and a sub-pawn the scene scan sees but the walk misses are each a named FAIL; the runtime side only collects the slots — PASS iff every catalogued binding resolved, zero injection errors, the registry loaded ≥1 model, the deep per-entry checks are clean, and the live **seam write-back self-test** did not FAIL (the boxed-struct chain every runtime offset uses — the combatZ died-in-the-box class, machine-caught since 2026-08-19); each fail reason surfaced; `repointed`-zero still passes but is NOTED (vacuous coverage announces itself), uninjected entries are named with a diagnosis, and the verdict is written to `haf_smoke_report.txt` next to the load/bindings reports. **Districts (2026-08-21):** live tiles are counted from whichever ledger owns the district (isolate `DistrictModel.tiles`, scoped `ScopedState.refreshPlbcs` — the `, N scoped` suffix), and **texture health** is judged from a pure `DistrictTexState` with `texErrors` read FIRST, because both apply paths give up after 3 exceptions by latching `texApplied=true`: gave-up → FAIL (named), applied → `N/M textured`, pending → NOTE, no atlas or no live tile → not judged |

These map directly to the registry bugs this codebase has actually hit — the `ParseGuidCsv` sign bug, `LongestMatch`
ambiguity, "wrapper-parse drops overrides", the substring pawn-match — so the suite is a **regression net, not
coverage theatre**.

## Extracting logic so it *can* be tested

Most of the plugin cannot be unit-tested: it is reflection against a live game inside Unity. But the *decisions*
buried in that code usually can be, once they are lifted out of the method that does the I/O. `SmokeVerdict` was the
first extraction of this shape; **`DialConfig`** (2026-08-20) is the second, and the pattern is now the standard move:

> Find a method that mixes I/O, engine access and a **decision**. Move the decision to a pure static that takes
> plain data and returns plain data. Leave the I/O where it is. Test the pure half.

**The same move works on the editor half** — the side the 2026-09-07 review showed carries the coverage debt
(thirteen defects, nearly all in editor code; the tested plugin half came back almost clean). Editor windows can't
be referenced by the test project (they need `UnityEditor`), but a Unity-free source file can be **compiled into
the test assembly directly** (`<Compile Include>` in the Tests csproj): `editor/GlbDisconnectedParts.cs` was the
first, and **`editor/EditorRules.cs`** (2026-09-08) collects the extracted window/baker kernels — the
extraction-freshness predicate whose wrongness was review finding 2 (it silently failed for a month; nothing
crashed or logged), the Workshop's natural name ordering, and (2026-09-14) the Vehicle Lab's probe-row parser and
flat-share alias merge (`VehicleLabRules`); **`editor/QuadEstimate.cs`** (2026-09-14) holds the multi-mesh split's
quad estimator and BSP partition. On the plugin side the same move produced `Patches/DescriptorRepoint.cs` (the
hand-prop / multi-mesh descriptor repoint — the "spike plague" arithmetic, previously inline at two sites and
reachable only with the game up) and `Patches/DistrictRules.cs` (the density auto-boost). The bar for what moves
there: logic whose wrongness is **invisible at use time**. Keep those files free of Unity types and file I/O — the
caller gathers facts, the kernel decides. And drill every kernel on arrival: each of the 09-14 ones was mutated
(an append one slot too far, tris/2, no pipe folding, a suffix strip…) and its tests went red before the PR.

The dials are the clearest case. Four `haf_*.txt` files each inlined their own `key=value` loop inside a `Poll*`
method, wedged between `File.ReadAllText`, `UnityEngine.Time` and live-pawn reflection — untestable, and all four
shared one failure: **any line the parser did not understand was `continue`d away in silence.** `radus=6`,
`hoverbanks=12`, a European `rate=1,5` — each produced a working plugin that quietly ignored the setting, with
nothing in the log. That is the "silently disarmed" class [the 07-31 audit](notes/Audit-2026-07-31.md) was written
about, sitting in the one part of HAF a user hand-edits mid-session.

The parse is now `Patches/DialConfig.cs`: text in, typed config + a list of problems out. The `Poll*` methods keep
the I/O and log whatever problems come back, so a typo now names its own line number.

### Guarding a refactor of shipped behaviour

Extracting live code risks changing it. Tests written *after* the extraction only pin what the code does now — they
would pass just as happily over a subtly wrong parser. So two extra things were done, and both are worth repeating on
the next extraction:

1. **A legacy parity oracle** (`Tests/DialLegacyParityTests.cs`). The original inline loops are kept verbatim as
   oracles and compared against the new parser over a 39-case corpus — valid input, half-typed input, CRLF, comma
   decimals, repeated keys, stray `@`. Values must match exactly; diagnostics are excluded, since emitting them is
   the point of the change. It found and documents the **one** deliberate divergence: a line like `@1=5` used to
   produce a trim with an empty bone name, and since `name.IndexOf("")` is `0` for every string, that silently
   rotated the **first bone in the skeleton**. It is now dropped with a message.
2. **A mutation drill.** Six mutations were planted in the parser and the suite re-run. Five behaviour-changing ones
   were each caught (dropping the `hoverbank` fallback → 5 failures; `only`/`skip` falling through to the numeric
   parse → 6; `lookahead` default 3→0 → 39; re-silencing unknown keys → 4; re-accepting an empty bone name → 4;
   dropping malformed-line reporting → 4). The sixth — resolving the `hoverbank` fallback inline rather than after
   the file — passed, correctly: it is a genuinely equivalent implementation, not a defect. A mutation that does not
   fail the suite is either a gap or an equivalence, and you have to tell which; assuming "gap" would have added a
   test asserting an implementation detail.

3. **An in-game drill**, because the two above are still only the suite grading itself. Six deliberately broken
   lines were planted across the live `haf_*.txt` dials — an unknown key, a comma decimal, a line with no `=`, a
   line with two, a transposed key, and a bone-less `@1=5` — each chosen to be provably **value-neutral**, so the
   dials had to keep working while every fault got named. The log showed all six warnings with correct line
   numbers, values byte-identical to the pre-change run, `reloaded 0 line(s)` for the `@1=5`, and — the negative
   control that matters — **zero warnings once the faults were removed**, proving they fire on faults rather than
   on every poll.

   **And the drill found a bug all 323 green tests had missed.** The `[Hug]`/`[TurnEase]` echo lines used plain
   string interpolation, so on a comma-decimal machine the log printed `lookahead=1,5` — the exact spelling the
   parser rejects, one line above the new warning saying *use '.' for the decimal point*. Copy a value out of the
   log back into the file and it silently dies. Fixed with `DialConfig.Inv()` and pinned by a round-trip property
   — *whatever the log prints must parse straight back* — asserted under `nl-NL`.

The rule this follows is the project's own: [review, then drill](notes/Audit-2026-07-31.md). A suite that has never
been shown to fail is not yet evidence of anything — and a suite that has never been checked against a real machine
is not yet evidence of much either. The unit tests could not have found the locale bug: they *are* the code's
opinion of itself, and both halves shared the same blind spot.

### The second extraction, and what it taught about the guard rails (`PoseMath`, 2026-08-20)

The per-frame pose decisions went the same way — `PickState`, the attack/after/pre-move windows, the nearest-fire
match, the deploy ramp and the recoil sweep, out of `StatePose`/`DeployPoseTime`/`FireOncePoseTime` and into the pure
`Patches/PoseMath.cs`. Two findings worth carrying forward:

**The oracle earns its keep on transcription, not on algorithms.** Reading the two nearest-fire call sites had
convinced me they were the same loop written twice. They are not: the recoil overlay seeded `best` with the radius
(strictly inside), fire-once seeded with `float.MaxValue` and range-checked afterwards (inclusive), so they disagree
for a fire at a distance of **exactly 4.0**. The corpus found it in seconds. Unified to strictly-inside — matching
what the other two matchers already do — and recorded as the one deliberate behaviour change, with a named test.

**A random corpus is the wrong instrument for an algorithm choice.** The mutation drill replaced `PickState`'s
proximity weight with a constant (turning the vote into a headcount) and the oracle sailed straight past thousands
of generated layouts. That is not a corpus-tuning problem: the two rules only disagree on small *unbalanced*
in-range splits, and as the sample count rises the two majorities converge, so a **bigger** corpus fires **less**
often. Widening the draw and enlarging the formations both failed to catch it; only an adversarial hand-written case
does (one sample at the pawn's feet against two at the radius edge). Two tools, two jobs — a generated corpus pins
that the code was *copied* faithfully, hand-written adversarial cases pin that it *decides* the right thing. Neither
substitutes for the other, and a mutation drill is how you find out which one you are missing.

## How it's wired

- **Framework:** xUnit, `net471` (matches the plugin), one test project `Tests/HumankindAssetFramework.Tests.csproj`.
- **Access:** the tested helpers are `internal`, exposed to the test assembly via `[InternalsVisibleTo]`
  (`Properties/AssemblyInfo.cs`). A few were bumped `private→internal` purely for this; none were made `public`.
- **`Plugin.Log`:** null outside the game, so each test class's ctor sets `Plugin.Log = new ManualLogSource("test")`
  (a listener-less source → every `LogXxx` is a safe no-op).
- **Dependencies:** needs the same gitignored `References\` DLLs as the plugin build; the test project mirrors them
  into its own bin so the plugin assembly's deps resolve at runtime. `Tests\**` is excluded from the plugin's compile
  globs so the xUnit files never leak into the plugin build.
- **The headless integration lane (opt-in):** `tools/editor_tests.ps1` runs the in-editor **BakeFeatureTest Tier 1**
  suite through Unity batch mode (`HeadlessBakeTests.Run` exits 0 on all-pass, 1 on any failure — deliberately
  binary, since exit codes wrap at 255; the log carries the per-check detail), against a modding project that
  resolves the HAF package (default `C:\Repo\ENCReload`, override with `-Project`/`HAF_UNITY_PROJECT`). It is
  deliberately NOT in the per-push gate: a Unity boot costs ~a minute and hosted CI has no licensed Unity. Run it
  before merging baker changes — the automated form of Factory-Manual §11's instruction. It refuses a project that
  is currently open in the Unity editor. Since 2026-10-02 it also runs the **Model Reader section**
  (`ModelReaderHeadlessTest`): every registry model and every GLB fixture (the script generates them with
  `fixtures.py` and passes `-hafFixtures`) read by `GlbReader`, built as Unity meshes by `ModelPreview` (the vertex
  and triangle counts must be the model's, and no Unity mesh may be left behind), written by `GlbWriter` to a temporary
  file and read back equal field by field (`HafModelDiff`) — in Unity's own runtime: the project's Json.Net 11, the editor's Mono, and the SYSTEM
  locale a batch run gets (first run: 42 files, 722 MB, read in 1.5 s, written in 1.5 s, culture nl-NL). The drills
  prove the same code outside the editor; this proves it inside.

## What is deliberately NOT unit-tested — and why

This boundary is intentional. Adding tests past it would be green ceremony that guards nothing real.

- **The runtime/integration seam** — inject, pose, muzzle, audio, districts, formations. These reflect into Amplitude
  types that only exist inside the running game process; they can't be loaded in a test host, and a fake object model
  under the reflection accessors was considered and declined (reflection is not funnelled — ~1,450 sites — and a fake
  encodes the very assumptions about the game that drills keep disproving; see Decisions.md). Their correctness comes
  from **fail-soft resilience** (per-entry try/catch, null-guards), the editor-side bake smoke/feature tests, and the
  **in-game smoke test**, which is the *right* instrument for this half because it reads the engine, not a model of it:
  - the **load tier** runs by itself once per session, on the first frame after the loading screen hides
    (`Amplitude.Mercury.LoadingScreen.VisibilityChanged`, `SmokeOnLoad = true`) — bindings, registry, clip roles,
    assets, sounds, files on disk, GPU budget, district tiles + textures, patched seams. A few ms, at a moment the
    player is already waiting; never per frame. Tagged `[load]`.
  - the **full tier** is the F8 button — load + the **live-pawn checks**: every live pawn slot carrying one of our
    descriptor ids sits on *our* skeleton (a unit rendering its donor is a named FAIL), the pose hook touched every
    entry with live pawns within 5 s, the sub-pawn walk re-audited against a full scene scan, the ObjectSpace
    write-back self-test. Needs pawns on the map and a few hook frames, hence not at load. Tagged `[full]`.
  - both write the log, the F8 panel and `haf_smoke_report.txt`; the **verdict and every classifier are pure and
    unit-tested** (`SmokeVerdict`, `GatherEntryFacts`, `GatherLivePawnFacts`, `UninjectedReason`, …) — only the
    gathering of live numbers via reflection runs in-game. Each check was earned by a shipped bug class, and each
    first in-game run has so far found a gate the check needed (retexture-only entries have no skeleton; the
    skeleton check once fired on one, and so did the live-pawn check on its first run).
  - the **rebuild → relaunch → read the log** drill is still the final word for *visual* truth (does the helicopter
    follow the terrain) — the smoke proves the engine state, not what it looks like.
- **`ParseGuidCsv`, `MakeGuid`, `EmitterName`** — build/consume Amplitude types via reflection, absent in the test host.
- **`FindEntryForUnitDefinition`** — delegates to the already-tested `LongestMatch` + `CoreDesc`; testing it would mean
  exposing the `entries` global as a test seam for ~zero new coverage.
- **Non-collection session statics** (`bool`/`int` latches like `registered`, `cachedEra`) — outside the
  `SessionStateTests` rule, which covers every static *collection*; they stay on the hand-list in
  `RearmModelRegistration`, and the registry cannot prove reset *order* either (Architecture.md §3).
- **Trivia** (`StrList`, `SanitizeFile`, one-line accessors) — too trivial to regress meaningfully.

## Adding a test

1. If the target is `private`, bump it to `internal` (never `public` just for tests) — `[InternalsVisibleTo]` handles
   the rest.
2. Only test **pure** logic (string/JSON/data in → data out). If it reflects into Amplitude/Unity, it belongs in the
   in-game smoke test, not here — and the pattern there is the same: extract the *decision* into a pure function that
   takes plain values (`GatherLivePawnFacts` takes `(descId, skeletonId)` slots, not pawns), test that, and keep the
   reflection side to a thin collector.
3. A new **smoke check** goes in the tier it can be true in: load tier if it needs only the loaded world, full tier if
   it needs pawns on the map. Gate it on what the entry *authored* (a retexture-only entry has no skeleton) and give
   it a unit test against `SmokeVerdict` before the first in-game run.
4. Set `Plugin.Log` in the fixture if the code under test logs.
5. Prefer tests that pin a **real invariant or a historic bug**, not line coverage.

See also: `docs/Building.md` (build/run), `docs/Code-Map.md` (where the tested functions live),
`docs/Framework-Review.md` (the dated changelog of what each test batch added).
