# Changelog — HAF Authoring Tools

The **package** changelog: what changed for someone who installs the tools. (The project-wide engineering log
lives in the repository's root `CHANGELOG.md`.) Versions are also git tags: `editor-vX.Y.Z`.

## 0.5.7 — unreleased

- **Model Fuser: a part that is double-sided by duplication is fused as two copies, not one soup** (user: "an issue
  with the fused gun that becomes visible in the Vehicle Lab", SMS Wespe). The gun carries every face twice, wound
  both ways, each copy with its own vertices — 6,000 of its 6,053 faces. Welded by position the two copies shared
  every vertex class, so every edge was a *four*-face edge: no face had a partner, the sheet walk paired nothing
  (62,928 sheets for 68,974 faces), each face was judged alone by the radial score, and the copies came out with
  scrambled windings. Then both copies landed on one output index triple, and Blender's importer — every Lab probe
  and every bake runs through it — drops duplicate polygons and kept one copy at random (9,033 of the gun's 14,463
  faces). Rendered: a clean solid before the fuse, holes through the reinforce and breech after. A face whose three
  welded classes match another's with the *opposite* orientation is now the second copy and keeps vertices of its
  own, as the source had them; each copy is one manifold sheet judged whole and emitted on its own vertices (the
  gun: 5,430 duplicate index sets → 53). Same-way duplicates stay welded — an importer dropping one of those loses
  nothing — and a vertex shared with an unpaired face stays welded so a copy is never torn from its neighbour. The
  report says how many twins it found. Measured per face in six directions against master: the Wespe's
  see-through faces 6,188 → 3,024 over 42,691 changed; the frigate and paddle steamer byte-identical; the Romanic
  and Teutonic a dozen invisible faces. Test: two coincident plates wound both ways fuse to two sheets of two faces,
  never four of one, with no two output faces on one vertex set; a plain plate and a same-way duplicate are untouched.

- **Vehicle Lab: a Shroud role with its own reduce dial** (user request). The standing rigging — shrouds, stays,
  ratlines — is the densest geometry on a sailing ship and wants a harder cut than the running rigging, and it must
  *not* ride the Sail bone the way Rigging can (it holds the mast up). Same soup-pass reduction as Rigging (no weld;
  dissolve + collapse), a **Shroud reduce (%)** slider in Vertices control, a Show-only filter, and `shroud` /
  `ratline` auto-guessed from part names. Reaches the rig script as a tagged `shroudreduce=@file|percent`, absent
  while the dial is 0, so an older script and a newer Lab still run together. Drilled in rig mode on three identical
  3,178-vertex rope tubes: Rigging at 50 → 46% cut, Shroud at 80 → 62% cut (the soup pass bottoms out at each
  island's minimum topology, as for Rigging), and the two dials never touch each other's parts. The role is
  appended *last* in the enum on purpose: a recipe stores roles as integers.

- **Vehicle Lab: Spin frames goes up to 100** (was 60; user request while rigging paddle wheels). A big slow wheel
  wants a long clip so one turn stays smooth at the slowest slice step. Only the slider was capped — the rig script
  never had an upper limit, and the wave rock already bakes longer clips.

- **Vehicle Lab: the second model scales per axis** (user: "what I meant by scale is Scale X, Scale Y, Scale Z").
  The merge had Offset and Rotation per axis and one uniform Scale — enough to reconcile units, not enough to fit a
  part borrowed from another model: paddle wheels cut from one steamer have to match the new hull's beam and its
  freeboard separately. **Scale (X, Y, Z)** replaces the single number; the same value three times is the old
  behaviour. Old recipes keep their number on all three axes (the legacy key is still read and multiplied in), a
  single-number `merge2=` argument still means uniform, and a zero or negative component counts as 1 at the Lab,
  in the recipe and again at the script boundary — zero flattens the model, a negative one mirrors it inside out.

  **Making it per axis exposed a placement that was only ever exact by luck.** The transform went onto the
  import's top-level objects, and a Blender object cannot hold a shear — harmless while the scale was uniform, which
  commutes with any rotation. Per axis it does not: a 4 × 1 box under a root rotated 30° inside its file, scaled
  (2, 1, 1), measured **6.82 × 4.93** where **7.93 × 2.87** was asked. (A 90° root, the Sketchfab shape, happened to
  be exact — the axes only permute — which is why the first drill passed.) Each `B_` mesh's target matrix is now
  taken as a full 4×4 and **baked into its vertices**, where a shear is just numbers; the rig path applied
  transforms later anyway. Checked against master on six uniform cases: four byte-identical, one differing only in
  `-0.0000`, and one — a **mirrored and rotated** root — where master was the one off (it missed an independently
  computed ground truth by up to 0.09; the new path matches it to the digit). The mirrored case needs its faces
  re-flipped after the bake, and that line is fault-injected: without it a closed cube's signed volume reads −8.
  **Review, P2:** baking into the vertices moves the vertices only — `Mesh.transform()` leaves shape keys where
  they were unless told, and a mesh with shape keys is *displayed* from its keys, so a glTF morph target (a rigged
  Sketchfab download often carries one) snapped straight back to its file position, uniform recipes included. A cube
  at x=27 landed at the origin. `shape_keys=True`, and a new manual drill (`tools/drill-merge2.py`) that generates
  its fixtures in Blender and asserts all three placements above; it fails on exactly that line when reverted.
- **Model Fuser: a bridge face no longer takes a reversed patch's colour** (user: "still see-through from the front",
  SMS Wespe, after the deck fix). The consistency pass colours every face of a sheet by the first path that reaches
  it. On the Wespe's hull that path ran through a 253-face patch wound backwards, and a riser at the platform step —
  wound consistently with the patch *and* with the correct majority, a bridge — inherited the patch's colour; its two
  majority edges became the sheet's only unsatisfied edges, and the majority flip turned a correct face over. A face's
  colour is now the one most of its own edges support: while any face has more unsatisfied edges than satisfied,
  recolour it (each step lowers the sheet's unsatisfied count, so it terminates). Measured per face in six directions,
  on the committed deck fix as baseline: the Wespe's one visible bridge face turned solid and 40 invisible faces
  recoloured; the frigate two invisible faces; Romanic, Teutonic and paddle steamer untouched. The report's
  `unsatisfied after` now says how many faces were recoloured and how many edges are left. **Self-review:** the repair
  now runs only on sheets the walk has already accepted as orientable — judged on the walk's own count, never on the
  repaired one, so a genuinely non-orientable sheet (a propeller blade) can never be argued under the 5 % threshold
  and then majority-flipped. Byte-identical on all five ships; the Möbius-band test now also asserts nothing was
  recoloured. Applying the lap exemption to the walk's cycle-closing edges was tried at the same time and *rejected
  by measurement*: 35 faces moved on the Romanic and one turned see-through, because that count decides
  orientability and was tuned on the Romanic's three-face rims. Three pure-kernel tests: the bridge case, that a
  consistent sheet is left alone, and that the count only ever falls and no face ends outvoted by its own edges.
  **Review, P2:** the repair ran at most 16 rounds, and a correction that has to travel *against* the face order
  costs one round per step — a 565-face sheet was left with six unsatisfied edges and a face still outvoted, and the
  winding pass used the unfinished repair. It now runs until nothing changes; since every round that changes
  anything lowers the count, one round per edge is a bound that cannot bind. A 41-face backwards cascade pins it:
  cut short at 16 rounds, finished without the cap. Byte-identical on all five ships.

- **Model Fuser: a deck with a raised edge is no longer turned over** (user: "a fusion issue causing transparent deck",
  SMS Wespe). The gun platform came out of the fuse see-through from above — 242 of the 251 see-through cells on the
  whole fused deck traced to that one 281-face sheet. Bulwark and coaming faces make a deck sheet a shallow *tray*,
  and a tray seen from above is the same surface as an open box wound inside out: its floor faces its own centroid,
  so the signed volume reads confidently negative and the rule turned it whole. The deck exemption written for
  exactly this sheet never got a say, because the volume rule ran first — and the rim drags the sheet's levelness
  under the 0.8 gate besides (0.57 here). What separates a rimmed deck from an inside-out hull is height: a hull lies
  at the model's floor, below the belly line by construction. So above the belly, facing up on balance is enough,
  and a deck by either reading is exempt from a *negative* volume verdict — only that one. A confident positive
  volume still settles a deck as kept ahead of the twin rule (the Romanic's seven decks reading `+1.00` were turned
  over by a twin majority the moment the volume stopped protecting them, in the first cut), and the twin rule still
  turns a double-skinned slab's underside. Three tests: the tray above the belly is kept, the same tray at the floor
  is still an inside-out hull and is turned, and the slab's underside still turns. Measured by ray-casting every
  fused ship in the model folder from above and below: the Wespe 397 → 138 see-through cells (the rest is
  superstructure the source already had), the Confederate frigate, Romanic, Teutonic and paddle steamer unchanged.
  The diagnosis went wrong twice on the way — a mis-joined face map blamed the twin rule, and the first fix let the
  twin rule at correct decks — which is why every sheet's report line now carries `level` and `at y`.
  **Review, P2:** the deck's height was the whole sheet's mean, which a rim lifts — so an inward-wound *shallow*
  hull with a low rim averaged to just above the model's floor and kept its plating pointing into the ship. The
  height that matters is the *floor's*: the faces that actually point up, which for a hull sit at the floor and can
  never clear it. Its test was red on the previous cut.
  **And its lowest vertex, not its mean** (review, second P2): a V-bottom's panels slope up from the keel, so
  their mean height clears the floor while they are the hull bottom all the same. What a hull's floor does that a
  deck's never does is reach the model's floor. Red on the previous cut too.

- **A failed district re-bake no longer destroys the previous building.** The unit paths have had E5 rollback since
  it was built; the district path never got it, and its steps are destructive by design — `BakeFxMesh` deletes
  `_DistrictMesh` and `_FxMesh` before re-creating them, so `CreateAsset` cannot keep a stale serialized ref, and
  the scoped selector does the same for `_Element` and `CityMapSelector_<name>`. A compose that threw, a part with
  no model file, a missing atlas: any of them left the previous building **gone** while `haf_districts.json` still
  pointed at its guids. A district bake is now one transaction: either the new district is fully in place **and**
  registered, or every asset and the registry are exactly as they were.

  **The rollback covers more than the district's own four assets,** which the first cut of this fix got wrong and a
  review caught. Step 1 of a district bake runs the *unit* baker, which sweeps and re-mints the shared outputs for
  the same `resourceName` — the atlases among them — and discards its own backup the moment it succeeds. But a
  district entry references those atlases **by guid** (`atlasGuid`, `normalAtlasGuid`, `roughAtlasGuid`, read at
  runtime by the scoped path), and delete-then-recreate mints new ones. So a base bake that succeeded followed by a
  district step that failed put the previous *geometry* back while leaving the registry's atlas guids pointing at
  assets that no longer existed: the old building, untextured. The backup is therefore taken **before** the base
  bake and covers the union. The registry write is inside the transaction for the same reason — the entry naming
  the new guids is precisely what failed to save, so keeping the new assets would strand the old entry.

  **The window's form rolls back with the files.** Restoring the assets was only half of it: the bake writes its
  results back onto the open entry (`fxMeshGuid`, the three atlas guids, `selectorGuid`, `posOffsetBaked`) and
  nothing reloaded it afterwards, so a rollback left the Factory holding guids for assets it had just deleted. One
  click of **Save settings** would then write those dead guids over the perfectly good restored district, because
  `Upsert` is a wholesale replace rather than a merge. (Its comment claimed otherwise, and that wrong comment is
  now corrected too.) `DoBake` snapshots the whole entry before baking and puts it back on any rollback — a whole
  object rather than a list of "the fields the bake writes", since such a list is exactly what `check_handlists.sh`
  exists to police and would need extending every time a baked field is added.

  **The four names are NOT added to `OutputSuffixes`,** although that array is described as the rollback whitelist.
  It also drives `SweepAllOutputs`, which *deletes*, and which both unit bake paths and the Factory's Remove call —
  and a unit and a district may legitimately share a `resourceName`, which the sweep already warns about. Folding
  them in would make baking or removing a *unit* destroy a same-named *district's* assets: a worse bug than the one
  being fixed. `CityMapSelector_<name>` settles it anyway, being a prefix that a `name + suffix` array cannot
  express. The district list and the union are pure kernels (`BakerRules.DistrictOutputBasenames` /
  `DistrictBakeBasenames`) with seven tests. One reads `OutputSuffixes` **out of the source file** and fails if the
  two lists ever overlap; another pins that the rollback set contains the three atlases the entry names by guid.
  Both were fault-injected — planting `_FxMesh.asset` in the unit array, and reverting the union to the first cut's
  district-only scope — and each turned red.

- **Vehicle Lab: markings survive a Fuser rename.** The Fuser names a fused node after the group's first member, so
  changing a group's membership renames it and a role or placement kept by exact name was lost on the next Probe
  (user: a Z offset on group Y "got wiped after I made some changes to the group in the Model Fuser"). A re-Probe now
  carries both over to the one fresh row with the same `Fused_<group>_` prefix and names the move in the status.

- **Vehicle Lab: per-part Offset and Scale** (user: "address the floating objects … also they are weirdly
  intersecting each other"). Before building it, the pipeline was measured end to end on the steam frigate — the
  Cutter keeps 962/962 parts in place, the Fuser 20/20 groups, the probe 20/20 — so a floating or intersecting prop
  is authored that way in the source (its original file has 54 parts with nothing under their footprint), and the
  correction belongs per part, by hand, not as another pipeline fix. Click a row and a Placement box offers Offset,
  Scale (about the part's own centre) and Reset; placed rows carry a ⇄ tag. The values ride the recipe like roles,
  survive a re-Probe by part name, and reach `vehicle_rig.py` through a tagged `parttx=@file` line list whose format
  is a tested pure kernel (`VehicleLabRules.PartPlacementLine`, split from the right so a `|` in a name survives).
  Applied after the split and before straightening in BOTH probe and rig modes, so the preview shows exactly what
  Generate bakes, and the log prints each part's centre and size before and after.
- **Vehicle Lab preview zooms in twice as far** (floor 0.2 → 0.1 on the scroll-wheel zoom; user request while placing a
  floating prop against its deck). The near plane and the radius floor leave room, so nothing clips at the new limit.

- **`Tools~/placement_shift.py` — the one-time re-dial after the placement change.** An animated entry dialed to
  compensate for the old placement over-corrects once the bake centres the model itself, and nothing moves until
  that entry is re-baked, so the migration is per-entry and easy to lose track of. Point the script at a `pack.json`
  and it measures every animated entry exactly as `rig_anim.py` does — junk cull, world box, the registry rotation,
  `size/longest` into game units — and prints the Position offset each one needs to look as it does today
  (`new = old + move`, less the grounding lift where the entry was not already auto-grounded). The sign is
  self-checking: a dial that was pure compensation lands on ~0, and on the shipped pack four independently dialed
  entries collapse at once (GatlingGuns −3.70 + 3.34 = −0.36, AntiTankIFV +0.50 − 0.497 = +0.003, StealthHelicopter
  and TOW-Infantry likewise). It also warns that an entry you have already re-baked is done and must not be moved
  twice — the one thing the measurement cannot tell you.

- **An animated bake now lands where a static bake lands** (user: "switching between static and animated should give
  the same result in both facing and offset"). Facing was unified on 2026-09-12; placement never was. The static path
  centres the model's box on the origin and drops its lowest point to it; the animated path did neither — the opt-in
  *Auto-ground* toggle did the vertical half and nothing did the horizontal one — so an animated bake sat wherever the
  artist happened to leave the model inside its file. Measured on the steam frigate: baked animated, its box sat
  **0.97 × 1.84 game units** off the pawn at size 5; now it sits at **0.0000 × 0.0000**, keel on the ground, with the
  rig, the skin and the clip untouched (posed box drift 0.000 cm). Both paths read the same box — the frigate's file
  gives centre (22.794, 12.024) and lowest −5.237, exactly the numbers the bake used.

  **The Auto-ground toggle is retired**, because there is nothing left to tick: a flyer is grounded exactly as the
  static path has always grounded one, and its flying height is the Position offset Z dial, the same dial on both
  paths. The registry field stays so older `pack.json` files still round-trip.

  **One-time cost, as with the 2026-09-12 rotation unification:** an animated entry dialed to compensate for the old
  placement now over-corrects, because the bake removed what the dial was cancelling — the shipped Gatling guns carry
  `y = −3.70` against a measured 3.34-unit miscentring. Re-check the horizontal Position dial of each animated entry
  after its first re-bake; most should end up near zero.

  **And a guard**: grounding is unconditional now, so the sky-lift trap (referencing a clip that holds a *struck*
  pose — a yard swung under the hull — grounds the model on that part) applies to every model, not just auto-grounded
  ones. The bake now says so in the log when it lifts a model by more than a quarter of its own height, and names the
  fix: reference the deployed frame, `Furl[0..0]`.

- **The Model Splitter is now the Model Cutter** (user: "we are mainly cutting away bad or insignificant parts and only
  on occasion split an object apart"). Menu, title and text follow the name; the window's own class keeps its old name
  so a saved Unity layout still finds it and nobody has to reopen the window.

- **The Multi-fragment split now works on ANIMATED bakes** (user: "for unknown reason the Multi-fragment split option
  no longer works" — on the animated path it never did). The engine draws at most 16,320 quads per fragment and the
  overflow silently does not render; the split has cured that for static bakes since 0.5.7, but `SplitForQuadCeiling`
  had exactly one call site, in the static builder, so on a rigged model the checkbox did nothing and the steam
  frigate's 40,891 quads lost their masts in-game. Most of the machinery was already general: the splitter carries
  bone weights and bindposes, so a chunk skins against the same skeleton and plays the same clips, and the plugin's
  chunk injection discovers chunks from the collection by name and has never cared which path baked them. What the
  animated path needed was a prefab of its own — the imported FBX is an asset, so the extra renderers go on a copy,
  saved as `<name>_Split.prefab`, which the skeleton is then baked from. The body mesh is renamed `<name>_ModelMesh`
  because that exact tail is what the runtime looks for when it retargets the body onto the donor's mesh name;
  without it, with chunks present, that search falls through to index 0 and can land on a chunk. As on the static
  path, the split makes a promise and the bake now verifies it: a chunk that still measures over the ceiling fails
  the bake instead of shipping geometry that will not draw.

- **A failed re-bake no longer loses the split prefab** (PR #71 review, P2). The animated split writes
  `<name>_Split.prefab` and the baked Skeleton *references* it, but it was written under `FactorySource` — which the
  E5 rollback does not cover — and deleted before the fallible split and skeleton steps. So a failed re-bake restored
  the previous skeleton with its source prefab gone, and re-creating that prefab later gives it a new GUID the
  restored skeleton cannot resolve, all while the rollback reported success. It now lives in `Resources` and is on the
  backup whitelist, so it is carried with its `.meta` and its GUID survives — the same arrangement that has always
  protected the static path's `_Model.prefab`. A structural check in the bake feature tests fails if it ever moves
  back out or drops off the list.

- **Splitting costs vertices, and the pool it spends is shared** (documented after it bit: units and districts all
  stopped drawing). Every chunk duplicates the vertices along its seam, by as much as the cut decides: the steam
  frigate measured 99,676 to 125,369 (**+26 %**) split static, but 99,676 to 100,648 (**+1 %**) split animated — read
  the `BAKED MESH` lines, don't carry one path's figure to the other. They live in the pawn vertex buffer,
  1,000,000 vanilla. When that buffer fills the game stops
  uploading meshes entirely, so units **and** districts vanish at once with no error anywhere. The two ceilings pull
  against each other: reducing triangles pays both, splitting pays one by spending the other. F8 reports the fill and
  `BufferOverrides = MeshWithSkeleton:verts=+N` raises the pool (~48 MB VRAM per million). Now stated in the
  checkbox's own tooltip, the Factory manual, the quickstart and Vertex-Budget.

- **Model Cutter / Fuser: every row shows the part's size**, the same figure the Vehicle Lab prints, from the box the
  analyzer already reads (user: "it would really help if this list also included the dimensions" — while hunting a flat
  panel by eye through 900 rows).
- **Fuse: the run's mirrored verdict reaches the groups that cannot see it.** How a file stores its mirrored parts is a
  property of the file, but the evidence — a mirrored part welded to a plain one — lives wherever it happens to live.
  On the frigate, group A has five such parts and the deck group none, so the deck kept the glTF reversal and half of
  it rendered see-through. The fuse now pools every group's evidence across the run and re-plans the groups that had
  none of their own; a group whose own parts disagree keeps its own counsel (the Romanic's group H judged 1 against 2,
  and forcing the file's verdict on it cost 12.5 % of its beam view). A single-group fuse still decides for itself.
- **Fuse: a deck below the hull's belly line is no longer turned inside-out.** The frigate's gun deck came out of the
  fuse see-through from above: 94 % of the group's struck surface back-facing, against 0.1 % for the same parts
  unfused (user: "there is still an issue with the deck, in particular Object_1002"). Where an island's volume and
  twin evidence say nothing, the fuse falls back to asking whether the surface points away from the hull's belly
  line. That line is taken a quarter of the way up the model, and a sailing ship's rig owns most of its height and
  area, so the line landed at 2.59 while the deck's three islands lay at 0.61, 2.06 and 2.53, every face pointing up.
  They scored -0.67 and were reversed whole. **A level sheet above the hull's floor that ALREADY faces up is not asked
  that question**: it is a deck and needs no correction. The rule is one-sided on purpose — turning every level sheet up
  was tried first and draped the frigate's two zero-thickness plates (authored facing down, to be seen from below) over
  its boat deck as a blank sheet, and keeping every level sheet as authored was tried next and left genuinely
  interior-facing sheets uncorrected, which four of this file's own tests pin down. Confident volume and twin evidence
  still decide first, and everything else is judged exactly as before. Measured: the frigate's deck group 94 % -> 0.0 % back-facing
  from above, its group T 4.9 % -> 2.3 %, every other group of that ship unchanged; the Teutonic's hull and all
  eleven Romanic groups byte-identical. Three tests (a deck kept, a deck authored upside down turned up, bottom
  plating left facing down); the first two fail without the rule.

- **Model Fuser: "Check mirrored parts", for files that store their mirrored parts already facing outward.** glTF
  says a mirrored part (a negative-scale node, one side of a symmetric hull) renders with its winding reversed, and
  the fuse applies that, which is what the Teutonic needed. The Confederate frigate's file stores its mirrored hull
  half already facing outward, so the reversal turned that whole side inward: 94.8 % of the fused hull rendered
  back-facing from that side, 0.1 % for the same parts unfused. Welded to the correct half, the two cancelled every
  direction signal and the fuse left the island as it stood. Ticked, the fuse compares each mirrored part with the
  plain parts it is welded to (a plain part's winding is never in doubt) and undoes the reversal only when the group
  gives a clear verdict: at least three mirrored parts judged and nine in ten agreeing. Parts with no plain neighbour
  follow that verdict; a part whose own seams disagree keeps the reversal; mixed evidence changes nothing. **Off by
  default** (user: "make it an option"), and the fuse warns when a group would need it. Measured with the real fuse
  on the real files: the frigate's hull 94.8 % -> 0.0 %; the Romanic's hull 108 -> 8 back-facing cells from the
  beam and its deck 161 -> 29 from above, which turn out to be the same bug; the Teutonic hull and eight other
  Romanic groups byte-identical; the Romanic's group H, whose judged parts disagreed 1 to 2 and got worse when the
  one was acted on, now left untouched. Four tests: a pre-flipped file fixed only with the option, a standard file
  byte-identical, mixed evidence unchanged, and a lone mirrored part following the file's convention (the failure
  reproduced with the option off). A lap strip lying on its plate walks their shared edge the same way on purpose; the
  check applies the consistency pass's own lap rule, so mirrored lap strips are never mistaken for inside-out (review
  of PR #67: three of them had drawn a unanimous verdict and 18 correct faces were turned inward).

- **Fuse: one stray edge, stray geometry, and double-skinned solids** (the SS Romanic). Its 41,799-face hull reached
  1 unsatisfied edge of 92 same-way in 59,000 and the "0 or nothing" orientability rule refused it: the parity pass
  must now resolve 95 % of the same-way edges (a propeller blade at 138 left of 32 is still refused) — with that the
  majority rule turned 19,923 faces (a single skin with half its regions wound the other way) and the hull renders
  complete. Its 144 anchor-chain links parked 3 km down the length axis and 190 m up had put the belly at 46 m on a
  ship whose deck is at 11: the frame drops samples farther than 4x the median distance from the model's median
  centre. And a third direction rule for thin solids built as two skins with opposite normals, where the volume and
  score rules cancel: material lies BEHIND an outward face — an island with a twin on at least half its faces is
  decided by the majority of twin-in-front vs behind, measured for every island before any flip — as a TIE-BREAKER
  only: from inside a gap, air between two solids looks exactly like a skin, so a confident volume verdict (a closed
  island, or an open one past the agreement and thickness gates) always wins. The frame is area-weighted: face
  samples weighted by their area, so a coarse hull outweighs a dense cabin and a chain of tiny links weighs nothing
  however many vertices it carries — sampled at each triangle's three corners, a third of its area each, so a big
  triangle's area lies across its whole extent. Twin evidence may veto a volume reversal (a cavity shell's twins lie
  behind it on at least 90 % of its faces, all from ONE other island whose box contains it — enclosed, as neighbouring
  solids never are) but never cause one against a confident volume. Ten tests.
- **Model Cutter / Fuser: a split fragment's preview carries only its own vertices.** A split writes its fragments as
  new *index* accessors over the parent's untouched position buffer, so a fragment addresses a handful of vertices
  inside a buffer holding the whole original part. The preview copied the buffer wholesale: on a real Khalandion split
  it held 5,202,111 vertices to draw 393,646 (13x), one 8-vertex fragment carrying 65,532 — and because Unity sizes a
  mesh with `RecalculateBounds` over every vertex it holds, clicking a fragment's row framed the whole parent instead
  of zooming to the part. Each primitive is now compacted to the vertices it references, indices remapped; the same
  file now holds exactly the 393,646 it draws. The 1 GB estimate counted the shared buffers too and is corrected the
  same way. Found in review of PR 66.
- **Model Cutter / Fuser: "Hide parts over (size)"** — the mirror of the existing lower bound, so the two bracket a
  size band, and alone it leaves nothing but the small clutter in the list, ready for the Delete key. Its travel is
  **logarithmic**: part sizes span four decades on a split model (the Romanic's 1,796 parts run 0.017 to 173), where a
  linear slider spends 99 % of its length doing nothing and crosses "219 parts shown" to "1,361 shown" inside one
  pixel. The number box beside it takes an exact threshold. At rest it sits a hair above the largest part, so the
  biggest row can never round its way into hiding.
- **Model Cutter / Fuser: an "Un-mirror" checkbox above the preview.** glTF is right-handed and Unity left-handed, so
  the preview showed the model mirrored — screen-left was the file's starboard, a trap in a window where picking a side
  is half the work. The box negates X and flips every triangle to compensate, measured to leave the surface exactly as
  solid (the Romanic split: 1.0 % of struck cells render back-facing either way; negating without the flip inverts the
  whole ship). Off by default, remembered per window, and it flips the built meshes in place rather than re-reading the
  file. The part list, the sliders, Find the mirror and every output always worked in file coordinates and are
  unaffected either way.
- **Model Cutter / Fuser: a file probed into the window starts fully visible.** The list filters kept their values
  across files and were only clamped into the new model's span, so four sliders left at the ends on a metre-scale ship
  arrived at the ends of a centimetre-scale one and hid all 113 parts ("why don't I see any parts?"). The first Probe of a
  file now resets every filter (sliders, Show only, the whole-parts toggle); a re-Probe or a slider move on the same file
  keeps them. A **Show all** button next to the toggle does the same by hand.
- **Model Cutter / Fuser: Probe is three times faster on a big file.** A 214 MB ship (113 parts, 2.6 M triangles)
  took 86 s to probe. Two causes, both fixed. The island analysis read every index and every position through the
  accessor's JSON again — string-keyed lookups, a boxed int and a fresh array per element — 30 s; the reader now
  resolves each accessor's layout once (same checks, same messages, same bytes: the rows of the real file are
  identical to master's) and reads elements straight from the buffer: 6 s. The preview went through Blender (an FBX
  export with the Vehicle Lab's visibility rays and inside-out verdicts these windows never read, 40 s) and a Unity
  FBX import (8 s); **the preview is now built straight from the GLB in C#, one object per node** — no Blender, no
  FBX, a few seconds — and that also fixes a real bug: the preview matched rows to renderers by NAME, so a file that
  names all 113 of its nodes "Material2" (the Salegs Revenge) lit the whole ship for any row. Rows and preview objects
  now meet on the node index; the flat-parts filter measures per node too. Same coordinates and winding as the cut
  preview, a submesh per primitive tinted with its material's base colour. Every GLB reader in the toolkit (split,
  cut, fuse, extract) shares the faster accessor; the BIN prefix copy also stopped enumerating 200 MB byte by byte
  through LINQ.
- **Bake: a point-UV material packs as the colour it samples.** The Romanic's deck is painted with a 512×1024
  plank texture whose every face carries the same single UV — the texture used as a colour picker, one tan
  texel. Packed as a texture, that point folded onto the bottom-left edge of its atlas cell, where the bilinear
  tap blends the neighbouring cell in and every coarser mip averages the whole plank image: the deck read as the
  image's dark-brown mean (0.61, 0.52, 0.36) instead of the tan texel (0.77, 0.68, 0.52) the web preview shows,
  and the brightness/saturation dials could not bring it back. Both atlas paths now detect a material whose whole
  UV span fits inside one texel of its own texture (`BakerRules.PointUv`), replace its albedo with an 8 px swatch
  of that texel (wrapped, as the source viewer sampled it) and pin its vertices to the cell centre like any
  flat-colour material. Tiled materials and real textures are untouched; the Generate log names each one as
  "point-UV material '…': every UV at (u, v) — one texel of a W×H texture, packed as the flat colour (r, g, b)".
- **Vehicle Lab: Default reduce actually runs.** Its tagged argument was parsed before the script defined its
  name-list reader, so the rig logged a warning and silently skipped the tier: a 208,000-triangle rig failed the
  bake while Verify had projected 156,000 (user: "I clearly have a model with less vertices yet the same error").
  Parsed after the reader now, and a malformed tag is a hard error like the other tags — never a silent skip of a
  reduce dial.
- **Vehicle Lab reduction welds the seams first — no more cracks in a reduced hull.** A GLB carries a vertex per
  UV seam and per hard edge, and the rig's importer keeps them apart, so a fused hull reached the collapse as a
  triangle soup: 68,000 of its 73,000 vertices on a "boundary", every seam's two sides moved on their own, and the
  hull came out cracked along every plate (small fittings, each triangle collapsing alone, were ground to slivers).
  Every tier but Rigging now welds coincident vertices (1e-5 of the part's size; double skins stay apart), marks
  edges over 40° sharp so hard edges survive, and collapses to a dial that counts welded vertices. **The weld stays
  within a material:** a ripped hull paints its plating, stripe and portholes as per-face materials, and the first
  cut of this welded across them — the collapse then slid vertices from a black plate into the gold stripe for
  free (a flat surface, no quadric error) and the user's hull came out smeared ("the hull texture is wrong"). A
  material border left as a mesh boundary keeps the decimate's boundary quadric and stays on its line: the Romanic
  hull at 65 % renders identical to the source (portholes, stripe, plating), 25,300 triangles either way, beam
  holes 109 vs 108 unreduced. Measured on
  the Romanic's hull at 50 %: the old pass added holes (from the beam, 0.3 m cells: 9 → 50 starboard, 108 → 99
  port, and visible cracks); the welded pass adds none and reads smooth; the Teutonic's hull the same (255 → 305
  became 255 → 255). Ventilator cowls that reduced to dust keep their shape at the same triangle count. Rigging
  keeps the raw-mesh dissolve + collapse: welded rope segments bottom out at each island's minimum topology,
  where the soup collapse thins ropes to the lines the tier is for. The log line reads "welded from N raw".
- **Fuse: the twin reach is 0.5 % of the length (was 1 %) — the Romanic's bridge deck.** A 39-face roof region on
  each side, facing up and correct as authored, welded into a deckhouse sheet and found 74 "twins in front": the
  deckhouse's undersides a metre and more above — neighbours, not a skin — and the twin rule turned the roof over
  (34 m² transparent from above) while its own inside-out score read +0.55. Measured on the ship, every genuine double
  skin has its twin within a third of the old reach (hull skins 0.03–0.34, deckhouse skins 0.05–0.13, the fixture
  0.20); the roof's neighbours sat at 0.72. Half the reach keeps every skin and loses every neighbour. Holes from
  above, ray-cast over the whole ship: 339 cells → 126, group D 230 → 17; the Teutonic's fused hull 70 → 49, its
  per-group numbers unchanged. Each sheet's report line now names its parts ("— parts: Object_6 ×37, …") and how far
  and how straight its twins lie ("at 0.72 of reach, 0.95 straight"), the two numbers that found this.
- **Vehicle Lab: "Default reduce (%)", and Detail now reads before Preserve.** The catch-all tier — every part still
  marked Default, nothing chosen for it — cut by one dial at Generate exactly as Body parts are, so a model whose
  thousand small fittings are all undecided slims without marking each one. It joins the tier summary, the Verify
  projection and the recipe; the rig script takes it as a tagged argument (`defaultreduce=@<file>|<pct>`), so an
  older script and a newer Lab still run together. Preserve's dial moved below Detail's: the exception reads last.
- **Mark a part for deletion — Delete key or the row popup, in both windows.** The part loses its mesh in the
  output (its transform and children stay; the mesh data is left in the file as an orphan the bake never reads), applied
  by Split, Plane cut and Fuse alike after their own work. The Splitter's row popup is "–  / Split / Delete" (a whole part
  offers no Split), the Fuser's letter popup ends with "✕ Delete"; Insert marks the highlighted row for split, Delete
  toggles deletion, – / 0 / Backspace clear every mark. A deleted part is neither split nor fused. "Show only" lists
    "Marked for deletion". The checks and the deletion marks persist in `<source>.marks.txt` beside the fuse sidecar
  (S = split, X = delete): written by **Save marks** in the Splitter, by Save groups in the Fuser, and by every Split,
  Plane cut and Fuse; read at the first Probe of a file, and by Load marks / Load groups. Both windows share it.
- **The Model Workshop is two windows: Model Cutter and Model Fuser** (user: "too many responsibilities, which makes
  it cluttered — one screen for cutting and one for merging"). One implementation; both share the file pickers, the
  probe, the filtered part list, the preview, the mirror finder and the keyboard sweep. The Splitter shows the Split
  checkboxes, "Check all splittable", the plane cut and Split (output `_split` / `_cut`); the Fuser the ⊕ letters,
  the A–Z keys, the weld slider, Save/Load groups and Fuse (output `_fused`). "Show only" lists each window's own
  kinds. The one-step Generate (fuse AND split) went with the split: chain the outputs — the ⊕ letters travel with a
  cut or split output, and a fused output's sidecar names each shell under its group's letter (an unfused group keeps
  its parts' lines), so either order works (the Splitter re-reads the sidecar on every Probe; the Fuser keeps its
  edits and reloads through Load groups). A saved editor layout that held
  the old Model Workshop drops that window once — the class is abstract now. Each window is its own file
  (`ModelSplitterWindow.cs`, `ModelFuserWindow.cs`): Unity binds a docked window's saved layout entry to the script
  whose FILE NAME matches the class, so a window class nested in another file vanishes on the next restart.
- **Generate: every group fused in parallel, and the big ones 5x faster.** Nineteen groups took 3½ minutes chained.
  Planning a group (gather, weld, islands, sheets, direction) reads only the source, so every group now plans at once
  on the thread pool and the meshes are appended in letter order into one output — the same bytes chaining gave,
  in the time of the longest group. Two hot spots went with it: the edge tables hashed packed pair keys by
  `a ^ b` (adjacent vertex classes collide, a chain walk per lookup — 11 s on a 99,000-face group), and the twin
  search tested thousands of candidates per face where a centroid-distance reject skips nearly all of them (44 s → 9 s).
  The report carries a `timing:` line per group.
- **Fuse: orientation by SHEET, not by island** (the SS Romanic's group D: hull shell + decks + bulwarks). Winding
  parity propagates across two-face edges only; an edge shared by three faces (a deck meeting the hull side mid-plate,
  a lap strip on plating) is a junction no two faces own. An island joined through such junctions holds several
  parity-connected sheets whose winding relative to each other was whatever the seeds assigned — then ONE majority flip
  and ONE direction verdict for the whole island fell on either side of a coin: the starboard deck rendered
  transparent with 12 parts in the group and solid with 14. Each sheet is now made consistent by its own majority,
  tested for orientability on its own edges, and judged for direction on its own volume, twins and score; a sheet's
  edges at a junction are boundary, so a deck cut off at the hull side is open, never a "closed shell". A manifold
  island is one sheet — the Romanic's hull group and the Teutonic's hull, boats and decks read the same as before.
  Pairing the two faces that continue each other at a junction was tried and rejected: it closed odd cycles inside
  the Romanic's hull shell (0 → 3 non-orientable sheets).
- **Fuse: authored normals take the sign of the final winding.** The Romanic's whole port side ships normals pointing
  UP over a winding that renders DOWN (a mirrored instance). The direction pass turned the deck up, and the old rule
  — negate the normal when every face flipped — then pointed the normals down: the deck was visible from above at
  last, and lit from below. The artist's smoothing is kept; its sign follows the sum of the incident faces' geometric
  normals. The Teutonic's masts (group G) had rendered dark for the same reason.
- **Fuse report: where and what.** A sheet the parity pass refuses now says where it fails ("… unsatisfied after at
  Object_4~Object_4 (2-face edge) ×52"), and every fuse lists "rewound by part: Object_4 1,566/11,955; …" — the first
  question after "why does my port side still render inside out" is which part the pass left alone.
- **Model Workshop: "Only flat parts (≥ % level)"** — the Vehicle Lab's deck finder, measured on the Blender preview
  the Workshop already builds: slide it up to ~60 and only the walking decks, platforms and hatch tops remain, so the
  deck plate a fuse group is still missing shows up in a list of a dozen rows instead of 1,900. Parts the preview
  cannot measure stay visible; the visibility filter stays in the Lab (it needs the Lab's escape-ray probe).
- **Model Workshop: "Find the mirror of …"** — one button under the part list finds the highlighted part's twin on
  the other side of the centreline: the part whose world box is this one's reflected (every bound within 3 % of the
  part's size), triangle counts ignored because the two sides are often remodelled rather than instanced (the SS
  Romanic's Object_6 has 1,262 triangles, its port twin Object_1774 1,274). The twin is highlighted and scrolled into
  view, so a letter key marks it next; a part on the centreline reports itself as its own mirror. The centreline is
  voted by the pairs themselves — the cluster of midpoints between parts of the same box that mirrors the most distinct
  parts — so a stray has no vote and a cluster of identical strays counts each of them once (the median was the first
  rule; a pair at ±5 with a keel and one stray fitting put it at 2.5).
- **Model Workshop: the highlight follows the sweep.** With "Show only" set to one fuse group, changing the highlighted
  row's letter (key or dropdown) took it out of the list and the next ↓ started over at the first row. The highlight
  now moves to the row after it (before it at the end of the list), as the Vehicle Lab's marking sweep does — the same
  for the Split checkbox under "Checked for Split" and for the group filters.
- **Vehicle Lab: "Mark all shown as …"** — one role for every part the filters currently show. The SS Romanic
  carries 144 stray copies of one fitting 3 km down the length axis and 190 m up (an authoring leftover that
  makes the preview frame a speck of a ship); "Hide parts below (height)" isolates them in seconds and one Apply
  marks them Ignore. Undo by filtering the same way and applying Default.

- **Bake Tests: Cancel works anywhere.** The Cancel button existed only on the between-rows bar, and a row is a
  whole section of bakes lasting many minutes ("I have no way to stop it"). Every bar the runner draws is
  cancelable now: a click stops the section between models, the runner between rows, and kills a running Blender
  step within a quarter second. What finished is kept — the report is rewritten after every row and the section's
  body ends with a CANCELLED line naming where it stopped; a killed bake is not counted as a failure of the baker.
  Unity's own "Hold on… Importing assets" modal still covers every bar during a synchronous import and eats the
  clicks — nothing draws over it — so the runner's bar is drawn from the first second of a row and comes back at
  every phase boundary inside the bake (after each import, each save, each Blender step), and a click there stops
  the bake at that boundary rather than at the next model. And a **STOP button in the window itself**: the blocked
  main thread cannot deliver a click to it, so at every poll the runner asks the OS whether the mouse is held down
  over that button (or Esc is held) — hold it a moment and the button reads "Stopping after the current step…".
  Windows editor only; elsewhere the modal bar's Cancel remains. It counts only while Unity owns the foreground
  window, so Esc or a click in another application cannot stop a background run; and a cancel is a distinct
  outcome everywhere — the bake rolls back its outputs like a failure but the runner sees CANCELLED, never a
  failed bake, in every section (smoke, options, control rig, deploy golden, registry conversion) and through every
  subprocess wrapper; the control-rig litmus never reads an unfinished bake as a pass, and the deploy-golden loop keeps
  the results above the cancelled model.

- **Fuse: the inside-out score measures against the MODEL's belly, not the group's.** A group made only of deck
  strips had its own bounding box as its world, its belly line ran through the strips themselves, and a deck facing
  down scored ~0 — undecidable, kept as authored (the Teutonic's starboard deck strips, transparent from above).
  The belly (25th percentile of height) and the side centre now come from sampled vertices of every mesh node in the
  file, fused or not; a deck-only group above the hull is reversed to face up. Test with a hull outside the group.

- **Model Workshop: the ⊕ letters travel with a cut or split output**, and output names chain. A cut and a split keep
  every node, so their output now gets the groupings sidecar too — point Source GLB at it and the letters are back,
  no copying by hand. The proposed output name follows the operation, `_cut` while the cut panel is open, `_split`
  otherwise, and counts up when the source already carries it: cutting `ship_cut.glb` proposes `ship_cut2.glb`.

- Review of the branch: a non-orientable island is now left alone by the direction pass too (it read "6 of 6
  rewound" while saying "kept as authored"); a split or cut output passes each ⊕ letter down to the `_Part_NNN` /
  `_CutA` children it created — and only to those: a child that existed before keeps its own letter or none (the meshless parent resolved to nothing); the fuse report lists EVERY island (the
  status keeps the largest six). Tests for all three.

- **Bake: tiled materials keep their grain.** A SketchUp-style material repeats a small texture 13 to 1,000 times
  across a part (the Teutonic's decks, hull skin, funnels, masts); an atlas cell cannot wrap, and the fold-into-one-
  tile that serves islands parked in a single tile smeared every triangle spanning several — the deck grain read as
  a dense hatch. A material whose UV span exceeds 1.5 tiles on an axis now gets its cell image pre-tiled along that
  axis (as many repeats as authored, capped so each keeps 48 px of the texture's own resolution) and its UVs mapped
  linearly across the cell: continuous, correctly oriented grain at a coarser repeat. Untiled axes and flat swatches
  are untouched; both the static and the animated atlas paths do it, and the console names each tiled material with
  its spans and repeats. Kernel `BakerRules.TileRepeats`, tested.

- **Fuse: an island whose winding cannot be made consistent is left as authored.** The Teutonic's propeller
  blades render right, yet 32 of a blade's 1,287 edges are walked the same way by both faces, and after the parity
  assignment 138 edges are still unsatisfied — the surface has odd cycles (fins, fillets, a twisted rim) and no
  winding satisfies it; the majority rule turned 198 faces per blade and every tip showed jagged holes. Every real
  hull, deck and boat island measured resolves to exactly 0 unsatisfied edges, so the rule is: 0 = fix, anything
  else = keep and say so ("N not orientable by traversal (kept as authored)" in the summary, the edge counts on
  each island's line). Test: a Möbius band of three quads.

- **Model Workshop: a fused part is named `Fused_<letter>_<first part>`** (was `<first part>_Fused`), so the Lab's list
  shows which ⊕ group a shell came from and the fused parts sort together.

- **Model Workshop: one Generate button for Fuse AND Split** — every ⊕ group is fused into one shell, then every
  checked part is exploded into its islands, in one output GLB (chained in memory; a row that is both lettered and
  checked is fused, not split, and the report says so). The fuse report gains a `Split` section.

- **Model Workshop: the Vehicle Lab's list filters** — hide parts under a vertex count or a size, a height band, a
  side band across the beam, and a **Show only** popup (checked for Split, in a fuse group, not in one, more than
  one island, already whole, skipped — or any ONE fuse group, every letter in use is listed). The bands come from each node's world bounding box, read from the file's
  accessor min/max, so nothing slows the Probe. Marks on hidden rows are kept and Fuse/Split act on every marked
  row. The visibility filter stays in the Lab: it needs the Lab's escape-ray probe (the flat filter followed on 2026-09-18).

- **Model Workshop: Fuse writes a report file** beside the output GLB (`<output>.glb.fuse-report.txt`): per group the
  parts, the summary, every island's verdict and every stitched candidate's numbers, one item per line. The status
  box keeps one summary line per group and the report path — 711 parts in six groups had made it a wall of text.

- **Model Workshop: the preview zooms in twice as close** (the scroll-wheel floor went from 0.2 to 0.1 of the
  framing distance — the camera can now sit a fifth of the model's radius from it), so plating, laps and seams
  can be judged up close before choosing what to fuse or cut. And **the window scrolls**: header, part list,
  the 600-px preview and the Split/Fuse controls overflow a short window, and the Fuse row was simply cut off —
  a vertical bar now appears when the content is taller than the window (the preview keeps its wheel zoom).
  And **fuse groups run A–Z** (were A–H): one letter per lifeboat is more than eight on a liner. And **the
  lap-strip warning fires only for laps**: it used to count vertex sharing alone, so a gunwale rail or a boat cover
  attached along its whole edge (100 % shared by construction) was flagged on every boat, nine warnings for three
  boats; a part is a lap only when its faces are parallel to the other part's faces AND their centres sit on them
  (within 1e-3 of the model's length — Object_8's strips sit at 1e-4, the boat covers half a metre off), and one
  warning per fuse names the parts. The console line "stitched parts: …" lists the numbers behind the verdict.
  And **↑/↓ keep the highlighted row in view** past a hundred rows: the list scrolled by an assumed 22 px per
  row, but "already whole" rows draw in the mini font and are shorter, so the estimate drifted and the highlight
  walked out of the visible list; rows are now placed by their measured rectangles.
  And **the `–` key clears the group** like 0 and Backspace — it is what the popup shows for "no group".

- **Model Workshop: FUSE — weld a plated hull into one shell, at the source.** The Teutonic shipped see-through
  with a hole in its side: its hull plating is 861 disconnected islands over four parts, the per-island inside-out
  fix flipped some plates and not others, and any reduction opened gaps because plates share no vertices. Each
  Workshop row now carries a **⊕ fuse-group letter** (popup, or keys A–H on the highlighted row; ↑/↓ move it, 0
  clears), and **Fuse … into one shell each** turns every group into ONE mesh in the output
  GLB, seam vertices welded within a dial (‰ of the model's length; **default 0** = exactly coincident positions),
  the winding made **consistent by majority** across each welded island, and direction judged once where it can
  be — open sheets by the inside-out score, closed shells by their signed volume. Pure C# in
  `GlbDisconnectedParts.FuseNodes`, 10 tests (the inverted plate, the inward deck, the inside-out box, the UV
  seam, materials, the weld distance, parent transforms, collapsed faces, refusals, vertex normals). The rule came
  out of a headless Blender drill on the real hull the same day, and the C# port was then run on the same hull:
  the hole is a **1,613-face plate region glued on the wrong way round along a 26-edge seam** inside a 4,013-face
  island — 99.6 % of edges read consistent, so no per-island or edge test could see it; parity propagation finds
  it, and the two small shells authored inside-out (126 and 76 faces) are reversed whole by their signed volume.
  Three rules were tried and rejected: a blind normal recalc (flipped 40 % of that consistent island — overlapping
  plates are not the manifold solid it assumes), the radial score on closed shells (inner faces cancel outer; it
  reads ~0), and a non-zero weld by default (0.5‰ collapsed 1,564 rivet-sized triangles and, stripped of their
  adjacency, they broke the hull island apart — the plates already touch exactly; a distance is for gapped
  sources only, and the result line reports what collapsed). Two more came from the first real use: **"0" means
  coincident within float rounding, never bit-identical** — parts carry different node transforms, so one seam
  point computed through two matrices differs at the 1e-6 level and the eight hull parts stayed eight islands
  ("still separated"); and **a lap is not an inverted neighbour** — `Object_8` is 671 riveted strips lying ON the
  plates, stitched to them, authored facing the same way, and in manifold terms a face folded back over its
  neighbour must face the other way, so the plain rule turned every strip inward and they rendered as dark
  lines along the strakes; same traversal AND agreeing authored normals now reads as "on the same side on
  purpose" (532 of 656 strips with their plate after the fix). Fixing the source means the Lab, the Factory and the
  static bake all see a whole hull; a Vehicle-Lab-side version was built first and dropped in favour of this one.
  Review of the PR then found three more, all fixed and each with a test: **the signed volume was judged about
  the origin** for any island under 30 % boundary edges — an open surface's signed volume is the cone from the
  origin over it, so it reads where the sheet sits, not which way it faces (a correct 5×5 deck under y=0 came
  back all 50 triangles reversed); it is now judged about the island's own centroid and trusted only where the
  per-face cones agree (|Σv|/Σ|v| > 0.5) and there is real thickness (|volume|/area^1.5 > 0.01 — plating regions
  read 0.04–0.22, a lap strip 0.001, a flat sheet 0), a flat sheet falling to the inside-out score; a "closed
  = no boundary edge" rule was tried first and lost the Teutonic's 3,907-face plating island (21 % boundary,
  a thin solid the radial score cannot see) — its side plating fell from 96 % outward to 78 %, the agreement
  rule keeps it. **Welded vertices kept their own positions** where a UV seam or hard edge made them separate
  output vertices, so two plates a gap apart inside the weld reported one island and still showed the gap;
  every vertex of a welded class now sits at the class centroid. **The Workshop's Fuse button wrote the output
  itself** and only asked the ordinary "overwrite?" — output == source now refuses like every file entry point.
  And a fourth found by the drill on the port side: **a mirrored instance arrived inside-out** — glTF renders a
  negative-determinant node's triangles with the front face reversed, and the Teutonic's port half is the
  starboard meshes under a (0.0254, −0.0254, 0.0254) node; the winding is now swapped at gather, so the port
  hull is judged as it renders (its 9,379-face island is kept at +0.93 agreement, 2 small islands reversed
  instead of 36) and a port piece mixed into a starboard group no longer needs "correcting". Second review
  round, two more with tests: **vertex colours and second UV sets were dropped** (only POSITION / NORMAL /
  TEXCOORD_0 were written) — every other vertex attribute (COLOR_n, TEXCOORD_1.., custom) now rides along, takes
  part in the merge decision (a colour seam keeps its vertices like a UV seam), and a part without the attribute
  gets white / zero; TANGENT alone is dropped with a warning (it follows winding and UVs, both of which this
  pass may change; the importer recomputes it). And **normals went through the plain world matrix** — under a
  non-uniform scale a normal needs the inverse transpose (a (2,1,1) scale put a sloped normal 35° off its
  surface); the Teutonic's nodes are uniform (0.0254) so nothing changed there, but a scaled part now lights right.
  Third round, two more with tests: **one part without UVs stripped TEXCOORD_0 from every part of its material**
  — an unmapped contributor is now padded (0,0) and only a primitive no vertex of which had UVs ships without;
  and **the groupings sidecar restored by name alone**, so two parts called "Panel" with one saved in A both came
  back A — lines now carry `letter|name|node index` (`WorkshopRules.ResolveFuseSidecar`, 5 tests): a line applies
  at its node index, or by name where the name is unique, and a shared name is refused and named in the console.
  Old name-only files keep working where names are unique (the Teutonic's 1,435 mesh nodes have no duplicate).
  Fourth and fifth rounds: the sidecar is now **format v2** — a `#fuse-groups v2` header, then `letter|index|name`
  with the name LAST, so a `|` inside a name is never mistaken for a field (the name-in-the-middle layout could not
  tell `A|Hull|3` for a part `Hull` from one for a part `Hull|3` once a re-export moved `Hull`); the two older
  layouts still read, and where both of their readings fit different parts the line is refused rather than
  guessed; and a **cleared selection can be saved** — Save groups with no letters removes the sidecar,
  and the sidecar is read only when a file is first loaded into the window (a re-Probe or slider move of a file
  whose letters were cleared keeps them cleared; Load groups is the explicit way back).

- **Vehicle Lab: the ⟲ inside-out marks — see the fix's reach before it runs.** Every probed part the
  **Fix inside-out faces** pass would reverse now carries a ⟲ tag in the list (with its island count), and
  the checkbox itself reports the total — whether the fix is on or off. The verdicts come from the SAME
  island scoring Generate uses (average face normal against the radial from the hull's length axis, < −0.25
  = interior-facing), computed at Probe in ~1s and shipped as a new `PART|` field; roles the fix skips
  (Sail/Oar/Flag/Rudder/Preserve) show a struck-through tag instead. Two stated approximations: the probe
  judges against the whole model's axis (Generate re-judges each merged role mesh against its own — identical
  for the dominant Body pool) and in the current orientation — re-Probe after straightening for exact
  verdicts. Old probes show "re-Probe to classify", like the visibility verdicts did.

- **Vehicle Lab: "Only flat parts" filter — the deck finder.** "Which Object is the deck?" on a 1,435-shard
  liner used to be a headless-Blender analysis; now it's a slider next to the height/side filters. It hides
  parts whose surface area is less than the dialed % LEVEL (within 30° of horizontal — the Workshop facing
  cut's tilt), measured on the preview meshes with geometric triangle normals and cached per probe. Slide to
  ~60: walking decks, platforms and hatch tops remain while masts, plating and rigging vanish; bracket with
  the height sliders to isolate one deck level. Parts the preview can't measure always stay visible — a
  filter must never hide what it cannot measure.

- **Vehicle Lab Generate: batched decimate — a 1,435-shard liner's reduce stage drops 67s → ~3s.** The
  morning's chunked dissolve fixed the flat-panel whale; the RMS Teutonic exposed the OTHER one: the reduce
  loop called `bpy.ops.object.modifier_apply` per part, and every `bpy.ops` call is an operator round-trip
  plus a whole-scene depsgraph sync — ~45 ms × 1,435 BODY shards. The loop now just attaches each part's
  DECIMATE modifier (plain API, no sync) and ONE depsgraph evaluation applies them all in a single parallel
  C pass, the evaluated meshes copied back with UVs and materials intact. Same modifier, same ratio —
  drilled on the Teutonic itself: 319.9s → 1.7s for the decimate phase headless (the editor run showed 67s),
  vertex totals identical to within collapse-ordering noise (±1 vert in 344k). Part lookup is a dict now too
  (the 1,435-name linear scan was the second-order cost).

- **District primitive ceiling auto-sizes.** A district mesh draws as at most 255 sub-particles × the layer's
  PrimitivePerParticleCount; `DistrictMeshDensityBoost` raised PPC on HAF's private layer clones, but as a
  static config int — a model needing ×40 with the config at 8 still clipped silently. The plugin now reads
  the injected FxMesh's own triangle count and sizes the boost itself (`ceil(tris / (255 × PPC))`, config as
  the floor; footprint decal layers keep the config-only behavior). Backed by the ceiling investigation that
  read the encode's IL: 8-bit particle count over a 24-bit start index — see District-Visuals for the map of
  all three engine ceilings (units / districts / terrain) and what remains hard in each.

- **Past the 16,320-quad draw ceiling — an over-ceiling static bake can split into multiple draw fragments
  (opt-in per model).**
  The engine draws at most 16,320 quads per FRAGMENT (255 sub-particles × 64 primitives; the stride is compiled
  into the pawn shader, so raising it shreds pawns — the density-boost post-mortem), and the overflow never
  rendered: the Bremen baked 51,072 quads and the game silently clipped two thirds of the ship. With the new
  **Multi-fragment split** checkbox on the Factory entry (default OFF — more fragments are more draw work, so
  going multi-fragment is a conscious per-model choice; off keeps the classic warn-and-clip behavior), a static
  bake over the ceiling BSP-splits the mesh into spatial chunks (`…_ModelMesh`, `…_ModelMesh_B`, …), each under
  the budget, one SkinnedMeshRenderer per chunk in the same prefab — the SDK's Skeleton bake turns each into
  its own mesh entry — and the plugin appends one FragmentEntry per overflow chunk at injection (our collection,
  the body fragment's own output layer and atlas, our root bone), patching the GPU pawn descriptor per
  definition exactly like the proven hand-prop append. Discovery is from the collection itself: a fitting bake
  ships no chunks and nothing changes; chunk letters are spatially stable across re-bakes; the E5 rollback,
  Remove and re-bake sweeps all know the chunk assets. A split bake stays LOUD: because every chunk now says
  "fits", the quad report adds a **multi-fragment budget warning** (console + dialog) naming the total — "N
  quads across K fragments, K.Kx the normal per-unit budget" — so a heavy unit never ships quietly. The console
  `BAKED MESH` line reports every chunk, and
  `[Uni][Multi]` log lines name each appended fragment at load. Headless bake test: a 70,844-tri grid must
  split into fitting chunks with the triangle sum preserved and one skeleton mesh entry per chunk. Animated
  bakes do not split yet (their ceiling remains hard); up to 8 fragments of static budget. Review P1 hardened
  the budget itself: the SDK pairs only triangles SHARING AN EDGE into quads (a Faceted bake pairs nothing —
  quads == tris — and even a welded hull paired at ~0.6 quads/tri, not 0.5), so chunks are sized by an
  SDK-style pairing ESTIMATE rather than tris/2, and after the skeleton bake the SDK's real per-chunk quad
  counts are verified — a chunk still measuring over fails the bake (E5 restores the previous outputs) instead
  of shipping geometry that would silently clip.

- **Model Workshop: Plane cut — split a CONNECTED part in two.** Island splitting is helpless against the
  ocean liner's Object_45: hull and deck are one welded mesh (1 island), one Vehicle Lab row, one role. Select
  a part's row and press **Plane cut**: pick the axis (Y = horizontal deck-off-hull cut in a standard glTF,
  X/Z = vertical) and slide the position — the preview swaps to that part alone, two-colored **from the source
  file's own bytes**, so the yellow triangles ARE what becomes `_CutA` and grey `_CutB` (no Blender round-trip,
  no axis-convention guesswork). The cut is the island splitter's lossless mechanism with a different partition
  rule: WHOLE triangles assigned by centroid side, vertex data byte-identical, only filtered index accessors
  appended, triangle totals verified — nothing is sliced, the boundary follows the existing triangulation
  (invisible once each half takes its own role or reduce dial). Node transforms are honored (the plane lives in
  world space), morph-weight animation retargets to both children, a second node sharing the cut mesh keeps the
  original and is warned about. Chain cuts by pointing Source GLB at the output and re-Probing. A second **cut
  rule — Horizontal surfaces** — partitions by FACE ORIENTATION instead of position: a triangle joins `_CutA`
  when it lies flatter than the Max-tilt dial (undersides count — a ceiling is as level as its floor), with an
  Only-above height floor so the equally-horizontal hull BOTTOM stays in `_CutB`. That's the deck-vs-bow-plating
  split no flat plane can trace. Drilled on the Bremen: plane cut 61,232 triangles → `_CutA` 29,126 / `_CutB`
  32,106 in 0.8s; facing cut caught exactly the 1,447 level faces above the floor — both verified in Blender.

- **Vehicle Lab Generate up to 6x faster on flat-panel-heavy models — chunked limited dissolve.** The
  source-side reduction's limited dissolve joins coplanar faces region by region, and its cost is quadratic
  in region size: game rips triangulate a big flat deck into ONE region, so the OceanLiner's 23k-vert
  deckhouse alone took 39 seconds and the whole Generate 73s (71s of it in this single Blender op, measured
  headless). Parts above 4,000 faces now dissolve in spatial chunks (BSP-split at the face-centroid median;
  only cell-interior edges dissolve) — linear cost, same panels flattened; the thin lines of seam verts left
  across flat regions are exactly what the collapse pass already trims when the dial asks for more. The
  OceanLiner's reduce stage drops 66s → 13s; smaller parts keep the exact single-call behavior, and the
  timing print now names the stage (`VEHICLE timing: reduce`) so a slow Generate is diagnosable from the log.

- **ONE axis frame — the static bake now matches the animated one, no toggle, no legacy mode.** The static
  path used to ingest a GLB's Y-up vertices raw into the Z-up baker world and then auto-align the longest
  axis by guess — so the same file needed a different Rotation per path (the Lembos arrived upside-down on
  one and level on the other). A first attempt shipped this as a per-entry "Unified axis (v2)" toggle with a
  byte-identical legacy mode; the TOW acceptance test sank it (the flag didn't reach the bake, and a rigged
  Vehicle-Lab GLB extracted unassembled either way) and the ruling was one convention, period. Now: glbconv
  ALWAYS converts Y-up→Z-up (+90°X, normals included, winding preserved) **and evaluates skinned sources at
  their bind pose** (joint × inverse-bind per vertex — a Vehicle-Lab rig extracts assembled and upright, the
  TOW tripod under its launcher instead of scattered), the longest-axis auto-align is gone, and Rotation is
  the only orientation knob with the same meaning on BOTH paths — including at nonzero values: the static
  combine applies the registry fields exactly the way the animated path does (X = pitch, Y = heading/yaw,
  Z = roll, composed roll→pitch→yaw; review P1 — a plain Euler on the Z-up frame would have made Y a roll).
  The converter reads every JOINTS_n/WEIGHTS_n influence set and judges mirrored winding per vertex from the
  actual deforming transform (review P2/P3). Every cached extraction re-runs (cache stamp `v3`). **Breaking on
  purpose:** pre-existing static entries re-bake into the unified frame — re-dial their Rotation once
  (typically back to 0,0,0). Direct **.fbx/.obj** static sources (which never pass through the converter) get
  the identical Y-up→Z-up conversion applied in the combine instead, so the unified frame and the Rotation
  semantics hold for every source format. Field-verified on the TOW with cross-bakes at (0,0,0), (0,±45,0)
  and on the X and Z axes — every rotation field now faces static and animated identically (the last fix was
  a mirror conjugation: the static rotation applies after the importer's handedness flip, so yaw and roll
  negate internally; pitch, about the mirror axis, doesn't).

- **Flag fold — a Flag-marked part can FOLD at its top hinge instead of the naval strike.** Marking a land
  unit's stand/tripod **Flag** used to give it the ships' behavior only: mirrored below the keel while moving
  (the disappear-under-terrain strike). A new **Fold mode** checkbox in the Deploy section (with a fold angle
  −175..175° and a frames slider) re-homes the Flag bone's hinge to the TOP of the flag geometry and authors
  the `Furl` clip as a played fold — frame 0 deployed → frame N folded — while `Spin` holds the folded pose.
  Assign Pre-move `Furl[0..N]` / After-move `Furl[N..0]` and the unit folds before moving and redeploys on
  arrival, waiting for the fold like the howitzer's trails. Angle 0 in fold mode = the part simply stays
  deployed while moving; unchecked = the naval strike, byte-identical for every existing ship. Not available
  on the source-skeleton fast path (needs the generated Flag bone + Furl clip).

- **Idle/reference is `Furl[0..0]` on every flag/sail rig — and Auto-detect knows it.** The reference clip's
  frame 0 becomes the model's REST pose on the convert path, and a flag rig's `Spin` holds its strike/fold on
  EVERY frame (deliberately — movement must never flash the deployed pose mid-loop). Referencing `Spin[0..0]`
  therefore baked the hidden pose into the rest skeleton, and Auto-ground lifted the model by the struck
  part's depth (the sky-floating TOW). `Furl` frame 0 is always the deployed state, so it is the reference on
  any rig that has it: Auto-detect now recognizes a FLAG/SAIL rig and fills `Furl[0..0]` itself, and every Lab
  printout and HelpBox teaches the same. Wheel/rotor rigs (no Furl clip) keep `Spin[0..0]`, unchanged.

- **A material literally named 'Material' no longer scrambles the animated atlas.** The animated path pairs
  each submesh with its atlas cell by simplified material name — and simplifying strips the word "material",
  so a bare 'Material' became an empty string, which the substring fallback matched to the FIRST cell (every
  string contains ""). The SteamTransports' hull baked wearing the sails' canvas (white streaks) while its
  static bake — which pairs by object identity, not names — was perfect. Empty simplified names now skip name
  matching entirely and use the order-correct index fallback. Also from the same import: "Make static" no
  longer writes `deploySpeed`/`recoilSpeed` = 0 (the schema default is 1; the zeros produced two harmless but
  permanent per-bake validator warnings), and the registry floors both on every save, healing scarred entries.

- **The preview dropdown can show the Idle stance override.** "Why doesn't it hide the sails at idle?" — it
  did, in game; the Lab just couldn't show it: the stance override bakes to its own `anim_idle/` folder and
  the dropdown had no entry for it, so the only idle-looking view was the reference (deliberately DEPLOYED on
  a flag/sail rig). "Idle stance (override)" is now in the dropdown whenever that bake exists — the
  furled-at-anchor look is verifiable without launching the game. Auto-detect also stopped wiping a
  configured Furl idle stance: it can't guess one (a ship wants `Furl[N..N]`, a land flag wants it empty —
  identical rigs), but it now KEEPS what you dialed and says so in the status line.

- **Small fixes.** The Factory preview no longer shows a leftover animated rig after a static re-bake of a
  formerly animated entry (it fell through to the fresh static model only when the stale FBX was gone). The
  Animation section's probe re-runs when the model FILE changes in place, not only when its path changes (a
  regenerated GLB at the same path used to keep the section stale). "Make static" no longer scars the entry
  with `attackRepeats: 0` — the registry floors the value at 1 on every save, healing existing entries.

- **Per-source Brightness dials — merged models read as one unit.** The TOW launcher arrived several stops
  lighter than its tripod ("acts more like a whole unit rather than a patched model"): two sliders in the
  Second-model section (first model / second model, 0.25–2.5, default 1 = untouched) multiply each source's
  ALBEDO — base-color textures' pixels and textureless base colors, never normal/roughness maps — baked into
  the generated GLB in Blender, so the part preview, the probe and the Factory atlas all show the same tone.
  The first-model dial works with or without a merge. Drilled to the decimal: value materials measure ×0.600
  exactly at 0.6, and a 0.5-gray texture at ×1.6 measures 0.804 through the full export round trip.

- **The discard warning now knows whether you saved.** The Vehicle Lab's "discard the current session?" dialog
  fired on every switch, even straight after a Save. Dirty now means "differs from the last Save/Load": the
  window state is serialized and compared byte-identically against a clean-state snapshot taken at that
  moment (after the load guards run — see the review-hardening entry). A cleanly saved session switches
  silently; real unsaved changes say so explicitly.

- **Review hardening for the merge** (own 8-angle review + a convergent external one): the single-mesh
  loose split is now PER SOURCE (a combined-mesh first model kept splitting after a second model was set —
  both reviews' top finding); a typo'd second-model path fails loudly instead of crashing silently and
  wiping the probe's markings; the flatten's Icosphere purge also catches `B_`-renamed skinned bone-shape
  spheres from the second model; the discard dialog compares against a clean-state snapshot taken after the
  load guards run, so a guard-tripping recipe file no longer reads as eternally dirty; and geometry-producing
  modifiers (Array/Mirror/Solidify — `.blend` sources) are now BAKED after import, so they reach the part
  list, previews and output for the first time (they never did — the exporters always ignored them). The
  Icosphere artifact purge went from name-only to SIGNATURE + role protection (a real ball that kept the
  default name lists and survives; a marked part is never purged), and curve/surface/text objects are
  CONVERTED to meshes at import instead of being swept as helpers — they become ordinary markable parts
  (face-less wire paths are dropped; they render as nothing anywhere).

- **Two models can merge into one rig.** A collapsible "Second model" section (optional — most vehicles never
  need it) imports a second source into the same scene before the probe: its parts arrive with a `B_` prefix
  and take roles, reduce dials and rigging like any others. Offset / Rotation / uniform Scale place it against
  the first model (the scale dial reconciles cm-vs-m sources), and the Generate log prints the placed `B bbox`
  so alignment is dialed with numbers. The merge rides a tagged `merge2=` argument that both probe and rig
  modes scan, `.blend` seconds are rejected (opening one replaces the scene), and the fast path rejects the
  merge loudly. Drilled headless: Khalandion + half-scale Triconter at (0,40,0)/90° — 92 `B_` parts probed,
  bbox lands exactly at the dialed transform, full Generate exports the combined 549k-vert scene.

- **Sails can FOLD at idle instead of hiding.** A "Fold sail at idle" checkbox (shown when sails are marked)
  swaps the Furl stance's below-the-keel strike for the vanilla ships' brailed-up look: the canvas gathers
  into a visible bundle at the yard — an accordion pleat on a generated Sail→SailF1→SailF2 fold chain, the
  cloth band-skinned in height thirds and the fold bones zigzagged ±160° about the yard axis. Pure rotation
  by design (per-bone scale is the pipeline's measured AW101 trap), so the bake recipe and stance assignment
  are unchanged. Rigging keeps standing on the root Sail bone. Rejected loudly on the source-skeleton fast
  path. Drilled headless on the Khalandion: folded canvas = 0.38× raised height, top pinned at the yard.
  Follow-up ("moving too fast, in a single frame"): **Fold frames** (default 12) and **Fold angle** (default
  160°) dials — with frames the fold is the split-trail deployment mechanic on canvas: Pre-move `Furl[N..0]`
  lets the sail out as the ship gets under way, After-move `Furl[0..N]` gathers it on arrival, `Furl[N..N]`
  holds the idle stance; the legacy strike keeps its out-of-sight 1-frame snap. Two field passes reshaped the
  fold itself: first the chain grew to four bands (Sail→SailF1..F3) because parity decides where the bottom
  edge lands ("the underside should fold to the top of the beam"); then the alternating pleat became a CURL —
  "fold more how an open hand thumb and fingers close" — every joint bending the same way, the Curl dial the
  TOTAL roll (default 270° = 90° per joint). Measured at 270°: the canvas's foot lands 0.06 sail-heights ABOVE
  the beam, wrapped onto the yard like fingers on a palm's edge; bundle 0.39× raised, mid-frame 0.50 (the
  close plays through). A **Reverse curl direction** checkbox mirrors the roll to the other side of the sail
  plane — which way is "backwards" depends on the source model's facing, so it can't be auto-derived; the
  reverse roll curls against the billow camber and bundles a little looser (measured 0.53× vs 0.39×), foot
  at the beam either way. A **Sag (gravity)** dial (0–1) DRAPES the folded roll so it stops "behaving like
  in space" — first modeled as extra curl (field verdict: "it's just curling more"), now real drape math:
  gravity pulls every cloth segment toward hanging vertical, so the segment angles' horizontal component is
  squashed by (1−sag)² and the per-joint deltas re-derived (all folds share the yard axis, angles compose as
  scalars). Measured at curl 180: horizontal spread 3.89 → 2.60 → 2.05 across sag 0/0.5/1 — the floor is the
  canvas's own billow camber (rotations can't flatten a curved sheet), so sag 0.5 roughly halves the
  protruding roll and 1.0 hangs it flat; the foot stays at the beam throughout.

- **The Era Lab grid can be switched off without losing it.** A new "Apply era ageing" checkbox saves as
  `eraGridEnabled` in the pack: unchecked, the grid stays authored (and editable) but the runtime treats
  every cell as 1.0 — units keep their Resize Lab scale in every era. Absent key = enabled, so packs saved
  before the toggle are unchanged. F8's resize overlay says when a grid is authored-but-disabled instead of
  reporting "0 rows" as if none existed, and the load report names a disabled pack's grid.

- **The Animation Lab jump unlocks only after Save or Bake.** The Factory's "Open / Edit in Animation Lab"
  buttons handed the Lab a form the registry had never seen — the Lab (which edits the SAVED entry) then
  opened on nothing or a stale namesake, and the two windows fought over who defines the entry, whoever saved
  last clobbering the other. The buttons now grey out until the entry exists in the registry and the form
  matches it, with the reason in the tooltip and the detection notice.

- **Rigging rides the Sail bone.** When sails are marked, Rigging-marked parts (halyards, sheets, stays) weld
  to the Sail bone — struck below the keel WITH the canvas at idle, raised underway — but stay **single-sided**
  in their own mesh (never doubled with `Mesh_Sail`). Without marked sails, rigging welds to the hull as
  before.
- **Blade roll is mirror-correct across the banks.** Two rounds: the roll axis came from the raw PC1
  (arbitrary sign), so one dial could roll opposite banks' blade faces opposite ways (measured on the
  Triconter: sheet tilt 89° starboard vs 49° port). The first fix outboard-canonicalized the axis alone —
  measured still asymmetric, because the mirror of "roll +θ about an axis" is "roll **−θ** about the
  mirrored axis" (rotation conjugation). Final rule: outboard-canonical axis AND the angle carries the side
  sign, making the banks true mirror images.
- **A Generate can be pure geometry surgery.** The gate required a spinner, oars, or wave rock — a static
  ship needing only Flip, a facing fix or reduction couldn't Generate at all. Geometry work now satisfies the
  gate on the mesh path (drilled: a motion-less rig exports cleanly, `Spin 0..50 0 deg`); the fast path is
  unchanged (geometry work is inert there and says so).

- **Both oar banks finally mirror.** The rowing stroke's dip/lift axis derived from each oar's raw PC1
  direction — whose sign is arbitrary — and the outboard normalization ran *after* the axis was taken. A bank
  whose directions converged inboard (the Triconter's port side) got its lift inverted: blades riding a full
  2× lift below the other bank, pointing at the seabed, while the sweep stayed correct. The outboard fix now
  runs first, so the dip axis mirrors per bank by construction. (The Khalandion was unaffected — its raw
  directions happened to converge outboard on both banks, measured.)
- **Flip — a winding-surgery role** (hotkey `F`; Flag stays dropdown-only). The marked part's winding is
  reversed **once** at export, applied on top of the inside-out fix as an XOR **per island**: islands the fix
  flipped wrongly (a curved deck, a stern overhang — surfaces the belly-radial test mis-judges) land back on
  their authored winding, and with the fix off (or on islands the fix left alone) the mark alone reverses
  them. A part whose islands got *mixed* fix verdicts can't be fully repaired by Flip — split it in the
  Workshop first, or use Rudder (double-sided) there. Own **Flip reduce (%)** dial; mesh rig only (the
  source-skeleton fast path refuses it loudly, and the whole Vertices-control section now says when the fast
  path makes it inert).
- **Preserve gains an opt-in reduce dial.** **Preserve reduce (%)**, default 0 — which keeps the role's
  original byte-identical promise (and is what every older recipe loads as). A non-zero dial opts that one
  mutation in; the other exemptions (no winding fix, no doubling) remain unconditional. The Generate log
  states which mode ran.
- **Detail — a plain reduction tier of its own.** **Detail reduce (%)** for ornament/trim geometry that wants
  a dial between Structure and Body; welds to the hull like Body, no exemptions.

## 0.5.6 — 2026-09-07

- **External review hardening (three finds on this release's own fixes).** A kept failed-restore backup is no
  longer destroyed by simply retrying the bake — each bake attempt backs up into its own unique directory, so
  an unresolved recovery backup survives until you delete it. The Workshop's output path now *tracks* the
  source file while auto-derived (typing a source no longer freezes the output at the first keystroke's
  fragment); editing the field takes ownership. And the Factory's Browse unit-scale guess disarms the moment a
  save or bake persists it, closing the window where a stale guess could overwrite a newer Lab-saved value.
- **Recipe loading: absent-key defaults have one source of truth.** The load path carried hand-written
  fallbacks for keys missing from old recipes, justified by a comment claiming JsonUtility ignores field
  initializers — measured false (Unity 2021.3.1f1 batch probe): initializers DO run, and absent keys keep
  them. The duplicated fallback constants (a silent-divergence hazard) are gone; the DTO initializers alone
  define what an old recipe loads as. No recipe loads differently — every removed fallback equaled its
  initializer.
- **The source-skeleton fast path says NO instead of silently doing nothing.** The fast path spins **Wheel**
  bones only — but the Generate gate accepted Rotor / Tail rotor markings and Wave rock on it, the script
  parsed and ignored them, and the result was "RIG DONE" with nothing moving. Both roles and wave rock are now
  rejected loudly on the fast path, in the window (gate + warning boxes with the workaround: mark the spinning
  source bone as Wheel, or disable the fast path) and in the script (hard error, like the existing Oar guard).
- **Typing a different source path into the Workshop resets the probe.** Only the Browse button cleared the
  part list — typing or pasting another file's path kept the previous probe's rows, checked node indices,
  preview and output path live, so Split would carve the *new* file by the *old* file's node indices and write
  over the old file's `_split.glb`. Any source change (however entered) now clears the probe state and asks
  for a fresh Probe; the output path re-derives from the new file.
- **The Workshop's distance merge recognizes diagonal dashes.** The direction gate — which stops two parallel
  dashed lines from fusing into one part through a near crossing — judged "elongated" by the axis-aligned
  bounding box, so a dash at 45° read as a blob (two equal extents), skipped the gate, and parallel diagonal
  rigging lines merged into one part. Elongation is now measured in the island's own frame (principal-component
  aspect, rotation-invariant); axis-aligned models behave exactly as before. Locked by a regression test that
  is the original gate fixture rotated 45°.
- **Browse's "Fix 100× oversize" auto-guess now actually reaches the bake.** The guess is a Lab-owned field,
  so on an already-saved entry the Factory's ownership rebase silently reverted it right before baking — the
  status line promised "carried by the next Bake" while the bake ran with the old value (a 100×-giant or
  floating result on metre-scale rigged GLBs). The guess now stays armed through the rebase until a Bake or
  "Save settings" persists it, after which the Animation Lab's checkbox owns the field again; picking another
  entry or using Make static disarms it.
- **The Factory's "Save settings" can no longer write dead baked-asset GUIDs.** The ownership rebase carried the
  *form's* copies of the skeleton/atlas/clip GUIDs into the save — but a Lab rebake of the same entry regenerates
  those GUIDs without refreshing an open Factory form, so a later "Save settings" wrote the old, dead ones next to
  the live role clips (unresolved-GUID warnings; the unit stopped injecting until the next bake). Baked GUIDs now
  always come from the registry's saved copy, and the button runs the save path that restores the full GUID family
  (which also brings the richer status line: what applies on load vs what still needs a Bake, and a note when the
  Model file differs from the last bake).
- **Cutout transparency survives on every atlas path, not just one.** The fix that kept alpha-mask foliage
  intact (transparent texels preserved, DXT5 chosen at compression) had landed only on the static
  multi-material branch — the animated multi-material path and the shared single-material path still forced
  every texel opaque, flattening cutout cards into solid triangles. All four paths now run the same detection
  (>1% transparent samples = intentional alpha); fully opaque sources bake byte-identical to before.
- **A failed albedo extraction now fails the bake instead of shipping a flat-grey model as a success.** On the
  animated path, the stale-extraction cleanup deletes every extracted albedo before re-running glbconv; when
  glbconv then failed (missing dotnet, a broken GLB), the bake logged one warning and carried on to a grey
  atlas, a green "Baked" toast, and an updated registry. The extraction failure is now a bake failure with the
  cause and the fix in the error text. (The static path already failed properly.)
- **"Reuse extracted files" now actually protects a single-material model's hand-edited albedo.** The
  protection (and the freshness test) hinged on the extraction's MTL file — which glbconv writes only for
  multi-material sources. A 1-material GLB therefore read as permanently stale: every bake deleted
  `<name>_albedo.png` (hand-edits included, the exact loss the checkbox prevents) and re-ran the extraction.
  Freshness is now judged by the extraction stamp, which both shapes get, and the checkbox protects whichever
  extraction shape exists. Keeping an extraction that no longer matches the source warns instead of staying
  silent.
- **A failed re-bake restore no longer destroys its own backup.** The rollback path deletes the current outputs
  before copying the backup back; if that copy then failed (a file locked by antivirus or an indexer), the cleanup
  still wiped the backup directory — old assets gone, partial new assets gone, backup gone, git the only recovery.
  The backup is now discarded only after a **successful** restore; on a failed one it is kept and the error names
  its path with copy-back instructions.

## 0.5.5 — 2026-09-04

- **Rowing — a galley oar bank, animated from merged meshes.** A new **Oar** role (hotkey `O`) in the Vehicle Lab.
  A galley's oars usually arrive as a few merged meshes — poles in one, blades in another (often split front/back) —
  each holding *every* oar across *both* banks. Mark those meshes Oar and, uniquely among the roles, one marked mesh
  becomes **many** bones: the rig recovers each individual oar (projecting the geometry onto the plane perpendicular
  to the common pole direction, where the oars separate cleanly — naive distance clustering fails because the poles
  converge at the oarlocks and fan to the blades), gives each a bone at its oarlock, skins it rigid, and bakes a
  unison stroke into `Spin` — a fore-aft **Sweep** about the oarlock plus a phase-locked **Dip** (blades drop on the
  aft drive, lift on the recovery), a seamless loop. The new **"Oars — a galley rowing"** section exposes **Sweep**,
  **Dip**, and **Stroke frames**, tuned against the preview loop. Adds one bone per oar (~60 on a full galley), well
  within the skeleton budget. The oars row whenever the movement clip plays. Validated headless on a 64-oar galley.
  Recovery tolerances and bank centre are derived from the marked geometry, so uniformly scaled or translated source
  models behave the same. When wave, wheel, and rowing periods differ, each motion is fitted to a whole number of
  cycles over the shared `Spin` range instead of freezing at its last key. Older recipes migrate to the rowing defaults;
  source-skeleton fast-path generation now blocks Oar roles with instructions to probe the mesh parts instead.
  Marked Oar meshes are excluded from BOTH double-siding paths, the global checkbox included — galley blades ship
  as authored front/back sheet pairs, and doubling them z-shimmers four near-coincident layers.
- **Fix inside-out faces — a targeted winding repair, not a blunt recalc.** A source whose side planking ships
  wrong-way-out (the Khalandion) reads see-through from outside while showing the far wall's interior. The new
  Vehicle Lab checkbox reverses the islands that provably face the hull's interior (judged against an axis through
  the hull *belly* — a bbox centre gets dragged to mast height and mis-judges the deck); everything the test cannot
  call decisively keeps the artist's winding, as do marked **Sail** and **Oar** meshes (a whole-model `Shift+N`
  recalc, and then a sheet-detection heuristic, were both tried and rejected — each flipped or missed authored
  surfaces). No extra triangles; weights and UVs untouched. Verified with backface-culled renders: deck solid from
  above, hull solid from both beams.
- **Sail — marked canvas, double-sided, switched on/off via its own `Furl` clip.** A new **Sail** role
  (dropdown; the `S` hotkey marks Structure): explicit marking replaces sail auto-detection outright. All sail parts weld to one `Sail` bone and are
  **always exported double-sided** (canvas reads from both tacks, artist winding untouched). The hide is its own
  generated **`Furl` clip** whose frame 1 **flips the canvas 180° below the keel** — rotation-only, the same
  Deploy-proven stance mechanism the trails use — used as a **stance, never played**. The clip format carries no
  visibility or alpha, so out-of-sight is the only disappear it can express; the clean on/off comes from never
  playing the move. (Four designs were rejected in the field: the hide keyed inside Spin's frame 0 twitched at
  every loop restart; a 12-frame visible descent read as the sail sinking through the deck; a 1-frame drop showed
  travel when the transition was played; and a translation-based stance fought the converter's rest-fold and
  location-strip and shipped misplaced in both clips.) Assign: Idle/reference = `Spin[0..0]` (defines the rest —
  never put `Furl` in the reference field, or the bind adopts the struck pose), Idle stance (override) =
  `Furl[1..1]`, Movement = `Spin`, **After-move / Pre-move empty** — the state change swaps the pose in one tick.
  Keep bone translations can stay OFF: the strike is pure rotation.
- **Blade roll — square feathered blades to the water.** Some sources model the oar blades *feathered* (flat face
  parallel to the stroke), so they knife through the water edge-on instead of scooping. The Oars section's new
  **Blade roll (deg)** spins each recovered oar about its own long axis in the rest geometry — the cylindrical
  pole shows no change, only the blade face turns, with no seam at the blade root. 0 (the default) leaves the
  source untouched; the Khalandion wants 90. Measured: the blade sheet normal turns by exactly the dialed angle.

- **Rigging — selective source-side decimation for rope geometry.** A new **Rigging** role: dense line/rope
  meshes are often a model's single biggest vertex sink while being barely visible at game distance (the
  Khalandion's ropes alone: 65k verts). Mark them Rigging and the new **Rigging reduce (%)** dial
  collapse-decimates exactly those parts at Generate, at the source — the previews, the clustering, the winding
  fix and the bake all see the slim mesh, and the Factory's global *Reduce to ~tris* budget stops being spent on
  invisible ropes. Per-part before/after vert counts are printed so an over-aggressive dial is loud, not silent.
- **Structure — a second reduction tier with its own dial.** Small-but-dense detail geometry (railings, a carved
  bow figure — another 65k verts each on the Khalandion) is more visible than rigging, so it takes its own,
  usually gentler **Structure reduce (%)**. Same dissolve+collapse treatment at Generate; both tiers print
  original → dissolved → final against the dial's target.
- **Body reduce (%)** completes the tiers — parts explicitly marked Body, default 0 (untouched: the hull is the
  model's face, and the Factory's global *Reduce to ~tris* is usually the smarter place to slim it).
- **Oar reduce (%) / Sail reduce (%)** extend the tiers to the animated roles (a galley's merged oar meshes are
  dense; sail canvas ships twice, once per side). Both run in the same pre-armature pass, so the per-oar
  clustering, bones and weights land on the slim mesh — verified identical cluster recovery at 0% and 50%.
  Defaults 0. Note the dials are floors: the dissolve pass can overshoot on flat canvas (a 30% sail dial cut 66%
  on the Khalandion), so any non-zero sail value already cuts hard.
- **Save can no longer silently eat a tuned recipe.** A reopened Vehicle Lab window starts with default knobs
  while still naming the saved recipe — one reflex Save then replaced a tuned rowing stroke with defaults.
  Overwriting a recipe this window has not read since it was opened now asks first, and every overwrite leaves
  a `<name>.json.bak~` backup beside the recipe (the `~` keeps Unity from importing it) — a confirmed mistake
  is one rename from recovered.
- **The Factory reports every baked mesh's quad count against the engine's draw ceiling** — Humankind renders a
  unit mesh as at most 255 sub-particles × 64 primitives = **16,320 quads**, and the overrun is silent in-game:
  the mesh stores fully, the last-baked parts (masts, rigging, sails) simply never draw (how the Great Galley
  shipped mastless through five bakes). After each Bake the console prints per-mesh `N quads — fits (M to spare)`
  or a warning naming the excess, and an over-ceiling bake also raises a dialog. Dial *Reduce to ~tris* against
  this line — no game launch needed to know.
- **The part filter + marking list is a foldable "Parts" section** — collapse it once the roles are decided to
  reach the preview and tuning sections without scrolling past 280px of rows; the header keeps the part count
  and how many are still undecided. (Folding it also parks the keyboard review loop.)
- **All five reduce dials + the two facing checkboxes now live in their own "Vertices control" section** —
  everything that reshapes exported geometry at Generate in one foldout, out of Spin where it had no business.
  The collapsed header summarizes the active facing fixes and each marked tier's dial.
- **Flag is now the OPPOSITE of sails** (2026-09-05): banners fly AT ANCHOR and are struck below the keel while
  the ship moves — one Flag bone held flipped through the whole Spin clip; the idle stance shows them at rest.
  Rudder split into its OWN parts file to keep the always-visible treatment (a rudder must never vanish).
- **Flag — double-sided like a sail, but never hidden.** Banners and pennants must read from both sides, yet a
  flag keeps flying at anchor — so the new role gets the sail's doubling and winding protection without the Furl
  strike: no bone, no clip, welded to the body. **Rudder** shares the exact treatment under its own name — a
  closed slab with one face-side wound inward scores ~0 in the inside-out flip (the halves cancel), so no
  whole-island flip can repair it; doubling can.
- **Positive Sweep rows forward** (2026-09-05 sign flip): the field-verified forward stroke needed a negative
  dial while positive Rake already shifted toward the bow — the sweep sense flipped so both dials agree that
  positive points at the bow. Recipes saved before the flip negate their Sweep once.
- **Sweep accepts negative** — if the galley rows backwards, flip the sign (the wheels' Spin-degrees convention);
  the dip phase stays put, so blades still bury on the reversed drive. **Sweep is the TOTAL arc**, split evenly
  about the rest rake: 24 = 12° forward + 12° back (it was a half-amplitude before — a 24 dial swung 48°, all of
  it reading as "backward" against the Khalandion's already-aft modelled rake). Slider range widened to ±90.
  **Dip accepts negative too** — it flips which half of the stroke is submerged, the second independent way to
  reverse the rowing direction (flip either Sweep or Dip, not both: both flips cancel). Keep |Sweep| above ~2× the
  dip, or the dip's fore-aft component on a raked oar drowns the sweep and the stroke churns instead of pulling.
- **Lift (deg)** re-centres the stroke height — a constant tilt about the dip axis with the dip oscillating
  around it. The knob the dip sign cannot be (±dip is the same oscillation, phase-flipped): a source whose oars
  are modelled raked steeply into the water rides too deep at any dip; positive lift brings the bank toward
  horizontal. Measured: lift 30 raises the Khalandion blade path ~0.5 units, deepest point 0.45 shallower.
- **Rake (deg)** — the horizontal twin: a constant fore/aft rotation about the oarlock re-centring the sweep
  ARC, with the sweep oscillating around it. A source that models the oars raked far aft (the Khalandion: ~50°)
  swings "all backward, nothing forward" at any sweep, because the arc is symmetric about that modelled rake;
  rake it toward the bow until the stroke straddles the perpendicular. Same sign convention as Sweep.
- **Pivot (%)** — where the oarlock, the fulcrum every stroke rotation happens about, sits along each oar
  (percentage of its inboard→outboard extent). 0 = at the handle, the whole oar swings; higher = further out,
  less oar moves and the handle counter-swings more. 30 was the hardcoded value through the whole build.
- **Length (%)** — stretches each oar along its own axis ABOUT the oarlock: the pivot stays planted at the
  hull, the blade reaches further out and down, the handle further in. Pure axial scale — blade width and pole
  thickness untouched. 100 = the modelled length.
- **The Animation Lab preview floats boats at the calibrated water level.** It drew its hex at ground height
  (-0.02) even for boat pawns — water-blue in colour, ground in height — so the Factory showed oar blades in the
  water while the Animation Lab showed them dry. Both panes now share the pack's one-source-of-truth
  `ModelRegistry.WaterLevel`; the forward arrow and reference man ride the same plane.
- **Split disconnected GLB parts** — a new `Tools ▸ HAF ▸ Model Tools ▸ Split disconnected GLB parts…` command
  turns every disconnected geometry island inside a GLB mesh node into its own selectable child part. The source
  is never overwritten. Materials, transforms, skins, animation targets, textures and original vertex data are
  preserved; the tool appends only filtered index accessors and child nodes, verifies the triangle total, and writes
  a new `<name>_split_parts.glb`. Duplicate vertices at UV/normal seams are welded with a tight scale-relative
  tolerance, so a visually continuous surface is not split merely because its shading data has a seam.
- **Rudder reduce (%) and Wheel reduce (%)** complete the source-tier set at seven. Rudders ship double-sided
  (every kept vertex counts twice), and CAD-style rims carry far more vertices than a spinning disc shows;
  both cuts run before doubling/clustering. Each reduced part keeps at least 8 verts, so triangle-soup wheels
  cut less than the dial asks — the Generate log prints the real totals.
- **Rolling-contact wheel speeds apply only to wheels that reach the ground.** Four propellers marked Wheel on
  a biplane: one spun at 540° because the 1/diameter ground-rolling scaling hit something hanging at wing
  height. A wheel cluster now scales only when its bottom lies within 15% of model height above the global
  minimum; airborne wheels (props, fans) keep the dialed degrees uniformly, and the log names the exemptions.
- **Preserve — a role that keeps a part exactly as authored.** No reduction, no inside-out flip, no doubling
  (the global Double-sided switch included); welds to the hull like Body but ships as its own untouched mesh.
  The escape hatch for parts every automatic pass keeps getting wrong.
- **Model Workshop — the aimed version of the splitter.** `Tools ▸ HAF ▸ Model Workshop`: Probe lists every
  mesh-carrying node with its triangle count and disconnected-island count; check exactly the parts hiding
  floating junk and Split writes a GLB where ONLY those become `_Part_NNN` children (same lossless method).
  Born from the galley: junk islands welded into hull-shared parts could not be marked Ignore in the Vehicle
  Lab, and the split-everything command exploded the rigging into ~1,500 parts. Feed the output to the Vehicle
  Lab, mark the junk Ignore, rig as usual.

## 0.5.4 — 2026-09-03

- **Double-sided for animated vehicles — now a Vehicle Lab option, applied at the source.** The engine culls
  backfaces, so a single-sided / CAD-style source (thin spokes, flat plates) renders see-through from the wrong
  angle. The Vehicle Lab gained a **"Double-sided (fix see-through parts)"** checkbox: when set, the rig export
  appends a reversed copy of every face to the Spin GLB itself, nudged slightly inward so front and back aren't
  coincident (coincident faces read ~50% transparent under the game's alpha-to-coverage shader). Bone weights are
  carried on the duplicated vertices, so the skeleton bake still validates. Because the fix lives in the source
  geometry, the rig, the preview meshes and the baked model are all the same vertex count — so it just works in
  the Vehicle Lab turntable, the Model Factory preview, the Animation Lab and in-game, with no runtime doubling
  and no preview special-casing. Doubles the triangle count; the Factory's **Reduce to ~tris** still caps the
  shipped mesh. (This replaced an earlier runtime-doubling attempt whose rig-vs-baked vertex-count mismatch caused
  a long string of preview glitches — half-rendered, grey, and partially-transparent models.)
- **The Model Factory's Double-sided checkbox is removed.** Double-siding for rigged models is a source-geometry
  concern owned by the Vehicle Lab; the Factory had no runtime doubling, so a checkbox there only did nothing for
  animated models. One fewer knob. (The static-bake path's own doubling and the `doubleSided` field remain for
  backward compatibility — existing entries bake as saved.)
- **The Animation Lab's runtime Position offset now shows on a *playing* clip too** — it was only applied to the
  static rest pose, so an offset model looked mispositioned in the Lab versus the Factory; and the domain-reload
  restore resumes the clip.

## 0.5.3 — 2026-09-02

- **A failed re-bake can no longer ship a mismatched normal map.** The output whitelist behind the E5
  rollback, the cross-path sweep, and Remove was missing the static path's three surface atlases
  (`_NormalAtlas` / `_RoughAtlas` / `_NormalAtlasPrev`) — so a re-bake that failed after packing them
  restored the *old* colour atlas next to the *new* normal atlas (different packing rects, silently wrong
  shading), and removing a model orphaned all three in the shipped Resources folder. Found by a critical
  review, not an incident.
- **The suffix list now exists exactly once.** Both bake-test cleanups carried their own hand-copies of that
  whitelist; the Tier-2 copy had already drifted (state-driven `_ClipsMove`/`_ClipsAttack`… outputs were never
  deleted, stranding throwaway fixtures in shipped Resources). Both now reference the baker's own list —
  a fourth place to update no longer exists.
- **A district that layers on a model's outputs is no longer swept in silence.** A district bake builds on a
  same-named model's `_Atlas` and overwrites the atlas trio with processed versions — so a model re-bake or
  Remove could yank those out from under it with no word said. The Remove dialog now names the layered district
  *before* the decision, and every sweep logs which district needs a re-bake afterwards.
- The E5 rollback test's fixture gained a `Textures/` normal map, so the restore assertion now covers the
  surface atlases too — the exact whitelist entries this release added would otherwise have stayed untested.

## 0.5.2 — 2026-09-02

- **Flat-colour swatches now actually load — no more red parts.** glbconv writes each untextured material's
  colour as an 8×8 `.tga` swatch, but both bake paths loaded albedos with `Texture2D.LoadImage`, which decodes
  only PNG/JPG: on the animated path every swatch silently became Unity's 8×8 **red** placeholder (the all-red
  Bell H-13 — this bug predates 0.5.0 and was the true root of the whole flat-colour saga), and on the static
  path swatches were skipped entirely, landing flat materials on the grey tile. Both loaders now share one
  decoder that reads glbconv's TGAs directly, and any file that still can't be decoded — or an MTL entry whose
  albedo file is missing — logs a loud `[Factory]` warning naming the file instead of baking a placeholder.

## 0.5.1 — 2026-09-02

- **Changing a model's source file can no longer bake against the previous source's extraction.** glbconv writes
  an MTL only for multi-material sources, so re-pointing an entry at a different file could leave a *chimera*
  extraction folder (old MTL + swatches, new stamp + albedo) that the next bake silently consumed — the first
  0.5.0 re-bake of the Bell H-13 sampled a leftover 256×32 palette strip from the previous source and came out
  dark chaos. On a source change every derived extraction artifact is now removed before re-extracting, each
  with its `.meta`, so Unity's refresh has no orphans to complain about (*Reuse extracted files* still protects
  hand-edited textures by skipping the refresh entirely).
- **A multi-material source baked with Material mode Single now warns**, naming the material count and the fix —
  before this the log said nothing while every part sampled one atlas whole.

## 0.5.0 — 2026-09-02

- **Flat-colour (untextured) multi-material models bake correctly — no external atlasing step.** A SketchUp-style
  model whose materials are pure colours (`glass`, `paint`, `copper`… with no texture) already got an 8×8 solid
  swatch per material in the packed atlas, but its submeshes kept their source UVs — which such models fill with
  garbage (islands parked anywhere, even outside 0..1), so faces sampled neighbouring rects (wrong colours) and
  part edges bilinear-sampled the padding between rects (grey fringes). The bake now pins every vertex of a
  flat-swatch submesh to the **centre of its rect** — one interior sample point, immune to bad UVs, seam folds,
  padding bleed and mip averaging. Applied on both the animated and static multi-material paths; the per-submesh
  bake log says `(flat swatch — UVs pinned to rect centre)` when it fires. Hand-editing an extracted swatch into
  a larger real texture returns that part to normal UV mapping automatically. (Driven by the Bell H-13: rigged in
  the Vehicle Lab, 10 flat materials, previously only bakeable after an external "flat-colour atlas" rebuild of
  the GLB.)

## 0.4.13 — 2026-08-25

- **A menu click is answered with a dialog.** Every outcome of `Check for Updates…` now shows one — *up to
  date*, *update already in progress*, *could not reach the repository*, *check failed* — because the person who
  clicked is looking at the menu, not the console. The daily automatic check stays a single console line, as
  promised.

## 0.4.12 — 2026-08-25

- Version-only bump: the live fixture for 0.4.11's in-flight latch. From an 0.4.11 install: *Check for
  Updates…* → *Update now* → click the menu again **during the fetch** — it should answer *"update to 0.4.12 is
  in progress"* instead of re-offering the update.

## 0.4.11 — 2026-08-25

- **No more stale "update available" during an update.** Between *Update now* and Unity's reload, the old
  version's code keeps answering the menu — Package Manager already shows the new version while the check still
  reports the old one, and a re-click re-offered an update that was already applied. The check now latches while
  a fetch is in flight (*"update to X is in progress — Unity will reload when it's done"*), unlatches itself the
  moment the running version matches, and a failed fetch clears the latch so checks are never wedged off.

## 0.4.10 — 2026-08-25

- Version-only bump: the live fixture for 0.4.9's one-click update. From an 0.4.9 install,
  `Tools ▸ HAF ▸ Check for Updates…` should show the *Update now* dialog for this release — the first update
  ever applied without opening Package Manager.

## 0.4.9 — 2026-08-25

- **The update check now applies the update.** `Tools ▸ HAF ▸ Check for Updates…`, on finding a newer release,
  offers *Update now* — one click hands the fetch to Package Manager (`Client.Add` with this install's own URL,
  the same operation as its Update button). The daily check stays a console line on purpose: an unrequested
  dialog on editor start is exactly the surprise this package promises not to be.

## 0.4.8 — 2026-08-25

- Version-only bump so the update check shipped in 0.4.6 could be verified live: an install on 0.4.7 should
  report this release via `Tools ▸ HAF ▸ Check for Updates…` and, within a day, unprompted in the console.

## 0.4.7 — 2026-08-25

- **This changelog exists**, and Package Manager's *View changelog* button now opens it. It pointed at a page
  that was never created (found by a user pressing the button — the URL was written plausible-looking and
  unverified).

## 0.4.6 — 2026-08-25

- **Updates announce themselves.** A git-installed package gets no update indicator from Unity — verified live:
  a new release, an editor restart and a Package Manager refresh all showed the installed version as current.
  The tools now check for themselves: once a day (installed packages only) they read `editor/package.json` from
  the package's own repository — one anonymous, read-only fetch, nothing sent — and print one console line when
  a newer release exists. `Tools ▸ HAF ▸ Check for Updates…` asks on demand. Disable with EditorPrefs
  `HAF.UpdateCheck = false`.
- Releases are now **tagged** (`editor-vX.Y.Z`), so an install can be pinned:
  `…?path=/editor#editor-v0.4.6`.
- Docs: `Installation.md` gained *Updating the tools* — updates never touch your authored data.

## 0.4.5 — 2026-08-25

- Version-only bump used to verify the update mechanism live (no code change).

## 0.4.4 — 2026-08-24

- **Bake-test progress you can actually watch.** Two bars inside the Bake Tests window (run level + step level),
  live during the synchronous run; the modal bar carries both levels and elapsed time; a 250 ms heartbeat keeps
  everything ticking during minutes-long Blender steps; and the run position rides in the throwaway fixture
  names (`__smoketest__03of14_…`) so even Unity's own *Importing…* dialog — which nothing can draw into — shows
  where the run is.

## 0.4.0 – 0.4.3 — 2026-08-24

- **The Blender helpers and the glbconv GLB/glTF importer ship inside the package** (`Tools~`). Before this,
  any `.glb` import — and every Blender-dependent bake — failed in an installed package because the helper
  scripts lived outside it.
- **Home vs. installed is decided by how the package is installed** (git/registry = consumer install;
  `file:`/embedded = the developer's working copy), not by whether it is one.
- Bake-test documentation: the three verdicts (SKIPPED is a first-class answer), the fresh-install baseline,
  and the what-needs-Blender table (`Factory-Manual.md` §11).

## 0.3.0 – 0.3.1 — 2026-08-24

- **Your pack is your own.** The tools read and write `haf_packs/<YourProjectName>` — starting empty — instead
  of a hardcoded pack. Before this, on any machine with ENC installed as a player mod, the tools showed and
  tried to re-bake ENC's models inside other projects. An installed package can no longer see or touch another
  mod's pack.
- **A clean install cannot fail a bake test.** Missing prerequisites (no models yet, no Blender) report SKIP
  with a named reason in an installed package; they stay loud failures in the development checkout.
- No ENC-specific names on an installed package's screens; neutral preference keys (`HAF.BlenderPath`,
  `HAF.BepInExConfig`) with the historical keys still read as fallback.

## 0.2.0 — 2026-08-24

- The package moves into the HumankindAssetFramework repository (`?path=/editor`) and ships its own
  `Haf.Schema.dll` — installing from the old location cloned 65 MB of another mod's content and then failed to
  compile for want of that assembly.
- **An installed package changes nothing until asked**: automatic backups, the asset-delete guard and console
  filtering all default off outside the development checkout, and a first-run console line says so.

## 0.1.0 — 2026-08-24

- First installable package (`package.json` + asmdef): `Window ▸ Package Manager ▸ + ▸ Add package from git
  URL…`. The first real install immediately found the `.meta` files missing (Unity generates them silently
  under `Assets/`, and cannot in an immutable package folder) — fixed, and gated so the class is extinct.
