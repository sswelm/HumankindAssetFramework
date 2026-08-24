# Getting started

Your first custom unit, from nothing to on the map. This page is the **ordered path**; each step links to the deep
doc for detail. The rest of the docs are reference — read this first to see how they fit together.

HAF has **two halves, and you work in both:**

- **Author** — a **Unity editor** project (the Model Factory + Labs under `Tools ▸ HAF`) where you bake a 3D model into
  game-ready assets and register it against a vanilla unit.
- **Run** — **Humankind** with the **BepInEx** plugin, which injects those baked assets onto the unit at runtime.

So a custom unit is a round trip: **bake in the editor → build & deploy the mod → launch the game → see it.**

> **Just want a retexture, colour tint, or sound swap — no new mesh?** You can skip the editor entirely: hand-write a
> `pack.json`, drop it in `haf_packs/`, done. Jump straight to [Multi-Mod.md](Multi-Mod.md) and its
> [template](haf-pack.example.json).

---

## 0. What you need

| | |
|---|---|
| **Humankind + BepInEx 5.4** | The game with the BepInEx mod loader installed. |
| **The HAF plugin** | A released `HumankindAssetFramework.dll` (or build it — [Building.md](Building.md)), dropped into `<Humankind>\BepInEx\plugins\`. It auto-detects your game and Blender paths. |
| **Unity + the Humankind SDK** | The editor half runs in a Unity project that has the Humankind Mod SDK — your own mod project is fine. Unity **2021.3.1f1**, the version the SDK targets. You install HAF's tools into it in step 1. |
| **Blender** (free) | Only for the **animated** and prep paths; a static model needs none. Auto-located; every Blender feature warns *before* a bake if it's missing. |
| **A 3D model** | HAF is the *pipeline*, not an art library — bring a licensed **GLB / glTF / FBX / OBJ** (a free download, a commission, or your own build). Models aren't shipped with HAF. |

---

## 1. Install the authoring tools

The tools are a **Unity package**. In your mod project: **`Window ▸ Package Manager`** → **`+`** (top left) →
**`Add package from git URL…`** → paste:

```
https://github.com/sswelm/ENCReload.git?path=/Assets/Scripts/Editor
```

It resolves as **HAF Authoring Tools**, and every window appears under **`Tools ▸ HAF`**. Nothing to copy, nothing to
reference: the tools reach every game type by reflection, so they carry no compile-time dependency on Humankind's
assemblies.

**A clean install is the contract.** You should see one white console line naming the menu, and no errors. The tools
ship with automatic backups, an asset-delete guard and a console filter — all of which stay **off** in an installed
package and change nothing until you turn them on in `Tools ▸ HAF ▸ Backup & Restore`. If you see red, report it
rather than working around it.

> **Don't use the Releases page.** `HumankindAssetFramework-v0.1.0.zip` there is the *runtime plugin* (the BepInEx
> DLL from the table above), not the tools. The URL is the only install.

**Two current limits**, both being worked on:

- The **Blender helper scripts don't ship in the package yet** — anything that shells out to Blender (tri-reduction,
  strip-parts, animated conversion) won't find them. Set `targetTris` to `0` and bring your mesh in at the polycount
  you want.
- **Pack identity is still fixed** to `haf_packs/ENCReload` / `modId: "enc"`. Harmless for the projectile axis, which
  never touches the registry; blocking if you want unit models in *your own* pack. See
  [Decisions.md](Decisions.md).

---

## 2. Bake your first model (start static)

Open **`Tools ▸ HAF ▸ Model Factory`**, then:

1. **Model file** — Browse to your `.glb`/`.obj`.
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
the fix (start with the troubleshooting table in [Factory-Manual.md](Factory-Manual.md)).

---

## 5. Go deeper

- **Animate it** — [Animated-Models.md](Animated-Models.md) · [Donor-Clip-Flight.md](Donor-Clip-Flight.md) ·
  [Turn-Ease.md](Turn-Ease.md)
- **Texture / reskin** — [Textures.md](Textures.md)
- **Other injection axes** — [districts](District-Visuals.md) · [pawn props](Pawn-Props.md) ·
  [projectiles](Projectiles.md) · [formations](Formations.md) · [unit size](Unit-Size.md)
- **Ship it for others as a pack** — [**Multi-Mod.md**](Multi-Mod.md): the `haf_packs/` drop folder, how packs merge, and
  the load order (which follows your Humankind mod order).

Welcome aboard.
