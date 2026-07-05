# Capabilities — what the Factory does (proven in-game)

The full, detailed capability list. The README carries the highlights; this is the reference. For *how to use* these
see the [Factory Manual](Factory-Manual.md).

- **Animated custom models — a first, now one-click.** A **quadcopter drone** injected onto a land-vehicle unit renders
  full-size and textured **and spins its own propellers from its own baked animation** — no engine mod, no GPU-skinning
  hang. Tick **Animated** in the Factory and a single Bake does it all: Blender slims the rigged model (keep armature +
  chosen clip, strip to the spinning bones, auto-clamp the frame range), then it bakes an Amplitude `Skeleton` +
  `ClipCollection` + atlas and writes the registry; at runtime the clip is registered and a `PawnManager.AddPawnEntry`
  hook drives the pawn's pose onto it — normalized by clip duration so it plays at real speed. Works for **any number of
  instances**. Clip/bone/hide-donor fields are **Pick-driven** (read from the model's glTF + the plugin log).
- **Multiple static models live**, each added with no new code: a **Zeppelin**, an **LCAC Hovercraft**, and a
  fully-textured **USS Zumwalt stealth cruiser** (first textured naval-combat unit) — correct orientation, correct skin,
  sitting at the waterline.
- **Match the donor to your model.** A model rides a donor unit's skeleton + animation, so pick a donor whose *moving
  parts* match yours: a custom **helicopter** body (modelled rotor-less) borrows the donor's spinning rotor for free; a
  drone/ground model wants a donor with **no animated sub-parts and a full idle/move animation set** (a land vehicle is
  ideal). The one thing injection **can't** do: *remove* a donor's *animated* sub-part (a rotor, spinning wheels) — those
  are baked into the pawn at spawn. But it **can** give your model **its own** animation (see the animated bullet above),
  which overrides the donor's. Choose the donor accordingly; see the drone case study in the docs.
- **Any number of materials.** A model with N materials (the Zeppelin has 4) is packed into one atlas and each
  sub-mesh's UVs are remapped into its rect — no per-model code, no material cap. Near-black UV dead-zones are filled
  neutral so unused texture regions never render as black patches.
- **Heavy or single-sided/CAD meshes, handled.** A built-in **vertex reducer** (Blender quadric decimation, per-object
  so thin parts survive) shrinks oversized models to fit the engine's shared mesh buffer. A **winding fix** rewinds
  faces outward so single-sided / CAD "sketch" meshes render single-sided instead of culling to invisible (e.g. a
  hovercraft skirt); a **double-sided** toggle is the heavier fallback for genuinely non-convex thin shells (a mixed
  model — convex hull + non-convex fans — can use both). And **height-based UVs** map a simple vertical-gradient albedo by
  height (black skirt low, grey hull high) so an untextured CAD model gets a usable skin without UV-unwrapping.
- **Know the ceiling.** Custom meshes share **one GPU buffer — ~100k vertices / ~250k indices (~83k triangles), 32-bit
  indices — across ALL injected models *and* the game's own fx meshes** (from the decompiled
  `FxComponentMeshContentManager`). Overflow is silently dropped (missing/see-through geometry); the reducer exists to
  stay under it, and double-sided counts twice.
- **Any format in:** GLB / glTF / OBJ / FBX, and **`.blend`** (auto-converted via an auto-detected Blender install).
- **Correct textures out of the box:** custom skins land right-side-up — the bug that put the Zumwalt's markings on the
  superstructure (a glTF-V-top vs OBJ/Unity-V-bottom mismatch) is reconciled during OBJ import, so every model's texture
  maps correctly. (Note: the `glbconv` converter itself writes UVs *raw* — it does **not** flip V; the convention is
  handled by Unity's OBJ import.)
- **Texture isolation:** each model gets a private `FxOutputLayer` clone, so its skin never bleeds onto the vanilla donor
  unit — proven on screen with a custom cruiser and its donor corvette side-by-side, each keeping its own skin.
- **Add a model = bake it.** The Factory writes the registry; the plugin picks it up on next launch.

## Known limitations

- **Editor-only texture preview:** right after a multi-material bake, Unity may show the baked atlas stale until the
  source textures are touched (open them in the Project view, return to the model). The shipped/in-game result is
  correct — a Unity editor texture-residency quirk, not a bake defect.
- **Multi-material GLB:** the GLB→OBJ converter currently flattens materials into one group, so multi-material *GLB*
  loses its per-material split. Use **FBX** for multi-material models (fully supported); GLB is fine for single-material.
