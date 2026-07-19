# Pawn Props — custom weapons & gear on attachment slots

**Status: SOLVED (2026-07-16) — a custom prop renders in a pawn's hand in-game.** The third injection axis, after
units and districts. Proven end-to-end the same night the district pipeline shipped: Humankind's Slingers — bare-handed
since release — now carry an actual sling (a free Sketchfab model), positioned in the hand.

**Extended (2026-07-19): HAND PROPS ON CUSTOM SKELETONS — the Combine soldier carries an M60.** A second, fully
runtime attachment route for models injected by the unit axis (whose donors — vehicles — have no weapon slots, and
whose skeletons are ours, not the game's): see §"Hand props" below. Weapon in the soldier's hand, correct grip
through idle/run/stance/fire, in its own gunmetal skin.

Unlike the unit and district axes, this one rides the game's **own data path**: a pawn definition's attachment slots
already reference equipment assets by GUID, and pawn definitions are ordinary moddable data. The plugin only has to
cross one gate (below).

---

## How attachments work (decompiled: `C:\tmp\props\`)

```
PresentationPawnDefinition.Attachements[] = { SlotName ("Weapon_RightHand_0"), Fragment.Guid }
  → PresentationPawnFragmentMesh (ScriptableObject — the EQ_* assets)
      .ModelPrefab (GameObjectReference)  → matched against a REGISTERED MeshCollection.SourcePrefab
      .ModelName                          → GetFxMeshIndex(name) inside that collection
      .MaterialRef (Guid)                 → must have an OutputLayerEntry (borrow a vanilla weapon's)
      .CastShadow / .tags                 → tags only flag Bow/Shield animation variants; default is fine
  → a RIGID mesh glued to the slot's bone (no skinning), GPU-encoded at pawn spawn
```

`MeshCollection` is itself trivial: `{ prefab (the SourcePrefab key), skeleton (null for a rigid prop),
skinnedMeshInfos[] = { MeshName, FxMeshContent { Guid → an FxMesh, ImportAngles } } }` — the FxMeshContent encoding
fields fill themselves at `GetMeshIndex` time, so authoring only needs the FxMesh GUID. **`Skeleton` derives from
`MeshCollection`**, which is why the same registration API serves both.

**Proof shortcut (zero code):** point a vanilla pawn's weapon slot at an existing EQ fragment (Slingers +
`EQ_DLC_04_Weapon_Boomerang_01`) in the SDK, rebuild the mod — the slot honors mod data entirely.

---

## The editor side — Prop Lab (`PropBaker.cs`)

**Tools ▸ HAF ▸ Prop Lab** authors the whole chain from a model file:

1. **Dump** (the authoring template): paste any vanilla fragment's GUID — the Asset Picker's info panel shows it in
   32-hex form, accepted directly (nibble-swap each byte, then four little-endian int32 = Amplitude `{a,b,c,d}`; the
   same encoding as image references). The dump lists every field; its `MaterialRef` is the one you'll borrow
   (the **Default** button fills the shared `EQ_DLC04_Weapons` material, verified working).
2. **Bake prop chain**: static bake (`UniversalBaker.Build` — `pawnDescription` is registry-only, so **no dummy pawn**)
   → bone-free FxMesh (`DistrictBaker.BakeFxMesh` with `mergeSubMeshes: true`) → `<name>_Collection.asset` +
   `EQ_<name>_Fragment.asset`, GUIDs printed (collection GUID → clipboard).
3. Assign the fragment to the pawn's slot via the Asset Picker (it indexes project assets; the blank 3D preview there
   is normal — the editor can't resolve collections the game registers at runtime).

Settings persist in EditorPrefs; the embedded preview shows the baked prop (decimation damage is visible *before* a
relaunch).

## The runtime side — `[Props]` config

| Key | Meaning |
|---|---|
| `PropRegister` | master enable |
| `PropCollectionGuids` | semicolon-separated `a,b,c,d` GUIDs of our MeshCollections |
| `PropCollectionNames` | matching asset names (fallback loader — see trap 3) |

`Hk_PropRegister` (Harmony postfix on `AnimationManager.AnimationLoad`) registers our collections via the **public**
`RegisterMeshCollection` right after the game registers its own — before any pawn definition resolves its fragments —
and re-arms per session. An Update-tick registration survives as a late-repair safety net only.

---

## Traps (each cost a relaunch — read before authoring)

1. **The mammoth herd.** If the fragment's collection isn't registered when the pawn definition loads, the definition
   FAILS its Load, never gets a pawn id, and its units render as **pawn definition 0 — a mammoth**. A unit of stacked
   mammoths means "collection not registered", nothing else.
2. **Registration timing.** Pawn definitions resolve fragments inside the loading chunk — an Update-tick loses that
   race *by construction*. Hence the `AnimationLoad` postfix.
3. **Amplitude's asset catalog misses mod-bundle MeshCollections by GUID** (type-specific: the same bundle's FxMesh
   GUIDs resolve fine). Fallback: the plugin loads the object **by name** from the game's already-mounted Unity
   bundles (`PropCollectionNames`). Needs `UnityEngine.AssetBundleModule.dll` referenced in the plugin.
4. **The pawn-fragment GPU encoder draws only submesh 0.** A multi-material bake splits submeshes — the two-material
   sling rendered cords but no pouch. `BakeFxMesh(mergeSubMeshes: true)` flattens them (safe: the packed atlas already
   unified the UVs).
5. **Decimation eats thin sheets.** The sling's leather pouch survived only at Target-tris 20000 (1500 collapsed it
   entirely). Props are small; check the Prop Lab preview after each bake.
6. **Re-bake GUID drift.** Unity usually recycles the GUID for delete+recreate at the same path *within a session*,
   but not across editor restarts — re-pick the fragment on the pawn slot after a re-bake (a stale slot GUID = trap 1).
   The stale cfg collection GUID is harmless (the name fallback covers it).
7. **Orientation/position iteration.** ⚠ CORRECTED 2026-07-19: editing the `<name>_FxMesh` asset's Import Angles
   does NOT work — the baked angle field doesn't survive the mod bundle (in-game the class default `(-90,0,0)`
   applies; see §Hand props). Author orientation with the Prop Lab's **Rotation offset** (baked into vertices,
   preview-visible) and position with **Position offset**; test one axis at a time with a deliberately large value
   to learn the frame mapping, then dial in. The `handPropAngles` registry override is the relaunch-only knob.

## Remaining refinements

- ~~**Own texture**~~ SOLVED 2026-07-19 for hand props: the plugin paints the prop's own `<name>_Atlas` on a private
  clone of the borrowed layer, repainted per tick (see §Hand props). Vanilla-slot fragments still borrow.
- **Registry**: prop *recipes* now persist per prop in `enc_props.json` (Edit existing / New / Remove in the Prop
  Lab); the vanilla-slot runtime remains config-key driven (`PropCollectionGuids/Names`).

---

## Hand props — a weapon on a CUSTOM skeleton (2026-07-19, the soldier's M60)

The vanilla slot path above needs an attachment slot in the pawn **description** (`GetSlotIndex` −1 silently drops
the attachment) — and a unit-axis model's donor is typically a **vehicle with no weapon slots**, while its rendered
skeleton is OURS (renamed `b###_` bones). The hand-prop route sidesteps all of it: the plugin **constructs the
FragmentEntry itself** at repoint time and glues the Prop-Lab mesh to a bone of the injected skeleton.

**Authoring (all in the two Labs):**
1. **Prop Lab**: bake the weapon (`M60`) — Size relative to the pawn (a size-2 soldier carries a ~1.25 gun),
   Target-tris ~2000, the shared weapon material (Default). **Orientation is authored with Rotation offset**
   (baked into the vertices, visible in the preview) and placement with **Position offset** — both re-bake knobs.
2. **Animation Lab → Hand prop**: pick the baked prop from the combobox (name + collection GUID fill themselves),
   optional bone substring (default `R_Hand`, matched case-insensitively against the renamed bones). *Save (no
   bake)* → rebuild the mod.

**Registry** (per model): `handPropName`, `handPropGuid`, `handPropMat` ("" = the shared EQ_DLC04_Weapons
material), `handPropBone`, and `handPropAngles` — a hand-edited "x,y,z" **runtime rotation override** stamped onto
the FxMesh asset before encoding ("" stamps **zero**; see the third discovery below). Override iteration =
change + relaunch only.

**The three engine discoveries that made it work (all decompiled + field-verified):**
- **The GPU pawn descriptor snapshots fragments at registration.** Appending to `FragmentEntries` after that leaves
  the renderer drawing the old fragment count (glued-yet-invisible). The game's own full rebuild is UNSAFE mid-load
  (it skips not-yet-loaded definitions WITHOUT reserving their slot — every later pawn type shifts onto the wrong
  fragments/bones; in the field this scattered the recon drones and the howitzer). The plugin instead patches ONLY
  its own definition: existing fragment slots copied to the buffer tail + the prop appended there +
  `descriptor[defId]` repointed at the new contiguous block.
- **Weapon materials are streamed proxies.** The EQ layer's material holds 64×64 `_Proxy` textures; Amplitude can
  reset the material after a one-shot paint (the skin flip-flopped between sessions). The prop atlas is painted on
  a **private clone** of the layer (unit-retexture isolation — real DLC weapons untouched) and **repainted every
  tick** with a ReferenceEquals fast-path, exactly like the unit retexture recovery.
- **Baked FxMesh import angles do not survive the mod bundle** — in-game the asset reports the CLASS DEFAULT
  `(-90,0,0)`, silently tipping every prop over vs. the preview. The plugin therefore ALWAYS stamps the angles at
  load (the registry override, else zero), making in-game orientation equal the baked vertices.

**Limits:** the fragment record has no scale channel — the runtime `scale` knob moves the glue position but cannot
resize the mesh (match sizes at bake). One hand prop per model entry for now.
