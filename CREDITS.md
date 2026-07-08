# Model Credits & Licenses

Third-party 3D models injected via the Universal Model Factory. Each carries its own license,
independent of this project's code. **Include this file (or its attributions) in any distribution
of the mod** — CC-BY requires the author be credited.

| Resource (registry) | Model | Author | Source | License | Notes |
|---|---|---|---|---|---|
| `StealthCruiser` | USS Zumwalt (DDG-1000) | Yakudami | Sketchfab | CC Attribution (CC-BY) | Free; commercial use allowed **with credit**. Low-poly (Cities Skylines) asset; albedo hand-cleaned (stray yellow fill removed). |
| `AttackHelicopter` | AH-1 Cobra | manilov.ap | Sketchfab | CC Attribution (CC-BY) | Free; commercial use allowed **with credit**. Multi-material GLB (52 materials); own main + tail rotor stripped so the donor's spin through. First model to exercise the converter's multi-material path. |
| `Hovercraft` | LCAC esboço | LM3D | Sketchfab | CC Attribution (CC-BY) **+ NoAI** | Free; commercial use allowed **with credit**. Clean polygon remodel (41.7k tris, 6 meshes) — replaced an unusable CAD-tessellated version. Static bake; model has **no UVs**, so a height-UV vertical-gradient skin is used (dark skirt / grey hull), Winding-fix + Double-sided for the single-sided skirt. **NoAI:** must not be used as input to / training data for generative-AI programs (does not affect normal in-game use). |
| `Zeppelin` | Airship / zeppelin | _TODO_ | _TODO_ | _TODO_ | Fill in source + license before distribution. |

## License quick-reference
- **CC0** — public domain; no attribution required.
- **CC-BY** — free (incl. commercial); **must credit the author** (name + link).
- **CC-BY-NC** — free but **non-commercial only**; fine for a free mod, not for paid distribution.

## How to add a new model's credit
When you bake a new model in the Factory, add a row here with its author, source URL, and license.
Keep the `Resource (registry)` column matching the `resourceName` in `enc_models.json`.
