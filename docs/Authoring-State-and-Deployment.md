# Authoring state and deployment — what must I run after a change?

HAF has several stages because Humankind loads model assets and runtime configuration through different paths. The
most common wasted iteration is running too much (a Blender bake for one runtime checkbox) or too little (relaunching
after a bake without rebuilding the mod bundle).

Use this page as the operational checklist. Field-level meaning still lives in the
[Factory Manual](Factory-Manual.md); symptom-first diagnosis starts in [Troubleshooting](Troubleshooting.md).

## The pipeline

| Stage | Action | What changes |
|---|---|---|
| Source preparation | edit a model, or **Generate rig** in Vehicle Lab | the external GLB/FBX/OBJ and optional Vehicle Lab recipe |
| Registry Save | **Save settings** / **Save (no bake)** in the owning HAF window | the git-tracked project registry and its deployed runtime artifact |
| Bake | **Bake** in Model Factory/Animation Lab or another Factory/Lab | generated assets under `Assets/Resources` and the registry entry that names their GUIDs |
| Mod build | Humankind Mod Editor build, or `haf build` | the asset bundle/module in Humankind's `Community` folder |
| Runtime load | launch or relaunch Humankind | the plugin reloads packs, runtime files, and the newly built bundle |
| Live dial poll | edit a supported `haf_*.txt` dial | the running plugin re-reads it in about one second; no relaunch |

These stages are cumulative. A model bake does not build the mod. A mod build does not regenerate a stale Vehicle Lab
GLB. A runtime-only Save does not need Blender or a new asset bundle.

## Change → required actions

| What changed | Generate rig | Save | Bake | Build mod | Relaunch game |
|---|:---:|:---:|:---:|:---:|:---:|
| Vehicle Lab roles, orientation, axle, tracks, trails, recoil, or rotor settings | **yes** | recipe optional | **yes** | **yes** | **yes** |
| Source model geometry or model file | — | included by Bake | **yes** | **yes** | **yes** |
| Size, material mode, atlas size, Keep black, triangle reduction, clip selection/slicing, rig conversion | — | included by Bake | **yes** | **yes** | **yes** |
| Runtime model setting: donor clip/VFX/audio flags, freeze donor, respawn-after-load, flight character, position offset, attack repeats | — | **yes** | no | no | **yes** |
| Formation, unit-size, or other data-only registry rule | — | **yes** in its Lab | no | no | **yes** |
| Supported live dial (`haf_turnease.txt`, `haf_hugterrain.txt`, `haf_rotortrim.txt`, `haf_battleturn.txt`) | — | edit file | no | no | **no** — wait for the poll |
| Baked atlas/mesh/skeleton/clip asset | — | — | already changed | **yes** | **yes** |
| Custom runtime skin or WAV copied into `BepInEx/config` | — | owning tool/file copy | no | no | usually **yes**; follow the feature guide |
| Documentation or editor-only code | — | — | no | no model build; update/recompile the authoring package as appropriate | — |

“Save” means the button in the window that owns the setting. Model Factory calls it **Save settings**; Animation Lab
uses **Save (no bake)**. Both persist registry-only changes without regenerating model assets.

## Three locations that are easy to confuse

### 1. The authoring source

`Assets/Pack/<PackName>/pack.json` is the git-tracked source of truth for the unit-model pack. The editor reads and
writes this file. Commit it when you want the change in project history.

Districts and formations currently use their own git-tracked sources under `Assets/Databases/`; their filenames retain
the historical `.backup.json` suffix even though they are now authoritative. Props keep an editor recipe registry at
`Assets/Databases/haf_props.json`.

### 2. The deployed runtime artifact

`<Humankind>/BepInEx/config/haf_packs/<PackName>/pack.json` is derived from the project source on Save/Bake. The running
plugin reads it, but authors should not hand-edit it: the next Save overwrites it. The same source/artifact distinction
applies to district and formation registry files.

Because registry-only settings are loaded from `BepInEx/config`, they need **Save + game relaunch**, not a Humankind mod
bundle rebuild.

### 3. The built asset bundle

Meshes, skeletons, clips, atlases, materials, district meshes, props, and projectiles are generated in the Unity project.
Humankind cannot see a fresh bake until those assets are included in a new mod build under `Community`. This is what
**Ship Status** checks: source registry freshness is not the same thing as shipped asset freshness.

## What is authoritative, generated, or disposable?

| Location | Class | Recovery |
|---|---|---|
| External licensed model files | irreplaceable authoring input | source backup/offsite copy |
| `Assets/FactorySource/` | imported/extracted bake input; may include hand-edited albedos | backup; some files can be regenerated, hand edits cannot |
| `Assets/Pack/<PackName>/pack.json` | **authoritative, git-tracked** model-pack source | git; last valid deployed artifact; Backup & Restore runtime-config group |
| `Assets/Databases/` registry sources | authoritative project configuration for their owning tools | git + Backup & Restore |
| `Assets/Resources/` HAF outputs | generated bake assets | re-bake, or restore a backup for a known-good version |
| `BepInEx/config/haf_packs/.../pack.json` | deployed artifact | regenerate via Save/Bake; usable as last-good recovery when the source is missing/corrupt |
| `BepInEx/config/haf_*.txt`, plugin cfg, skins, sounds, ground textures, facing state | runtime/user state; some files are hand-tuned | Backup & Restore runtime-config group |
| Humankind `Community` module/bundle | generated deployment | rebuild and deploy |
| `haf_load_report.txt`, `haf_bindings_report.txt`, logs, atlas dumps | generated evidence | reproduce by launching/dumping; do not treat as configuration |

## Fast verification after each boundary

1. **After Generate rig:** play/frame-step the Vehicle Lab preview; confirm pivots, axes, and moving groups.
2. **After Save:** inspect the owning source registry diff and confirm the deployed artifact was refreshed.
3. **After Bake:** check the first Console error, the DONE line, and the post-Bake preview.
4. **After Build:** open **Tools ▸ HAF ▸ Ship Status**; the entry should no longer say *BAKED, NOT BUILT*.
5. **After launch:** open F8 and `haf_load_report.txt`; confirm the pack loaded and the target pawn uses the entry.

If a change is still absent, identify the last boundary that definitely contains it. That is faster than repeating every
stage: a correct atlas but old in-game model is a build/deploy problem; an updated runtime registry but old checkbox
behavior is a relaunch/load problem; an old generated GLB is a Generate rig problem.

## Related guides

- [Getting Started](Getting-Started.md) — first complete bake/build/launch loop
- [Vehicle Lab quickstart](Vehicle-Lab-Quickstart.md) — static vehicle to generated animated rig
- [Ship Status](Ship-Status.md) — baked versus built, and orphan cleanup
- [Backup & Restore](Backup.md) — recovery for irreplaceable and uncommitted working data
- [Headless CLI](Headless-CLI.md) — automate re-bake, build, and deployment
