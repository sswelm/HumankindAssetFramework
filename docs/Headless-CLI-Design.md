# Design note — headless CLI (make HAF *operable* by AI/automation)

**Status: designed, not built.** The documentation work made HAF **readable** by AI (llms.txt, the Pages site). This is
the complement: a command-line surface that makes the editor's functions **operable** without the GUI — so an agent, a
script, or CI can author, validate, and bake content the same way a human does in `Tools ▸ HAF`, minus the clicking.

## Premise

Unity + the ENCReload project are **already a hard requirement** to use the authoring tools. A batch-mode CLI therefore
adds *no new requirement* — it just drives the existing, GUI-free pipeline from the command line. The bake tests already
prove this works: `BakeSmokeTest` / `BakeFeatureTest` run the real `ConfigFor → UniversalBaker` path as **static methods
with no window open**. The CLI is the same invocation, parameterised.

## What can run where (the boundary)

| Capability | Needs Unity? | How |
|---|---|---|
| Edit data-only registries (resize, formations, era, sound overrides, retexture config) | **No** | plain JSON (`pack.json`, `enc_*.json`) — editable directly or by a standalone verb |
| Validate a pack (bones/paths/GUIDs/schema) | **No** (mostly) | the [pack validator](Pack-Validator-Design.md) core — pure logic + file checks |
| Convert a model file (GLB/glTF/OBJ/FBX) | **No** | `glbconv.exe` (already standalone) |
| Blender prep (rig/decimate/clip extract) | **No** | already headless (`blender -b`) |
| **Bake** `Skeleton` / `Atlas` / `ClipCollection` / district FxMesh | **Yes** | the Amplitude SDK is Unity-bound → **Unity batch mode** |

So: everything *except baking the Amplitude assets* can run with no Unity at all; **baking** runs headless in Unity
batch mode. There is no way to bake Amplitude assets on a machine without Unity — that's the one hard limit, stated plainly.

### "Can't we reverse-engineer the editor to drop Unity?"

Two different targets hide behind this:

- **The editor's own code** — nothing to reverse-engineer; it's *our* code, already factored behind `ConfigFor →
  UniversalBaker` and already called headless by the bake tests. The CLI just invokes it via batch mode. This is the
  pragmatic path, full-fidelity, no RE.
- **The Amplitude asset format** (to bake with *zero* Unity) — HAF already understands the *data* side deeply from
  decompiling the runtime (Skeleton/ClipCollection buffers, bone TRS, pose data, atlas layout — see
  [Animated-Runtime.md](Animated-Runtime.md)). But the part that ties baking to Unity isn't the data — it's Unity's
  **serialization + asset-bundle envelope**: emitting valid `.asset` files with correct meta-GUIDs and packaging them
  into the loadable Resources/bundle the game reads. That envelope is what the SDK importers do; replicating it
  standalone is large, version-fragile, and duplicates working tooling for the sole benefit of removing a dependency the
  editor already requires. **Not recommended** — the payoff (no Unity) is exactly the requirement we've accepted as fine.

## Execution model

A single entry class **`HAF.Cli`** in the ENCReload editor assembly (where `ConfigFor`/`UniversalBaker`/the registries
live), invoked via:

```
Unity.exe -batchmode -quit -projectPath <ENCReload> -logFile - -executeMethod HAF.Cli.Run -- <request.json>
```

wrapped in a small `haf` shim (`.bat` / `.sh`) so callers don't hand-write the Unity path. `-batchmode -quit` means no
GUI and a clean exit; `-logFile -` streams to stdout.

**Fast path (optional):** pure-data verbs (`validate`, `list`, simple registry edits) touch only JSON and need no Unity —
they *can* be a standalone `.exe` (like `glbconv`) to avoid Unity's ~1-minute batch startup per call. Recommended split:
**standalone for data/validate (instant), Unity batch mode for `bake` (necessarily slow).** One requirement, two speeds.

## Command surface (proposed)

Request in, structured result out — **no interactive prompts** (batch mode has no console input).

| Verb | Unity? | Does | Reuses |
|---|---|---|---|
| `list [--kind models\|districts\|formations\|sounds]` | no | dump current registry entries as JSON | the registries |
| `validate <pack>` | no | pre-flight content check → `{warnings, errors}` | `ValidateEntry` ([validator](Pack-Validator-Design.md)) |
| `bake <request.json>` | **yes** | bake one model/district/prop from a JSON bake request → produce assets + upsert registry | `ConfigFor` → `UniversalBaker` |
| `set-resize` / `set-formation` / `silence-sound` … | no | scripted data-only edits (thin wrappers over the registries) | `ModelRegistry` etc. |

**`bake` request** = the JSON form of a `BakeConfig` (model file, pawn, size, shading, animated/clip, strip, etc.) — the
same fields the Factory/Animation Lab collect. Mapping through `ConfigFor` (the single shared config path the GUI and the
tests already use) means the CLI **can't drift** from the GUI's behaviour.

## Machine-readable contract (the point of it, for AI)

- **Structured output:** every verb prints a single JSON object to stdout — `{ ok, produced: [...asset paths], registry: "...", warnings: [], errors: [] }`.
- **Exit codes:** `0` ok, `2` validation failed, `3` bake failed, `4` bad request — so a caller can branch without parsing prose.
- **No prompts, no partial state:** validate-before-bake by default; registries are already **corruption-guarded** (an unparsable file is never overwritten) and git-backed, so an agent's mistake is recoverable.
- **Deterministic:** same request → same assets (the bake pipeline is already deterministic; see the golden regression tests).

## Phasing

- **Phase 1 (proof):** `HAF.Cli.Bake` — one `-executeMethod` command that bakes a single existing registry entry headless
  and prints the result JSON. Proves batch mode works in *this* project/Unity before building more. ~half a day.
- **Phase 2:** the standalone data/validate `.exe` (`list`, `validate`, `set-*`) — no Unity, instant, pairs with the
  validator.
- **Phase 3:** the `haf` shim + a `bake <request.json>` that accepts arbitrary bake requests, plus docs + an example
  request. This is the point an agent (or CI) can author → validate → bake end-to-end.

## Non-goals / risks

- **Not** a way to bake without Unity — the SDK is Unity-bound (see the boundary table).
- **Schema surface:** the `bake` request and the data verbs are *another* consumer of the registry schema (the
  `ModelDef`↔`ModelEntry` duplication that was deliberately left un-refactored). Route everything through `ConfigFor` and
  the registry classes — don't hand-roll a third schema — and extend `check_schema_parity.sh` to cover the request shape.
- **Maintenance:** a new surface to keep in step with the GUI. Mitigated by reusing `ConfigFor` and the registries rather
  than reimplementing.
- Complements, doesn't replace, the editor windows ([Editor-Tools.md](Editor-Tools.md)) — the GUI stays the human path;
  the CLI is the scriptable/agent path to the same pipeline.
