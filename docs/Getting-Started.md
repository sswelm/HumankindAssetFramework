# Getting started

This is the shortest supported route from an empty project to a custom unit on the map. Follow it in order; use the
linked reference pages only when a step needs more detail.

HAF has **two halves, and you work in both:**

- **Author** — a **Unity editor** project (the Model Factory + Labs under `Tools ▸ HAF`) where you bake a 3D model into
  game-ready assets and register it against a vanilla unit.
- **Run** — **Humankind** with the **BepInEx** plugin, which injects those baked assets onto the unit at runtime.

The complete loop is:

| Stage | Where | Result |
|---|---|---|
| **1. Install** | Windows, Humankind, and Unity | BepInEx, the runtime plugin, and `Tools ▸ HAF` are available |
| **2. Bake** | Unity: Model Factory | Game-ready assets and a pack entry are written |
| **3. Build and deploy** | Humankind Mod Editor or `haf build` | The current assets reach the Community module |
| **4. Verify** | Humankind | F8 and `haf_load_report.txt` confirm what loaded |

Follow it in order. There *is* a second, editor-free route — hand-writing a `pack.json` for a retexture, tint or
sound swap — but that means authoring JSON against a schema by hand, with no UI and no validation as you type. It's
an advanced shortcut, not an easier start; it's described in [Multi-Mod.md](Multi-Mod.md) when you want it.

---

## 0. What you need

**[→ Installation.md](Installation.md) walks through all of it, step by step.** The short version:

| | | Get it |
|---|---|---|
| **BepInEx 5.4** | The mod loader the plugin runs inside. | [Download](https://github.com/BepInEx/BepInEx/releases) (x64, Windows) |
| **The HAF plugin** | Injects your baked assets into the running game. Not needed for projectiles. | [Releases](https://github.com/sswelm/HumankindAssetFramework/releases) · or [build it](Building.md) |
| **Unity 2021.3.1f1** | The exact version the Humankind ModTools SDK targets. | [Unity archive](https://unity.com/releases/editor/archive) |
| **The Humankind ModTools SDK** | Turns a Unity project into a Humankind mod project. | [Games2Gether modding](https://www.games2gether.com/amplitude-studios/humankind/modding) |
| **The HAF authoring tools** | The Model Factory and every Lab, as a Unity package. | [Installation §3](Installation.md#3-the-authoring-tools) |
| **Blender** (free, optional) | Needed for tri-reduction, part-stripping, `.blend` import, and animated bakes. Static GLB, glTF, OBJ, and FBX can bake without it. | [blender.org/download](https://www.blender.org/download/) |
| **A 3D model** | HAF is the *pipeline*, not an art library — bring a licensed model — **.glb preferred** (also glTF / OBJ / FBX / .blend). Prefer a **game-ready** model. | [Sketchfab](https://sketchfab.com/features/free-3d-models) — every model HAF ships came from there. Filter by **Downloadable** + a **CC** licence; **CC-BY** means free to use *with credit*. Record yours in [CREDITS.md](https://github.com/sswelm/HumankindAssetFramework/blob/master/CREDITS.md). |

> **Look for "game ready".** It is a real tag on Sketchfab and it is the single best predictor of a smooth bake: a
> low, sane triangle count, one or few materials, proper UVs and a baked texture. The opposite — a CAD or
> "sketch" model — arrives tessellated into hundreds of thousands of triangles, often single-sided, sometimes with
> no UVs at all. HAF *can* rescue those (a vertex reducer, a winding fix, height-based UVs, an N-material atlas
> packer), and the LCAC hovercraft in [CREDITS.md](https://github.com/sswelm/HumankindAssetFramework/blob/master/CREDITS.md) is one that had to be replaced outright by a clean
> remodel. Starting game-ready skips that entire class of problem.

---

## 1. Install the authoring tools

In your mod project: **`Window ▸ Package Manager`** → **`+`** → **`Add package from git URL…`**:

```
https://github.com/sswelm/HumankindAssetFramework.git?path=/editor
```

The windows appear under **`Tools ▸ HAF`**. You should see one white console line and **no errors** — nothing runs on
its own and your project isn't modified.

Full detail, and what to do if that isn't what you see: [**Installation.md §3**](Installation.md#3-the-authoring-tools).

---

## 2. Bake your first model (start static)

Open **`Tools ▸ HAF ▸ Model Factory`**, then:

1. **Model file** — Browse to your model. **Prefer `.glb`**: it is one self-contained file (mesh, textures, and
   optionally a rig) and imports through the shipped converter without Blender. Static `.gltf`, `.obj`, and `.fbx`
   files also work without Blender; `.blend` and every animated bake require it.
2. **Resource name** — one token, e.g. `MyTank` (letters/digits/`_`/`-`, **no spaces**).
3. **Pawn description** — **Pick** the vanilla unit your model replaces (its `PresentationPawnDefinition`).
4. Leave the shading/geometry defaults; set **Size** to roughly the unit's real scale.
5. **Bake.**

The console prints `Baked '…'` and the entry is written to the pack registry. Every field, the animated workflow, and a
troubleshooting table live in [**Factory-Manual.md**](Factory-Manual.md) — *the* guide for adding a model. Not sure HAF
can import *your* model? [**Animated-Models.md**](Animated-Models.md) answers that in three plain-language levels.

---

## 3. Build & deploy the mod

Baking writes assets into the project; the game loads them from a built **asset bundle**, so you package + deploy once
per set of changes:

> Unsure whether a later edit needs Save, Bake, Build, or only a relaunch? Use the
> [authoring-state action matrix](Authoring-State-and-Deployment.md#change--required-actions).

- **Headless (simplest):** `haf build` runs the full Mod Editor build + deploy from the command line. See
  [**Headless-CLI.md**](Headless-CLI.md).
- **In the editor:** run the Humankind Mod Editor build, then deploy — or use `Tools\haf-deploy.bat` (a pure file copy,
  no Unity) to push an editor build into the game.

The deployed module lands in Humankind's `Community` folder as a normal mod.

---

## 4. Launch & verify

1. Start Humankind, **enable your mod** in the mod manager, and load a game containing your target unit.
2. Press **F8** in-game for HAF's status window — compatibility health, the Smoke Test, and the live GPU-budget readout.
3. Open **`BepInEx/config/haf_load_report.txt`** — your pack should be listed with its model count and no conflicts.

Your unit now renders with the custom model. If it doesn't, the load report and the Factory's own failure messages name
the fix. Start from the symptom in [**Troubleshooting.md**](Troubleshooting.md); it routes installation, bake, bundle,
pack, texture, animation, district, sound, binding, and performance failures to their authoritative sections.

---

## 5. Go deeper

- **Animate a static vehicle/helicopter** — [Vehicle-Lab-Quickstart.md](Vehicle-Lab-Quickstart.md)
- **Import an existing rig or go deeper** — [Animated-Models.md](Animated-Models.md) · [Donor-Clip-Flight.md](Donor-Clip-Flight.md) ·
  [Turn-Ease.md](Turn-Ease.md)
- **Texture / reskin** — [Textures.md](Textures.md)
- **Other injection axes** — [districts](District-Visuals.md) · [pawn props](Pawn-Props.md) ·
  [projectiles](Projectiles.md) · [formations](Formations.md) · [unit size](Unit-Size.md)
- **Ship it for others as a pack** — [**Multi-Mod.md**](Multi-Mod.md): the `haf_packs/` drop folder, how packs merge, and
  the load order (which follows your Humankind mod order).

Welcome aboard.
