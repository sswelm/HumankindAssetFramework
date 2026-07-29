# ShaderDump — read Humankind's GPU shaders as ground truth

Built 2026-07-29 during the Resize Lab endgame ("dig deeper"): when C# decompiles and in-game
experiments contradict each other, the compiled shaders are the only authority. This toolchain
got us instruction-level proof that the render pipeline never scales geometry at runtime.

## What it does

Extracts and disassembles D3D11 shader bytecode from the game's Unity asset bundles
(`AssetBundles/*/*.assetbundle`), using:

- **AssetsTools.NET** (NuGet) to parse the UnityFS bundle and asset serialization,
- **d3dcompiler_47.dll** (stock Windows) via P/Invoke `D3DDisassemble` for DXBC -> readable asm.

## Key discoveries about the formats (the hard-won part)

- **ComputeShader assets (classId 72)**: `variants[] -> kernels[] -> variantMap[] -> second.code`
  is the RAW DXBC per kernel (targetRenderer 2 = D3D11, 21 = Vulkan/SPIR-V). Disassembles directly.
- **Shader assets (classId 48)**: `compressedBlob` holds LZ4-block-compressed segments; the tables
  `offsets` / `compressedLengths` / `decompressedLengths` are **vector<vector<uint>>** (outer =
  platform). Decompress a segment, then scan for `DXBC` magic (total size = uint32 at blob+24).
  Unity STRIPS reflection (RDEF) — binding names live in Unity's own name table that precedes each
  blob in the segment; register order in the asm (t0, t1, ...) matches the name-table order.
- Grepping the bundle file directly finds name strings (LZ4 literals) but blob bytes are mangled
  by back-references — decompress properly before carving DXBC.

## The files (crude but effective — one Main active at a time, others renamed *Old)

- `Program.cs`  — raw DXBC scan of a file + bundle decompress (first, naive pass)
- `Program2.cs` — list ComputeShader assets, dump one's serialized field tree
- `Program3.cs` — extract + disassemble ComputeShader kernels (used on AmpliAnimation)
- `Program4.cs` — find Shader assets consuming a marker buffer, LZ4-decompress segments, save them
- `Program5.cs` — debug: list asset typeIds + a Shader asset's field layout
- `Program6.cs` — carve + disassemble DXBC blobs from a dumped segment (used on the pawn VS)
- `Program7.cs` — batch sweep: disassemble ALL blobs, grep for instruction patterns across variants

Run: `dotnet run -c Release -- <path>` after renaming the wanted `MainXOld` back to `Main`.

## What it proved (the Resize Lab verdict, 2026-07-29)

Pawn rendering = `AmpliAnimation` kernels (CSAnimateFirstPass/SecondPass) + the
`Amplitude/ParticleSkinnedMeshRender Implementation` vertex-pulling draw shader:

1. First pass hardcodes every animated bone's Scale to 1.0 (`mov r3.y, l(1.000000)`).
2. Second pass emits `entry.Scale = 1/IBP.Scale x ObjectSpace.Scale` (chain scale is always 1).
3. All 128 D3D11 draw-VS variants use `entry.Scale` ONLY on the bind-pose translation; vertex
   positions get pure rotation+translation. IBP.Scale is never read by the draw.

Runtime geometry scaling is structurally impossible; size lives in baked vertex data only.
