# Installation

Everything you install, once, before you bake anything. Three parts — and **you may only need two of them.**

| Part | Who needs it |
|---|---|
| [1. BepInEx](#1-bepinex) | Everyone. It's the mod loader the plugin runs inside. |
| [2. The HAF plugin](#2-the-haf-plugin) | Everyone injecting custom **units, districts, props, wonders, formations or sizes**. Not needed for projectiles. |
| [3. The authoring tools](#3-the-authoring-tools) | Everyone who wants to **bake** a model. Skip it if you're only editing a pack by hand. |

---

## 1. BepInEx

The mod loader. HAF's runtime half is a BepInEx plugin, so this comes first.

1. Download **BepInEx 5.4.x**, the **x64** build for Windows, from
   [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases).
   *(5.4, not 6.x — 6 is a different, still-changing architecture.)*
2. Unzip it **into your Humankind install folder** — the one containing `Humankind.exe`. Typically
   `…\Steam\steamapps\common\Humankind\`.
3. **Run the game once, then quit.** That first launch is what creates `BepInEx\plugins\` and `BepInEx\config\`.

You should now have a `BepInEx` folder next to `Humankind.exe`, with `plugins` and `config` inside it. If those two
folders don't exist, the game hasn't been launched since you unzipped.

---

## 2. The HAF plugin

The runtime half — it reads your pack and injects the baked assets into the running game.

1. Get `HumankindAssetFramework.dll` and `Haf.Schema.dll` — from a release, or build them yourself
   ([Building.md](Building.md)).
2. Drop both into `…\Humankind\BepInEx\plugins\`.
3. Launch the game. Press **F8** — HAF's status window should open.

It auto-detects your game and Blender paths, so there's nothing to configure.

> **Not needed for projectiles.** A custom munition mesh rides Humankind's own data path: your unit's presentation
> pawn already names a `Projectile` asset, and that's ordinary moddable data in your mod. No plugin, no pack.
> See [Projectiles.md](Projectiles.md).

---

## 3. The authoring tools

The bake half — a Unity package containing the Model Factory and every Lab.

**You need:** Unity **2021.3.1f1** with the Humankind **ModTools SDK**, and a mod project open. Your own mod project
is the right place; you don't need a separate one.

In Unity: **`Window ▸ Package Manager`** → **`+`** (top left) → **`Add package from git URL…`** → paste:

```
https://github.com/sswelm/HumankindAssetFramework.git?path=/editor
```

It resolves as **HAF Authoring Tools**, and the windows appear under **`Tools ▸ HAF`**.

### What a correct install looks like

One white line in the console naming the menu, and **no errors**. That's the contract — if you see red, report it
rather than working around it.

The tools ship with automatic backups, an asset-delete guard and a console filter. In an installed package all three
are **off**, and your project is not modified until you turn them on in `Tools ▸ HAF ▸ Backup & Restore`.

### What it reads and writes

- **Your pack is your own.** The tools read and write `haf_packs/<YourProjectName>` — derived from the project name,
  authorable via the `HAF.Pack.Name` / `HAF.Pack.ModId` EditorPrefs — starting **empty**. An installed package never
  reads, shows, or bakes another mod's pack.

### One current limit

- **Blender helpers aren't in the package yet.** Anything that shells out to Blender — tri-reduction, part-stripping,
  animated conversion — won't find its scripts. Set `targetTris` to `0` and bring your mesh in at the polycount you
  want.

> **Don't use the Releases page for this.** `HumankindAssetFramework-v0.1.0.zip` there is the *runtime plugin* from
> step 2, not the tools. The URL above is the only install.

---

## Check it worked

| | Expected |
|---|---|
| **BepInEx** | `BepInEx\plugins\` and `BepInEx\config\` exist next to `Humankind.exe` |
| **Plugin** | **F8** in-game opens the HAF status window |
| **Plugin** | `BepInEx\config\haf_load_report.txt` exists after a launch |
| **Tools** | `Tools ▸ HAF` appears in Unity's menu bar, no console errors |

Now go to [Getting-Started.md](Getting-Started.md) and bake something.
