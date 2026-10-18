# Testing

HAF is verified in **three tiers**, each a machine, each at the level where its bug class actually lives:

| Tier | Runs | Guards |
|---|---|---|
| **Unit tests** — **1,621 as of 2026-10-13** (`dotnet test` prints the live number; this one drifts) | `dotnet test`, the pre-push gate, CI | the pure logic: registry/parse/era, pack resolution + merge + tuning tables, pose math, dial config, the session-state rule, the smoke **verdict and classifiers**, and since 09-14 the kernels behind the game-touching code: the descriptor repoint, the multi-mesh quad estimator + partition, the district density boost, the Vehicle Lab's probe-row parser |
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

**A step has three verdicts, and SKIP is not PASS (2026-10-11).** A step exits 0 when its claim held, **3** when a
prerequisite was absent and its claim was therefore *not tested* (its own `SKIP —` line says what did run: the C# side
of a parity drill, the fixtures without the registry population), anything else when it failed. The gate prints a
skipped step as `[SKIP]`, records `SKIP` in `PROGRESS.md`, lists every skipped step under the summary, and **fails the
push**: on this machine every prerequisite exists, so a SKIP means Blender or the modding project went missing, and a
port change pushed on that gate would have lost its oracle silently. `HAF_ALLOW_SKIP=1 git push` accepts a partial gate
on purpose and says so in the summary (`CHECK: PASS WITH n SKIP`); a `[FAIL]` beside a skip still fails. Until 2026-10-11
eight of the 26 steps printed `PASS … Blender not found, so NO parity comparison` and exited 0 — the general review's
verification pass counted them — so the gate reported 26/26 while eight claims were untested. `Tests/test_parallel_gate.py`
drives the real gate functions through a skipped worker, a skipped serial step, a skip beside a failure, a skip with lost
completion metadata (still a FAIL) and `HAF_ALLOW_SKIP`.

**One gate at a time, and no step runs forever (2026-10-12).** Two gates on one machine — two sessions pushing, or a
push beside a drill run — failed each other for weeks: the reader-gate drill's plant vanished under the other gate, and
this file's own timing test missed its three-second deadline under the other gate's thirteen drills (two rejected pushes
on 2026-10-11). The gate now takes a **lock** — a directory under the user's temp folder, so every checkout and worktree
shares it (`HAF_GATE_LOCK` overrides) — and a second gate prints `another gate is running (…); waiting for it`, notes the
same in `PROGRESS.md`, and polls until the first finishes (up to 45 minutes, then it fails and names the holder). The
holder heartbeats every 20 s; a holder that stopped (a killed shell) is taken over after 3 minutes, and only the holder
ever removes the lock. Every step runs under a limit (`HAF_STEP_TIMEOUT`, 30 minutes): a hung Blender or Mono is killed
and reported as `[FAIL] … timed out`, where before it blocked the push with no message. `Ctrl-C` stops the workers, drops
their logs, frees the lock and notes `DONE ABORTED` — and it stops what the workers spawned, which is the part that
needed a drill: Windows keeps no usable parent chain for what Cygwin `exec`'d (every drill script, Blender shard and
Mono run has a parent that is already gone), so `taskkill /T` stops at the worker's shell and the drills ran on
underneath (a gate killed 60 s into its drills left eleven processes still starting Blender a minute later). Every
step's `mktemp` now lands under the gate's own temp directory (`TMPDIR`), whose random name is on the command line of
everything a step runs, and the abort kills by that name — plus any shell or `timeout` whose command line names this
checkout's `tools/`, which is how the drill scripts themselves are reached (so an abort also stops a drill of the same
checkout started by hand; another checkout's are untouched). The step limit's own `timeout` reaches only the step's
shell, so a Blender shard a timed-out step had started finishes on its own. The main checkout's gate now reports to the worktrees' `PROGRESS.md`
too (its own parent folder has none), and the hook and CI run the same source-only steps again (the vehicle-rig math test
joined the hook, the exit-status gate joined CI). `Tests/test_parallel_gate.py` drives the lock (a waiter, a stale
takeover, a non-holder's unlock) and the step limit through the gate's real functions; the lock itself was drilled by
starting two gates at once and interrupting the first.

| Surface | `tools/check.sh` (pre-push hook) | also in CI | ~time |
|---|---|---|---|
| **Runtime + shared contract** | `dotnet build` · `dotnet test` · docs guard · binding-catalog surface · hot path · parse shape · member shape · schema parity | all source-only checks | seconds |
| **Editor package** | Roslyn editor compile-check · registry engine drill · backup dedup drill · blender exit drill · GLB reader drill · GLB writer drill · vehicle probe drill · mesh layout, prep and deploy drills · Workshop compaction drill · schema parity · hand-list gate | parity + hand-list; compile check and drills stay local | the drills AT ONCE: the longest one's time |

**Since 2026-10-11 the drills run concurrently.** The thirteen drills are independent (each works in its own temp
directory and builds its own exe), so `check.sh` starts them together (`run_bg`), each into its own log, and prints
the logs in declaration order. Each worker records its elapsed time and reports progress as soon as it finishes;
the wall time is the longest drill's. Measured on
the deploy port's branch: 870 s where the sequence took about 1,500. Inside the deploy drill the ~160 single-job
tamper replays (one drill run per mutated dump) run eight at a time up front (`prerun_tampers`, `DEPLOY_PAR`), and
every loop requires an exact exit code from `$PAR/<dump name>.rc` plus its expected diagnostic. Jobs and dump paths
are passed as NUL-delimited arguments, preserving spaces and quotes. `Tests/test_parallel_gate.py` exercises these
contracts, ordered output, failure propagation and delete-only pushes in both the hook and CI, without Unity or
Blender. Every gate step appends `HH:MM gate PASS/FAIL <step> (N s)` to
`PROGRESS.md` beside the worktrees (`HAF_PROGRESS` points it elsewhere; nothing fails on its absence), so a run can
be watched without asking. A push that only deletes refs (`git push origin --delete`) skips the gate: the hook reads
the refs on stdin and exits when every local sha is zero. Not done: skipping a drill whose inputs did not change.

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
The deploy port has the same pair since 2026-10-13. The Bake Tests row *Does the in-process deploy conversion match
Blender's?* (`BlenderDeployHeadlessTest`, on by default): every registry entry with `deployConvert` on — today the
T-62, the >127-bone pair-merge path the push gate's sample never reaches — is converted in process AND by
`deploy_convert.py` with the argv the Factory hands both (`UniversalBaker.DeployArgv`), and the two written GLBs must be
equal byte for byte (a difference is read back and named field by field); a shape the port leaves to Blender by name
is a SKIP that says why, a port that CRASHES is a FAIL — in the Factory the same crash is a `LogWarning` and Blender
converts. An intentional script exit passes only when Blender completes with exit code 1, no output file or Python
traceback, and the same final `DEPLOY ERROR` diagnostic as the port; timeouts, crashes and incomplete logs fail.
`tools/deploy_drill.sh` also runs a complete conversion of the moving-mesh fixture under Unity's bundled **64-bit
Mono**, through `tools/deploy-drill/MonoHost.cs`, and compares its written GLB byte for byte to the real Blender
export. The standalone `mono.exe` is 32-bit and cannot supply the native float math a full deploy needs. This host
uses Mono's [embedding API](https://www.mono-project.com/docs/advanced/embedding/), without starting an editor;
the registry's own deploys still need the Bake Tests row in Unity. Missing runtime prerequisites return SKIP (3).
The drill requires a written comparison and rejects empty jobs, a missing or changed Blender oracle, and a conversion
that falls back. The port — wired into the Factory on 2026-10-11 — still needs its first T-62 run in the editor.
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

**Replacing `deploy_convert.py`, part 4: the bake** (2026-10-11). From `bpy.ops.nla.bake` to the script's
`# --- 5a.`: the bake (every bone's Copy Transforms gives it its part's world matrix; `bake_action` brings that into
the bone's own space frame by frame - `inverse(armature) @ target`, then `BKE_armature_mat_pose_to_bone` against the
parent's CONSTRAINED pose - and afterwards, bone by bone, sets `matrix_basis`, makes each quaternion compatible with
the one before and writes a location, a quaternion and a scale key per frame, LINEAR), the armature taken off its
root-motion anchor, and on the contract path the scale curves stripped and the delta-form rebase
(`LocRotScale(l, q) @ M0^-1`, `decompose()`, the hemisphere rule, every bone but those with "leg" in the name).
`BlenderDeploy.Decide(..., bake: true)`; `Result.Keys`. The oracle runs the script in TWO stages: to the bake (the
scene of parts 2 and 3), then on to `# --- 5a.`, where it writes every fcurve of the action key by key and every
object's `matrix_world` again. The open question of the plan - bit-exact or a tolerance - is answered by the
measurement: 798,655 baked keys of 67 baked jobs equal Blender's TO THE BIT, the T-62's contract path with them, and
the 2,946 object matrices of the scene the bake leaves.

What the comparison showed: (1) a bone that stands still DRIFTS - `Quaternion.make_compatible` goes through the
rotation between the two quaternions and does not give back the one it was handed; StaticRoot moves an ulp a frame
(the port has `quat_to_compatible_quat` as written); (2) the rebase writes `kp.co[1] += new - kp.co[1]` in Python: a
DOUBLE difference added back to a float32 key - where the old key is some thirty bits bigger than the new value the
difference loses that value's last bits (all 1,103 keys that differed at first: the T-62 and the chain fixture).
*Review before the PR* (an independent agent, about 1,100 generated jobs): (a) `BlenderEigen.InvertM4` decided
"singular" on a determinant in DOUBLE where Eigen's is float32 - a part flattened on one axis on a frame the cull
does not sample has a float32 determinant of exactly 0, Blender bakes zeros, the port baked NaN, and the cull of part
2 went wrong with it (16 of 908 fuzzed jobs): the determinant is now Eigen's own, operation by operation, and six of
forty generated cases that tell the two apart are in the gate (`flat3`); (b) `nla.bake` with `only_selected=False`
also bakes every SELECTED object with a pose, and the importer leaves everything selected: a surviving imported
armature gets a key a frame and loses its own object animation. Where that cannot show (nothing hangs from its bones,
its node is not animated) only the scale-free step's COUNT changes (three a bone of each such armature, plus the
importer's own pose-scale curves in every animation: 384, 381, 378 on the `wall_rig` jobs); where it can, the job is
Blender's FROM THE BAKE ON - marked `BAKELEFT:`, its scene before the bake still held: the dugout canoe (ten objects
hang from its armature's bones) and four fixtures. So the helicopter, the towed howitzer and the T-62 are baked in
C#, the canoe is not until that re-bake is modelled. 21 of 22 planted defects fail; not caught: the ASSOCIATION of
the determinant's last sum (read from Eigen, no job tells it apart). Not held: the action's NAME (`Action.001` when
another armature is baked first); a quaternion's magnitude within an ulp of the rebase's 1e-6 gate. The gate also
takes a curve, a key, the bake's log line or the action row out of the real dump and turns one bit of a baked value:
each must fail. Next: the re-bake of a surviving imported armature (the canoe), then the script's steps 5a-5d.

*Review of PR #137* (ChatGPT, two findings, both executed here): (1) the bake OPERATOR has its own frame limits -
`frame_start` is an IntProperty of 0..300000, `frame_end` of 1..300000, and a value outside is clamped: a clip whose
keys all sit within frame 0 has the range 0..0 and Blender bakes frames 0 AND 1 (two keys a curve; the port baked
one). `Result.BakeFrameMin/Max` now; the rebase still runs over the script's own 0..0, so on the contract path the
extra key stays as baked. Fixtures `frame0_one`, `frame0_two`, `wall_frame0`; a range below 0 or past 300000 is left
to Blender. (2) the drill placed a curve by its channel and component index without checking the index: `location[3]`
would have landed in the quaternion's slot - refused now, and the gate plants it. Both variants planted: caught.

**Replacing `deploy_convert.py`, part 4b: the bake takes the imported armatures too** (2026-10-11). `nla.bake` with
`only_selected=False` bakes every SELECTED object that has a pose, the importer leaves everything selected, and so an
imported armature that survived the strip is baked with the new one: each of its bones gets a location, a quaternion
and a scale key a frame from its animated pose brought into its own space (the same two steps as the new armature's
bones: `KeysFromBasis`), a NEW action replaces the importer's - the armature OBJECT's own animation is gone with it,
the object stays where the bind frame put it - and the scene goes on from those keys (`Pose.Rebake`, `Freeze`; on
the contract path the scale curves are stripped from this action too and a bone keeps the scale it holds:
`DropScale`). Part 4 left such a job to Blender from the bake on; that mark is gone - the dugout canoe is baked in
C#, as are the four fixtures that shared it: every recorded conversion whose source exists goes through the bake
here. The oracle's second stage now also writes the curves of every other armature's action (`FCA`), what each of
their pose bones holds when the script goes on (`PB2`), and - after everything else - the scene and those bones once
more at the LAST frame of the range (`O3`, `PB3`: the bind frame cannot tell a frozen object from an animated one,
nor a stripped curve from a kept one). 76 jobs, 2,807,725 baked keys - 1,864,590 of them on re-baked imported
armatures, most on the howitzer's five soldier rigs - 3,264 object matrices at the bind frame and as many at the last,
357 pose bones: all equal to the bit.

Found by measurement: between the end of the bake and the next frame set the re-baked bones stand at the BIND frame's
keys, not at the last frame's values the bake left in their properties - the script's next operator (`mode_set`)
updates the scene and evaluates the new action (46 object matrices of four jobs were wrong on the first model).
*Review before the PR* (an independent agent, 171 generated jobs: the dummy-root armature, two armatures, an armature
that is itself the root-motion hull, culled or under a stripped parent, 110 random rigs): no P1 or P2. Its notes,
taken: a node's `extras` become custom properties and the bake KEYS them (`channel_types` has PROPS) - they move
nothing and the script clears the action later; not modelled, the drill counts such a curve and goes on (fixture
`bones_extras`); the bind frame alone could not hold `Freeze` or `DropScale` - hence the last-frame rows. 7 of 8
planted defects fail; not caught, a zero's sign only: a bone holding its FIRST key after the bake instead of its
last. Not held: a re-baked bone at a frame that is not whole (every frame the script sets is); a bone name with a
control character (the drill does not unescape it; the keys are right). Next: the script's steps 5a-5d.

**Replacing `deploy_convert.py`, part 5a: the fire-window snapshot** (2026-10-11). The script's steps 5a-5d rework
the baked keys for a firing cycle (of the registry's models the towed howitzer alone uses them) and are much denser
than what came before, so they go one at a time. 5a: `argv[8]` is a list of segment starts, `argv[9]` of ends each with
an optional `/step`; with the recoil step (`argv[10]`) on, every frame of every segment is set and each pose bone's
location and quaternion of the new armature recorded - before 5b and 5c re-key the barrel and the legs.
`Result.Segments`, `FireSnap`, `FireLog`; `ArmPose` is what each of those bones HOLDS: after the bake the last frame's
key with the bind frame's written over it (an equal value is not written), scale 1 on the contract path, then the
key of every frame set. The oracle runs a THIRD stage, to the script's `# --- 5b.`, and writes the log line, the
snapshot row by row, what the pose bones hold and every object's matrix after it. 83 jobs, 7,349 snapshot rows equal
to the bit - the howitzer's two segments (`442..530/1, 305..441/2`, 226 frames) and fixtures for two segments, one
past the clip's end, overlapping ones (the snapshot is keyed by frame: 8 captured, not 12), more starts than ends
(the lists are zipped), a backwards segment with a step of 0, the contract path with its rebased keys, a range with
the step off, a part thrown past float32.

**The oracle was wrong, and is corrected**: to read matrices the dump called `view_layer.update()` between its
stages - and on the contract path that update EVALUATES the action the rebase has just rewritten, which the script
does not do there; the dump reported pose bones the script never has. Both calls are gone (the state they read is
current without them), everything of parts 2-4b still agrees, and the reviewer ran the script in ONE piece to 5b
with no dump code in between: byte-identical rows on 154 jobs. *Review before the PR* (an independent agent, 154
generated jobs, 61 argument strings): 5a held; it found (1) `BlenderEigen.InvertM4` inverting a matrix whose
determinant is NaN - Eigen asks `abs(det) > 0`, which a NaN fails, and gives the zero matrix; the port asked
`det == 0` (a part of 3e38 on one frame as the root-motion anchor: Blender bakes zeros, the port NaN) - fixed,
fixture `fire_huge`, planted; (2) Python's `int()` takes white space around the digits but NOT the separators
U+001C..U+001F that `str.strip()` removes: the port read a start the script dies on - fixed
(`BlenderDeployTests`); (3) AGAIN the reader's `-0.0` read as `+0` (9 of 70 fuzzed rigs differ in a held zero's
sign; no snapshot row did), now with a second importer rule beside it that is not traced (a node of translation
`[-0.739, -0.0, 0.0]` holds `+0, +0` straight after the import): in the backlog, not fixed here. 9 planted defects,
all caught; the gate takes a snapshot row, a pose row and an object row out of the real dump, doubles each in place
of its neighbour, renames, truncates and turns a bit of each: every one must fail. Not held: a range Python reads
and `int.TryParse` does not (`1_0`, digits past ASCII, a value over int32) is left to Blender; an INFINITE key
(Blender clamps a written value to FLT_MAX). Next: 5b and 5c - they key with Blender's default BEZIER interpolation,
so its curve evaluation between two keys has to be ported - then 5d, which re-enters edit mode.

**Replacing `deploy_convert.py`, parts 5b and 5c: the barrel retarget and the leg scale** (2026-10-10). 5b
(`argv[5]` the ready frame, `argv[7]` the barrel scale): every bone with "barrel" or "cannon" in its name loses its
curves and gets two keys - the rest pose at `max(int(end / 2), 1)`, the ready pose with its turn and its move scaled
at the end. 5c (`argv[6]` the leg scale): every "leg" bone's quaternion is keyed at the first frame, at the spread
frame and at the end, slerped between the folded and the spread pose; its other curves go. `Result.Rekeyed` (per bone
ten channels of `ArmKey`: frame, value, both handles), `RetargetLog`, `AfterRetarget`. What had to be ported:
`keyframe_insert` (a BEZIER key, AUTO_CLAMPED handles, CONSTANT extrapolation; a key already on that frame is MOVED
by `new - old` in float32, `replace_bezt_keyframe_ypos`, which is not the new value to the bit), the handles
(`calchandleNurb_intern` for an fcurve: flat, a third of the way to the neighbour through `6 / 2.5614 * 2.5614`, a
lone key's at frame -/+ 1), `Quaternion.to_axis_angle` (`acosf`/`sinf` of the UCRT, the 0.0005 guard),
`Quaternion(axis, angle)` with the angle wrapped in float32, `Quaternion.slerp`, `clear_bone_channels` (the raw name
inside the escaped data path), `bone_of.values()` naming a pair-merged part's bone twice, and Python's `float()` (a
negative zero keeps its sign). `mode_set` evaluates NOTHING: after 5b a barrel bone holds the end pose assigned last,
not the curve at the ready frame. The oracle runs a FOURTH stage, to `# --- 5d.`: the log, every curve of the action
with its extrapolation and smoothing and every key with interpolation, handle types and both handles (`FC4`), what
the pose bones hold (`APB4`), every object's matrix (`O5`). 149 jobs, 4,460 re-keyed keys with their handles equal to
the bit: the howitzer (neutral scales only - no registry model scales) and a fixture gun through scales 0, 0.25, 0.5,
1.5, 2, 2.9, 3, -2.3, 5.9, 1e6, -0, ends 0, 1, 2, 24, 25, the ready frame before, on, between and past the new keys,
with a fire window, on the contract path with merged "Cannon" links and with a barrel under a turning turret;
three generated guns about general axes (names with a dot and brackets, a bone that is barrel AND leg, a leg turning
250 degrees, one standing still) and one whose ready poses are 0.001 to 0.06 degrees off the rest (the guard).

*Review before the PR* (an independent agent, 89 generated jobs through the real script): three defects, all
executed, all fixed and planted. (1) The port evaluated the animation at `mode_set('OBJECT')`; Blender does not -
the held pose after a barrel-only retarget with the ready frame before the end was the curve's, not the end pose
(`gun_held_*`, `wall_held`); with that gone the "ready frame inside the new segment" job no longer needs Blender.
(2) A replaced key was set to the new value; Blender adds the float32 difference - 1 to 10 ulp off past a 120 degree
turn, and `+0` where the value is `-0` (`gun_replace_*`). (3) `double.TryParse` reads `-0` as `+0` on .NET
Framework and on Mono (`gun_bs_negzero`, `gun_ls_negzero`: six curves, and the log's `x-0.00`). 27 planted defects:
26 caught; the one that is not - an evaluation after the LEGS are keyed - is equivalent (every frame set there lies
on a key whose value is the one held). The gate takes a Bezier curve, a pose row and an object row out of the real
dump, removes, doubles, renames and turns a bit of each, and removes the log line: thirteen checks, each must fail.

*Review of PR #141*: barrel and leg scales use the correctly rounded Python double even where .NET Framework's
`double.TryParse` differs by one ulp. `gun_ls_decimal` holds the log's `x0.39` for `0.39499999999999999` (the old
conversion printed `x0.40`), and `gun_bs_decimal` uses the same scale in the barrel retarget. Unit cases hold the
parsed bits near decimal rounding boundaries, exact binary midpoint ties (including a power-of-two boundary),
subnormal and normal transitions, underflow and signed zero. The parser corrects the runtime's estimate by comparing
the exact decimal input to the adjacent doubles' exact midpoints; unsupported Python forms still fall back.

Left to Blender from the bake on, by name (`BAKELEFT:` jobs, `BlenderDeployTests`): a frame set strictly INSIDE a new
Bezier segment (a negative end with a leg scale; the evaluation needs Blender's cubic solver and comes as its own
part - the export samples every frame, so it must); a middle key that is no extreme of its neighbours (the
smoothing solver; 5b and 5c cannot make one: two of a leg's three values are equal); a barrel scale that takes a
value past a float (the location's setter clamps to FLT_MAX); a scale Python's `float()` reads and the port does
not (`inf`, `nan`, `1_0`, digits past ASCII); a leg scale outside 0..1 (the script itself dies in `slerp`); a
barrel or leg bone with a quote or a backslash in its name, or past ASCII. Not held: a new curve takes its
smoothing from the USER PREFERENCE "F-Curve smoothing" - the port is the default's (CONT_ACCEL), the drill asserts
the dump says so, and a Blender set otherwise would differ; the curve's group name and the keys' type are not
compared. Next: the Bezier evaluation between two keys, then 5d.

**Replacing `deploy_convert.py`: the Bezier evaluation between two keys** (2026-10-10). `editor/BlenderFCurve.cs`:
`fcurve_eval_keyframes` for Bezier keys - the key search with its 0.0001 frame threshold, the "all at one height"
shortcut, `BKE_fcurve_correct_bezpart` (a handle longer than the keys are apart is cut back), `findzero` /
`solve_cubic` (the time solved for the curve parameter in DOUBLE: Cardano through the C runtime's `exp`, `log`,
`acos`, `cos` - `BlenderTrig` calls ucrtbase's - the FIRST root in -1e-10..1.000001 in the formula's order) and
`berekeny` (the value's cubic in FLOAT). `EvalArm` uses it for every re-keyed bone; the "frame inside a Bezier
segment" fallback of 5b/5c is gone (`gun_end_negative` and four more jobs are decided here). Two oracles:
(1) `blender_bezier_dump.py` - Blender's own `FCurve.evaluate()` on generated curves stored RAW (the extrapolation
set first and the keys through `foreach_set`: handles pointing backwards, keys out of order, two keys on one frame,
a first key without a handle and a second 2^100 away, exactly quadratic time curves, double roots placed by hand,
thirds on whole frames, spans to 1e30, infinities) - `deploy.exe --bezier`: 5,408,360 values of 56,016 curves equal
to the bit over four seeds (the gate runs 6,000 curves and requires 16 named branches to be reached); (2) the
decisions dump ends with a frame SWEEP (`SW` rows: what every pose bone of the new armature holds, frame after
frame - over the range, about the barrel's mid and end key and the ready frame wherever they lie, across the
segment, at the frame limits, and back again): 162 jobs, 217,635 swept rows equal, 57 jobs with a frame between two
Bezier keys; `Result.ArmAt`. The drill makes the SAME frame list and takes no other.

*Review before the PR* (an independent agent; 287 + 44 + 10 + 38 generated jobs through the real script, 4 million
raw curve values): no value differed. Eight notes, all acted on. (1) My oracle claim "Blender does not store a
backward handle" was FALSE: the dump set the extrapolation last and that setter sorts the keys and recalculates the
handles - the generator is now the reviewer's raw one, and two branches I had called unreachable are reached
(1,602 and 17,572 hits a seed). (2) The drill replayed whatever frames the dump named: a dump with a frame taken
out, its tail cut off or its frames reordered passed - the list is now recomputed and compared; planted in the
gate. (3) The Bezier drill counted curves, not values, and skipped unknown rows: the end row counts both, an unknown
row fails; planted. (4) My uncaught plant "the last-frame probe does not evaluate the armature" is caught by a new
fixture (`gunnery_signs`: a bake that keys -0, a fire window ending on such a frame). (5) `Math.Acos` is not
ucrtbase's `acos` on 98,216 of 20 million doubles, but the solver carries it into the float 4 times in 576 million
evaluations: those four rows are FIXED rows of the dump and catch the plant. `Math.Exp` and `Math.Log` gave the same
double as ucrtbase on 20 million samples each: the port calls ucrtbase's all the same, but no drill can tell.
(6) Without the 64-bit Windows C runtime (`BlenderTrig.Exact` false: 2 of 19,078 values differ under a 32-bit Mono)
the conversion went on silently: a barrel retarget or a leg scale is now left to Blender there. (7) The sweep
stopped at frame 3000: now about the keys wherever they are (ends of 5000, 65537, -5000 in the gate; the reviewer
ran ends to 40,000,000). (8) A NaN's sign is not portable (which of two NaNs survives follows the compiler's
operand order): the Bezier drill holds a NaN as a NaN. Very large finite barrel scales can also overflow the
intermediate Bezier coefficients and produce NaNs during conversion; the pose oracle compares their bits on
the tested Windows runtime, without asserting that their signs are portable.
39 planted defects: 35 caught; the four that are not are equivalent - `exp` and `log` (above), `2*a*a*a` grouped
the other way (a factor of two is exact), the linear root divided in float (a float quotient rounds the same from
double).

Not held: five branches no curve reached, ported from the source only - a quadratic time curve's zero
discriminant, the 1e-8 key test after the search, the segment miss, two keys on one frame INSIDE the search (a
doubled frame is found as a key), no equation left; 64-bit Mono (Unity's own runtime: only a 32-bit `mono.exe` is
at hand - the drills run on 64-bit .NET Framework); NaN or infinite key FRAMES and sub-frames (the script sets
whole frames); the smoothing solver for a middle key that is no extreme (still left to Blender; 5d needs it).
Next: 5d.

*Review of PR #142*: the last-frame probe now clamps requested frames to Blender's scene bounds before both
object and baked-armature evaluation. Two unit regressions expose the old baked-key subtraction overflow at
`int.MinValue` on a clip starting at frame 24, and evaluation past the scene limit at `int.MaxValue` on a
re-keyed barrel. The sweep's generated neighbours are clamped to Int32 before consecutive deduplication on both
sides of the oracle; four real-script fixtures cover minimum/maximum end and ready frames. The raw-curve oracle
also checks sequential curve ordinals and each curve's ordered `TIMES` requests: replacing a sample by another
equal value no longer preserves a passing result just by preserving totals. Five additional negative controls
cover replaced samples and curves, missing requests, and rows or a second end marker after the end. Validation:
1,435 unit tests, 162 conversion jobs with 217,635 swept rows, 90 additional generated conversions with 152,615
swept rows, and 579,367 raw values from a second seed, all passing. Three further extreme finite-scale jobs match
Blender, including the NaN interpolation case above. The existing missing `SiegeHowitzersCar` source is still
reported as untested.
The pre-push run also exposed parallel error-log interference in `FormationCollisionTests`: its capture now
observes the synchronous action's thread. A regression emits an error from another thread and one from the
action, proving that the former is ignored and the latter still reaches the assertions.

**Replacing `deploy_convert.py`: the handles of automatic keys** (2026-10-10). The script's step 5d keys its
RecoilArm with a turn that rises and falls: its keys are no extremes, and their handles come from Blender's
smoothing solver - until now such a key was left to Blender. `BlenderFCurve.RecalcHandles(keys, constant)` is
`BKE_fcurve_handles_recalc` for Bezier keys whose handles are all AUTO_CLAMPED under the default smoothing
(CONT_ACCEL): `calchandleNurb_intern` (each handle a third of the way in time; an extreme key and, under CONSTANT
extrapolation, the two ends flat; a handle stopped at its neighbour's height turns the other with it), then
`BKE_nurb_handle_smooth_fcurve` - every run of free keys between two locked ones is a tridiagonal system
("no jump in acceleration"), solved in DOUBLE (`BLI_tridiagonal_solve`), re-solved with every handle that overshoots
its limit locked there, and a locked handle that wants back released, twice at most
(`tridiagonal_solve_with_limits`). Under LINEAR extrapolation the ends are free and their outer handle mirrors the
solved one. `BlenderDeploy`'s `Insert` calls it; the "key between a lower and a higher neighbour" fallback is gone.
Two oracles, `deploy.exe --handles`: (1) `blender_handles_dump.py` - generated curves built four ways (stored and
`FCurve.update()`, `keyframe_points.insert()` in and out of order, stored raw with doubled frames and
`handles_recalc()`): both handles of 2,409,595 keys on 80,000 curves equal to the bit over four seeds (the gate
runs 6,000 curves, including two fixed rare-release shapes, and requires 15 named branches);
(2) `blender_keyframe_insert_dump.py` - the real thing,
`pose_bone.keyframe_insert` on an armature the way step 5d keys its arm, every curve written after EVERY insert:
320,970 keys on 17,666 curves equal (the gate runs 40 key sequences). All 162 conversion jobs still pass.

*Review before the PR* (an independent agent; 4.9 million keys through the real `keyframe_insert`, 2.4 million
more on its own shapes - every two- and three-key sign pattern, systems of 5,000 unknowns, up to 13 re-solves of
one system, denormals, frames an ulp apart): no value differed, and NOTHING of an earlier insert survives in the
handles - each state equals one calculation from the keys alone (the first pass rewrites all four coordinates of
every key, so the 0.001 clamp of old handles and the shift of a replaced key leave no trace). Five notes, all acted
on. (1) BLENDER ITSELF is not deterministic for two keys on one frame at a chunk boundary of its parallel first
pass (a key's thread marks its doubled neighbour while that neighbour's thread resets the mark): the same 520-key
curve gave two results, 35 and 5 of 40 runs. `keyframe_insert` never leaves a doubled frame; the generator makes
long curves by update and by insertion only, never raw (I then made it do so by mistake and measured the same: 23 keys of 4 curves differed, and one of six identical runs gave another dump). (2) A NaN's sign differed on keys past float32: the
handles drill now holds a NaN as a NaN, as the Bezier drill does. (3) My claim "both handles cannot be stopped at
once" holds for finite keys only (9,481 hits on overflowing ones; the port keeps C's left-first order, the results
are equal). (4) Every curve is judged on its own, so a dump that holds fewer curves is still consistent: the gate
now requires the count it asked for; the real-`keyframe_insert` oracle is in the gate. (5) Only a new curve's
SMOOTHING follows the user preferences (handle type and interpolation do not, measured); the script reads factory
settings and the drill refuses a dump that is not CONT_ACCEL. 41 planted defects: 34 caught; of the seven that are
not, five are equivalent (`6 / 2.5614 * 2.5614` IS 6 in float; the left-first order on finite keys; the clamp of
a flat step; the two-unknown form when nothing cycles; the last key's ratio, which is 1) and two were mutations
that changed nothing.

Not held: `bezier_calc_handle_adj` scaling a fixed end handle to fit (dead with AUTO_CLAMPED keys alone: the
reviewer's argument and 900,000 raw curves); other handle types, a Cycles modifier, smoothing NONE. Next: 5d itself.

*PR #143 follow-up*: fixed Blender 5.1.2 oracle rows (seed 1, curves 903 and 18282; 12 and 40 keys) now
hold a handle released a second time and one kept after two releases in the unit tests. The handles generator
reserves its first two curves for these shapes, reading only their frame/value inputs and recalculating the
handles in Blender; the gate requires both branches to be reached. Unity 2021.3.1f1's actual 64-bit embedded
Mono also passed 195,174 keys on 2,938 independent curves in an isolated batch-mode project: exhaustive small
sign patterns, signed zero, extreme finite values, and curves up to 5,000 keys. Both rare fixtures also passed
inside Unity (two second releases and one handle kept after two releases). Limiting a handle to only one release
in a temporary copy of the solver made the fixed oracle reject two differing keys. No handle differed in the
unchanged solver.

**Replacing `deploy_convert.py`, part 5d: the recoil tail** (2026-10-10). The script's step 5d reads the source's own
kickback - each barrel/cannon node's world matrix at the aim frame and across the first fire segment (`argv[8..10]`:
start, end, step) -, picks the tube that moves most, freezes its cradle at the aim and takes the clean slide, scales
it (`argv[11]`), derives an arc radius from a slam in degrees (`argv[14]`, or a legacy radius `argv[12]`, or 1e9 for
none), and in EDIT mode puts a `RecoilArm` bone between the tube's root and its parent (head at the pivot, a capped
radius away, tail ten along the arc axis). Leaving edit mode REBUILDS EVERY BONE from its edit bone (head, tail, roll
0): the tube's subtree lands an ulp off its old rest (measured: 5 of the howitzer's 26 bones change, 2 of the
fixture gun's 6). The arm is then keyed: the identity at frame 0 and the deploy's end, one key a window frame with a
quaternion about the arc axis in the arm's own frame (`theta = -|slide| / R`, the sign from the slide direction, the
quaternion negated when it turns away from the previous key), the same backwards and `argv[13]` times slower for the
return, the identity to settle. `BlenderDeploy`: `Result.Recoil` (every matrix, vector and angle the script's own
variables hold), `RecoilLog`, `ExitAtRecoil` (no tube to pick: the script's `exit(1)`), `BonesAfterRecoil`,
`AfterRecoil`, `ArmPoseAfterRetarget`; `BlenderDeploy.PoseMatrices` (`BKE_pose_where_is` over the script's bones);
`PyFormat.General` (Python's `%g`, correctly rounded on the exact value - the log prints the slide scale and the
radius with it). What had to be ported beside: `Vector.length` and `.dot` (float32 products summed in a double from
the last component), `Vector.normalized` (the SQUARES in double - `len_squared_vn`, not `dot_vn_vn`: one ulp on the
slam fixtures until measured), `cross`, `Vector * float` (the scalar a float32), `Matrix.to_3x3().inverted()` (the
adjugate over the float32 determinant), `max()` (the first of equals), `peak.length or 1.0` (a zero of either sign is
false), `range(rs, re + 1, step)` both ways round, and the order of the script's own checks (a gun without a tube
exits BEFORE the frames are listed: `fire_backwards` found it). The oracle runs a FIFTH stage, to `# --- 6.`: the log,
the exit, the script's death (`DIES5`: a matrix without an inverse), every measurement (`R5`: frames, names, scalars
as float64 hex, the aim and home matrices, every source matrix of every window frame, every slide vector, the peak,
the directions, the pivot, `Cbar3`, every theta), the bones rebuilt (`RBONE5`), the action with the arm's curves
(`FC5`), what the pose bones hold (`APB5`), their pose matrices (`PM5` - only where the step evaluated last:
`pb.matrix` is what the LAST evaluation left, a dropped scale curve lingers in it, so a job without the step would
compare a stale matrix), the scene (`O6`); the frame sweep now walks the arm too. 192 jobs, 0 failed: the howitzer
(its arm 91 keys a channel, slam 0, no return) and fixtures through a slam of 5 and -3 degrees (the radius derived,
the kick down and up), a legacy radius, the return at x4, x2 and none, a step of 2 (the window's end appended), of 3,
one past the window, a slide scale of 2, 0 (treated as 1, logged) and -1.5, a window past the clip (no slide: a
zero arc axis, the arm a zero-length bone elongated by 2e-6), a window run backwards, a tube that slides straight up
(the arc axis from the second cross product), a barrel that is a root part (the cradle is the tube), merged "Cannon"
links on the contract path (a bone named twice among the recoil bones), a gun without a tube (the exit, 12 jobs
take it) and - left to Blender from the bake on - a tube whose scale is zero at the aim frame (`Matrix.inverted()`
raises; the script dies, `BAKELEFT:gun_recoil_dies`). 2,428 measurement rows, 203 arm curves on 29 jobs, 340,553 swept
pose rows equal to the bit. Not held: a step the script reads as 0 ("00": `range` refuses it - "0" itself switches the
recoil off before the step), a return key past int32 (`keyframe_insert` takes a float), a slam Python reads and the
port does not (`nan`, `inf`): all left to Blender by name (`BlenderDeployTests`); the step's default of 2 is dead
(an empty `argv[10]` switches the recoil off first); the ROTATION MODE of the new arm's pose channel is not read -
within 5d nothing depends on it (its one matrix read is at the identity) and 7c sets every bone to QUATERNION.

*Review of PR #144*: finite negative radii such as `-1e39` and `-1e300` overflow the float32 edit-bone pivot
(positive radii are capped at 1000). Blender then fails to invert the rebuilt arm's pose matrix, while the port
previously continued with NaNs. Radii below `-float.MaxValue` now fall back explicitly. Unit assertions and two
`BAKELEFT:` oracle jobs hold the regression; a `1e300` positive-radius control remains supported. An additional
48 independent jobs check extreme slide scales, slam angles, radii and return slownesses against Blender.

*Review before the PR* (an independent agent; 222 generated jobs through the real script, 11,043 measurement rows
and 266,781 swept rows equal; 120,005 doubles through `%g` and `%.1f`, equal): ONE defect - a legacy arc radius of
ZERO (`argv[12]` = "0", "-0", "0.0" with the slam empty or 0): the script divides by it (`theta = -length / R`) and
dies, the port went on keying `-inf`/`NaN` thetas - fixed (left to Blender by name), fixtures `gun_recoil_arc0` and
its slam-0 twin. Holes it found in the GATE, now fixtures: the cradle that IS the tube (a root cannon, a tube under
a static mesh: my `gun_recoil_root` did not reach that branch - its driver was the sliding tube, whose parent bone is
the barrel), the same under a hull that travels (the root-motion anchor), three recoil bones deep (the arm between
Barrel_A and Barrel_B), parts already named RecoilArm (the bone is RecoilArm.002), the deploy's end before frame 0
(the kick crosses the identity key), the arc axis's 1e-4 threshold a hair under and over, and the script's deaths
on a window run backwards for a positive step, on a start without an end, on a cradle whose scale is zero at a
window frame off the aim. The frame SWEEP never reached the tail's end (the arm's return and settle were judged by
their keys and handles only): it now walks the kick's end, the settle and nine points of the return, and runs for a
recoil without a retarget too. The drill named the wrong cause when the script died and the port went on (a missing
pose-matrix row before the death): the death is reported and the rest skipped. Measured on the way: every pose
channel's rotation mode is QUATERNION from the start (a zeroed channel is ROT_MODE_QUAT), `pb.matrix` of the arm
follows its keys after a frame_set, `mode_set` evaluates nothing (the held -0 jobs agree). Not re-planted after the
fix (the fix is one fallback line with its own BAKELEFT fixtures). Noted, not held: the port leaves to Blender what
Python's `float()` reads and it does not (`1_0`, `inf`, `nan`, `1e400` as a slide scale: the script goes on with
`xinf`, `xnan` in its log), a return slowness or a deploy end whose keys pass int32 (the script keys floats), and a
recoil bone name past ASCII (read, never executed: no fixture reached it); the bone ORDER after the rebuild (the arm
takes the tube root's place in its parent's child list) is not compared - nothing in 5d depends on it, the export
may.

**Replacing `deploy_convert.py`, part 6: the bind** (2026-10-10). The script's step 6 sets the bind frame, then for
every mesh (`bpy.data.objects` order, the culled gone) finds its nearest animated ancestor - itself included - in
`bone_of`'s keys, detaches it (`parent = None`: the parent inverse is the identity again, a bone parenting dropped),
folds its world matrix into its vertices (`mesh.transform`), gives it ONE vertex group over every vertex at weight 1,
an Armature modifier, and puts it under the armature at the identity (`BKE_object_apply_mat4` through the armature's
inverse). `BlenderDeploy`: `Result.Bound` (per mesh: its group, its datablock, the matrix folded in, the vertices after
it, the custom normals), `BindLog`, `MeshData` (every datablock with its users), `AfterBind`, `ArmPoseAfterRecoil`;
`BlenderDeploy.ImportedMesh` (the importer's vertices and custom normals of an unskinned mesh, BlenderReduce's
reading). Measured, not read: `anim_ancestor` returns the PART'S NAME, so the group is named after the part, not its
bone - a pair-merged link's group names NO bone (the script's own bug: the wall and T-62 riders never deform; kept, in
the backlog); a group name holds 63 bytes (`bind_longname`: an ASCII and a two-byte name cut); a shared datablock is
copied and the copy numbered by `namemap_get_name` (Plate -> Plate.001 and .002, the last sharer keeps the original;
from Plate.001 the copies count on; a stripped sharer let go of its user); `mesh.transform` is `math::transform_points`
((x c0 + y c1) + z c2) + loc in float32, no FMA) and it SKIPS a matrix within 1e-6 of the identity on every entry
(`skip_transform`: four jobs kept the importer's -0 where the product gives +0; `bind_near` holds both sides of the
threshold); the custom_normal attribute is INT16_2D and `transform_custom_normal_attribute` leaves it alone (float3
only); the armature stands at the EXACT identity at the bind frame in every job (it is set there after the anchor
parenting, and hull x inverse rounds to I), so every bound mesh's own transform is the identity too. The oracle runs
a SIXTH stage, to `# --- 7.`, BEFORE the last-frame rows and the sweep (they see the bound scene): the log, the
death (`DIES6`), per mesh `BIND` (group, datablock, users, parent, parent type, transform, parent inverse), `VG6`
(group names, the weights' census), `MOD6`, `V6` (every vertex position hashed, float32 little-endian) with `VX6` (64
sampled vertices in the clear), `N6` (the attribute's type, domain, count, hash), `DATA6` (every mesh datablock with
its users), `O7` (every object after an update). 204 jobs, 0 failed: 3,489 meshes bound, 52,434 vertices, the T-62's
140 meshes from 11 shared datablocks among them. Fixtures: a datablock shared by three (two copies numbered), the same
from a numbered name, the same with a sharer stripped, a mesh under a mesh part and one two static meshes below a
part, file normals under an uneven mirrored matrix, no mesh at all, names past 63 bytes, the root-motion anchor at
the bind, the skip threshold both ways. Planted: 32 in the port, 30 fail the drill (the skip threshold at 1e-5 and 1e-7, judged on the translation alone, or not applied: `bind_near` catches each); the two that do not bypass the
armature as the parent of the bound mesh, and the armature is at the identity in every job (equivalent in practice;
the rule is `BKE_object_apply_mat4`'s, drilled under 2b with arbitrary parents); one planted equivalent (a mesh's
parent inverse kept: the importer gives a mesh none) was dropped. The first plant run found the DRILL reading the
parent and parent type as constants instead of the port's state - two plants passed; every field of a row now comes
from the port. The gate tampers the bind rows (missing, doubled, renamed, cut short, one bit or hex digit off), the
log line and a claimed death. LEFT from the bake on, by name: a surviving bone shape (the dugout canoe - strip list
"camera" -, `small_strip`, `bones_norig`: the icosphere's vertices are Blender's primitive). Not held: the order of
`bpy.data.meshes` (compared as a set: nothing later reads it); a mesh with a parent inverse of its own (the importer
sets none); whether the exporter writes the merged links' bone-less group (step 8).

**Replacing `deploy_convert.py`, parts 7, 7b, 7c: the purge and the role clips** (2026-10-11). Step 7 removes the
animated empties (a removed part's children are roots with the transform they held; the meshes are under the
armature by then; an imported armature that is a part goes too), 7b clears every other object's animation, removes
every action but the armature's and renames it "deploy", 7c samples the baked deploy frame by frame into role actions
keyed with `pose_bone.keyframe_insert`: unfold (fmin..deploy end), fold (backwards), folded (the rest frame twice, or
the wheels spun: argv[17..20] bones, axis, frames, degrees - Euler keys about the local axis nearest the axle, XYZ
mode, every key of the role LINEAR), deployed (the end twice), recoil (the pristine fire window with the slam layered
on the RecoilArm, the window played back `argv[13]` times slower, the later segments appended with their step).
`BlenderDeploy`: `Result.Finish` - LAZY: what an object holds after 7b is what the LAST evaluation left, and the
oracle sweeps frames after the bind, so the drill runs Finish after its sweep and the Factory will run it right after
Decide -, `Log7`, `Objects7` (copies; `Objects` stays the pre-purge list), `Actions7`, `AfterPurge`, `BoneOrder`,
`RoleFallback`, `RoleLog`, `Roles`, `ActiveAction`, `ArmPose7`; `Obj.Eul`; `ArmKey.Interpolation`;
`Mat3ToEulXYZ`, `QuatToEulXYZ` (hypotf, atan2f from the C runtime). Measured, not read: the armature is unparented
after the bake already, so step 7 moves nothing; `arm.data.bones` after the recoil is the edit list's order nested
depth-first (the RecoilArm the last child of the tube root's parent) and the pose channels follow it - the drill now
checks that order on the RBONE5 rows; `arm.matrix_world = Identity` leaves the armature's Euler Y at MINUS zero
(`mat3_normalized_to_eul`); `pb.rotation_mode = 'XYZ'` CONVERTS the held quaternion (normalized in place) into the
Euler angles (`BKE_rotMode_change_values`: found by `wheels_negative`, where no key overwrites them); the role curves
are Bezier, AUTO_CLAMPED, CONT_ACCEL, extrapolation CONSTANT; the bind's apply is the value the animsys compares the
next evaluation against (`bind_longname`: a mesh part kept the apply's +0 where its curve says -0 - the port's pose
history had to take the apply, `pose.Write`). The oracle runs two more stages AFTER its sweep: the purge (`LOG7`,
`DIES7`, `OBJ7`, `ACT7`, `E7` - mode, location, quaternion, Euler, scale as held -, `O8` after `frame_set(fmin)`,
7c's first act) and the role clips (`LOG7C`, `DIES7C`, `ACT7C` with slots, `FC7C` every curve of every action with
handles and interpolation, `ACTIVE7`, `APB7` with the mode and the Euler angles). A span past 20,000 frames is not
run by the oracle (`SKIP7C`: the script would key for hours - `gun_end_max` would never finish) and the port must
leave such a job by name; the script's own death (a deploy end before the bind frame: the snapshot it never took,
`KeyError`; a wheel count of 0: the division) likewise - a job so left is marked `ROLELEFT:` and compared through the
bind and the purge. 217 jobs, 0 failed: 4,787 objects after the purge, 92,622 role curves, 2,295,177 keys to the
bit; the T-62's 124 bones in five roles, the howitzer's recoil role with its epilogue. Fixtures: the wheel spin with
the axle by AUTO (12-vertex rings), forced to Y and Z, a bone found by a substring and one not found (logged), 8
frames of -180 degrees, a negative count (no keys, the mode set and the quaternion converted), a wheel named twice,
a wheel past ASCII found by its exact name, with the recoil; the span guard; ROLELEFT: `gun_end_65537`, `gun_end_max`,
`role_span` (the span), `gun_end_min`, `gun_end_minus5000`, `gun_end_negative*`, `gun_gen*_negative`,
`gun_recoil_end_m8/m4` (the KeyError), `wheels_zero` (the division), `wheels_nonascii_sub` (Python's lower case of a
name past ASCII, sought by a substring).

PR #146 review regressions: wheel angles `2e40`, `-2e40`, `1e300` and `-1e300` hold the RNA
`rotation_euler` clamp to +/- FLT_MAX, including intermediate keys, handles and the held pose. Counts of
20,000, 30,000 and Int32.MaxValue are `ROLELEFT:wheels_span_*`: the folded clip holds N+1 poses and must reach
the 20,000-frame guard before allocating keys. The oracle skips those wheel clips too; a count of Int32.MaxValue
with no requested wheel remains a fully compared control. Nine unit cases cover both fixes and finite-angle controls.
Review validation: 1,488 unit tests pass; 104 independent Blender jobs compare 13,552 role curves and 193,720 keys
without a mismatch (including shifted bind frames and recoil settle extremes with multi-segment epilogues).
Removing either fix makes its matching one-job oracle regression fail; both intact controls pass.

Planted: 41 in the port, 41 fail the drill (the parts removed by their action,
a removed part's child under the grandparent, the armature's action cleared, the action's name, the Euler sign, the bones'
order, the span guard both ways, the snapshot's KeyError taken as the identity, unfold a frame late, fold not reversed,
folded and deployed keyed once, keys at the source frame, a bone born after the snapshot zeroed, the arm's location held,
the slam into the return, the palindrome's last frame, the return without its switch, the epilogue's step and end, the
peak the last of equals, the settle's default, the decay not mirrored and in float, theta in float, the axle's sign, the
local axis by the signed dot, degrees not in radians and in another order, the keys left Bezier, the mode left
QUATERNION and the mode change without the conversion, the last wheel frame short, the substring search case-sensitive,
the verts threshold, the thin extent the last of equals, the verts through the local matrix, the held recoil keyed once,
the count's default, the active action the last role). A plant that raises the span guard HANGS the port on
`gun_end_65537` (the quadratic keying the guard prevents): that direction is judged by the other jobs only. The gate tampers the purge and role rows (missing, doubled, renamed,
cut short, one bit off), both log lines, the active action, and claims a death or a skip; each must fail, replayed
locally against the gate's reasons before the push. Not held: an Euler-mode object given a rotation other than the
identity is now extracted by the real function, but only the identity is reached (the armature); the order of the
curves within a role action (compared as a set; the export may read it).

**Replacing `deploy_convert.py`, part 8a: the export's structure** (2026-10-11). The script's last step trims the scene's
frame range to argv[2]..the recoil tail's end (or argv[3]), purges every object but the meshes and the armature, de-animates
any bone with a key that is not finite or beyond 1e6 in any action, and calls `export_scene.gltf` (GLB, animations,
frame range, Y up; the exporter's defaults otherwise). The oracle for it is the GLB itself: `tools/deploy-drill/
blender_export_dump.py` runs THE WHOLE SCRIPT per job and reads the file it wrote back row by row (`LOG8`, `SCENE`,
`NODE` with children, mesh, skin and transform bits, `SKIN`, `IBM`, `MESH`, `PRIM` with the material, the mode, the
indices' count and hash and every attribute's count, type and hash, `MAT`, `IMG`, `TEX`, `ANIM`, `CHAN`; `EXIT8`,
`DIES8`). It runs in `EXPORT_JOBS` Blender processes (8) BESIDE the decisions dump, on every job the port does not leave
before the export (a ROLELEFT job is not run: the whole script keys its role clips for hours - two such jobs held two
processes until they were killed, 2026-10-11). `editor/BlenderDeployExport.cs` (`Build`, after `Finish`) makes the
structure from the port's state and REUSES the prep step's exporter ports: `BlenderExport.MeshPrimitives` over the bound
mesh (the folded vertices, the importer's faces, UVs, colours, custom normals and sharp faces, ONE vertex group at
weight 1; `BlenderReduce.Reduce` at ratio 1 with `mergeUvs: false` - the script applies no modifier),
`VehicleProbe.ExporterTrs` (joints without the snapping), `InvertedSafe` for the inverse bind matrices,
`NeutralBone`. Read in the exporter and measured on its files: the serializer numbers a node AFTER its members
(children, then mesh, then skin), so the first mesh node's skin reaches the joints - each bone after its bone children,
the neutral bone last - before that node; the skin's joints are pre-order (the `BoneOrder` of part 7); the armature is
last; a mesh node has no transform of its own; a bound mesh without a triangle (a line) is a node without a mesh and
without a skin; a vertex group that names a pair-merged PART (no bone of that name) puts every vertex on the
`neutral_bone` the exporter adds once (13 jobs); with no mesh with a triangle the skin is still written, unused; the
materials are numbered at first use along the walk (BlenderNames.MaterialOf); every glTF scene but the default one is
a Blender scene of its own (`scene.name or "Scene %d"`, numbered where taken) and is written empty after the purge
(`r2_scenes_nodefault`); an action without a curve is no animation (`wheels_negative`'s folded role); the trim's two
assignments go through the RNA setters (each value clamped to 0..1048574, the other end pulled along when crossed:
`gun_recoil_backwards` prints 0..0 for a tail that ended at -15); images are the source's own bytes (the 24 of the
howitzer); the sanitize line is printed always. Found and declined by name on the way: a TRIANGLES primitive whose
corner count is not a multiple of three (the importer's last face takes the leftover corners as an n-gon, which the
triangle layout drops - `BlenderReduce.FallbackReason`, so the prep declines it too; the `wheels_twice` tail wheel had
8 vertices and is a 9-vertex ring now). `deploy.exe --export`: the export step's log lines, the scenes, every node row,
every skin and inverse bind matrix row, every mesh and primitive row (the material index included), the animations'
names, each animation's channel and sampler count (three per bone). An independent reader went through the port
against the exporter's source BEFORE the PR and found two rules the 192 jobs never exercised, both run through Blender
before they were believed: `export_frame_range` cuts every action to the trimmed scene range (`action.py`: the action's
range, int(first key)..int(last key), against `scene.frame_start..frame_end`; the sampler keys `while frame <= end`), so
a deploy from frame 5 drops "deployed" and "folded", keyed at fmin and fmin + 1 alone (`small_start5`); and the skin no
node uses is written only with ZERO mesh objects (a line mesh alone carries the armature modifier: no skin at all,
`r5_lines_only`) and only from the LAST scene the exporter gathered (`export.py` reads the vtree the last scene left:
a second scene "Zoo" after "Scene" leaves no skin, "Alpha" before it keeps it - `r2_scenes_nomesh_zoo`, `_alpha`).
Also from the review: the script keys its garbage bones by `data_path.split('"')[1]` (a bone name with a quote maps to
its escaped prefix, and a sibling sharing it loses its curves too) - ported, no fixture; `need_neutral_bone` is decided
over every bound mesh's vertices, a triangle or not (`prepare_data`) - ported from the source, no fixture (it needs a
pair-merged line mesh); a material read with two sets of UV indices (written twice by the exporter) and a vertex
attribute that is not finite (`validate` resets it) are left by name; the importer's bone-shape icosphere, which the
exporter filters out, never reaches the export (the bind leaves such a job to Blender already); the deploy action
with every curve taken by the sanitize is left by name (its range without curves is not modelled). 197 jobs, 0 failed:
181 exports compared (15 stop before it: the script's own exits; `EXPORTLEFT:bind_ngon` left by name), 6,895 nodes,
3,230 joints and inverse bind matrices, 3,477 meshes with 3,478 primitives and 55,183 vertices (positions, normals, UVs,
joints, weights and indices as bytes), 930 animations. Fixtures: `bind_export` (a two-material mesh part with a line
mesh below it), `bind_ngon` (eight corners: EXPORTLEFT), `small_start5`, `r2_scenes_nomesh_zoo`/`_alpha`,
`r5_lines_only`. The gate tampers the export rows (missing, doubled, changed, cut short), the log lines, the written
line, the end row, and claims a death or an exit; each must fail. Planted: 21 in the port, 18 fail the drill (the
joints in post-order, the root bones before the meshes, the neutral bone out of the skin, a joint snapped like an
object, the inverse bind matrix without the basis, the mesh named after its object, the material by its glTF index, the
group's bone in creation order, the other scenes dropped, the trim unclamped, an action without a curve kept, the purge
line past one object only, the imported positions exported, the trim end from argv beside a recoil tail, the n-gon
fallback dropped, an action outside the range kept, the unused skin whatever the last scene, the unused skin beside a
line mesh); three cannot fail it, and say why: the modifier-apply UV merge run on the export's mesh (one glTF vertex per
Blender vertex - its corners share their UV, so the merge moves nothing on an imported mesh), the group's weight at 0.5
(the exporter divides by the sum: one group is 1 whatever its weight), and the garbage keyed by the bone's name (no job
has a garbage bone). A whole `tools/deploy_drill.sh` runs in 10 min 40 s: Blender's export of 197 jobs takes 54 s in 8
processes beside the decisions dump (the 446 s first written here was the time until the parent collected the shards,
after the decisions and their tampers - the review of PR #148 made each shard note its own end), the comparison 95-137 s. NOT compared yet, named: the materials' contents, the
textures and images (8a-ii), the animations' channels (8b), the GLB's bytes - accessor and buffer layout, JSON (8c).
`BlenderDeployExportTests` hold the walk, the trim's setters, the scenes and the empty role on small models.

**PR #149 review regressions** (2026-10-11). `BlenderMaterialExportTests` and the four
`export_review_*` material fixtures hold the port to Blender for an ORM image shared across different UV layers
(and different samplers: the occlusion sampler wins), ignored unlit slots that must not reserve image names, and
unreferenced meshes whose UV layers must not influence a written material, and vertex-colour variants that
resolve their UV layers independently. `MaterialDrillTests` requires a complete,
unique file block with one matching final DONE, rejects fatal or contradictory records and empty dumps, and checks
an explicit source manifest. A Blender import/export failure or a missing source fails the material comparison.
The shell gate also replays twelve malformed envelopes in addition to the existing row mutations; a claimed death
must fail instead of silently replacing a comparison.
Verified by execution after the review (not by reading): Blender's round trip of the four fixtures equals the port
(the ORM texture shared on two UV layers with the occlusion's sampler, the unlit material's one image named
`shared`, no texCoord from an unreferenced mesh, the two vertex-colour variants at 0 and 1); the port as it was
before the review fails three of them and leaves the fourth; the 104 files still at 1,090 materials, 997 textures,
59 samplers, 637 images, none failed; the six deploy jobs' 110 materials and 143 images equal; the importer's unlit
path (`imp/KHR_materials_unlit.py`) calls `base_color` alone, so no other slot's image is created or numbered.
Four plants reverting the review's rules: three fail the drill, the ORM one turns a compared file into a LEFT and
fails the gate's known-left check (as do the plant that writes every source material and the one typing a packed
image by its declared mime). The full gate passes locally in 10 min 10 s. A source Blender cannot import is a FAIL
of the comparison now, where it was a NOTE: a sample file that dies in Blender fails the gate.

**Replacing `deploy_convert.py`, part 8a-ii: the materials, textures, samplers and images** (2026-10-11). Every
source material takes the same round trip in `deploy_convert.py` and `prep_model.py`: the importer builds a node tree
from it, the exporter reads the tree back and writes a material. The oracle is that round trip alone:
`tools/deploy-drill/blender_material_dump.py` imports each file of part 1's sample into a fresh Blender, exports it with
the script's settings and reads the written materials (`MAT`, the JSON), textures (`TEX`), samplers (`SAMP`) and images
(`IMG`: name, type, bytes) back; it runs in part 1's shards beside the other dumps. A census over the 57 registry,
recipe and deploy sources came first (1,090 materials: base colour and metallic-roughness textures, normal and
occlusion textures, KHR_materials_specular on 136, ior on 84, specular-glossiness on 46, clearcoat on 3, alpha modes,
three sampler shapes, JPEG and PNG) and the rules were MEASURED on those 57 files and 44 prep fixtures before they
were read: every factor comes back as the socket's float32 (0.3 to 0.30000001192092896), metallic and roughness are
left out at 1, the base colour at white, doubleSided when false, alphaMode when OPAQUE, alphaCutoff at 0.5; a
specular-glossiness material becomes metallic-roughness (the diffuse colour and texture, metallic 0, roughness 1 -
glossiness) with KHR_materials_specular's colour from its specular factor and an IOR of 1000 beside it; 632 of 633
images are the source's own bytes (the Espana's JPEG read for its alpha is the one encoded again), named without their
extension (`os.path.splitext`), one per (bytes, type, name) - the T-62 keeps 112 twins of equal bytes under other names;
textures are one per material slot, samplers one per value (NEAREST only when both filters are NEAREST or the one
given is; a wrap written only when not both REPEAT, then both). An independent reader then mapped the whole round trip
in both sources (per feature, with citations); it refined five rules the measurement could not tell apart - a specular
factor over 1 folds into the colour, a specular colour texture suppresses the colour factor (the exporter compares a
list to a tuple), clearcoat's weight is written when not 0 and its roughness when not within 1e-5 of Blender's 0.03,
an occlusion strength is written whenever the JSON double is not 1 (the Mix node exists then, its float32 written
whatever it is), images are deduplicated by bytes, type and name rather than by source - and gave the emission rule
(a grey factor folded into the strength on the doubles, colour times strength in double, a component over 1 moved into
KHR_materials_emissive_strength). `editor/BlenderMaterialExport.cs` (`Build` for a file in the prep's walk order, or
for the names the deploy export numbered) makes the material JSON, the textures (the occlusion and metallic-roughness
slots share one when they read one image: the ORM composition), the samplers and the images; `deploy.exe
--materials` (`MaterialDrill.cs`) compares the JSON structurally (the writer turns an integral float into an int, so
numbers are compared as doubles and keys as written), the textures' image and sampler, every sampler, every image's
name, type and SHA-256. The deploy export drill (`--export`) compares the same four row kinds on its jobs through
`BlenderDeployExport.MaterialRows`. 103 files (57 sources, 44 fixtures; one recipe source is gone), 0 failed: 1,078
materials, 991 textures, 57 samplers, 635 images; 3 files left by name (a JPEG whose alpha is read - the exporter
writes it again as PNG -, a material read with two sets of UV indices - written twice). On the deploy jobs: 110
materials and 143 images of 6 jobs, the howitzer's 24 among them. Left by name, not reached by any source: a
specular-glossiness texture (the importer bakes a roughness image), an occlusion image beside a metallic-roughness
image of the same size (composed into a new one), clearcoat textures, transmission, volume, sheen, anisotropy, a WebP
or any image that is not a PNG or a JPEG; not modelled and not detectable through the reader: KHR_texture_transform.
A second independent reader then went through the C# against both sources and found seven rules the first reader
had wrong or the port lacked, every one run through Blender before it was believed (the `export_material_edges` and
`export_uri_mime` fixtures): beside a specular colour TEXTURE the colour factor IS written whenever the source's is
not white (the exporter compares the Mix node's list to a tuple: always different) and as (fac, fac, fac) when the
factor is over 1 (the first reader had read "dropped"); a texCoord the mesh has no UV layer for is written as 0; the
emissive factor's grey fold runs on the JSON doubles, where 3 times 0.3 is 0.8999999999999999 and its float32
0.8999999761581421 - the reader kept only the float32, so `HafMaterial.EmissiveFactorJson` carries the doubles now; a
colour socket clamps a negative component to 0 on the way in; a wrap value the importer does not know reads back as
CLAMP on both axes; a uri image's type is its file EXTENSION, and bytes of another magic are encoded again (a
"liar.png" holding JPEG bytes came back a 248-byte PNG: left by name); KHR_texture_transform is left by name, since
the reader does not carry a texture's transform. 105 files, 0 failed: 1,090 materials, 997 textures, 59 samplers, 637
images; 4 left by name.
`BlenderMaterialExportTests` hold each rule on a material small enough to read, through the drill's canonical form.
The gate tampers the material rows in both drills (missing, doubled, changed, cut short) and the material dump's end
row; malformed completion records and claimed import/export failures fail the material comparison. The gate's material sample is part 1's plus every prep fixture
(`fixtures.py --prep`), where the material shapes live, and the round trip's own fixtures (`--materials`: the edge fixture's
specularFactor of 1.5 is folded by Blender's importer but refused by the Factory's converter, so the prep drill, whose
C# prep passes the source material through unchanged, does not get it; the Factory wiring will write the port's
materials). Planted: 31 in the port, 30 fail the drill (factors in double,
roughness at 1, the RGB clamp, a flat MASK compared in float32, a textured MASK at cutoff 0, the cutoff at 0.5,
doubleSided false, the glossiness as roughness, the ior alone, the specular colour at white, the factor at 1, the
unlit roughness in float32, the empty object, NEAREST as linear, the wrap at REPEAT, the extension on the name, images
by bytes alone, the JPEG with alpha passed, the ORM texture unshared, the normal scale at 1, the occlusion strength in
float32, the emissive strength unfolded, every source material written, the specular colour dropped beside its texture, the
factor over 1 not folded there, a texCoord beyond the layers, the grey fold in float32, a negative component kept, an
unknown wrap as REPEAT, a uri image typed by its bytes); four of them needed a fixture before they
could fail (`export_material_edges`: a flat MASK under its cutoff by less than float32 tells, an IOR alone, an image
declared PNG holding JPEG bytes, an occlusion strength of 0.99999999 - the last showed the reader keeping only the
float32 of the strength, so `HafMaterial.OcclusionStrengthJson` carries the double now); the 24th (the image type by
the file's word, not the bytes') turns the compared file into a fallback instead of a wrong answer, which the drill
accepted as "left" - so the gate now requires every file the material port leaves to be one of the KNOWN reasons
(`KNOWN_MATERIAL_LEFT`), and that plant fails there.

**Replacing `deploy_convert.py`, part 8b: the animations' channels** (2026-10-11). The export samples every action
(export_force_sampling, the default): `BlenderChannelExport.cs` holds the rules read in the add-on's
`exp/animation` (action.py, sampled/armature, sampling_cache.py, fcurves/channels.py, anim_utils.py) and
`gltf2_blender_math.py`, with Blender's C behind each product. Per action, the range is the first and the last key of
its curves cut to the scene's (`export_frame_range`: the script's `scene.frame_start/frame_end`), frame by frame. An
action that is not the armature's active one is switched to after `reset_bone_matrix`: every bone at the identity, so a
property without a curve in that action reads 0, the identity quaternion, Euler zeros or scale 1; the active one (the
legacy `deploy`) is first in the sort and is NOT reset: a property without a curve there holds what the script left
(`BlenderDeploy.Result.ArmPose7`: the garbage bones the sanitize de-animated ride at the role clips' last pose). Each
sample is `rest_mat.inverted_safe() @ parent.matrix.inverted_safe() @ bone.matrix` (a root bone: `matrix_local`'s
inverse times its pose matrix) in mathutils float32, decomposed by `mat4_to_loc_rot_size` and
`mat3_normalized_to_quat_fast`; a bone's pose matrix is its channel matrix (the quaternion normalized, or
`eulO_to_mat3` for the spun wheels in XYZ) through the pose chain already drilled in part 2. A channel whose samples
do not change (`np.ptp` of every component below 0.0001, in double) keeps its first and last sample, animated or not;
its interpolation is STEP where the property has no curve in the action, or where the two kept samples are equal
within one float step per component (mathutils' `==`), else LINEAR (a Bezier curve's CUBICSPLINE falls back to LINEAR;
a rotation_quaternion curve does not count on a bone in an Euler mode, nor a rotation_euler one on a QUATERNION bone).
The values go through `correction_matrix_local` (the Y-up basis change times `matrix_local` for a root bone,
`parent.matrix_local.inverted_safe() @ matrix_local` under a bone parent) as `transform` does - the translation of
`correction @ Translation(loc)`, the quaternion of `correction @ the normalized quaternion's matrix` through
`mat4_to_quat`, the scale of `correction @ Diagonal(scale)` through `mat3_to_rot_size` -, the quaternion written
(x, y, z, w); the times frame / 24 in double, stored as float32. The channels follow the vtree's bone walk (root bones in
pose order, each followed by its children), translation, rotation, scale per bone, one sampler per channel. The oracle
is part 8a's dump, whose `CHAN` rows carry each channel's target, path, interpolation, count, first and last time and
the SHA-256 of the input and output bytes; `deploy.exe --export` compares them. Measured: 197 jobs, 51,801 channels
equal to Blender's bit for bit, the howitzer's recoil and spun wheels among them (63 bones in an Euler mode; 36,466
channels reduced to two samples, 9,566 sampled through, 831 of one sample; 29,773 STEP, 17,090 LINEAR), none left.
`BlenderChannelExportTests` hold the walk, the keep, the cut and the transform on a hull and a gun. The gate tampers
the `CHAN` rows (missing, doubled, changed, cut short). Planted: 20 in the port, 18 fail the drill - the fps, the
quaternion's order, the normalization, the 0.0001 threshold (3 jobs, the howitzer among them), mathutils' one-step equality
(74 jobs), the reset of the active action or of none, the scene cut, the STEP rules, the channel order, the root's
basis change, the Euler parity, the Euler rotation ignored, the inverses swapped, the garbage curves kept, the keep
only when animated; a quaternion curve counted on a bone in an Euler mode passed 197 jobs and needed the new
`wheels_one_frame` job (the export cut to one frame: STEP for the property without a curve, LINEAR with it) -; two are
equivalent on whole frames (the times in float32 arithmetic, the channel matrix's zero terms in double). An independent
reader against the sources found the reset rule tied to the active action's NAME where the exporter ties it to its
POSITION: the switch runs for every action in the sorted list, so the active one escapes the reset only when it sorts
first (`deploy` does; a role named below it would reset `deploy` too - latent, held by a unit test that adds such a role);
an animation the export names but the sampler cannot fill is a contradiction now, not an empty animation; and three
cases the script cannot produce are named in the file's header (the armature object's own channels, a connected bone,
accessor sharing - gather_accessor is cached on its bytes, count, min and max for the whole export, which part 8c's
writer has to reproduce). Not here: the bytes (8c).

**Replacing `deploy_convert.py`, part 8c: the GLB's bytes** (2026-10-11). `BlenderGlbWriter.cs` writes the file the
exporter writes from what parts 8a, 8a-ii and 8b hold, read in `exporter.py`, `export.py` (both), `buffer.py`,
`accessors.py`, `cache.py` and `gltf2_io.py`: the scene graph flattened depth first through each object's members in
dir() order (alphabetical), a child-of-root object appended to its root list when its members are done - a node's
children, mesh and skin before the node; a primitive's attributes, then its indices, then its material's images; a
texture's sampler before its image -, binary data appended to the one buffer as met, each view padded to four bytes,
the animations after the last scene, an unused skin last; `gather_accessor` is cached on (bytes, component type,
count, max, min, type), so the indices of two primitives with the same index bytes, the animations' inputs with the
same times and the identical constant outputs share one accessor and one view, while an attribute's (`array_to_accessor`)
never does. The JSON: every object's keys alphabetical (`to_dict`), the root's in `save_gltf`'s order, a material's
extensions in their gather order and a primitive's attributes in insertion order; `__fix_json` drops None and the
empty (`KHR_materials_unlit`'s `{}` kept) and writes an integral float as an int (a negative zero as 0); `json.dumps`
with `(',', ':')` and `ensure_ascii`; floats as Python's repr - `PyFloatRepr.cs`, the shortest decimal that reads back
as the double, found by exact integer arithmetic against the double's rounding interval (.NET Framework's `R` is not
shortest and its `Parse` is not correctly rounded, so neither could judge it), Python's exponent rule - held on the
6,151 distinct float tokens of 190 Blender files and 39,996 random doubles (float32 values, frame / 24, denormals,
every binade), all equal to `repr`. The GLB: the header, the JSON chunk padded with spaces, the BIN chunk with zeros.
The oracle is part 8a's dump, whose `GLB` row carries the file's lengths and its SHA-256 now, and the files themselves
(`--glbdir`: the first differing JSON character or byte, for the finder). Measured: 201 jobs byte-identical with
Blender's files (185 of them on disk at the time, 77 MB - the 50 MB tank, the howitzer, the infantry with its images among
them; the rest by length and SHA-256), the active scene's index in a two-scene file the one rule the first run missed.
`BlenderGlbWriterTests` hold the key orders, the dropped empties, the escaping, the padding, the sharing and the repr
on small inputs. The gate tampers the `GLB` row (missing, doubled, changed, cut short). Planted: 17 in the writer and the
repr, 15 fail the drill (the paddings, the sharing on and off, the key orders, the dropped empties, the generator, the scene
index, an image view's target, the images before the indices, sampler and source swapped, the input's min and max in
float32, a float's `.0`, .NET's `R` digits); the exponent threshold is held by a unit test (no float of the population
sits in 1e15..1e16), and a BIN chunk padded with spaces is equivalent (every view is padded, so the chunk never needs it).
An independent reader against the sources found three gaps the 201 jobs could not show, each confirmed and fixed:
the repr's shortcut (the nearest n-digit decimal) is wrong at an exact power of two, where the rounding interval below
is half as wide - 2^-24 came out as 17 digits where Python writes 16 - so both decimals around the value are tested
now (held on every power of two and its neighbours, 10,487 values); an integral float of 1e18 or more is Python's
int of any length (float32's max, 39 digits), not an exponent form; and an extension's own keys follow its gather
order, not the alphabet - KHR_materials_specular writes specularFactor, specularTexture, specularColorFactor,
specularColorTexture and meets the images in that order (measured in Blender on a material carrying all four; the
population's specular materials carry one key each). Three contracts on the earlier parts are named in the file's
header (the nodes' index order as the walk, every texture and sampler traversed, no explicit null in a material).
With this the port writes the file the script writes; what is left of `deploy_convert.py` in Blender is named per
shape (LEFT) and the Factory still calls Blender until the wiring.

**Replacing `deploy_convert.py`: the Factory wiring** (2026-10-11). `BlenderDeployInProcess.Run` is the script in
this process - the reader, the decisions and the bake (`BlenderDeploy.Decide`), the role clips (`Finish`), the export
(`BlenderDeployExport`) and the bytes (`BlenderGlbWriter`) -, each held to the script by `tools/deploy_drill.sh`;
`UniversalBaker.EnsureDeployConverted` runs it first and runs Blender only for what it names (a Fallback at any step:
the reader's refusal, a shape a part leaves by name, a failure here), exactly as the prep does since part 5. The
script's own exits (no animated part, no tube for the recoil) stop the bake as the script would, with the DEPLOY log;
nothing is a half-written file (a temporary file, then an atomic replace: the old file stays until the new one is
there). The work is bounded as the prep's is: the port refuses an animation past 5,000 frames before the decision
phase's own frame sweep (every later step walks every frame for every bone on the editor thread; Blender runs it under
the Factory's five-minute timeout), a bake-test cancel
passes through the checkpoint between the steps and the same five minutes leave the model to Blender - the review of
PR #153 asked for both. The script's argv[2:] is one list for both paths
(`Argv`) and the cache key carries `CacheRevision`, so a changed port converts again where the script's mtime cannot
tell. `BlenderDeployInProcessTests` hold the argv positions the port reads (13 the return, 15 the settle, 16 the extra
strip, 17..20 the wheels), a small rig converted end to end (its roles read back), an early long-clip fallback, the refusals that are Blender's,
and the script's exit. Measured: `Run` over every job of the deploy drill's dump that Blender wrote a file for - 185 of
185 written files hash-equal to Blender's (the six registry jobs in 20 s, 178 fixture jobs in 75 s).
`BlenderDeployInProcessTests` hold the seam; the drill holds the parts,
and the script's exit. Not here: the Bake Tests row in Unity that bakes a registry entry through the in-process
conversion and compares it with the Blender-converted bake - the gate cannot run Unity, it is the user's to run, as the
prep's row is; the prep writing the port's materials (the converter refuses a source material Blender would fold) is
now implemented in PR #155.

**The prep writes the materials as Blender writes them** (2026-10-11). `BlenderPrep.Assemble` takes its materials,
textures, samplers and images from `BlenderMaterialExport` (part 8a-ii's round trip, held to Blender on 105 files) for
the materials the stripped tree writes, in its order - so the Factory's converter reads of the prepped file what it
reads of Blender's: a specularFactor over 1 reaches it folded into the colour (carried verbatim, SharpGLTF's validator
refused the file - the finding of the 8a-ii review, the edge fixture back among the prep drill's files since), an unlit
or specular-glossiness material its exporter form, no extras. Where the material port names a shape it does not
model, the materials are carried as before (the converter's output was equal on every drilled file either way; only the
refusals differed). The prep drill holds it: 139 runs on 48 files, every written file read by the converter as Blender's;
`BlenderPrepTests` the fold on a material carrying the factor alone. The rules the old path noted for the drill's
coverage rows (unlit, specular-glossiness) are noted from the round trip too.

**Replacing `rig_anim.py`, step (a): the stage dump** (2026-10-12; the plan is in `docs/Review-Backlog.md`, item (4)).
`tools/riganim_drill.sh` is the oracle the stage ports are held to. `riganim_jobs.py` reads the registry and writes,
for every animated entry, the argument line the Factory hands Blender (UniversalBaker's slim step: the reduce target,
the bone prefixes, the primary clip, the albedo, the material slots, the rotation in "0.###" form, convertRig, the
state roles in the Factory's order, auto-ground, the socket bones as typed, keepTranslations, the static parts,
localNodeAnim; a deploy-converted entry runs on its `deploy_converted.glb`). `blender_riganim_dump.py` then runs the
script ITSELF on each job - its source cut at four of its own top-level lines into the four stages (the import and the
object-animation clear; the skin conversion and the flatten; the bone cap, the clips, the rest fold, the strip and the
frame range; the albedo, the join, the weld, the decimate, the rename, the sockets, the fold and the placement),
executed in one namespace, the FBX export not run - and writes the scene after each stage: every RIGANIM line in order,
every object with its two matrices, every mesh's positions, loops, polygons, material indices and UVs hashed, every
vertex group's weights hashed, the bones at rest (parent, deform flag, length, `matrix_local`), the pose (mode, loc,
quat, euler, scale, `matrix_basis`), every action's fcurves (keys hashed, handles hashed; `RIGANIM_KEYS=1` adds every
key), the albedo's bytes and 8-bit pixels. A marker not found exactly once fails the dump: a script edit that moves a
stage line is a FAIL, never a silently different cut. `RigAnimDrill.cs` reads the dump through `RigAnimDump` (the
class the stage ports will read it through) and holds the EVIDENCE: a block per job of the jobs file, stages 1..k
without a gap, a stopped job named with its stage and reason and failed as an incomplete recipe (the script exits
only with 1), every row's field count
and bits, and the rows against each other (a mesh row names an object, a weight row a group its mesh lists, a pose
bone a bone, an fcurve its action, an action its fcurve count, stage 4's albedo line its image row). 37 damaged copies
of a real block (`missing_riganim.py`: a missing, doubled, short or misplaced row of every kind, a stop claimed
wrongly, an actual early stop, an unknown job or row kind) are refused by name; the intact copy passes. Missing local
sources (including a deploy recipe's raw source and generated GLB) make the drill SKIP and invalid registry recipes
make it FAIL, rather than silently reducing coverage. NO RULE
OF THE SCRIPT IS COMPARED
YET, and the PASS line says so. Measured: 23 of the 24 animated recipes (the light assault mech's source is an .fbx:
LEFT by name until the native FBX reader, reported as such, never as the whole registry) in 17 s over four Blender
shards, all 23 through all four stages; a second run byte-identical to the first (the oracle is deterministic). The
census the recipes give the script (COVER rows): 21 take the conversion path, 2 the legacy one (the drone, the deploy
conversion), 19 carry state roles, 16 slice a clip and pad a held stance, 21 fold the rest, 15 carry a rotation, 23
are moved by the placement; AT ZERO in the registry, so a fixture before its port: the bone-parent skinning, the bone
cap, staticParts, localNodeAnim, the no-op root collapse, the weld, a collapsed material, a centred model, the
legacy amplify, a script exit or death.
Step (b)'s groundwork (2026-10-12): `riganim_fixtures.py` writes the stage 1 branches no recipe takes and the drill
runs them beside the recipes - the material-less drop (a lone mesh, one whose textured child becomes a root, one
coloured by COLOR_0, which the importer gives a "DefaultMaterial" and the script therefore keeps, a mesh of one
primitive without and one with a material, kept with an empty slot), the DeployArmV2 contract, object-level animation
on a mesh node (cleared on the conversion path, kept on the legacy one), several animations with a longer second one
and a STEP sampler, a bone-parented mesh, a scaled and turned wrapper empty, and a file without an armature. That last
one is the script's own exit: a job key starting with `EXIT:<stage>:` says the script is expected to stop at that stage
with a RIGANIM ERROR (the drill fails it if it runs through or stops elsewhere, and fails any other job that stops);
`LEFT:` marks
a job the C# will leave to Blender by name (localNodeAnim). `RIGANIM_KEYS=1` now records each key's handles,
interpolation, easing and handle types. In that mode the C# validator requires the keys and reconstructs both `FC`
hashes from them, so a changed or missing key cannot pass as importer-curve evidence.
Step (b), stage 1 in C# (2026-10-12): `editor/BlenderRigAnim.cs` is the first stage of the script without Blender - the
arguments as the script parses them (its WARN lines included), the import, the material-less drop, the armature lookup
and its exit, the DeployArmV2 marker, the object-animation clear - built from the deploy and prep ports (BlenderNames,
BlenderPosedState, the posed world matrices, BlenderReduce at a ratio of 1, the armature and the skinner) and the
importer's rules read for this stage (mesh.py: slots only when a primitive has a material or COLOR_0, a vertex group per
joint with every non-zero weight added with REPLACE, the modifier named "Armature"; animation.py and its helpers: one
action per animation, a slot "OB" + name per touched object in node-index order, the fcurves in that order three or four
per channel, a lone key's handles left at the zeros `keyframe_points.add` gave them, the first animation's action
restored as the active one and every other touched object keeping animation_data without one). `riganim.exe --stage1`
renders the model's scene as the dump's own rows - float32 bits and SHA-256 hashes - and compares every job's stage 1
with Blender's string for string; a differing field names the row and both values, a differing `FC` hash the first
differing key under `RIGANIM_KEYS=1`. A job the model would leave to Blender FAILS unless its key says `LEFT:`, and a
`LEFT:` job the model takes fails too. Measured: every dumped recipe (23 of 24 with FULL=1, 19 in the default sample)
and all 11 fixtures equal row for row - 48,102 rows of the full registry, 428 of the fixtures; two new fixtures for
the model's branches no recipe takes (`fx_groups`: an unused joint, a repeated joint, zero weights, a mesh without UV,
COLOR_0 beside a material, keys closer than 0.01 frame, a refused second channel, the armature object's own curves;
`fx_skinned_anim`: an animated skinned mesh node, a mesh on the armature node, one mesh on two nodes). Eight defects
planted in the model were each caught (no junk drop, no `OB` slot prefix, a lone key's handles computed, COLOR_0 not a
material, no animation clear, actions unsorted, fcurves in channel order, weights reversed), and twelve `S1_*` tampers
(`missing_riganim.py`: one changed value per row kind of stage 1, the dump otherwise valid) are refused in `--stage1`
mode while the intact control passes in both. Found on the way: the importer's vnode rotations are PRODUCTS with the
identity (`rotation_before @= rot`, `rotation_after = rot_inv @ rotation_after`), so the conjugate's minus zeros come
out plus and a bone key's zero takes that sign - four recipes (LightCruisers, SteamTransports, Zeppelin,
ReconHelicopter) read -0 in C# and +0 in Blender until `ImportRig.FinalTrs` used the products (the posed-state drill
could not see it: a written zero keeps the sign of the one it replaces); a dropped mesh's children keep their
`matrix_world` (the same at stages 1 to 3), not their local transform. Not modelled and named, so Blender's: a
.fbx/.blend source, localNodeAnim, several scenes, cameras, lights, instancing, variants, pointers, CUBICSPLINE, morph
weights, a sampler without keys, a mesh on a bone node, a twice-listed joint, and what BlenderReduce declines - none
in the registry. Stages 2 to 4 are not compared yet, and the PASS line says so.

Step (c), stage 2 in C# (2026-10-12): `BlenderRigAnim.RunStage2` continues on the scene stage 1 left - the bone-parent ->
skin bind (the governing bone up the parent chain, staticParts by lowered object and material names, the rest-world pin,
the full-weight vertex group on the governing bone or the root bone when the armature has no bone of that name, the
Armature modifier when none is there) and the wrapper flatten (every MESH and ARMATURE under an EMPTY unparented with
its world kept, every EMPTY removed) - and `riganim.exe --stages 2` compares stage 2's rows as stage 1's; a `LEFT:` job
may be declined at either stage, its earlier stage compared all the same. Blender's rules read for it: `matrix_world = m`
is BKE_object_apply_mat4 (the parent's matrix times the identity parentinv, Eigen's inverse of that times m,
mat4_to_loc_rot_size, mat3_normalized_to_quat times the inverse of the identity delta quaternion) and it writes the
PROPERTIES only - the object's matrix_world stays the last evaluation's until the next `view_layer.update()`, so the
flattened objects' rows of this stage still show the old matrix (Dirty in the model; stage 3's first update will use
the recomposed one); `parent =`, `parent_type =`, `parent_bone =` each reset parentinv to the identity; the update
after the bind re-evaluates what is tagged and what depends on it - the armature's pose (REST: pose_mat = arm_mat) and
everything hung from its bones, the re-homed meshes and their descendants - and what nothing tags keeps its matrix.
Measured: every dumped recipe and 25 fixtures (14 new, `fx_s2_*`; 22 equal row for row at both stages and 3 left by name),
among them the chain fixture's three nested hung meshes with rotations and scales (bit-exact recomposition), the second
armature hung from the first one's bone (its skinned mesh bound through it,
its 'Arm' group REPLACED to 1.0 from 0.3, the root-bone fallback for 'Other', a same-named bone bound to the first
armature's), the wrapper with an odd scale and rotation over a hung mesh and three empties, nine static meshes (the
log's ellipsis), the armature itself a dropped mesh's child (re-rooted by the bind's update before the pin). Thirteen
defects planted in RunStage2 were each caught (the pin taken in the pose instead of at rest, no decomposition, a
duplicate group entry instead of REPLACE, the modifier overwritten, the static list unsorted, the governing bone read on
the mesh only, the flatten evaluating the world, the orphan kept, no root fallback, the empties kept, staticParts
ignored, a bound mesh's descendants left stale, the pin against the armature's stale matrix) - three of them invisible
until a fixture had the shape (a weight other than 1.0 on an existing group, a static child under a bound mesh, the
orphaned armature), each added - and ten `S2_*` tampers (one changed value per row kind of stage 2) are refused. Found on the way: a dropped mesh's child keeps its matrix_world ONLY until the next depsgraph
update - the bind's update makes it a root at its own transform (`fx_s2_junk_hung`; the junk fixture of stage 1 never
evaluates the scene before stage 4, hence "the same at stages 1 to 3"); the importer leaves an animated skinned mesh
node as an EMPTY, which the flatten removes; the dump tells one action's fcurves apart by data path alone, so two
armatures with an animated bone of one name make an inconsistent dump (the FC identity carries no slot) - the fixture
animates the second armature's root instead. Not modelled, named: a bone-hung mesh whose datablock another object shares
(one vertex-group list and weight layer for two objects; `LEFT:fx_s2_shared_hung`). Review of PR #164 added
`fx_s2_unicode_static`: Python `str.lower()` expands U+0130 and applies final-sigma casing, which .NET invariant
lowercase did not; before the fix the C# model bound two meshes Blender kept weightless. The C# matcher now follows
those rules. Supplementary-script casing remains a named Blender fallback (`LEFT:fx_s2_supplementary_static`). Stages 3
and 4 are not compared yet, and the PASS line says so. The review also corrected the diagnostic census for an EMPTY that
wrapped nothing: it now counts wrapper parents before unparenting their children.

Step (d1), stage 3 in C# - the clip path (2026-10-13): `BlenderRigAnim.RunStage3` continues on the scene stage 2 left:
the clip resolution (a named action, or a slice sampled frame by frame into a new action: the source assigned with its
FIRST slot, every frame of the span evaluated and every pose bone's `matrix_basis.decompose()` taken, then location and
rotation keyed with `keyframe_insert` frame by frame, the held-stance nudge on a single-frame slice, the pose restored),
the action prune (the objects holding a pruned action lose it), the bone filter and its exit, legacy keepTranslations
(the role floors, the poison gate, the kept paths), the location strip, the delta-rebase and x100 amplify (not under
the V2 marker), and the scene range as Python rounds the action's. The rest fold and rebake (every conversion recipe
with a location key on a pose bone of its primary) and the bone cap (over 240 bones) are step (d2)'s: the model DEFERS
such a job by name, the drill counts it apart (`deferred` in the STAGES line, `$n_s3def` in the script), and its stages
1 and 2 are compared all the same. Blender's rules read for it: `assign_action` hands the armature the action's first
slot whichever object it was made for (the multi-slot trap: the bones play statues and the armature object takes that
object's curves); `frame_set` evaluates every object's active slot (LINEAR and CONSTANT segments as the importer made
them, `BLI_easing_linear_ease`; the stashed NLA tracks are muted) and the active depsgraph writes the values back to the
original properties; `Matrix.decompose()` is `mat4_to_loc_rot_size` and `mat3_normalized_to_quat_fast` (no checks);
`keyframe_insert` makes a path's curves in channel order at the first insert and MOVES a key already on the frame by
the difference; `bpy.data.actions` keeps its name order on an insert; `Action.frame_range` is the first and the last key
over every slot's curves, (0, 0) without one. Measured: both legacy registry recipes equal at stage 3 (ReconDrone's
bone filter keeping 140 of 810 curves; UniversalTanks' 105 kept translation paths, the amplify over its attack and move
slices, 301,434 keys), the 21 conversion recipes deferred at the fold with stages 1 and 2 equal; 20 new fixtures (`fx_s3_*`: slices plain, reversed with a step, single-frame with the nudge - the three on the conversion path deferred at the fold as Blender's own log shows the rebake there; slices on the legacy path over LINEAR and CONSTANT keys, one landing on its end frame, one held; four roles sharing slices and the primary; the three exits; the bone filter keeping, matching nothing, and over the armature's own curves; legacy keepTranslations kept with the rebase and amplify, nothing kept, under the V2 marker without the amplify; the multi-slot trap; an animated mesh under a slice; keys off the frame grid) - all 45 fixture jobs: 38 equal at every compared stage (5,543 rows), 3 left by name, 4 deferred.
Twelve defects planted in RunStage3 were each caught (the armature given its own slot instead of the first, a slice not landing on its end frame, no held-stance nudge, LINEAR keys sampled as Bezier, a pruned action left on its objects, the filter keeping object-level curves, the range truncated instead of rounded, every location curve stripped, the amplify without the rebase, the role floors swapped, a new action appended unsorted, no object evaluation at a frame) - three of them invisible until a fixture had the shape (a kept slide starting off zero, a move-role bone under its floor, a whole-action role clip), each added - and eight `S3_*` tampers (one changed value per row kind of stage 3, on the first dumped job the model compares there) are refused. Found on the way: `BlenderFCurve.Evaluate` evaluated every segment as Bezier - the importer's LINEAR and
CONSTANT keys sampled between frames were wrong until the segment follows the key's interpolation (the deploy drill
never saw it: `keyframe_insert` keys are Bezier). Not fixtured, named: the poison gate's NaN rejection (no glTF
fixture carries a NaN key), Unicode digits in a slice spec (Python's `\d`). A script finding the fixtures made plain:
the legacy keepTranslations floor table is keyed by the role clip's SOURCE name, so a SLICED attack or move clip
(`<source>_<role>_...`) never matches it and keeps nothing (`fx_s3_keept_kept`) - recorded in the backlog.

Step (d2), stage 3 in C# - the rest fold and rebake and the bone cap (2026-10-13): nothing of stage 3 is deferred any
more. The fold on the conversion path: every exported clip's pose matrices snapshotted frame by frame (the evaluation
of step (d1)), then at frame 0 every Armature modifier applied as Blender's linear deform (`armature_deform.cc`: the
mesh's positions into the armature's space through `target.world_to_object * armature.object_to_world` and its Eigen
inverse, each weighted bone's `chan_mat` = `pose_mat * invert_m4_m4(arm_mat)`, the weighted deltas summed and
scaled by one over the contribution, the point back; a multi-user mesh copied first, the copy named by the ID name
map), the pose applied as the rest (`applyarmature_set_edit_position`: head = `pose_mat[3]`, tail = head +
`pose_mat[1]` times the length, roll = `mat3_vec_to_roll` through `vec_roll_to_mat3`, `invert_m3_m3`, `mul_m3_m3m3`,
`mat3_to_quat` and `quat_split_swing_and_twist`'s `2 * atan2f(y, w)`; the rest rebuilt by the edit-bone round trip the
armature port already had), the pose channels reset, the bone-parented children kept in place
(`applyarmature_fix_boneparents`), the modifiers re-bound to the script's armature, the visual rebake with mathutils'
arithmetic (the adjugate inverse, the double-dot matrix product, `Vector.length`'s double sum from the last
component, `to_quaternion` = `mat4_to_quat`) keyed frame by frame, the primary's frame-0 residual printed with an
exact `%.6f` (`PyFixed`, BigInteger) and checked against its threshold (the exit modelled). The bone cap: zero-weight
leaf bones removed pass by pass; the survivors' rest stands (the same edit bones give the same matrices). Measured:
all 23 dumped recipes equal through stage 3 (5,834,428 rows with key rows; every conversion recipe runs the fold),
six new fixtures (`fx_s4_*`: two skinned nodes sharing one mesh - copied as `pad.001` before the modifier is applied; a
static part hung from a bone through the fold; the nested armatures with a location key on the carrier - the inner
mesh deformed by its own armature, then re-bound; keepTranslations on the conversion path with a single-frame role
clip; a rig a thousand times the size; 492 bones for the cap, two passes with a weighted leaf that stays) - all 51
fixture jobs: 48 equal at every compared stage, 3 left by name, 0 deferred. Fourteen defects planted in the fold and the cap were each caught - the mesh not taken into the armature's space,
`chan_mat` without the rest's inverse, the roll zeroed, the tail without the bone's length, the rebake through Eigen's
inverse instead of mathutils' adjugate, through `mul_m4_m4m4` instead of the double-dot product, the quaternion
without the normalize and checks, every bone taken as translating, no single-frame pad, the write-back without its
sign rule, the bone-parent fix skipped, the mesh copy unnamed, the cap ignoring the weights, the child's evaluated copy
refreshed - twelve by the fixtures and two only by the registry (the inverse's arithmetic on AntiTankIFV, the sign rule
on TankDestroyers). Found on the way: `frame_set` writes an evaluated value back to the original
object's property only where it differs, so a zero of the other sign the script set earlier keeps its sign
(TankDestroyers' mirrored wheels: eight pose rows differed by `-0` until the rule was modelled; the key rows were
equal); `applyarmature_fix_boneparents` takes the child's parent matrix from the EVALUATED armature (the old pose at
frame 0, the old bone length) and tags nothing on the child, so its evaluated copy keeps the old transform and
parent inverse until something refreshes it (`SceneObject.EvalStale`; two fixtures differed by rotation noise until
modelled). Not fixtured, named: a fold whose residual exceeds its threshold (the fold makes frame 0 the rest by
construction; only a NaN could trip it), a bone with an object hung from it removed by the cap (a named fallback).
Stage 4 followed in step (e).

Step (e), stage 4 in C# (2026-10-14): everything rig_anim.py does before its FBX export is modelled (`RunStage4`),
and `riganim.exe --stages 4` holds it to Blender. The albedo: the first mesh with materials, its first material's
base-colour image - or, without one, the first image node of the material's tree, an extension's texture included
(`base_color_image`'s fallback) - its dimensions from the encoded header; the importer's image NAMES made on first use
in the order the materials are made and, within a material, in `pbr_metallic_roughness`'s node order (emission, base
colour, metallic-roughness, normal, occlusion, then the extensions' textures; `MaterialTextureOrder`), one Blender image
per file path; the IMG row is compared by path, width, height and channels (the PNG's bytes and pixels are Blender's
encoder's). The join (`mesh_join.cc`): the active object - the first with an Armature modifier - first, then scene order;
the vertices through the active's inverse world with `transform_points`' identity skip at 1e-6 per entry; materials by
first appearance, vertex groups and UV layers by name (zeros where a mesh has none), custom normals, sharp flags and
edges carried; the other objects deleted, their children re-rooted. The weld skipped where the script skips it (a
one-material, one-bone mesh is LEFT by name: bmesh's remove_doubles is not modelled). The material slots kept or
collapsed to the first. The decimate: `BlenderDecimate` on the joined mesh at `min(1, max(0.02, target/total))`, the UVs
merged. The rebind. The root collapse (a root with one child, no key in the primary, no weight above 1e-6; the fallback
for an object hung from the victim stays as a guard - unreachable from a glTF: a hung mesh is joined, a hung empty
flattened, a hung armature's mesh binds to the carrying bone and weights it). The topological rename (depth-first with
the children sorted, `A###_` with sockets else `b###_`; Blender's rename fixes the assigned action's paths AND the NLA
strips' stashed ones - `BKE_animdata_fix_paths_rename_all` - and the script's patch covers the rest over `all_acts`).
The donor sockets (edit bones: the parent by lowered substring, head = parent head + offset, the direction normalized
with mathutils' double length and float reciprocal at `max(0.02, length / 4)`, the parent's roll, `use_deform` off; the
no-parent exit modelled; the sort exit LEFT by name - its message reads `_parent.name` AFTER `mode_set('OBJECT')` freed
the edit bones, and four Blender runs gave the right name once, an empty name once and a UnicodeDecodeError twice). The
rotation and scale fold (`Matrix.Rotation`'s angle wrapped to (-pi, pi] in float, then cosf/sinf; the parent-less
objects' worlds through `BKE_object_apply_mat4`; the primary's object-level fcurves stripped; `transform_apply` parents
first - `mesh_transform` with the skip rule, `armature_transform_recurse`, the properties reset, the world from the
UPDATED parent, each child's properties re-decomposed and its parent inverse rebuilt from the updated parent). The
placement (the bounds in double through mathutils' matrix-vector product, the move in the mesh's and the armature's
space through the adjugate inverse, `%.4f` and the `%.1f` warning). And the depsgraph evaluations the operators imply:
before the join, the modifier apply and the transform apply, and once more at the end (a bone-hung object follows the
bones the placement moved). The dump's MESH row gained a field for this step: every UV layer, name and hash (the
active layer alone could not see a second layer dropped by the join - a plant found that), and the registry was
re-dumped. Measured: all 23 dumped recipes equal through stage 4 (6,550,596 rows with key rows), 30
new fixture jobs (`fx_s4_*`: the albedo on the first mesh, on the second only, from an emissive-only material, the image
names, a KHR_materials_specular image as the first made, a mesh made after the body but sorting before it, the albedo
off; keepmats 1 and 0; the decimate on 13 and 12 triangles, on the legacy path and at 1/60 under the ratio's 0.02
floor; the root collapse done, kept when
keyed, kept with two children, untouched on the legacy path; the rebind; every mesh culled - the stage 4 exit; a mesh
already centred; the weld - LEFT; one, two and no UV sets joined; a body under a rigid hull mesh; sockets with an
offset and a bad one, through a rotation fold, the no-parent exit, the sort exit - LEFT; the fold at 10/90/-30 degrees,
at 180 on the multi rig, on the legacy path): 28 equal, 2 left by name; the earlier 66 fixture jobs unchanged (61
equal, 5 left). 20 defects planted in stage 4 were each caught - transform_points without its identity skip, Matrix.Rotation's angle unwrapped, a child's parent inverse kept through the fold, the stashed actions' paths not renamed, the join in scene order (the active object not first), the joined materials sorted instead of by first appearance, no evaluation before the join, no evaluation after the placement, the placement moving bone heads but not tails, a socket's roll zeroed, the images named in reverse material order, the root collapse ignoring the weights, the rename's children unsorted, the decimate ratio without its 0.02 floor, the join keeping only the active object's UV layers, the joined edges not offset, the joined vertex groups not united by name, no evaluation before the transform apply, the rename prefix b###_ with sockets, the script's path patch over the kept actions only - 20 by the fixtures and 0 only by the registry (none). Ten S4_* tampers refused (a placement log line, an object matrix, the mesh's positions, groups and
UV layers, a vertex group hash, a bone's rest and name, an fcurve path, the IMG row's width). Found on the way: the importer names
images in the order of first use, not glTF index order (`fx_s4_albedo_names` said `tex.001` where Blender said `tex`
until modelled); `scene.objects` keeps the creation order, so the first mesh node with materials is the albedo's even
when it sorts last (`fx_s4_albedo_ext`); `base_color_image`'s fallback is the tree's first image node, an extension's
texture included (the emissive fixture, first LEFT, is equal); the registry's five KHR_materials_specular recipes turned
LEFT until the extensions' texture order was read from the importer (`pbrMetallicRoughness.py`: clearcoat,
transmission, volume, specular, anisotropy, sheen; `KHR_materials_pbrSpecularGlossiness.py`; `KHR_materials_unlit.py`).
Not fixtured, named: a no-op root bone with an object hung from it (unreachable), the weld (left), a volume thickness
or sheen texture (read, no source has one).

Step (f), the GLB rig route (2026-10-14): the stage 4 scene written as a GLB - the file the Factory's animated path will
read instead of rig_anim.py's FBX. The oracle is Blender's own glTF exporter run on that scene:
`blender_riganim_dump.py` exports once after stage 4 (every action in the file, the scene's frame range widened to
their rounded ranges, the first frame evaluated as the script's export pins the pose, `export_scene.gltf` as
deploy_convert.py calls it) and reads the file back row by row (`tools/deploy-drill/glb_rows.py`, the deploy export
dump's reader shared; the rows under `X`). `editor/BlenderRigExport.cs` makes the same file from stage 4's state
through the deploy port's exporter model: the vtree (`tree.py`: roots and children in `bpy.data.objects` order, an
armature's objects then its root bones, a bone's bone children then the objects parented to it - their world
re-based on the REST bone, `rest @ pose.inverted_safe() @ matrix_world`), the serializer's numbering (a node after
its children, mesh and skin; a skin after its joints, each bone after its bone children, the neutral bone last), the
skins and inverse bind matrices (`skins.py`, the armature object's world), the joined mesh's primitives with its real
weights (`BlenderExport.MeshPrimitives`: the groups above 0.0001 that name a joint, the strongest four normalized, a
vertex without one on the neutral bone; a group naming no bone - the root collapse's - counts for nothing), the
materials by first use, the animations (`action.py`: every action with a slot of the armature, an empty one too, by
name lower-cased, cut to the scene's range) and their channels through `BlenderChannelExport`'s sampler - split from
the deploy's state into a Spec (the bones in the vtree's walk with matrix_local and offset, the modes, the pose the
script left, the actions visited, each one's curves) so the deploy and the rig share every rule of part 8b -, then
`BlenderGlbWriter` for the bytes. `riganim.exe --export` compares every row (the scene, the nodes, the skins and
inverse bind matrices, the primitives' bytes, the materials, textures, samplers and images, the animations, the
channels sample for sample) and the GLB's lengths and SHA-256; `RIGANIM_GLB_DIR` names the first differing byte.
Measured: all 23 dumped recipes equal (11,133 rows), 17 of them byte-identical GLBs (the six with a JPEG albedo are
compared without the file: stage 4's `image.save` makes a PNG of Blender's encoder, whose bytes the exporter packs; a
PNG albedo's bytes stay the source's and its file is byte-identical); 95 of 116 fixture jobs' exports equal and
byte-identical (three new fixtures, `fx_f_*`: 300 weighted bones - the joints as unsigned shorts -, an animation of
one key - the exporter undoes frame_range's +1 -, sparse STEP keys - CONSTANT segments sampled between the keys; the
EXIT jobs have no file; 11 left by name). 17 defects planted in the export were each caught - an armature's bones listed before its objects, a node numbered before its children, the inverse bind matrix with the basis change on the other side, a joint's transform snapped like an object's, no neutral bone, vertex groups mapped to joints by index instead of by name, the scene's range not widened to every action, the animations sorted case-sensitively, the albedo image not renamed after the saved file, a missing colour layer filled white instead of zeros, the render colour index kept at 0 through the join, a mesh without vertex groups given a skin, the active action's bones reset like the others', the pose the script left taken as the identity, an action without curves not exported, materials numbered by slot instead of first use, colour layers dropped by the decimate - 12 by the fixtures and 5 only by the registry (the animations sorted case-sensitively, the active action's bones reset like the others', the pose the script left taken as the identity, materials numbered by slot instead of first use, colour layers dropped by the decimate). Three more plants were no defects and were dropped: a single-key action's range (Blender 5.1's frame_range is (f, f) for it - the exporter's +1 undo is dead code here), a CONSTANT key evaluated without the flag (BlenderFCurve honours the key's interpolation itself), and the hung object's re-basing order (no compared file reaches it). Nine S5_* tampers refused (the export's RIGANIM line, a node's translation and
children, an inverse bind matrix, a primitive's indices, a channel's output and interpolation, an animation's channel
count, the GLB's hash). Rules found on the way, each by a differing row: the importer's image is renamed after the
saved file's stem (stage 4's `image.save`) and typed image/png; `__fix_json` walks dicts and lists but not tuples, so
a colour factor the exporter builds as a tuple (`specular.py`: the socket's `default_value[:3]`) keeps every float's
repr while a scalar ior of 1000.0 becomes `1000` (two registry rigs' JSON differed by `[0,0,0]` against `[0.0,0.0,0.0]`);
a mesh with the Armature modifier but no vertex groups gets joint attributes on the would-be neutral index and no skin;
the join unites colour layers by name and zero-fills a layer a mesh lacks (not white: `fx_groups` said so), and keeps
the active object's render colour index, which decides whether a material's Color Attribute node finds a layer (none:
COLOR_0 is the forced 255s and the layer follows as COLOR_1); an action with a slot and no curves is exported with
every bone's three constant channels; a slot of an object the join removed animates nothing. Left by name (EXPORTLEFT:,
the four stages equal): a second armature no mesh uses - its skin is gathered after the animation export, from the
scene the sampler leaves, and neither the first frame's posed nor its rest world gives those matrices (three
fixtures; no registry rig has a second armature). Not fixtured, named: a mesh of 65,535 vertices or more (the
registry covers the unsigned-int indices), a bone-parented object with children of its own (their delta), a child
of a skinned mesh node, the re-basing of a bone-parented object itself (only a
second armature hangs from a bone once the join and the flatten are through - EXPORTLEFT, so no compared file reaches
it; its product order is read, not measured).

**Step (g), the Factory's animated path on the GLB route (2026-10-14).** `editor/BlenderRigAnimInProcess.cs` is
rig_anim.py in this process: the four stages (`BlenderRigAnim`) then the export (`BlenderRigExport` +
`BlenderGlbWriter`), one GLB with every kept action, written atomically, with the RIGANIM lines, the primary action's
name and each state role's (the FBX route wrote one file per role; the GLB names its animations after the actions),
and the albedo the script would have saved (a PNG source's bytes as they are; a JPEG source's the Factory encodes to
a PNG with `Texture2D.EncodeToPNG` - and inside the GLB the source JPEG, typed image/jpeg, where Blender's file
carries its own PNG re-encode). What a stage or the export names as Blender's is Blender's: the bake takes the FBX
route for that model and the Console says why (at info level for a named shape, as a WARNING for a crash - a crash is
not a named shape). The script's own exits stop the bake as they would in Blender. The route is chosen per recipe
(`rigRoute`: the editor's default, `blender`, `csharp`; the Lab's "Rig route" popups) with an editor-wide default
(EditorPrefs `HAF.RigRouteDefault`, Blender until the Unity acceptance below passes); the slim cache key carries the
route and `BlenderRigAnimInProcess.CacheRevision`, so a switch or a changed port re-slims. On the GLB route the Factory
imports the rig with `HafModelRig` (the Unity-space frame of the clip picker; the bake scale folded into the vertices,
the node positions, the bind-pose translations and the position curves), persists it under `anim/` as a prefab, its
meshes and material and one clip per role, and the clip collections point at those clips (the PR #151 slice's path).
The drill holds the wrapper on every exported job: `riganim.exe --export` also runs `BlenderRigAnimInProcess.Run`
with the job's arguments and requires the file written, its SHA-256 the dump's GLB's when the export compared the
bytes (a JPEG albedo's file is read back instead: every image with bytes, the export's animation count), the primary
and the roles named as the model names them, the roles sidecar reading back, the albedo written when the model says
so (`EXPORTS jobs N rows R inprocess N`; the PASS line says how many). The dump's export step is the shipped
`editor/Tools~/rig_anim_glb.py` (rig_anim.py's source cut before its FBX export marker, then the one glTF export),
imported by `blender_riganim_dump.py` from the rig_anim.py argument's folder, so the drill and the Factory's Bake
Tests row have one oracle; the registry re-dumped through it gave the same 11,133 X rows. Measured: all 23 registry
jobs through the wrapper (17 files byte-identical to Blender's, the six JPEG-albedo rigs read back whole), 95 of 116
fixture jobs (every exported one) and the 9 review fixtures. The Bake Tests row "Does the in-process rig slim match
Blender's GLB export?" (`editor/BlenderRigAnimHeadlessTest.cs`) is the acceptance the backlog asks for before the
default flips: for every animated registry entry with a glTF source (a deploying entry's converted rig from its
cache), `UniversalBaker.RigAnimArgv` - the slim block's own argv builder - runs the port and Blender's
`rig_anim_glb.py` and holds the two GLBs byte for byte (a JPEG albedo's with the image bytes aside, read back and
named field by field), the albedo PNG byte for byte or by its size, the script's exits on both sides; a named
fallback is a SKIP that says why, a crash a FAIL. A source-reading review before the PR found three defects the drill cannot see (the GLB is Blender's byte for byte; they
are in the Factory's use of it), each fixed: a fallback mid-bake left the earlier key's GLB and sidecar beside a csharp
stamp, so the next bake with 'Reuse extracted' took the stale GLB (now the fallback deletes them and stamps a fallback
key, which the next bake at the same settings and port revision honours by taking the FBX route up front); Blender's
exporter writes absolute times and `HafModelRig` samples from 0, so an unsliced action starting at frame F baked F
frames of held pose (each clip is rebased on its animation's first key); the Lab's and the Factory's previews read only
`anim/<name>_anim.fbx` (a shared `RigPreviewPath` resolves either route's output, and each route removes the other's
files when it writes). Owed: the row's run in Unity, then the in-game check of each model
playing its roles, then the default.

Review of PR #174 (2026-10-14): the new validator accepted truncated exports (even a GLB header alone),
missing unreferenced objects/actions/logs, and malformed GLB records. Each completed or stopped job now ends in an
`EVIDENCE <key> <row-count> <sha256>` row before `DONE`, sealing every preceding row (including logs and export rows,
UTF-8 with LF and a final newline). Export rows also receive independent field, integer, float-bit, hash, JSON,
index/reference and child-count checks. The negative drill repairs seals for semantic counterexamples and preserves
them for corruption counterexamples, so the checksum cannot replace structural validation. Old unsealed dumps must
be regenerated. When masking work paths for deterministic comparisons, regenerate the seal after masking too.
The recipe reader rejects absent or malformed part lists as invalid, and the jobs writer retains the window's raw
primary source-path spelling. JOB/FAST definitions must use exactly `rig`/`rigfast`. Focused unit regressions and
native Unity recipe parity cover these contracts, including sparse recipes and partial vectors.

**The vehicle_rig drill, step (a) of item (5) (2026-10-14).** `tools/vehiclerig_drill.sh` is the oracle the vehicle_rig
ports will be held to, built as the rig_anim drill was. The jobs are the 24 saved Vehicle Lab recipes
(`Assets/FactorySource/VehicleLab/Recipes`), each the argument vector a Generate hands Blender: `editor/VehicleRigArgs.cs`
is the ONE builder of that command line - `VehicleLabWindow.Vehicleize` fills it from its fields, the drill's jobs writer
(`tools/vehiclerig-drill/VehicleRigJobs.cs`) from the recipe's JSON read as the window loads it (absent keys keep the
initializers, the load guards repeated) - and the two fillings are held equal to the character on every recipe by the
headless Generate (the Lab gate rows) and by `tools/riganim_unity_import_drill.sh` (24 of 24). The names files and the
placement file are written as the window writes them. Blender runs the script's own source
(`tools/vehiclerig-drill/blender_vehiclerig_dump.py`) cut at four of its top-level lines into five stages - the import,
merge and flatten; the reduction tiers; the rig; the actions; the export - the scene dumped after each with the shared
rows of `tools/riganim-drill/scene_rows.py` (now imported by the rig_anim dump too) and the exported GLB read back
(`glb_rows.py`, the X rows); the source-skeleton fast path exits 0 in stage 1 after its own export and is read back the
same. A Blender 5 action with several slots repeats a curve path per slot (the dugout canoe's 'Take 001'), so this
dump's FC and K rows carry the slot after the action. `VehicleRigDrill.cs` holds the evidence: every recipe's block
complete and consistent (the rig_anim parser's rules), a run to its end with its GLB rows, a stop named with the
script's exit, and the census (COVER rows) of the branches the recipes take by the script's own VEHICLE lines; 45 damaged
copies of the real dump are each refused (`missing_vehiclerig.py`). Measured: all 24 recipes dumped in 90 s with four
Blenders (FULL=1; the 18 under 40 MB in 47 s by default), 23 through all five stages and the Ehrhardt on the fast path;
115 scene dumps, 2,624 VEHICLE lines, 870 bones, 3,151 fcurves and 3,804 GLB rows on record; deterministic: two full
dumps are equal with the work folder masked (the output path in the script's DONE line is the only difference).
Branches at zero in the census - fixtures before their port: the modifier bake, the script's own exits and deaths.

**Step (b), stage 1 of item (5) (2026-10-15).** `editor/BlenderVehicleRig.cs` is the first stage of `vehicle_rig.py` in
C#: the arguments (`ParseArgs` - the names files by role, the orientation padded or cut to three values with the WARN for
one float() refuses, the tagged merge2/bright/flagfold/parttx and reduce tokens; the script's own exits in their places:
bright= and flagfold= before the import, the merge2 field count and a missing second model before the second import,
its offset and rotation deaths and its scale exit after that import's lines, the four reduce tags after the merge, the
dial and names-file deaths before the orientation, the all-Ignored crash in `_guard`; the rig path's own lines - a bad
orientation, static tracks - AFTER the merge lines), the import through `BlenderRigAnim.ImportScene`
(factored out of rig_anim's stage 1: no material-less drop, no armature required, every clip's objects posed at frame 1),
the importer's bone-shape placeholders purged (by name at import, by signature after), the brightness line's counts from
each material's Base Color chain (a flat colour, a texture linked straight in, a MULTIPLY tint absorbing what it can),
the second model merged (imported into the same name pools and posed at frame 1 like the first, renamed B_ in
bpy.data order with the slot identifiers left as imported, its target matrix baked into the vertices, a negative
determinant's faces flipped, the helpers dropped, the MERGE lines), the flatten (every mesh a root holding its world,
the vertex groups and modifiers cleared, the armature dropped), the single-mesh loose split (the walk of
BM_mesh_calc_edge_groups_as_arrays: group 0 stays with its vertices sorted and the face order kept, every other group a
new object with the walk's vertex order, the edges in walk order and the faces in discovery order, each edge's faces
in radial order - the last created first), the per-part placements (the detach round trip, the world box, the PLACED
line, the WARN for a part the split has not), the ignored parts, the orientation on the roots, and transform_apply (a
shared datablock copied, a matrix within 1e-6 of the identity left untouched as BKE_mesh_transform does, a negative
determinant's faces flipped). `vehiclerig.exe --stages 1` holds it against every dumped job's stage 1 rows as strings -
the dump's own float32 bits and hashes: 22 of the 24 recipes equal (153,497 rows of objects, meshes and actions), the
2 `.fbx` sources left by NAME (`SourceFallback`; any other decline the model makes is a FAIL), and the 20 fixtures of
`tools/vehiclerig-drill/vehiclerig_fixtures.py` equal - the branches no recipe takes: a placement beside a missing
part and one with a zero scale, the orientation beside an Ignore name nothing matches, a shared mesh with a mirrored
instance, a second model with a turned root, an empty, an animated node and an icosphere placeholder, static tracks
beside a mirrored, instanced and separately brightened second model, a skinned source, one mesh of three loose boxes,
brightness on flat, direct and tinted materials, and eleven exits and deaths (`--exits`: the writer's jobs post-edited
into the malformed tokens the Lab never writes, keyed EXIT:1: / DIES:1: - every part Ignored, bright, flagfold, the four
reduce tags, a second model that is not there; int("15.0") for the frames, a merge2 offset float() refuses, a wheels
file that is not there); the fixtures are recipes the jobs writer turns into jobs like the saved ones. The S1_*
tampers (`missing_vehiclerig.py`: one changed value per row kind of stage 1 on a valid, re-sealed dump - 13 kinds, 11 on
the gate's default sample, whose damaged job prints no VEHICLE line in stage 1) are each refused in `--stages 1` mode. Found: transform_apply leaves a matrix within 1e-6 of the identity untouched (the bell's
1.0000001 scale); separate LOOSE's face order is the walk's discovery order with each edge's faces in radial order;
a slot identifier does not follow its object's rename; the import's world of an animated object is its first clip at
the scene frame (the importer's keys start at frame 0, so frame 1 is interpolated); `rel.matrix_world = mw` on a root
is a quaternion round trip, not a no-op. The fixtures caught the last three after all 22 recipes agreed; a source-reading
review then found the argument-time lines placed before the import's (no recipe or fixture combined static tracks with a
merge), the orientation's pad/cut, .NET's half-away rounding where Python's `%.Nf` rounds half to even (`PyFixed` now at
every line), the unmodelled exits and deaths above, and static tracks still in `_role_marked` - each fixtured. Not
modelled, named: a brightness texture without an image (the script's WARN chain) is a Fallback; a zero-size image the
script skips cannot be told from this side.

Review of PR #175 (2026-10-15): four added Blender fixtures failed the original model: a removed helper still
reserved the loose split's object name; an imported bone-shape name stayed reserved before the second import; two
files with the same animation name made duplicate action names; and a `DeployArmV2` source printed rig_anim's
contract line on the vehicle path. Object deletion now releases its name while leaving orphan mesh datablocks
reserved, the second import shares the existing action namespace (empty animations reserve no action), and the
contract line stays on the rig_anim path. Three more death fixtures cover infinite/NaN integer dials and an infinite
flagfold count. The drill compares the exception's type and message as well as its stage and preceding log lines;
a unit counterexample proved the original drill accepted a different exception. The expanded full run compares all
24 recipes and 27 fixtures: 49 equal, two FBX fallbacks, 153,697 stage-one rows, and 90 damaged-evidence cases refused
with all animation keys emitted. Color layers are also retained in their own point/corner order through loose splits,
corner colors move with mirrored faces, and mesh copies own their color buffers; focused unit tests cover these
attributes, which the current scene-row format does not record.

**Step (c), stage 2 of item (5), the weld tiers (2026-10-15).** `editor/BlenderVehicleRig.Stage2.cs` + `editor/BlenderWeld.cs`
are the script's SOURCE-SIDE reduction tiers in C#, held by `vehiclerig.exe --stages 2` after stage 1 on the same rows. Per
tier (DEFAULT, RIGGING, SHROUD, STRUCTURE, BODY, OAR, SAIL, RUDDER, WHEEL, GUN, WINDOW, FLIP, PRESERVE, DETAIL; skipped without
names or at 0.5 % or less, the dials read as `min(95, max(0, float))` - a `nan` is 0, as Python's max keeps its first
argument) and per named part (the first object of the name; a name the scene has not is the WARN): the mesh into a bmesh;
the WELD tiers (all but RIGGING and SHROUD) run `remove_doubles` within each material - the vertices whose corners all
share one material, grouped in the order the materials are first met, at 1e-5 of the part's largest extent -, then mark
the manifold edges over 40 degrees sharp; triangulate (a no-op on triangles); the bmesh back into the mesh, every face
smooth; the dial's target `max(8, int(v0 * (1 - pct / 100)))` from the welded count and, above it, the DECIMATE modifier at
`ratio = max(0.02, target / vmid)` (a float32 property) - `BlenderDecimate`'s collapse on the mesh as the modifier converts
it -, the evaluated mesh into a new datablock named after the old through the mesh name pool and the old removed; the lines
per part and per tier (`%.0f` through `PyFixed`). Rules read from Blender 5.1.2 and measured: `BMesh` now carries
BLI_mempool's free list (a killed slot is reused by the next creation, last freed first; the walks stay in slot order),
since the weld creates faces after kills; `find_doubles` is BLI_kdtree's median-balanced tree searched in the BALANCED
node order, a cluster of two keeping the lower index, a larger one the member nearest its float32 centroid (the search's
origin first, then the lowest index); `weld_verts` re-makes each edge between the targets (an existing edge reused, a new
one with the old flags) and each face from its mapped corners (an identical face reused, else the new face takes the old
face's attributes and the corners' data, then the two SWAP so the old slot lives on with the new corners), then kills the
tagged faces, edges and vertices in that order; the operator is flagged NORMALS_CALC, so every face normal is recomputed
when it finishes - the sharp test reads those, not the copied ones (8,062 sharp edges on the Romanic's hull against 8,041
before that line, and two vertices of difference after the collapse); `calc_face_angle` is `angle_normalized_v3v3` on the
stored normals (2 asinf(|a - b| / 2) through the C runtime's asinf) against `math.radians(40)` in double; the vertex
normals the modifier reads are `Mesh::vert_normals()` = the custom normals mixed per vertex, whose fans now stop at the
weld's sharp EDGES as at sharp faces (`VehicleProbe.DecodeCustomShorts` learned the edge set) - the true normals broke a
matching part, the fans without the edges left the Romanic two vertices off; a packed vertex pair must hash through
`BlenderMesh.EdgeKeyComparer` (the plain `long` hash XORs its halves: a 65k-vertex part took 6 s to convert, 83 ms after).
Measured on the registry dump: 20 of the 24 recipes equal row for row after stage 2 (306,717 rows; the two FBX sources
left by name), 2 left by name at stage 2 - the khelandion's RIGGING and the Wespe's SHROUD tier run the limited dissolve,
`bmesh.ops.dissolve_limit`, not ported yet -, and the 30 fixtures equal, three of them for this stage
(`vehiclerig_fixtures.py`: a 24-vertex split box over two materials welded to 16 with every box edge sharp, a bumpy grid
with a doubled seam row welded then collapsed to the dial, four tiers in one scene with a WARN, a 0.5 dial skipped and a
120 dial clamped to 95). The S2_* tampers (7 kinds; 6 on the gate's sample, whose damaged job prints no tier line) are each
refused in `--stages 2` mode. A Blender probe of the weld steps alone
(`weld_probe.py`, scratch) was the arbiter for the Romanic: the welded mesh's positions, corners, UVs and edges hashed
equal from the first run, the sharp count and the normals did not until the two rules above. The review of that work found
the gate blind to the weld's edge order, its sharp_edge set and the custom-normal shorts it carries (the MESH row hashed
positions, corners, materials and UVs only, so a mis-copied normal on a part under its target passed): the row now carries
the edge list, the sharp_edge attribute (the importer writes it, all false, beside the custom normals - so only on a mesh
with file normals; a mesh-level operation keeps it; a BMesh round trip - the edit-mode split, the decimate, the weld - writes
it back only when an edge is marked: three Blender probes of the fixtures settled the rule) and the shorts per corner,
in both dumps and both renders - the rig_anim drill's 145 jobs hold them through its four stages too -, with S2_* tampers
on each; a fixture with a scaled, turned node in a weld tier holds the distance rule (o.dimensions after transform_apply).

**Step (c), stage 2 of item (5), the soup tiers (2026-10-16).** The RIGGING and SHROUD tiers run no weld: the script hands the raw
mesh to `_dissolve_limited(_rb, 5.0)` and then `bmesh.ops.triangulate` on every face, and part 1 left both recipes that take
them to Blender by name. `editor/BlenderDissolve.cs` is `bmesh.ops.dissolve_limit` (`BM_mesh_decimate_dissolve_ex`, Blender
5.1.2): the EDGE phase heaps every input edge with two faces by `bm_edge_calc_dissolve_error` (-cos of the face angle when
the faces are contiguous, invalid otherwise) against -cosf(angle) - the angle `math.radians(5.0)` into the float property -
and joins pairs from the top while the cost holds (`BM_faces_join_pair`: when the faces share the vertex twice over the edge
one is reversed; `BM_faces_join` builds the n-gon from the two rings' edges minus the shared one, sorted by winding
(`bm_edges_sort_winding`), reuses a double when `BM_face_exists` finds one, copies each new corner's data from the old corner
at the same vertex, kills the old faces and leaves the shared edge wire; the joined face re-enters the heap through its edges,
whose costs are recomputed from the new normal), then kills the edges left wire in reverse slot order and the vertices left
alone; the VERT phase heaps every input vertex with exactly two edges by `bm_vert_edge_face_angle` (pi minus the angle at the
vertex, scaled down by the face angle where the two faces meet) and collapses from the top while under the limit and not
degenerate (`bm_vert_collapse_is_degenerate`: the vertex's two faces projected on their dominant axis, the sign of each
corner's cross product before and after the collapse, and a point-in-triangle test for the other faces' corners);
`BM_vert_collapse_edge` -> `bmesh_kernel_join_edge_kill_vert` splices the edge, drops a degenerate loop or a duplicate edge
it makes, and the kernel's own kills reuse slots through BLI_mempool's free list as every creation here does. Face normals
are tri/quad/Newell (`normal_tri_v3`, `normal_quad_v3`, `normal_poly_v3`) and the operator's NORMALS_CALC recomputes them
all when it finishes. `editor/BlenderPolyfill.cs` is `bmesh.ops.triangulate` (`BM_mesh_triangulate` tag_only on every face
of four or more corners, the mapping variant: a double it would make is kept): a quad takes `BM_face_triangulate`'s BEAUTY
rule (`is_quad_flip_v3` first, then `BM_verts_calc_rotate_beauty` on the projected corners: the diagonal whose two
triangles score better by area and perimeter), an n-gon goes through `axis_dominant_v3_to_m3_negate` into 2D, then
`BLI_polyfill_calc` (sign 1: ear clipping that alternates CLIP_EVEN and the sweep, the concave and tangential corners in a
balanced kd-tree whose ear test is precomputed per candidate) and `BLI_polyfill_beautify` (every interior half-edge in a heap
by `BLI_polyfill_beautify_quad_rotate_calc_ex`, rotated while negative, the neighbours re-costed); the triangles become
faces through `BM_face_create_verts` in the polyfill's order (edges ensured (v2 v0), (v0 v1), (v1 v2)), the last one swapped
into the original face's slot and the rest killed. `BlenderVehicleRig.Stage2.cs` runs `_dissolve_limited` exactly as the
script chunks it: at or under 4,000 faces one call over every vertex and edge in slot order; above it, the faces'
`calc_center_median` (float32 sum, times 1/len) are BSP-split at the median along the widest span (Python's `max - min` in
double, the first widest axis, a stable sort, `len // 2`, the second half popped first) until every cell holds at most
4,000, then per leaf the edges whose two faces lie in the cell and the vertices whose faces all do, each list in index
order, one dissolve_limit per leaf when either list is non-empty - the greedy order the script fixed on 2026-10-12. The
dial's base is the raw count on a soup tier, `use_smooth` is set on the weld tiers only. Measured: the drill's "dissolve
pass" named-left rule is gone, so any stage 2 decline FAILs; 22 of the 24 recipes equal row for row after both stages
(307,475 rows, the two FBX left by name) - the khelandion's 91 and the Wespe's 26 parts through the dissolve, the Wespe's
141k-vertex shroud through the BSP cells -, 36 fixtures equal, three new (`fx_c_soup_flat`: a flat 6 x 6 grid dissolved to one
quad - 50 faces to 2 - and cut back to two triangles with its UVs; `fx_c_soup_L`: an L-shaped strip whose concave hexagon takes
the ear clipping's kd-tree, 40 faces to 4, beside a lone flat quad on the quad rule; `fx_c_soup_bumpy`: a 4,800-face bumpy
grid, over the cap, dissolved per BSP cell to 182 faces), equal from the first run of each. `Tests/VehicleRigDissolveTests.cs`
holds the flat grid's dissolve to one quad (4 vertices, the Newell normal, the UVs carried) and its triangulation, the
bumpy grid's four coplanar strips with their straight-run vertices collapsed, the hexagon's Newell normal, the ear clipping's
cover of a square, an L and a U (n - 2 triangles inside the ring, the area summed; sign 0 detects the ring's direction as
sign -1), and the beautify's choice of the short diagonal on a kite with the rotate rule's contract both ways.
Review of PR #177 (2026-10-16): a valid 4,002-triangle source reproduces a cross-cell splice. The first BSP cell joins two
triangles, collapses their collinear rim vertex, and splices away an existing edge queued for the second cell. Blender's
wrapper raises `TypeError: edges: 0 BMEdge has been removed`; the model now records the same death before `to_mesh`, rather
than leaving the case unmodelled. The stage comparison checks its exact exception and preceding log lines. A permanent unit
regression and `DIES:2:fx_d_chunk_splice` hold the case. `BMeshTests` also pins the two-disk edge lookup after a topology edit
puts duplicate edges in different disk orders (the author's concurrent follow-up supplied the lookup fix).
`fx_c_soup_attributes` holds custom normals, vertex colours and two UV layers through the soup's joins, collapse and
triangulation. The colours part was not true when the review wrote it: the stage rows' MESH row carried no colour layer at
all, so a plant that stopped the vertex collapse copying corner colours passed every drill (found verifying the review by
plant, 2026-10-16; the weld tiers of part 1 had moved colours unseen too). The MESH row now ends in a colour field, shared
by both dumps (`tools/riganim-drill/scene_rows.py`) and the C# render: every colour attribute in code-point order of its
name, its domain and the SHA-256 of its bytes (a BYTE_COLOR read back exactly as round(color_srgb * 255), the decimate
drill's reading; any other type hashed as floats under its type's name, so it cannot match a model holding bytes). Every
stage stayed equal with colours compared (rig_anim 134 of 145 through four stages, vehicle_rig 60 of 62), the same colour
plant now FAILs on `fx_c_soup_attributes` alone, and a format tamper is refused by both drills. The stage 2 value tamper
(`S2_MESH_colours`) is written only when the job the tamper copies come from has a colour layer, and today's (the first
all-stage glTF job) has none, so it does not run; the plant above is the evidence that a changed colour is caught. Full positive comparison: all 24 saved recipes and 38 fixture jobs, 60 of 62 equal after both stages,
307,490 rows, the two FBX sources left by name; 1,762 unit tests pass. Independent temporary Blender probes also matched
4,400 polygon/grid/soup cases in vertex and edge order, face winding, material/smooth flags and UVs, including degenerate,
non-manifold and inconsistently wound meshes. These probes are review evidence; the committed regressions run in the gate.
Remaining source-read branches still need dedicated fixtures (double-face reuse, degenerate refusals, kill-degenerate and
kill-duplicate, wire cleanup, tangential/desperate polyfill, concave-quad flips, triangulate doubles); randomized matches
do not prove each branch was reached.
Scoped out, named: a name listed twice in one tier's list would name its second datablock differently
(the Lab lists a part once).

**Step (d), stage 3 of item (5), part d1: the frame every rig shares (2026-10-16).** Stage 3 is 1,382 lines of branches keyed on
the role lists, so it is ported by branch: `editor/BlenderVehicleRig.Stage3.cs` declines a job by name ("stage 3: the ... is not
modelled yet") when a list a later part ports is non-empty - turrets, guns, muzzles, cradles, trails, rotors, tail rotors,
sails, flags, oars, tracks - and the drill (`--stages 3`) counts that job as left, any other decline as a FAIL. What part d1
runs, against vehicle_rig.py 1515-2896: the wheel clustering - per wheel name `find()` (a missing one is the script's VEHICLE
ERROR with the part list as Python prints a list of str, and exit 1), the part's `world_bbox` (`VehicleProbe.WorldBox`: the
local bound_box corners through matrix_world), `(max(s), c, s, name)` sorted by `-max(s)` (Python's stable sort keeps the list
order on a tie), a part joining the FIRST cluster whose anchor centre lies within `0.75 * m` of it (Vector.length: float32
products summed in double from the last component) - the armature `VehicleRig` (object and datablock names made unique),
`Root` (0,0,0)-(0,0.25,0) and, under the wave rock, `RootHull` (0,0,0)-(0,0.2,0) beneath it as the body bone; `rock_on` is
`(rock_deg > 0 or pitch > 0) and rock_frames >= 2` with Python's `min(45, max(0, x))` (a NaN amplitude clamps to 45), a blank
pitch reading 2.4, and a frame count of 0 or less taking the clip's (at least 2) - so the Lab's one-frame cycle leaves the rock
off; one `Wheel_%02d` bone per cluster at the anchor's centre, its tail `c + axle * max(0.05, max(s) * 0.25)` in float32
(`axle_axis`: X, Y or Z forced by argv[6].upper(), else the thinnest extent, the first on a tie), parented to the body bone;
leaving edit mode is `RestFromEditBones`, the BONE and PB rows in depth-first order with children in creation order. The
RUDDER, PRESERVE and FLIP sets resolve their names through `find_opt` (a missing one is a WARN) and print their lines (the
preserve dial as `%.0f`). The skinning gives every part the script holds (the scene's meshes with vertices, in scene order)
one vertex group - its cluster's bone, else the body bone - at weight 1, an Armature modifier on VehicleRig and VehicleRig as
parent (local transform and parent inverse kept; the armature is the identity). The join per bone groups the parts by bone, a
rudder, preserve or flip part keyed apart (`__rud__`, `__keep__`, `__flip__` + its name, in that precedence), joins each group
into its first member - `BlenderRigAnim.JoinMeshes`, rig_anim's join factored out of its stage 4 unchanged (the rig_anim drill
equal as before, 134 of 145) - frees the joined objects' names and renames the survivor `Mesh_<key>`, the object's own name freed
first and the new one made unique (a part still holding `Mesh_Wheel_00` gives the wheel group `Mesh_Wheel_00.001`). Measured:
10 of the 24 recipes equal row for row through stage 3 (Ribauldequin, the organ gun with 147 wheel parts in two clusters,
the Jurassic gatling, Bremen, the Teutonic's 21 meshes, the Romanic, the Svea, the Dragon, the Espana, the canoe with its
RootHull), 12 left at stage 3 by name, the 2 FBX by name; 41 of 42 fixture jobs equal, the tread fixture left; 457,526 rows
(FULL). Four fixtures hold the branches no recipe takes: `fx_r_wheels` (two tires thin along Y, an off-centre bolt joining
the first, a cube wheel whose tied extents give X, the tire listed twice, the rock's RootHull, a rudder also listed as preserve,
a missing name in each set, a body part named `Mesh_Wheel_00`, an animated part joined into the hull), `fx_r_axis` (the axle
forced to Z; a one-frame rock cycle), `fx_r_many` (101 clusters, the last `Wheel_100`; a 0.1-wide wheel on the 0.05 length
floor; a hundred tied sizes), `fx_r_missing_wheel` (find()'s exit in stage 3, `EXIT:3:`; the exit fixtures now name their
stage in a table). Eleven plants, one per rule, each caught on its own job with a row difference: the axle tie, the RootHull,
the one-frame cycle, the rudder/preserve precedence, the length floor, the stable sort, the 0.75 threshold, the name
collision, the WARN lines, the exit, and a wheel bone's head moved by 1e-6 (caught on Ribauldequin). The first plant run
reported all eleven caught when every job had died of a missing Newtonsoft.Json beside the plant's exe; the harness now
refuses an exception as a verdict. `Tests/VehicleRigStage3Tests.cs` holds the axle on ties and Python's repr of a part name
(quotes, backslash, control characters; the missing-wheel fixture also names a part with a quote). Not held by a committed fixture: a mesh object without
vertices (left in the scene unskinned by `mesh_objects()`; a glTF primitive cannot carry none), the blank argv[21] (the Lab
always writes the pitch), the joined objects' freed names being reused later in stage 3.
A source-reading review of part d1 before its PR found four defects; each is now fixed, fixtured and planted. (1) The join
drops nothing: Blender's `join_materials` adds the EMPTY slot for a member without material slots (its faces take it) and drops
the set again only when the active has none and nothing else joined - vehicle_rig keeps material-less meshes, rig_anim drops
them, so only the vehicle path meets it (`fx_r_slotless`: a slot-less active, a slot-less second member, two slot-less parts).
(2) find()'s exit prints the missing name with a plain `'%s'` (the part list is repr'd): `fx_r_missing_wheel` misses
"Ghost's wheel" beside a part named Bob's. (3) `Object.bound_box` is the bounds of the LAST EVALUATION (`bounds_eval`), not of
the mesh: `bm.to_mesh` only tags, every `bpy.ops` call updates the view layer before it runs (`bpy_operator_function.cc`), so a
part reduced by stage 2's last active tier with no decimate after it reaches stage 3 with its pre-reduction box - measured
(`fx_r_stale`: a wheel whose weld drops its extreme corner; Blender's Wheel_00 length 0.25000125 is the stale extent's, the
current mesh gives 0.25). The model keeps each part's pre-change box from its tier's to_mesh until the next evaluation (the next
active tier's `mode_set`, the decimate's `evaluated_depsgraph_get`, stage 3's `mode_set`); stage 2's own `o.dimensions` reads it
too; six jobs reach stage 3 with a stale box, one wheel is measured on it. (4) Nodes in no glTF scene go to the importer's
"Orphan Nodes" collection, listed after the scene's own objects - and that collection is NOT in the view layer when the script
runs (measured, the reviewer's reading was the scene order only): `_apply_transforms`' `select_set` raises inside `_guard` (the
crash line, exit 1: `fx_r_orphan`, and `fx_r_noscene` whose one scene lists no node), and a source whose ONE mesh is such a
node dies unguarded at the split's `view_layer.objects.active =` (`fx_r_noscene1`: "RuntimeError: Error: ViewLayer
'ViewLayer' does not contain object 'Hull'" - Blender's operator error ends in a newline, which the dump harnesses now write as
a backslash and an n, keeping the DIES row one row under its seal). The stage model orders scene objects by collection
(`SceneObject.Collection`) at the import, the second model, the split and the armature's link. Also: static tracks rig as plain
body parts (`tracks_static` empties `track_names`; `fx_b_merge2` is now equal through stage 3), and `max(0.05, nan)` is 0.05.
Six plants, one per fix, each caught on its fixture - the stale-box plant first passed: the fixture's twin was the vertex the
weld KEEPS, so the mesh still reached the extreme; the corner and the twin swapped, it bites. Measured after: 10 of 24
recipes and 47 of 47 fixture jobs equal through stage 3 (457,633 rows), 12 recipes left at stage 3 by name, the 2 FBX by name;
1,776 tests.

Review of PR #178 (2026-10-16): `find()`'s missing-part list differed for valid names containing non-breaking spaces or
invisible format characters: Python repr escapes them, while the port wrote them literally. The missing-wheel fixture now
contains both and failed before the fix. `PyStrRepr` uses the pinned Blender runtime's Unicode 15.1 printability ranges;
.NET Framework and Mono carry older Unicode data, so their category tables would also misclassify newer printable names.
Eight additional examples and an independent checksum of repr over all 1,112,064 Unicode scalars hold the escaping,
quote choice and supplementary characters (Blender 5.1.2 / Python 3.13.9). Unit suite: 1,785 passing.
The gate previously tested damaged rows only through stages 1 and 2; a sealed stage-three bone edit passed that comparison.
It now requires the intact `fx_r_wheels` seed to be fully compared with no named decline, then rejects 15 independently
sealed stage-three changes: added/changed logs, frame, object matrix/parent, mesh positions/loops/UV/colours, weights,
wheel-bone length/rest/parent, pose location and action range. Each must fail in the stage-three comparison, independently
of the parser and checksum. All 15 fail for the intended reason; the intact control passes. FULL=1 VEHICLERIG_KEYS=1 positive comparison: 57 of 71 jobs equal through stage 3, 457,633 rows (all 47 fixtures and 10 of
24 saved recipes; 12 later rig branches and two FBX sources left by name). Three independent temporary native jobs also
match for freed object-name reuse, blank pitch's default RootHull, and NaN pitch with a one-frame cycle. Those branches
still lack committed native fixtures.

**Step (d), stage 3 of item (5), part d2: the sail, rigging on the sail, the flag (2026-10-16).** Against vehicle_rig.py
1895-2023 and the skinning loop's fold bands: the sail parts (`find_opt`: a missing name is a WARN) go on ONE `Sail` bone
whose head sits at the sails' box centre in x and y and at the KEEL LINE less 0.05 in z - the keel the lowest z of every
mesh's eight world `bound_box` corners (`matrix_world @ Vector(c)`: the float32 products summed in double, rounded to float),
the box from the same corners, `0.5 * (a + b)` and `keel - 0.05` in double before `Vector((...))` rounds them - and whose tail
rises by `max(0.3, 0.25 * height)` (a float32 add). Under argv[71] == "1" (`sail_fold`) three bones chain below it,
`SailF1..F3`, their heads at the box centre on the fold cuts `top - h * q / 4.0` (h = `max(1e-6, height)`), each tail
`max(0.1, 0.25 * h / 4.0)` down; each sail vertex then skins to the bone owning its height band (its world z against the cuts,
`<`), a group per non-empty band in band order, and the join unites the bands by name in object order. A sail part also listed
as a wheel keeps its wheel cluster's bone but skins to the Sail. With a sail found, the rigging parts ride the Sail bone
(`find_opt`, a missing name skipped silently) as ONE mesh of their own (`__rigsail__`, never `Mesh_Sail`, which the export
doubles); without one, rigging stays on the body. The flag parts go on one `Flag` bone - struck: the head at the keel line
less 0.05; under `flagfold=`, hinged at the flags' top - its tail up by `max(0.3, 0.25 * height)`; `flagfold=` reads
`max(-175, min(175, float(deg)))` and `max(1, int(float(frames)))` (blank: 0 and 12). The lines keep the script's order:
SAIL, RIGGING, then RUDDER, PRESERVE and FLIP, then FLAG; the fold bands print per sail part in the skinning. Measured: 14 of
the 24 recipes equal row for row through stage 3 (the 1861 frigate, Salegs and the Great Eastern with a sail, the BGM-71 with a
struck flag - all equal on the first run), 8 left at stage 3 by name, the 2 FBX by name; `fx_r_sail` (an upright canvas grid
and a topsail falling into all four bands, the topsail also a wheel, rigging on the sail, a missing sail and a missing rigging
name, a struck flag, a wheel) and `fx_r_flagfold` (a flag folding -30 degrees over 8 frames, a missing flag name, rigging
without a sail) equal on the first run; 63 of 73 jobs equal (FULL, 457,785 rows); 1,785 tests. Nine plants, one per rule,
each caught on its job (the keel's offset and its set of meshes, the fold cuts, the band direction, the rigging's own mesh, the
sail winning over a wheel, the flag's hinge, the 0.3 rise floor, the sail WARN). No recipe takes the fold, rigging on a sail
or the flag fold (Triconter folds and the Khelandion rigs, both left for their oars).
The source-reading review of d2 found no geometry or skinning divergence, but argument parsing that was not Python's: (1)
float() is not .NET's TryParse - Python first turns a non-ASCII space into ' ' and a Unicode decimal digit into its ASCII
digit, strips only the ASCII whitespace (so "\x1c3\x1f", which str.strip() reduces to "3", is NOT a number), takes one sign,
then inf/infinity/nan or a decimal whose underscores stand only between digits ("1_0" is 10.0; "1__0", "1_", "--inf", "+-nan"
raise); int() is the same with digits only; str.strip() is str.isspace() (.NET's whitespace plus \x1c-\x1f, less U+180E).
`VehicleProbe.Input.PyFloat`, `PyNumberAscii`, `PyStrip` and `TryPyInt` now follow it, every case measured against Blender's
Python 3.13.9 (`Tests/VehicleRigPythonArgsTests.cs`, 53 cases; the vehicle probe drill holds the shared parser). (2) The
sail-fold and oar dials (argv 72, 73, 75 and 56-59) are parsed unconditionally between the reduce tags and the dials, so a value
Python cannot parse kills the script there - `Args.FoldDie`, fixtures `fx_d_sailfold_frames`, `fx_d_sailfold_inf`
(OverflowError) and `fx_d_oar_lift` ("1__0"). (3) flagfold= frame counts keep Python's exact int (3e9 prints 3000000000).
(4) `fx_r_sail`'s canvas lay FLAT: glTF is Y-up, and the rotation meant to stand it up laid it down, so the sail's rise and the
fold drop sat on their 0.3 and 0.1 floors; it now stands 4 tall (rise 1.15, drop 0.2875), with a jib that is also a rudder
(banded, keyed apart), a burgee that is sail and flag (banded inside Mesh_Flag), a halyard that is rigging and flag, and an
animated sail part; `fx_r_flagfold`'s flag stands 3 tall (rise 0.75). Plants: the underscore rule (three tests), the fold
dials' death and the sail rise pinned to its floor (the drill). After: 66 of 76 jobs equal through stage 3 (457,837 rows);
1,838 tests.

Critical review of PR #180 (2026-10-16): malformed turretreduce tags exposed error text that did not match Python's
repr(): an apostrophe must select double quotes when possible, and non-printable Unicode spaces and control characters
must be escaped. The C# reduce-tag parser now uses the existing Python repr implementation for these diagnostics,
including the shared Gun and Window paths. Five new unit cases fail before the fix and pass after it; the full script
also reproduces the quote and non-breaking-space mismatches (`fx_x_turret_quote`, `fx_x_turret_space`). Additional native
fixtures exercise Wheel -> Turret -> Gun order, the 95% clamp with two material regions, and exactly 0.5% skipping the
whole turret tier, including its weld. 1,862 unit tests pass. The Turret bone remains the separately deferred stage-three
part d3; reduction is compared before that named decline.
The FULL native run with key rows covers all 24 recipes and 62 fixtures: 72 of 86 jobs equal through stage 3
(454,529 rows), 14 explicitly left by name (12 later stage-three branches, two FBX). All three turret geometry
fixtures equal at stages 1 and 2 before their named stage-three decline.

Critical review of PR #179 (2026-10-16): the shared float parser still used Framework's decimal conversion and Unicode
tables. Against Blender 5.1.2's Python 3.13.9, 3,080 independent decimal/special/Unicode cases found 485 differences:
414 overflow exceptions, 13 rejected digit blocks, and 58 different bit patterns (including signed NaNs). The parser now
rounds an exact decimal rational once to binary64, ties to even, returns infinity on overflow, preserves signed zero and
NaN, and uses Python's Unicode 15.1 decimal blocks. Standalone probe/prep/decimate compilers reference System.Numerics.
Permanent tests pin 1,200 generated decimal results and the numeric character transform for all 0x110000 codepoints to
native Python SHA256 checksums. `fx_r_numeric_pct` proves the geometry consequence: a decimal just above the rounding
boundary above 0.5 must run the BODY weld (24 vertices to 8); Framework rounded it to 0.5 and skipped the tier.
`fx_d_sailfold_overflow` proves 1e309 becomes infinity before int() raises the script's OverflowError, and
`fx_d_unicode_fold` accepts a Kawi digit before the next dial raises ValueError. All three fail against the original PR.
`fx_r_flat_sail` also covers the zero-height sail's 1e-6 clamp, an entirely missing rigging list, and flagfold=| defaults.
Argument variants can remain positive after the fixture editor runs; its Unicode output is explicitly UTF-8.
The corrected parser matches all 3,080 independent cases; 1,854 unit tests pass. The FULL native run with key rows
compares 70 of 80 jobs through stage 3 (454,499 rows): all 56 fixtures and 14 saved recipes equal, eight recipes left
by name for later stage-three branches and two for the FBX importer.

**Step (d), stage 3 of item (5), part d3: the turret, the gun assembly, the trails (2026-10-16).** Against vehicle_rig.py
1657-1850. `_combined_bbox`: each part's world box, `min(c - s/2)` and `max(c + s/2)` over Python floats, the Vectors'
`(mn + mx) / 2.0` and `mx - mn` in float32 - one `Turret` bone at the turret parts' centre, its tail up by `max(0.05, extent *
0.25)` (`find()`: a missing name is the stage 3 exit). The gun assembly is the guns, then the muzzles not already listed (the
TUBE), then the cradles (one `Gun` bone for all of it, on the Turret when there is one). With four or more tube vertices (their
world points by `Matrix @ Vector`, per part in list order and per vertex in index order) the longest extent's axis (the first
on a tie) gives the two ends - `max`/`min` keep the FIRST point of a tie - and the end nearer the body bone's head is the BREECH;
a marked brake pins the muzzle at its point farthest from the breech; the head sits at `breech + (muzzle - breech) * gun_pivot`
in float32, and the script prints the pivot, the gun-local fire origin and, over the WHOLE assembly, the GUNSPAN line (the
breech and muzzle bone-local, and the extent `max - min` of the points' `dot` with the span's normalized direction, in double).
mathutils' rules, from the source: `length` and `dot` sum float32 products in double from the last component; `normalized()`
sums the SQUARES in double (`len_squared_vn`) and multiplies by `1.0f / float(sqrt)`; `cross()` is float32. Under `recoil_dist >
0` with a tube: the bore direction, the slide `|bore| * recoil_dist`, the arc radius `slide / tan(radians(3.0))` (the tangent as
Blender's Python computes it, 0.05240777928304121), the arc axis `bore x Z` (`bore x Y` when that is shorter than 1e-4, a vertical
bore), the pivot `tube centre + normalize(axis x bore) * R`, a `RecoilArm` bone there on the Gun and a `Barrel` bone at the tube's
centre on it carrying the tube and the brake (the cradle stays on Gun). The trail bones: per trail name (`find()`), the arm's
points, fewer than 4 skipped, its longest axis, the end nearer the body bone's head (`<=`) the HINGE, a zero-length arm
skipped, `Trail_%02d` by the name's index (skipped names keep theirs), the tail `max(len * 0.5, 0.05)` down the arm. Measured: 17
of the 24 recipes equal row for row through stage 3 - M114 (recoil, a cradle, two trails), the Wespe (a gun, pivot 0.36) and the
Jagdpanzer (its static tracks rig as body parts since d1's review) new, equal on the first run -; `fx_r_turret` (two turret
parts, a gun on the turret with a brake capping the tube's far end and recoil, a trail arm, a three-vertex trail and a trail of
four coincident vertices, neither boned), `fx_r_cradle_only` (a cradle and no tube: the span from the cradle, recoil on but no
split), `fx_r_vertical` (an upright tube: the arc axis from the Y fallback), `fx_r_missing_turret` / `fx_r_missing_trail` (the
stage 3 exits) equal on the first run. `fx_r_turret`'s first tube put its offset through the node scale, so the brake sat near
the breech, not at the far end its comment claimed - every rule still ran equal, but the fixture now has the geometry its
comment names. The same pass found thirteen more dials Python reads among the names (`float()` of argv 29, 30, 32, 34, 35, 38,
43, 44, 47, `int(float())` of 33, 39, 40, 45): a bad one now dies where Python reads it, in argv order with the names files and
the reduce dials (`fx_d_trail_frames`: int(float("nan")), `fx_d_gun_pivot`: float("x")). Ten plants, one per rule, each caught on
its job (the breech end, the brake's pin, the brake on the tube, the pivot, the gun on the turret, the recoil radius, the vertical
bore's axis fallback, the trail hinge, the trail's four-vertex minimum, the dial death). 63 of 63 fixture jobs and 80
of 87 jobs equal (FULL, 454,850 rows); 1,854 tests.
The source-reading review of d3 found no divergence in its arithmetic, branches, lines, bones or overrides (both breech ends,
both hinge ends, the marked tip, the vertical fallback, recoil with a cradle and the exits all traced), but the NAMES FILES were
not read as Python reads them: `open(path, encoding="utf-8")` iterated by line - universal newlines, a BOM kept as U+FEFF on the
first name (it is neither a space nor a newline), each line `str.strip()`ped (which takes \x1c-\x1f, .NET's Trim() does not) - and
open()'s errors: an empty path ("@" alone) is `FileNotFoundError: ... ''`, a directory `PermissionError: [Errno 13] Permission
denied`, where the C# crashed with ArgumentException / UnauthorizedAccessException (both measured in Blender's Python). A file
that is not UTF-8 dies of a UnicodeDecodeError whose text names the byte's offset in the reader's chunk: not modelled, the job is
left to Blender by name (`Args.Unsupported`, only when nothing earlier in argv died). Fixtures: `fx_r_names_ws` (a line wrapped
in \x1c..\x1f finds its wheel), `fx_r_names_bom` (the BOM stays: the wheel is not found, the exit), `fx_d_names_empty`,
`fx_d_names_dir`, `fx_d_dial_before_names` (a dial dying before a later names file). The review's unexercised d3 branches got
fixtures too: `fx_r_turret` now carries the rock (RootHull under the turret, gun and trail), a turret part also a wheel, the brake
also listed as a gun and a mantlet as gun and cradle (the de-duplication), and its three-vertex trail first (the arm is
Trail_01); `fx_r_spike` (a three-vertex brake and no gun under recoil: fewer than 4 tube points, the defaults);
`fx_r_gunpoint` (a tube of four coincident vertices: no GUNSPAN, a zero bore - and stage 4 then DIES dividing by it,
`DIES:4:`); `fx_r_pivot_end` (the pivot at the muzzle under recoil); `fx_r_turret`'s Ring now rings the dome. Two script
defects found on the way, reproduced bit for bit and recorded in the backlog. Plants: the old Trim(), the BOM stripped, the
directory's PermissionError (the drill fails on each target). After: 71 of 71 fixture jobs and 88 of 95 jobs equal
through stage 3 (FULL, 454,970 rows); 1,854 tests.

Critical review of PR #181 (2026-10-17): the strict names reader's exception handling covered positional arguments but
not names inside defaultreduce/shroudreduce/windowreduce tags. Directories and non-UTF-8 files could escape as CLR
exceptions, even when Python had already stopped at an earlier dial or tag. Both paths now use the same error
classification; reduce tags print str(exception) as the script does, unsupported decoding leaves a named fallback,
and later tagged files are not opened after an earlier error. Six unit cases and seven native fixtures reproduce
the original failures, including the first-error precedence. The native cases also exposed that VehicleRigDrill
rejected Args.Unsupported itself: it now accepts that exact parser-produced reason, while retaining rejection of
unexpected stage-one declines. Positional UnicodeDecodeError and a tag's caught decode error are both exercised.
All 1,860 unit tests pass. An independent 64-scene sweep of rotated guns and trails, varied sizes and pivots,
turrets, cradles, recoil, and wave rock matches Blender through stage 3.
The FULL recipe/fixture run with key rows compares 93 of 102 jobs through stage 3 (454,974 rows): 17 saved recipes
and 76 fixtures equal, five recipes left for later stage-three branches, two FBX sources, and two names-file
decoding cases explicitly left to Blender. The new directory and error-precedence fixtures compare exactly.

**Step (d), stage 3 of item (5), part d4: the rotors (2026-10-17).** Against vehicle_rig.py 1545-1608 and the cluster bone loop.
Each non-empty group (the main rotors, then the tail rotors) is ONE cluster appended after the wheel clusters, its bone named on
from theirs (`Wheel_%02d`). The pivot: per name `find()` (a missing one is the stage 3 exit), the parts' world boxes, their
centres summed in float32 from zero and times `1.0f / len` (mathutils' Vector division). MAIN: the hub is the part whose centre
lies nearest that mean (`min` keeps the first of a tie), the pivot its box centre, the axle its vertices' least-variance direction:
`np.cov` of the points through `matrix_world` minus their mean, `np.linalg.eigh`, the first eigenvector's components as float32,
`normalized()`. Blender's numpy 2.3.4 has no external BLAS: `cov` is a sequential mean, a sequential `average`, `X -= avg`, a
sequential `dot` and `* (1 / (n - 1))`; `eigh` is lapack_lite's DSYEVD('V', 'L'), which for a 3x3 runs DSYTD2, DSTEQR (implicit
QL/QR, its selection sort) and DORM2R - ported line for line in `editor/BlenderNumpy.cs` (f2c LAPACK 3.2.2, the old DNRM2's
scale/ssq) and matched bit for bit, covariance, eigenvalues, eigenvectors and axle, on 3,000 Blender-numpy clouds of ten shapes
(random boxes, flat and banded discs, the same turned, blade strips, near lines, axis boxes, centimetre models, tiny clouds)
under .NET and Unity's Mono; ten of them pin it in `Tests/BlenderNumpyTests.cs`. Branches it does not port (DSYEVD's, DLARFG's
or DLARTG's rescaling of a tiny or huge entry, DSTEQR's non-convergence, a covariance of fewer than two points or a non-finite
one) leave the job to Blender by name; no recipe or fixture reaches them. TAIL: the pivot is the mean, the axle 90 degrees about
the vertical from the boom - `(0, 0, 1) x (model centre - mean)` flattened to horizontal, the model centre the mean of every mesh's
box centre - or (0, 1, 0) when that is shorter than 1e-6. `axis_arg` (argv[6], the wheels' override) forces the main axle,
`tail_axis_arg` (argv[28].upper() when not blank) the tail's; a forced main axle skips the eigh (the script computes it and
discards it, catching any failure). The tail trim (argv[29], [30]) when either exceeds 1e-6: `Vector.rotate(Quaternion((0, 0, 1),
radians(yaw)))` - `angle_wrap_rad` in float32, `normalize_qt_qt`, `quat_to_mat3`, `mul_m3_v3` -, then the pitch about `(0, 0, 1) x
axle` when that is longer than 1e-6, then `normalize()`. The bones: the main rotor's tail along `ax * -1.0 * max(0.05, extent *
0.25)`; the tail fan's along the in-disc direction nearest up (`(0, 0, 1) - ax * ax.z`, or `(0, 1, 0) - ax * ax.y` below 1e-3,
normalized) with `align_roll(ax x that)` - `ED_armature_ebone_roll_to_vector`, the importer's port. Two VEHICLE lines per group,
the second with the axle as Python's `repr` of a tuple. Measured: 20 of the 24 recipes equal row for row through stage 3 - Bell
(the main axle forced Z), RAH-66 (the hub's eigh axle, an eight-part tail) and the Ilya Muromets (a tail rotor, a preserve part)
new, equal on the first run. Fixtures for the branches no recipe takes: `fx_r_rotor` (a tilted 16-sided mast hub with an action
- transform_apply bakes the action's first key and leaves the world at the identity -, two blades, the hub also a wheel and a
blade also a turret part, a three-part tail trimmed yaw 12.5 / pitch -7.25, the wave rock), `fx_r_rotor_forced` (the main forced
X beside a wheel, the tail forced Z: the in-disc fallback to +Y, the yaw leaving Z as it is and no pitch axis),
`fx_r_rotor_centred` (two unit-cube blades tied on their distance - the first is the hub, three equal eigenvalues - and a tail
above the model's centre: the (0, 1, 0) axle), `fx_r_missing_rotor` (the stage 3 exit) and the argument variant
`fx_r_tail_args` (the tail axis "x", the trim yaw "-inf" and pitch "nan": a NaN axle that `normalize()` zeroes, `align_roll` on a
zero vector) - all equal on the first run. Thirty plants: 27 caught on their jobs; `matrix_world @ v.co` replaced by the
bare vertex is equivalent (every mesh's world is the identity after transform_apply, the animated mast's too); dropping
`X -= avg` and dividing by n instead of n - 1 move only bits the axle's float32 hides, and the unit test catches both, with a
descending eigen sort and a dropped DLARTG sign fix. 87 of 89 fixture jobs and 107 of 113 jobs equal through stage 3 (FULL,
455,358 rows; left by name: the two oar recipes, the two FBX sources, the two non-UTF-8 names files); 1,885 tests. Next: (d) part d5,
the oars.
The source-reading review of d4 traced every operation against the script and Blender's C sources and found no divergence (the
plain wheels' path byte-identical, the forced main axle's skipped eigh equivalent). Its gaps: the hub chosen after a part listed
before it was reached only by RAH-66 - `fx_r_rotor` now lists a blade first -; no tail-only rig - `fx_r_tail_only` -; and the main
hub of fewer than two vertices was declined although the script handles it: Blender's numpy (measured) gives an all-NaN covariance
for one point, `eigh` raises LinAlgError (DSTEQR never converges) and the script's `except` takes the axis of least spread of the
parts' centres - `sum((c[i] - mean[i]) ** 2)` in Python floats (`**` is the C runtime's `pow`, now `BlenderTrig.Pow`), `min` over
range(3) keeping the first of a tie - with axle_src "least-spread (numpy missing)". Ported, with `fx_r_rotor_point` (a POINTS
primitive of one vertex as the hub, listed second, strictly nearest the mean) equal; two plants on it caught (the most spread
instead of the least, the first listed part as the hub). A unit test pins Eigh3 on a zero matrix (the identity, as numpy returns
it) and its decline on NaN; the scaling guards are documented unreachable from float32 coordinates. After: 89 of 91 fixture jobs
and 109 of 115 jobs equal through stage 3 (FULL, 455,410 rows); 1,886 tests.

**Step (d), stage 3 of item (5), part d5: the oars (2026-10-17).** Against vehicle_rig.py 2033-2230 (the bones), 2244-2256 (the
skinning) and 2877 (the join key). The oar parts (find_opt: a missing one is a WARN; a name listed twice is two entries, its islands
twice) are split into connected islands as `_oar_islands` walks them: `bmesh.from_mesh` (the edges created in mesh order, each
appended to both vertices' disk cycles), then per vertex in index order a depth-first walk - a stack popped from its end, each
vertex's `link_edges` in disk order from v->e -, the island's points `matrix_world @ co` and their float32 mean. The recovery
tolerances are fractions of the oar points' diagonal (`math.sqrt(sum(... ** 2))`: UCRT `pow`, a compensated sum): merge eps
0.00786, min span 0.01387, the beam centre the y midpoint. Per bank (+1 first; an island's side by its centre's y against the
centre, `>=`): the pole direction is the span-weighted sum of each long-enough island's PC1 (`_oar_pc1`: the float32 mean, the
scatter in Python floats, 40 rounds of the power iteration from (1, 0.3, 0.1) - each row a compensated sum into a float32 Vector,
a zero length stops it, so a point of no scatter returns (1, 0.3, 0.1) unnormalized), each signed to the first; a bank whose sum is
shorter than 1e-6 is skipped (its islands' vertices end in no group). The rake line uses `math.atan2` (UCRT) and `degrees`. The
islands' centres are projected on the plane across the direction (`e1 = dp x (0, 0, 1)` or `x (1, 0, 0)` past |z| 0.9) and joined
single-linkage: a grid of `int(u / eps)` cells (toward zero), the 3x3 neighbourhood, `j > i` within eps by `math.hypot` - CPython
3.13's own `vector_norm` (the larger's exponent scaled out, exact squares, Neumaier sums, one differential correction), not
`sqrt(a * a + b * b)` -, union-find with path halving (`parent[ra] = rb`), the clusters in the order of their first member. Per
cluster an `Oar_%03d` bone: every vertex of its islands skinned to it (oar_skin's insertion order makes the group order); under
`abs(roll) > 0.01` the whole oar turned about its outboard PC1 by the roll signed per bank (`Quaternion(axis, angle)` and `mul_qt_v3`)
and written back through `matrix_world.inverted()` (the adjugate) - the island lists are shared, so a part listed twice turns twice -;
the oarlock at the dialled beam distance on the oar's outboard axis (or, for an oar parallel to the hull, the vertex nearest it);
under a length off 100 the oar stretched along its axis about the oarlock; the tail `0.6` down the axis. Then the pivots line
(its mean a compensated sum) and the recovered line. CPython 3.12+'s `sum()` of floats is NOT a sequential sum: from the int start
0 the first float is taken exactly and every later one by Neumaier's compensated addition, the compensation added once at the end
(`[1.0, 1e-16, 1e-16]` gives 1.0000000000000002); `BlenderVehicleRig.PySum` ports it, and d4's least-spread rotor fallback uses it
too. Measured: 22 of the 24 recipes equal row for row through stage 3 - the Khelandion (three parts, 96 oars, roll 90, length 120)
and the Triconter (41 parts, 32 oars, roll 35, pivot 0) new, equal on the first run. Fixtures: `fx_r_oars` (two parts, raked oars
of pole, blade and oarlock ring - three islands each - and one raked the other way, a missing name, a part also a wheel),
`fx_r_oars_roll` (one part on both banks listed twice, roll 25, pivot 60, length 150; its node 1e-7 off the identity, which stage 1
still turns into an identity world), `fx_r_oars_odd` (exactly vertical oars - dyadic sizes make the float32 mean and so the PC1
exact -, the other bank only rings: skipped, its vertices in no group; the pivot and length dials clamped), `fx_r_oars_ghost` (no
oar found), `fx_r_oars_split` (shafts and blades in two parts - every cluster spans both -, a one-vertex island, ring stubs placed
in eps units against the grid: one in the next cell but farther than eps, one merged across the following cell boundary),
`fx_r_oars_args` ("nan" roll and pivot, "inf" length) and `fx_r_oars_inf` (an infinite roll: NaN geometry, Blender dies in stage
4 - DIES:4: -, left at stage 3 by name because which NaN, its sign bit, follows each compiler's operand order). Thirty-four plants:
32 caught on their jobs (the infinite roll's decline among them); the union direction is equivalent (the clusters come out in the
order of their first member either way); a sequential sum is below the drill (the unit test catches it: its 20 random cases are lists
where the two differ). `Tests/VehicleRigOarTests.cs` pins `math.hypot` (24 random pairs where it differs from the naive root, plus
zeros, subnormals, a huge pair, infinity beside NaN) and `sum()` (20 random lists, negative zeros, infinities, an overflow, NaN), as
Blender's Python computes them. 117 of 122 jobs equal through stage 3 (FULL, 456,386 rows; 1,945 tests; left by name: the infinite roll, the two FBX sources,
the two non-UTF-8 names files). Next: (d) part d6, the tread rig.

**Step (e), stage 4 of item (5), part e1: the Spin action, the wheels, the rock (2026-10-17).** Against vehicle_rig.py 2905-2978,
3179-3357, 3459-3477 and 3705-3710 (the drill's stage 4 ends before the inside-out fix). `bpy.data.actions.new("Spin")` takes a
name the existing actions leave free (an imported clip named Spin makes ours Spin.001 - and the purge later removes it, unlinking
the armature); the action's slot (OB<armature>) appears with the first key; frame_start 0, frame_end the longest period (the Spin
frames, the rock's when it is on), each subsystem repeating `max(1, int(round(clip / period)))` times. The WAVE ROCK keys the body
bone: the roll axis forced (X/Y) or the meshes' longer horizontal extent (world_bbox over objs, min/max into float32 Vectors), turned
by the heading (`Vector.rotate(Quaternion)`), the pitch axis `(0, 0, 1) x roll`; per sample `Quaternion(roll, A sin(...)) @
Quaternion(pitch, B sin(... + phase))`, `to_euler('XYZ')` (normalize_qt_qt, quat_to_eul's two solutions), keyed at
`int(round(t * clip))` - a sample on a frame already keyed moves that key, value and handles by the delta. The WHEELS: an idler
(static tracks: the rearmost cluster per side, the frontmost the other carrier) turns at the dial x the idler speed, snapped to its
spoke symmetry; a wheel whose cluster bottom sits above the ground band (15 % of the scene meshes' height over their lowest corner)
keeps the dial; any other non-carrier, non-rotor wheel rolls at the dial x (largest / its own size) x the road speed, snapped only at
road speed 1. `_wheel_symmetry` looks `Mesh_<bone>` up by name, takes the bone's armature matrix (Matrix @ Matrix, double-summed),
its local Y as the axle, every max(1, n // 1500)-th vertex across it (CPython's hypot and atan2), the rim beyond 0.45 of the largest
radius, and for n = 2..24 the mean resultant of cos/sin(n theta) - sums COMPENSATED (PySum), UCRT cos/sin -; the smallest n within
0.8 of the best, none below 24 rim vertices or a best under 0.4. A wheel is keyed on rotation_euler (XYZ) at 0 and the clip; a ROTOR
on rotation_quaternion every frame about its local axle (Y, a tail X). Every Spin key is then LINEAR (the auto-clamped handles kept),
the objects that are neither the joined meshes nor the armature removed, the actions not named Spin/Deploy/Recoil/Furl purged with
a line each (their users unlinked), every non-armature object's animation data cleared. `frame_set` evaluates EVERY active action:
an object whose action survived the join ends stage 4 at its last evaluated frame (the C# evaluates the objects after the rock and
at the end - the only states anything reads); the pose ends at each channel's last key (the last frame_set lands on the clip's end,
where every curve has a key). The PB row now carries the rotation mode and the Euler (matrix_basis by eulO_to_mat3). Measured: 15
of the 24 recipes equal through stage 4; the census reaches the idler and ground-wheel snaps, airborne wheels (the Sikorsky's
propellers), the rock with keys replaced, rotors, an animated object, purged clips. Fixtures: `fx_e_rock` (forced X, heading 30,
two roll and three pitch cycles at a 45-degree phase over 24 frames, the Spin repeating twice), `fx_e_speeds` (static tracks with
road wheels between the carriers, road speed 1.5 and idler 0.5 as the Lab never writes them, a preserved animated part and no rock -
a body part joins into the hull and loses its action), `fx_e_rockdefault` (rock frames blank: the Spin's) and `fx_e_long` (200,000
frames: left by name). Twenty-seven plants: 26 caught; a sequential sum is below the drill (the unit tests hold PySum). The gate
compares stage 4 now, with an intact stage 4 control and seven S4_* tampers (a planted and a changed rock line, the current frame,
the armature's action, a wheel's Euler and rotation mode, the action's range). 100 of 126 jobs equal through stage 4 (FULL, 461,052
rows; left by name: 21 for e2/e3, the infinite oar roll at stage 3, the two FBX, the two non-UTF-8 names files); 1,945 tests. Next: (e) part e2.
The source-reading review of e1 found three divergences, all fixed: (1) after the rock's frame_set, `Object.bound_box` is the
EVALUATED mesh's - the meshes skinned to the rig DEFORMED by the posed hull (measured in Blender: a skinned cube's min z -1.0 before,
-1.345 after) - so the ground band now reads the linear deform (each weighted bone's pose_mat x inverse arm_mat, VehiclePoseMats with
the hull's Euler) - `fx_e_ground`: a long hull pitched 3 degrees drops the band below every wheel (all grounded on the undeformed
bounds); (2) frame_set writes each keyed channel's CURVE value, and a property assigned after the last frame_set keeps the assigned
one - the final pose is modelled that way (`fx_e_rock2`: four samples replace the key on the clip's last frame; on every fixture the
replaced key equals the last sample, the rock's angles being float32-quantised by angle_wrap_rad so the delta is exact - the plant
is equivalent there); (3) a non-finite Spin angle, rock heading or phase makes Python raise in round()/sin() - declined by name (the
Lab writes finite values). Also: keys insert in near-constant time with one handle recalculation per curve (a rotor at 100,000
frames had been quadratic), the worlds recompute after every frame_set, a stale evaluated copy is declined, `fx_e_rock3` (no wheels;
a heading under the auto axis), `fx_e_rockdefault` forced about Y. No fixture for a rock past 1,000,000 samples or 100,000 frames:
Blender samples every one (a 200,000-frame rock took 40 minutes in the dump). 103 of 129 jobs equal through stage 4 (FULL, 461,465 rows); 127 kinds of damaged evidence refused; 1,945 tests.

Review of PR #184: the ground band must use the evaluated armature pose from the final `frame_set`, before the final rock sample
is assigned to the original pose. Using that new assignment instead changed grounded/airborne classification and wheel speeds in
29 of 160 independent animation scenes. The port now snapshots the evaluated pose before that assignment and retains the final
sample in the original pose. `fx_e_groundeval` and `fx_e_groundeval_dense` reproduce the defect: a small wheel incorrectly turned
-960 degrees instead of Blender's -360, both with distinct frame keys and with repeated replacements at the final frame. The
no-rock control (`fx_e_groundeval_off`) still rolls at -960 degrees. Three unit cases cover the same behavior and the final pose;
the two rock cases fail before the fix and all pass afterwards (1,948 tests). All 160 independent scenes match Blender after the
fix, including every animation key, on both CLR and Unity's 64-bit Mono.

**Step (e), stage 4, part e2: rowing, Furl and the Spin holds (2026-10-17).** `BlenderVehicleRig.Stage4` now models
vehicle_rig.py 2986-3177: each oar's local vertical/dip axes, the mirrored sweep, lift/rake offsets and per-frame quaternion
keys over the shared clip; a separate Furl action with sail strike or draped three-joint curl (including reverse), a flying
or folding flag, and explicit sail/flag Spin holds. Stage 3 retains the measured dip and hinge axes. Furl's range includes
the longer of its sail and flag endpoints. Both actions preserve Blender's name allocation, slot, curve and key order, auto
handles and final LINEAR interpolation; the existing purge removes colliding `.001` actions as Blender does.

The native fixtures `fx_e2_rowing` and `fx_e2_rowing_still` distinguish key insertion from evaluation: rowing never calls
frame_set, so the scene can remain at frame 1 despite keys through frame 24. With the rock's shared 60-frame clip, a 24-frame
oar period repeats twice (round(2.5) is even). `fx_e2_stances` combines a reversed 360-degree curl, sag .98, a Y yard, a folding
flag with a different endpoint, and a wheel that evaluates Spin. `fx_e2_sail_strike`, `fx_e2_sail_curl`, `fx_e2_flag_flying`
and `fx_e2_flag_fold` cover the separate stances and holds; `fx_e2_action_names` collides with both authored names;
`fx_e2_clamps` post-edits blank frames, NaN curl and infinite sag; `fx_e2_missing` has no found sail/flag and creates no Furl.

Furl exposes a signed-zero rule in original-pose write-back: a constant negative-zero Spin channel cannot overwrite the
positive zero explicitly restored after authoring the stance. A nonzero evaluation followed by zero must still write that
zero. The lazy evaluator records actual frame_set calls and replays the intervening values only for that zero ambiguity;
it keeps handle recalculation outside per-frame loops. Unit cases cover wheel evaluation versus no frame_set, both sail/flag
modes, per-frame rowing, Python argument defaults/clamps, and limit checks only when the relevant parts exist. All 1,965
tests pass. Non-finite rowing angles and clips beyond 100,000 frames are left by name; e3 still owns Deploy and Recoil.

FULL=1 VEHICLERIG_KEYS=1: 24 saved recipes and 118 fixture jobs run natively; 21 of 24 recipes and 130 of 142 jobs match
through stage 4 (499,914 rows). Twelve named deferrals: two FBX, two non-UTF-8 names files, the infinite blade roll, six e3
jobs and the long-Spin limit. Another 160 deterministic random e2 scenes vary banks, rake/dip/lift, roll/stretch, clip periods,
curl/sag/reverse, flag folds, wheel/rotor evaluations and source action collisions; every key matches on CLR and Unity's
64-bit Mono. The previous 160 independent e1 scenes also still match. Ten temporary implementation plants (missing sweep
mirror, wrong lift sign, full instead of half sweep, omitted drape, omitted reverse, omitted strike, wrong flag hold,
unallocated action names, reversed quaternion composition and unconditional zero write-back) are all caught by native fixtures.
The drill now requires intact row and Furl controls and nine S4_E2* counterexamples. Changed key values/interpolation repair
both FC hashes and the outer seal, so they must be rejected by the model; together with the existing controls, all 136
damaged-evidence kinds are refused (129 without K rows). The stage-four census reports rowing, Furl and curled sails separately.

**Step (e), stage 4, part e3: Deploy and Recoil (2026-10-18).** Stage 3 retains the trail hinges/spades, gun axis and
bore, and recoil pivot/axis/centre/slide/radius. Deploy uses the source's five-degree direction tests, local world-up axis,
gun-elevation axis and sign, two keys per quaternion channel and LINEAR interpolation. The gun is raised only when Deploy
actually exists (trails and spread greater than .01). Recoil keys rest, optional held lead-in, a 15%-duration kick rounded
half-even, and the return; it holds every deployed trail/gun quaternion at both endpoints. Spin is restored afterwards.

Neither action calls frame_set. Recoil's view-layer update evaluates the active armature action at the existing frame,
without advancing unrelated object animations. The first independent run caught three such object-matrix mismatches;
fx_e3_action_names and two unit cases now distinguish this from a wheel's actual frame_set. Source collisions with Deploy,
Recoil and Spin preserve the original actions and purge the newly suffixed authored actions, including unlinking Spin.
The native fixtures fx_e3_deploy, fx_e3_deploy_only, fx_e3_offaxis, fx_e3_no_spread, fx_e3_negative, fx_e3_no_gun,
fx_e3_long_lead and fx_e3_vertical cover timing, direction warnings, elevation signs, disabled branches and the vertical
fallback. The existing fx_r_gunpoint now compares the script's stage-four ZeroDivisionError; a unit case holds that failure.

Sixteen new unit cases bring the suite to 1,981 passing tests; all 117 editor sources compile. Another 120 deterministic
independent scenes vary trail count, yaw/tilt, pivot, elevation, recoil fraction/timing, source actions, rock, wheels, flags
and oars. Every supported row/key matches Blender on CLR and Unity's 64-bit Mono. Ten temporary implementation plants
(wrong trail direction, missing elevation, floor instead of half-even kick, missing lead hold, missing deployed holds,
reversed kick, non-LINEAR Recoil, omitted armature update, unrelated object animation evaluated, and inclusive spread
threshold) are all caught. The drill requires intact e3 Deploy/warning seeds and ten S4_E3* counterexamples; changed key
values/interpolation repair both FC hashes and the outer seal before comparison. Non-finite Deploy angles and endpoints
past 100,000 frames remain named limits. The script's clearance search names Mesh_RecoilArm rather than Mesh_Barrel;
normal rigs consequently produce no clearance line (recorded as a source defect in Review-Backlog).

FULL=1 VEHICLERIG_KEYS=1: all 25 saved recipes (now including rq4_global_hawk) and 127 fixture jobs run natively;
23 of 25 recipes and 146 of 152 jobs match through stage 4, with 501,773 compared rows. The six named deferrals are two
FBX, two non-UTF-8 names files, infinite blade roll and the long-Spin limit. The drill refuses 146 damaged-evidence kinds
(139 without key rows), including all ten new e3 controls.


**Step (f), stage 5: mesh finishing and GLB export (2026-10-18).** BlenderVehicleRig.Stage5.cs ports the inside-out
island test, XOR marked winding flip and inset duplicate back shells, including oar/preserve/rigging exclusions. BMesh
vertex/edge/face discovery and reversal preserve UVs, colours, raw custom-normal shorts and deform weights. Whole-model
inset bounds use the evaluated pose left by the last actual update; local inset divides by mean absolute world scale.
Welded sharp edges participate in export normals, and BMesh finishing activates the first UV layer while an untouched joined mesh keeps its inactive UV state.

The shared GLB exporter accepts the vehicle's full action ranges without changing the scene range; its rig_anim route keeps
its existing behavior. Orphan pose actions still export the current skeleton, material-less slots and neutral joints follow
the vehicle exporter, and mesh validation updates the final scene after the totals line. Export's action switching and final
frame restoration preserve the original pose, including negative zero reached after a nonzero animation sample. Curves are
converted once for sampling, avoiding quadratic allocation on per-frame rotor/rowing keys.

Both material scopes are combined with texture/image/sampler offsets rebased, including texture indices in extension objects.
Unnamed embedded images keep their import-local Image_N name before collision naming; a Wespe recipe exposed the difference.
Flat brightness and brightness absorbed by a texture tint are exported without mutating source material metadata. Pixel-edited
brightness, JPEG alpha requiring PNG re-encoding, and an auxiliary FBX preview are named deferrals; Blender remains the default
backend; Generate wiring follows this stage.

The eleven fx_f_* fixtures cover role/global doubling, winding XOR, corner layers, evaluated bounds, flat brightness, a Furl
action longer than Spin, merged textures and unnamed images, inactive UV state and sampled signed-zero write-back. Sixteen
unit cases bring the suite to 1,997 passing tests. Eighty independent scenes vary geometry, role mixes, yaw/tilt, wave pose,
sharp welds, layer combinations, action ranges and merged materials. Every supported scene/key/export row and every complete
GLB matches Blender on CLR and Unity's 64-bit Mono. Twelve S5_* corrupted copies change totals, geometry/layers/weights,
primitive/GLB hashes and material/image/texture metadata with repaired evidence seals; intact layer/merge seeds must first
compare fully, so a named decline cannot masquerade as detection.
Eleven independent implementation plants are caught: oar/preserve exclusions, winding XOR, inset size, evaluated bounds,
duplicated fold-chain weights, import-local image names, BMesh UV activation, sampled signed zero, sharp normals and full
action ranges. FULL=1 VEHICLERIG_KEYS=1 runs 25 saved recipes and 138 fixtures; 155 of 163 jobs compare through stage 5
with every scene/key row compared. All 112 supported exports match as complete GLB bytes. Eight named deferrals include the six from e3 plus pixel-edited brightness
and JPEG-alpha re-encoding. All 158 damaged-evidence kinds are rejected (151 in the default run without key rows).

Review of PR #176 (2026-10-15): a full run found the organ gun's newly recorded custom-normal shorts differed after
stage one's loose split. Blender's separate operator decodes the source BMesh normals into a vector layer and
re-encodes that layer on each copied component; copying the original shorts misses that quantization. The model now
uses BMesh's triangle normals, reciprocal-length normalization and fan averaging for that round trip; the surviving
first component keeps its original data. A measured two-triangle regression (a unit test) and the organ gun in the FULL run hold
it - the pre-review build fails the organ gun's custom-normal hash after the split and the fixed one does not (verified by
running both builds on its dump); `fx_b_split_normals` exercises the path but its small panels re-encode to the same shorts, so
it passes either way.
The documented bad-dial/later-missing-file mismatch is fixed too: the first positional read failure wins in the
script's interleaved order, held by a unit test and `fx_d_reduce_before_names`. Both dump parsers now validate the
three added MESH hashes (edges required, sharp edges/custom normals optional); sealed unit counterexamples and four
new negative cases per drill prove malformed hashes cannot pass even without a stage model. Optional-hash model
tampers now turn an absent attribute into a valid alleged hash instead of malformed text, so their rejection tests
the stage comparison independently of the parser. Independent Blender
probes also matched the weld's target maps on 120 near-coincident point sets and its topology/UVs on 60 seamed boxes,
including duplicate surfaces.
The full stage comparison runs all 24 recipes and 33 fixtures with every key recorded: 53 jobs agree on 307,213 rows,
with two FBX sources and two dissolve jobs left by name. The focused organ-gun/fixture drill rejects 102 damaged
evidence cases, including eight stage-two model mutations.

**Step (h), the acceptance row (2026-10-14).** `editor/RigRouteAcceptanceTest.cs`, the Bake Tests row "Does the GLB
rig route bake what the FBX route bakes?" (off by default: two full animated bakes per entry), is the backlog's gate
before the GLB route becomes the editor default. For every animated registry entry with a .glb/.gltf source it bakes
the recipe twice under throwaway names (`BakeSmokeTest.PREFIX`, the delete guard's and the registry filter's), `rigRoute`
"blender" then "csharp", through `ModelFactoryWindow.ConfigFor` and `UniversalBaker.BuildAnimated` as the Bake button
does, and compares what the Factory made in Unity: the Amplitude outputs present and non-empty on both
(`BakeSmokeTest.MissingAssets`); for every role (`RigPreviewPath` resolves either route's rig and clip) the bones by
name, the skinned mesh's triangle count, rest bounds and the SET of vertex positions (the FBX import splits vertices at
seams, the glTF carries them split already: duplicate counts may differ, the position sets may not - a grid lookup
within 1e-3 of the rig's extent, both ways), the root bone's placement and every bone's rest transform, the clip's
length and, at every clip frame (at least 24 Hz) and both imports' curve keys, every bone's world transform (position,
rotation and scale) and the skinned vertices (`SkinnedMeshRenderer.BakeMesh`); and each role's non-empty baked clip
collection and pose stream length (frames x bones). Two tolerances: the REST pose stays strict (1e-3 of the
extent, 0.06 degrees for rotation and matrix axes, 1e-3 relative axis scale); the CLIP accepts 0.6 degrees of rotation,
1.5% of the extent for position and 1% relative scale **in each bone's matched parent space**. For root bones this is world space.
Non-bone wrapper transforms are included in that relative matrix.
Unity's keyframe reduction acts on local curves, so allowed errors accumulate through a bone chain; a fixed world-angle
or world-scale cap incorrectly rejects deep rigs. Rotation and `lossyScale` alone also miss shear: matrix axes remain
part of both comparisons. In clips, the observed allowed quaternion difference can be amplified by a non-uniform
static helper; its stretch bounds the additional matrix-axis and axis-length changes. With no quaternion difference,
the axis-direction check stays at the strict rest margin. Bone-local scale changes, normalized to each rig's rest,
still have a direct 1% cap. See Unity's [rotation compression metric](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/ModelImporter-animationRotationError.html)
and [position compression metric](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/ModelImporter-animationPositionError.html).
The positional and scale margins are this row's acceptance policy; they do not equate Unity's percentage errors to mesh
extent. Skinned vertices retain the 1.5% base allowance, expanded only as needed by the **observed** displacement of
weighted bones' bind-space bounds, plus the strict rest allowance. This covers accumulated accepted pose errors without
letting unweighted sockets widen the mesh tolerance; wrong weights with identical bone motion still fail.
`BakeResult.rigRoute` says which route a bake took ("blender", "csharp",
"csharp-fallback" with the port's reason in `rigRouteNote`): a recipe the port leaves to Blender by name bakes through
the FBX route both times and is a SKIP that says why; a port crash is a FAIL. The in-game check of each model playing
its roles stays the user's; the row green on every entry and that check together flip the default. The comparison
itself is executed by `tools/riganim_unity_import_drill.sh` (licensed Unity and Blender, a temporary project): equal
imports and a real FBX/GLB pair (the same fixture slimmed by rig_anim.py and by rig_anim_glb.py) pass, with Unity's
default FBX compression and without it; a socket rotation of 0.4 degrees passes and one of 2 degrees fails; size,
duration, intermediate motion, subframe keyed excursions and absent/empty role pose streams fail. A cancel keeps the
verdicts made so far. Owed: the row's run.

Review of PR #172 (2026-10-14): native Unity reproduced false failures for three-bone chains with 0.4-degree
local rotation errors or 0.6% local scale errors, and a false pass for opposite socket shear despite equal world
rotation and lossy scale. The parent-space and matrix-axis checks above fix both. Native regressions cover weighted
and unweighted chains, excessive local errors, opposite shear, wrong weights with identical bone motion, and
allowed rotations versus excessive bone scales beneath static parents stretched 2x and 100x. The
real FBX/GLB pair still agrees with the default FBX compression and without it.

Review of PR #171 (2026-10-14): native Unity counterexamples reproduced false passes for a rotating unweighted
socket and motion that agreed at only the first/middle/last frames. The row now checks full world transforms at rest,
every clip frame and source key. Missing or empty pose streams on both routes no longer compare equal (`-1 == -1`);
all baked roles require their own non-empty collection and pose stream. Instantiated roots retain their imported
position, rotation and scale; resetting them to identity previously hid placement errors. The same native drill covers these regressions
and a real Blender FBX/GLB pair. The clip-time tolerance was then widened to Unity's compression allowance (the
user's call, 2026-10-14: the difference is the FBX import's keyframe reduction, not floating point and not the port).

Review of PR #170 (2026-10-14): `tools/riganim_unity_import_drill.sh` runs the real Factory GLB import in an
isolated Unity project. Five fixtures check exact bone names (no picker-only `#index` suffix), 125-byte action names,
action names starting with `_`, independently persisted role subassets, primary/movement previews, translation scaling,
and zero-based clip times. A two-material fixture deliberately reverses the material order and samples the atlas:
the blue submesh stays blue and the red submesh stays red after saving/reimporting the materials. The clip container's
asset filename starts with `_`, so Unity cannot rename it into a playable empty clip; previews select the subasset by
identity instead of name. A playable action beginning with `_` gets a non-reserved Unity clip alias, while its
GLB/sidecar identifier stays intact. The ordinary picker keeps its indexed node paths and Blender track names.

`RigAnimIntegrationRulesTests` checks that forced rebuilds or changed sources/tools retry a cached Blender fallback,
sidecars round-trip full action IDs (tabs/newlines included; old TSV caches remain readable), and the JPEG acceptance
exception ignores only explicitly re-encoded image indices without mutating the compared models. Other image bytes,
names and all scene/animation fields remain compared. `fx_g_jpeg_declared_png` holds a JPEG whose source declares PNG:
the wrapper's GLB still carries the correct JPEG type inferred from its bytes. JPEG wrapper files are checked against
the already oracle-verified export with the source image payload substituted, and complete role/action mappings are
compared. The Unity acceptance summary counts byte equality and image-excepted equality separately.

Review of PR #169 added nine `fx_f_review_*` export fixtures. The four Unicode clip cases hold action ID ordering,
Python lowercase (including dotted I, final sigma and Deseret), and code-point ordering to Blender. The four UV cases
hold a texture naming `UVMap.001` to the **joined** mesh's layers (added by another mesh, already present, absent,
and shared by two original meshes). All eight exports are byte-identical to Blender; the JPEG albedo case compares
structure with its encoded image bytes explicitly unmeasured. Five `S5_X_*` plants reject unknown export row kinds,
export rows before stage 4 or after DONE, stage rows after the export, and a claimed export death with file rows. `GlbRowsCompareTests`
also refuses missing, doubled and short GLB rows when JPEG encoding prevents a byte comparison; its intact control
reports that limitation without claiming byte equality. The drill counts byte-identical files from `EXPORT ...: equal`
rows only, excluding the coverage-summary row about unmeasured image bytes.

Review of PR #168 (2026-10-14): seventeen additional rig-animation fixture jobs hold bone-name allocation (the 63-byte
UTF-8 limit, the exact-limit control, a sequential collision and a dormant role clip), socket names (long ASCII and
Unicode names, a repeated overlong request), Python's Unicode code-point ordering (sibling prefixes and the socket's
parent-first gate, translation and filter diagnostics), and external image identity (dot and parent-directory URI aliases, distinct-file control).
Bone setters and new sockets use Blender's bounded, unique names; the script's dormant-curve patch still uses its
literal requested-name map. Images are shared by resolved file path, as `images.load(check_existing=True)` does.
The shared Python-name formatter also escapes private-use and control code points; six unit cases cover those
spellings and verify that printable Unicode, including newer supplementary characters, remains literal.

Review of PR #167 (2026-10-13): six additional stage 3 fixture jobs hold the bone-cap threshold and the conversion
fold's keepTranslations paths. A stored nominal `0.001f` is slightly above Python's double `0.001` threshold: the
weighted leaf must survive, with weights just below and above as controls. The fold builds its keep-path set from
literal bone names, while the fcurves use escaped RNA paths; ordinary names keep their location curves, names with
quotes or backslashes have them stripped as `rig_anim.py` does. Both the set and the residual check use that same
literal spelling. These are parity fixes in the C# model; the Python script's behavior is held by the oracle.

Review of PR #165 (2026-10-13): nine additional stage 3 fixture jobs cover repeated role assignments (last value,
first insertion position), quoted and backslash bone names (both the raw prefix's exit and the escaped prefix's match),
out-of-range primary and role slices (each returns the named Blender fallback), and long role tags whose synthesized
action names collide after Blender's 255-byte UTF-8 truncation. Against the original model, eight jobs failed and the
primary-frame fallback control passed; after the fixes, seven jobs match Blender row for row through stage 3 (including
the two script exits), and both frame-limit jobs match stages 1 and 2 before returning the named fallback. These jobs
are part of `riganim_fixtures.py` and run in the normal rig_anim drill, bringing its fixture total to 54. The model still
reproduces the script's literal escaped-path bone filtering; this review does not change `rig_anim.py`'s behavior.

Review of PR #148: the sanitizer now prints the number of escaped path keys, as the script does, when quoted bone
names share a prefix (one garbage key can remove curves from two bones). Confirmed with the real sanitizer in Blender.
`ExportDrillTests` run the actual comparator and pin its completion envelope: exactly one matching final `DONE`, one
block per job, no fatal or unknown rows, no contradictory exit/death/file records, a valid written-file row, and an
explicit `EXPORTLEFT:` marker for a fallback even when the script dies. The live export tamper lane also rejects
malformed completion and written-file records, duplicate jobs, fatal rows, and changed animation indices.
Export workers record their own completion timestamps; the duration excludes the decision comparisons and replays
that run before the parent collects their results. The shell regressions pin this timing, preserve failed worker
statuses, and reject missing completion metadata.


*Review before the PR* (an independent agent; 101 generated jobs through the real script over 13 fixtures, 49,732 role
curves and 851,116 keys equal): NO executed defect. It confirmed by execution what I had not fixtured: deploy ends at
fmin and past fmax, the return at 0, 1, 2, 7 and 50, settles of 0.5, 1.5, 2, 0 and -3, three fire segments with steps,
a point segment, a reversed segment, a one-frame window, wheels thin along each axis, rings of exactly 8 and 7
vertices, forced axes in any case, 1 to 2,000 frames, degrees of 0, -0, 1e-3, 720.5, -120.5, substrings among
look-alike names, names with both quote kinds and a backslash in the repr, a pair-merged wheel (its group names no
bone: logged as not found), the contract path with 142 parts and the recoil, the wheels and a leg scale together, the
bone order read through the wheel-error list on five recoil shapes, the rotation-mode conversion through its eul2,
gimbal and identity branches. Taken from its notes: a non-ASCII axis is AUTO (no such string upper-cases to X, Y or
Z), the normalize helper multiplies by the float32 reciprocal as `normalize_vn_vn` does (unobservable here: the rests'
columns are unit), `wheels_negative_turned` holds the conversion through a general quaternion. Noted, not held:
Python's numeric syntax with underscores is left by name as elsewhere; a bone name with a TAB cannot be dumped by the
tab-separated oracle; a CAMERA part cannot be drilled through step 7 (a camera leaves the file at the import, as before).

*Review of PR #145*: the bind's imported custom normals missed the Python setter's component clamp to `[-1, 1]`
([Blender's RNA declaration](https://github.com/blender/blender/blob/v5.1.2/source/blender/makesrna/intern/rna_mesh_api.cc#L329)).
Normals such as `(2, .5, -.25)` were normalized directly instead of clamped first, giving a different direction
and different stored shorts; very large finite normals also differed. The shared `EncodeCustomShorts` now clamps
before normalization, after the smooth/flat decision uses the original normals. Four unit cases hold Blender's
exact shorts, and three permanent bind jobs cover positive, negative and large components. All 85 additional
independent jobs agree after the fix (random hierarchies, shared meshes, mixed primitives, degenerate faces and
normal magnitudes); the shared encoder also serves the probe and prep, checked by the full pre-push gate.

*Review before the PR* (an independent agent; 29 generated jobs through the real script, 383 meshes and 2,557 vertices
bound, every stage-6 row equal): NO executed defect. It confirmed by execution what the port claims and I had not
fixtured: group names cut at 63/64 ASCII bytes and around multi-byte characters (an emoji cut, `€` runs, two parts
equal to byte 63); a sharer culled by the degenerate cull or stripped leaves one user (no copy) while its orphan
datablock keeps its name at 0 users and BLOCKS the copy's number (`Plate.001` present -> the copy is `Plate.002`);
four-way sharing in case-folded, non-ASCII-last object order; two glTF meshes both named `Plate`; nameless meshes;
numbered names at the edge (`Plate.2147483648` copies to `Plate.2147483648.001`, `Plate.2147483647` to `Plate.002`,
`Plate.` to `Plate..001`, `.001` to `.002`, `007` to `007.001`, `Plate.0001` to `Plate.003`); 255-byte shared names
whose copies are the 254- and 253-byte names; the skip threshold on each entry kind (diagonal, rotation, translation,
just under and over 1e-6, under an animated part at the identity, a datablock shared across an identity, a moved and a
near-identity node); custom normals unchanged through shear, mirror, zero and 1e-30 scales; the bind at frame 12, with
the root-motion anchor, with a RecoilArm added, with parts named `StaticRoot`, `DeployArm`, `arm`; points, lines,
strips, fans, unindexed and 8/16/32-bit indexed primitives, a shared POSITION accessor, an unreferenced vertex, mixed
normals per primitive. Notes: `math::transform_point` starts its sum from +0 (`result(0) +=`), so Blender's order is
`(((0 + x c0) + y c1) + z c2) + loc` - it differs from the port's only when all four terms are -0, which needs a -0
world translation that `BKE_object_to_mat4` never gives (fixtured, unreachable); a file with `KHR_mesh_quantization`
required is refused by `GlbReader` before the port starts (Blender imports it) - a reader gap, not this step's.

*Review of PR #138*: the imported pose rows at the bind and last frames (`PB2`, `PB3`) must name every expected
armature/bone exactly once, with all ten held values. A matching row count cannot substitute duplicates for missing
bones, and unknown names cannot skip comparison. `missing_imported_pose.py` keeps a passing single-job control and
plants sixteen mutations: one or all rows missing, a duplicate replacing another bone, an unknown armature or bone,
all identities and values replaced, and a truncated or oversized row, at each frame. Each must fail for its specific
validation reason.

*Review of PR #139*: the snapshot loop uses a wider counter so an `int.MaxValue` endpoint terminates. Requested
frames are clamped to Blender's `scene.frame_set` bounds before evaluation, while snapshot keys retain the requested
frame: a negative extreme can no longer overflow the baked-key index of a clip that starts after frame zero. Six
Blender fixtures exercise both Int32 limits on a late clip, an imported armature and the contract path. The range
parser also refuses trailing NUL characters, which .NET's integer parser accepts and Python's `int()` rejects;
four unit cases cover starts, ends and speed steps.

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

**The Bake Tests goldens, re-blessed** (2026-10-11). A run of the Workshop and Lab rows failed on eleven goldens; the
failures were run down outside Unity (`WorkshopGateTest.Exercise` rebuilt as a console program per commit, `git bisect
run`, headless renders of the fused files old against new) and none came from the deploy port: every Workshop difference
is the underside rule of 2026-09-26 (the HMS Svea crow's nest) and the exposed-hull-panel rule of 2026-10-04, with the
old and new renders pixel-identical; the Lab rows are the Gun section of 2026-09-26 (the pivot, the GUNSPAN line) and the
in-process probe's naming (the Wespe); the Khelandion's dissolve row changed without an attributable commit and is noted.
One standing defect found on the way: the salegs split's "23 split children are not one island" fails on
2026-09-25 too. The eleven goldens were re-blessed (`ENCReload/Tools/{workshop,lab}_golden`, the old copies kept as
`.bak-2026-09-25`); the dragon's preview FBX is stale and the battleship has no preview (both named, not fixed).

**What that re-bless could not say, and now can** (2026-10-12). The eleven files were rewritten by hand to the failing
run's values — a golden had no way to tell a run's capture from an editor's save, and no run followed. Three things
changed:

- **A golden carries its provenance.** Every golden the Workshop and Lab rows capture starts with one header line
  (`BakeGoldenRules.Compose`): `# golden captured=<UTC> editor=<commit> in.source=<hash> [in.recipe=<hash>] sum=<checksum>`.
  The editor commit comes from git's own files (`GitStamp.ShortHead` on the package's resolved path — a plain checkout or a
  worktree); the input hashes are the first 16 hex digits of the SHA-256 of the source bytes (and, for the Lab, of the
  recipe JSON); the checksum is FNV-1a over the golden's own lines. `BakeGoldenRules.Judge` reads the header before any
  line: a checksum that does not match is **EDITED** (a FAIL even when the run now produces the edited value — "restore
  it, or delete it and run again to re-bless"); an input hash that does not match makes a changed result **stale by
  input, not by code**; a changed result against an intact header names the capture stamp, so the suspects are
  `git log <stamp>..HEAD -- editor/GlbDisconnectedParts*.cs`. A golden without a header is **legacy**: compared as before,
  and given its header by the first Unity run that matches it (`BakeGoldenFiles.Judge`) — verified, not re-blessed. The
  checksum detects body edits that leave the header unchanged; it does not authenticate the header or prove that Unity
  wrote it. Legacy goldens have no such check until a matching Unity run adds a header. The Khelandion dissolve row,
  "without an attributable commit" above, had none because the script was NOT deterministic: the first run with headers
  gave a third value (2236 verts dissolved, after 2252 and 2239), and the Wespe's shroud moved with it. The chunked
  `_dissolve_limited` in `vehicle_rig.py` handed each cell's edges and verts to Blender in Python set order — by object
  address, different per process — and a limited dissolve is greedy over that order. Fixed the same day (the lists are
  sorted by element index after an index refresh; `Tests/test_vehicle_rig_math.py` guards the source); six headless runs
  of the helper alone measured 2254 / 2258 / 2249 unsorted and 2261 three times sorted. An earlier sentence here blamed
  the project's local script copy of 2026-09-15; that was wrong. The two Lab goldens are re-blessed once after the
  project's `Tools/vehicle_rig.py` is synced to the package copy.
- **The gate reads the headers** — `tools/check-goldens.sh`, seconds, no source needed: every golden under
  `Tools/{workshop,lab}_golden`, a planted one-character edit must come back EDITED or the step fails itself; SKIP without
  the project.
- **The Workshop row runs outside Unity** — `tools/workshop_golden_drill.sh`: the row's own `WorkshopGateTest.Exercise`,
  compiled from any checkout's `editor/` (`HAF_EDITOR_ROOT=<checkout>`), on every recipe source against the project's
  goldens, writing nothing into the project (candidates to `--out`, a missing golden is NOGOLDEN, a child that is not
  one island is named with `GlbDisconnectedParts.ExplainIslands`). Run at five commits it put every moved count on the
  underside rule (09-26) and the hull-panel rules (10-04), and showed the 64-bit .NET runtime producing Unity's own values
  on all 21 sources — so a FAIL here is a count that moved, not a runtime. A golden is still blessed by the Unity row only.

The salegs row is **fixed**, not re-blessed: the "children not one island" were the Workshop's weld tolerance — a
ten-millionth of each mesh's own diagonal, which for a child part 600 units from the origin is 15–28x finer than a
float32 ulp there, so one-ulp noise from an earlier fuse read as a gap; and a same-grid-cell test, which split two points
a tenth of a cell apart whenever a cell boundary fell between them. `ExplainIslands` printed the numbers (every gap
0.1–1.1 ulp); the tolerance now uses each axis's ulp plus a cross-axis noise allowance capped at a hundred-thousandth
of the mesh diagonal, then tests distance across neighbouring cells (`GlbDisconnectedPartsTests`: a shared edge one ulp
apart at x = 600 is one island; a 0.02-unit Y gap at x = 100,000,000 remains two islands). The 20 other Workshop goldens
did not move; the Espana battleship reads 1,838
source islands for 1,843 (five
noise-split pieces joined) and is the one golden to re-bless in Unity (delete it, run twice). The dragon's preview FBX
stays as named above.

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

**Its blind spots are its real failure mode, and it has had four (2026-08-22, 08-23, 08-23, 10-11).** This guard is a set of
regexes over source, so a call shape it does not match is not reported as unchecked — it is silently *not counted*, and
the pass line still says "all N catalogued" with a smaller N. Each time, the gate went green while the thing it exists
to catch sat in plain sight:

| found | shape it could not see | scale |
|---|---|---|
| 08-22 | `GetMember(GetMember(x, "Inner"), "Outer")` — pass 1 stopped at the first `)` | `TagAsAbilities`, read that way and only that way |
| 08-23 | the `CachedField` / `GFA` accessor family, newly added | 16 sites |
| 08-23 | `AccessTools.Field(x.GetType(), "name")` — pass 1's accessor list stopped at HAF's own helpers, and pass 2 gives up at the first `)` | **146 sites, 70 names** |
| 10-11 | a name that is not a literal at the call: `foreach (var fld in RenderMatFields) GetMember(ro, fld)` over a `static readonly string[]`, a `new[] { … }` loop, a local function forwarding its parameter — both literal passes see nothing and the count simply shrinks | 212 reads through 20 tables; `currentRenderMaterial` / `runTimeRenderMaterial` / `highResRunTimeRenderMaterial` / `mid`/`highResMaterialGuid` (the retexture recovery, the district albedo bind, the private-leaf clone), `Armies` / `Squadrons` (the era walk) and the middle pose slots held no catalog row |

The third was not found by the gate, by `bindcheck`, or by review. It was found by a **log line**: `allMeshNames` missed
36 times in one session, and `typeprobe --exact` then said no assembly in the game declares that field at all — a dead
probe whose fallback branch rebuilt an array by reflection and discarded it. The gate had been reporting "all 331
catalogued" while never looking at the site. Drilled by putting a bogus member name in that shape: the shipped gate said
`OK — all 331`, the widened one failed.

The fourth (2026-10-11, found by the general review's verification pass, not by the gate) was closed with a third
extraction pass, `tools/find-name-sites.pl`: every reader call whose name argument is an identifier is traced — a loop
variable to its `new[] { … }` or table, a table to its literals, a parameter to the function's callers — and a name the
pass cannot trace **fails the gate** by the function's name instead of going uncounted (`NAME_ARG_ALLOW` holds the two
genuinely computed ones, with reasons). `tools/drill-reader-gates.sh` plants all three table shapes plus one untraceable
parameter and requires each to be named.

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
