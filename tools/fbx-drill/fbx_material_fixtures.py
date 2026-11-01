"""Material, texture and image scenes for tools/fbx_model_drill.sh (sub-step 6b of the native FBX reader): the shapes the real
files lack - every material property the importer reads, every texture link type, the image formats Blender reads (PNG of
every colour type, a JPEG, a 16-bit PNG, a TGA), missing files, embedded Content, name collisions, wrap modes and
transforms. The image files are written beside the scenes (write_assets); the oracle is Blender itself (fbx_to_glb.py).
A fixture named *_fallback is one the reader must leave to Blender BY NAME (the drill wants a SKIP row for it)."""
from pathlib import Path
import struct
import zlib
from fbx_fixtures import Node, prop
from fbx_model_fixtures import P, vec, properties, obj, connect, header, scene, model, geometry, layer


# ---- image files --------------------------------------------------------------------------------------------------------

def png(w, h, rows, color_type, bit_depth=8, plte=None, trns=None, scanlines=None):
    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    out = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, bit_depth, color_type, 0, 0, 0))
    if plte is not None:
        out += chunk(b'PLTE', plte)
    if trns is not None:
        out += chunk(b'tRNS', trns)
    out += chunk(b'IDAT', zlib.compress(b''.join(b'\x00' + bytes(r) for r in rows) if scanlines is None else scanlines, 9)) + chunk(b'IEND', b'')
    return out


def tiff(w, h, pixels_rgb):
    """A minimal uncompressed 8-bit RGB little-endian TIFF (one strip)."""
    data = bytes(pixels_rgb)
    entries = [(256, 3, 1, w), (257, 3, 1, h), (258, 3, 3, 0), (259, 3, 1, 1), (262, 3, 1, 2), (273, 4, 1, 0), (277, 3, 1, 3), (278, 3, 1, h), (279, 4, 1, len(data))]
    ifd_at = 8
    bps_at = ifd_at + 2 + 12 * len(entries) + 4
    data_at = bps_at + 6
    out = b'II*\x00' + struct.pack('<I', ifd_at) + struct.pack('<H', len(entries))
    for tag, typ, count, value in entries:
        if tag == 258:
            value = bps_at
        if tag == 273:
            value = data_at
        out += struct.pack('<HHI', tag, typ, count) + (struct.pack('<HH', value, 0) if typ == 3 and count == 1 else struct.pack('<I', value))
    out += struct.pack('<I', 0) + struct.pack('<HHH', 8, 8, 8) + data
    return out


def tga(w, h, pixels_bgr):
    """An uncompressed 24-bit true-colour TGA, top-left origin."""
    return struct.pack('<BBBHHBHHHHBB', 0, 0, 2, 0, 0, 0, 0, 0, w, h, 24, 0x20) + bytes(pixels_bgr)


# Blender 5.1's own 8x8 JPEG of (0.3, 0.6, 0.9), quality 75
TINY_JPG = bytes.fromhex(
    'ffd8ffe000104a46494600010100000100010000ffe1000c4e656f47656f0000004bffdb004300080606070605080707070909080a0c140d0c0b0b0c1912130f'
    '141d1a1f1e1d1a1c1c20242e2720222c231c1c2837292c30313434341f27393d38323c2e333432ffdb0043010909090c0b0c180d0d1832211c21323232323232'
    '32323232323232323232323232323232323232323232323232323232323232323232323232323232323232323232323232ffc00011080008000803012200021101'
    '031101ffc4001f0000010501010101010100000000000000000102030405060708090a0bffc400b5100002010303020403050504040000017d010203000411051221'
    '31410613516107227114328191a1082342b1c11552d1f02433627282090a161718191a25262728292a3435363738393a434445464748494a535455565758595a6364'
    '65666768696a737475767778797a838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8'
    'd9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9faffc4001f0100030101010101010101010000000000000102030405060708090a0bffc400b5110002010204'
    '0403040705040400010277000102031104052131061241510761711322328108144291a1b1c109233352f0156272d10a162434e125f11718191a262728292a3536'
    '3738393a434445464748494a535455565758595a636465666768696a737475767778797a82838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4'
    'b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae2e3e4e5e6e7e8e9eaf2f3f4f5f6f7f8f9faffda000c03010002110311003f00b7451457d69f307fffd9')

W, H = 4, 4


def rgb_rows(seed=0):
    return [[(17 * x + 3 + seed, 40 * y + 9, (77 * (x + y) + seed) % 256)[c] for x in range(W) for c in range(3)] for y in range(H)]


def rgba_rows():
    return [[(17 * x + 3, 40 * y + 9, (77 * (x + y)) % 256, 255 - 60 * y - 7 * x)[c] for x in range(W) for c in range(4)] for y in range(H)]


def gray_rows(seed=0):
    return [[(13 * (x + 4 * y) + 5 + seed) % 256 for x in range(W)] for y in range(H)]


def gray_alpha_rows():
    return [[(13 * (x + 4 * y) + 5, 250 - 30 * (x + y))[c] for x in range(W) for c in range(2)] for y in range(H)]


def packed_rows(w, h, bits, values):
    """Rows of sub-byte grey samples / palette indices packed high bits first."""
    rows = []
    for y in range(h):
        bitstr = ''.join(format(values[y * w + x], '0%db' % bits) for x in range(w))
        bitstr += '0' * (-len(bitstr) % 8)
        rows.append([int(bitstr[i:i + 8], 2) for i in range(0, len(bitstr), 8)])
    return rows


def images():
    """name -> bytes, for the files written beside the fixtures."""
    out = {}
    out['tex/rgb.png'] = png(W, H, rgb_rows(), 2)
    out['tex/rgb2.png'] = png(W, H, rgb_rows(90), 2)
    out['tex/rgba.png'] = png(W, H, rgba_rows(), 6)
    out['tex/gray.png'] = png(W, H, gray_rows(), 0)
    out['tex/gray2.png'] = png(W, H, gray_rows(120), 0)
    out['tex/graya.png'] = png(W, H, gray_alpha_rows(), 4)
    plte = bytes([250, 10, 20, 30, 200, 40, 50, 60, 190, 7, 8, 9])
    idx = [(x + y) % 4 for y in range(H) for x in range(W)]
    out['tex/pal.png'] = png(W, H, [[idx[y * W + x] for x in range(W)] for y in range(H)], 3, plte=plte)
    out['tex/palt.png'] = png(W, H, [[idx[y * W + x] for x in range(W)] for y in range(H)], 3, plte=plte, trns=bytes([255, 128, 0, 200]))
    out['tex/pal2.png'] = png(W, H, packed_rows(W, H, 2, idx), 3, bit_depth=2, plte=plte)
    out['tex/rgbt.png'] = png(W, H, rgb_rows(), 2, trns=struct.pack('>HHH', 3, 9, 0))   # the pixel (3, 9, 0) at x = 0, y = 0 is transparent
    out['tex/gray1.png'] = png(W, H, packed_rows(W, H, 1, [(x * y) % 2 for y in range(H) for x in range(W)]), 0, bit_depth=1)
    out['tex/gray4.png'] = png(W, H, packed_rows(W, H, 4, [(3 * x + y) % 16 for y in range(H) for x in range(W)]), 0, bit_depth=4)
    out['tex/rgb16.png'] = png(W, H, [[b for v in row for b in (v * 257).to_bytes(2, 'big')] for row in rgb_rows()], 2, bit_depth=16)
    out['tex/gray16.png'] = png(W, H, [[b for v in row for b in (v * 257).to_bytes(2, 'big')] for row in gray_rows()], 0, bit_depth=16)
    out['tex/gray8.png'] = png(8, 8, [[(29 * x + 11 * y) % 256 for x in range(8)] for y in range(8)], 0)
    out['tex/big.png'] = png(6, 4, [[(31 * x + 7 * y) % 256 for x in range(6)] for y in range(4)], 0)
    out['tex/tiny.jpg'] = TINY_JPG
    out['tex/jpeg_named_png.png'] = TINY_JPG
    out['tex/png_named_tga.tga'] = png(W, H, rgb_rows(33), 2)
    out['tex/real.tga'] = tga(2, 2, [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120])
    out['tex/real.tif'] = tiff(2, 2, [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120])
    out['tex/notimage.png'] = b'this is not an image at all\n'
    out['tex/Upper.PNG'] = png(W, H, rgb_rows(5), 2)
    out['tex/same.png'] = png(W, H, gray_rows(7), 0)
    out['tex/other/same.png'] = png(W, H, gray_rows(77), 0)
    out['sub/deep/found.png'] = png(W, H, rgb_rows(60), 2)
    out['sub/deep/Case.png'] = png(W, H, gray_rows(3), 0)
    # These have image signatures/headers but Blender cannot load their pixels: they become placeholders.
    out['tex/header_only.png'] = out['tex/rgb.png'][:33]
    damaged = bytearray(out['tex/rgb.png']); damaged[29] ^= 1
    out['tex/bad_crc.png'] = bytes(damaged)
    out['tex/bad_filter.png'] = png(1, 1, [], 2, scanlines=b'\x05\x14\x28\x3c')
    out['tex/short_pixels.png'] = png(1, 1, [], 2, scanlines=b'\x00\x14\x28')
    out['tex/header_only.jpg'] = TINY_JPG[:TINY_JPG.index(b'\xff\xc4')]
    # Blender tolerates both boundaries: do not mistake them for the damaged images above.
    out['tex/no_iend.png'] = out['tex/rgb.png'][:-12]
    out['tex/extra_pixels.png'] = png(1, 1, [], 2, scanlines=b'\x00\x14\x28\x3c\xff\x00\x01')
    out['tex/transparent.png'] = png(2, 1, [[150, 30, 240, 0, 110, 20, 30, 128]], 6)
    # Blender 5.1.2's composed PNG of transparent.png. This unrelated unused image used to fool the comparator's
    # byte-equality heuristic into claiming that Blender passed the actual composition through.
    out['tex/unused_equal.png'] = bytes.fromhex(
        '89504e470d0a1a0a0000000d4948445200000002000000010806000000f4227f8a00000001735247420337c74d530000000467414d410000b18f0bfc6105'
        '000000206348524d00007a26000080840000fa00000080e8000075300000ea6000003a98000017709cba513c00000036655849664d4d002a00000018'
        '000000480000000100000048000000010002011a00050000000100000008011b00050000000100000010000000009078efbc000000096f46467300000000'
        '0000000000da2ab6ce000000097048597300000b1200000b1201d2dd7efc0000001149444154081d639c26f781e1c637bd0600125204225e7593e10000000049454e44ae426082')
    return out


def write_assets(out):
    for rel, data in images().items():
        path = Path(out) / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)


# ---- scene pieces -------------------------------------------------------------------------------------------------------

def material(ident, name, *props, shading='phong', version=True):
    children = ([Node('Version', [prop('I', 102)])] if version else []) + [Node('ShadingModel', [prop('S', shading)])]
    if props:
        children.append(properties(*props))
    return obj('Material', ident, name, '', children)


def color(name, r, g, b):
    return P(name, 'Color', '', float(r), float(g), float(b))


def number(name, v):
    return P(name, 'Number', '', float(v))


def texture(ident, name, relative=None, filename=None, props=(), content=None, empty_relative=False):
    children = [Node('Type', [prop('S', 'TextureVideoClip')]), Node('Version', [prop('I', 202)]), Node('TextureName', [prop('S', name + '\0\1Texture')])]
    if props:
        children.append(properties(*props))
    children.append(Node('Media', [prop('S', name + '\0\1Video')]))
    if empty_relative:
        children.append(Node('RelativeFilename', []))
    if relative is not None:
        children.append(Node('RelativeFilename', [prop('S', relative)]))
    if filename is not None:
        children.append(Node('FileName', [prop('S', filename)]))
    if content is not None:
        children.append(Node('Content', [prop('R', content)]))
    return obj('Texture', ident, name, '', children)


def video(ident, name, relative=None, filename=None, content=None):
    children = [Node('Type', [prop('S', 'Clip')]), properties(P('Path', 'KString', 'XRefUrl', filename or relative or '')), Node('UseMipMap', [prop('I', 0)])]
    if filename is not None:
        children.append(Node('Filename', [prop('S', filename)]))
    if relative is not None:
        children.append(Node('RelativeFilename', [prop('S', relative)]))
    if content is not None:
        children.append(Node('Content', [prop('R', content)]))
    return obj('Video', ident, name, '', children)


def link(src, dst, name):
    return Node('C', [prop('S', 'OP'), prop('L', src), prop('L', dst), prop('S', name)])


def pp(src, dst, name):
    return Node('C', [prop('S', 'PP'), prop('L', src), prop('L', dst), prop('S', name), prop('S', name)])


QUAD_V = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0]
QUAD_F = [[0, 1, 2, 3]]


def quads(materials):
    """One quad object per material (ids 1.., geometries 30..); returns (objects, connections)."""
    objects, connections = [], []
    for i, mat in enumerate(materials):
        objects += [model(1 + i, 'Obj%d' % i, vec('Lcl Translation', 2 * i, 0, 0)),
                    geometry(30 + i, 'Geo%d' % i, QUAD_V, QUAD_F, [layer('UV', 'UV', 'ByPolygonVertex', 'Direct', [0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0], name='map1')])]
        connections += [connect(1 + i, 0), connect(30 + i, 1 + i), connect(mat, 1 + i)]
    return objects, connections


def material_scene(materials, extra_objects=(), extra_connections=(), head=None):
    """Materials by id (already in extra_objects or appended), each on its own quad."""
    ids = [m.props[0] for m in materials]
    ids = [struct.unpack('<q', p[1:])[0] for p in ids]
    objects, connections = quads(ids)
    return scene(list(materials) + list(extra_objects) + objects, connections + list(extra_connections), head)


def head_with_material_template(*props):
    """The default header with a KFbxSurfacePhong template (FBX 7.4 writes 'FbxSurfacePhong') carrying the given properties."""
    head = header()
    defs = head[2]
    defs.children = [c for c in defs.children if not (c.name == 'ObjectType' and c.props[0] == prop('S', 'Material'))]
    defs.children.append(Node('ObjectType', [prop('S', 'Material')], [Node('PropertyTemplate', [prop('S', 'FbxSurfacePhong')], [properties(*props)])]))
    return head


def fixtures():
    T = 'tex\\'
    # ---- the material properties: every value the importer reads, with its clamps, defaults, the template and the U flag
    mats = [
        material(100, 'Defaults'),
        material(101, 'Factors', color('DiffuseColor', 0.2, 0.4, 0.6), number('SpecularFactor', 0.5), number('Shininess', 100.0), number('ReflectionFactor', 0.3), number('BumpFactor', 2.0)),
        material(102, 'Clamped', color('DiffuseColor', 1.5, -0.2, 0.5), number('SpecularFactor', 0.1), number('Shininess', -5.0), number('ReflectionFactor', 1.7), number('BumpFactor', 40.0)),
        material(103, 'Blend', number('TransparencyFactor', 0.3)),
        material(104, 'Opacity', number('TransparencyFactor', 1.0), number('Opacity', 0.25)),
        material(105, 'TransparentColour', number('TransparencyFactor', 0.0), color('TransparentColor', 0.6, 0.1, 0.1)),
        material(106, 'Masked', number('TransparencyFactor', 1.0), number('Opacity', 0.0)),
        material(107, 'Emissive', color('EmissiveColor', 0.5, 0.25, 0.0), number('EmissiveFactor', 4.0)),
        material(108, 'EmissiveGrey', color('EmissiveColor', 1.0, 1.0, 1.0), number('EmissiveFactor', 0.5)),
        material(109, 'EmissiveBlack', color('EmissiveColor', 0.0, 0.0, 0.0), number('EmissiveFactor', 3.0)),
        obj('Material', 110, 'UserFlag', '', [Node('ShadingModel', [prop('S', 'phong')]), properties(
            Node('P', [prop('S', 'DiffuseColor'), prop('S', 'Color'), prop('S', ''), prop('S', 'AU'), prop('D', 0.1), prop('D', 0.1), prop('D', 0.1)]),
            P('DiffuseColor', 'ColorRGB', 'Color', 0.9, 0.3, 0.3))]),
        material(111, 'Lambert', color('DiffuseColor', 0.3, 0.3, 0.9), number('SpecularFactor', 0.9), shading='lambert'),
        material(112, '', color('DiffuseColor', 0.5, 0.5, 0.1)),
        material(113, 'TemplateOpacity', number('TransparencyFactor', 1.0)),
        material(114, 'Specular1', number('SpecularFactor', 0.5)),
        material(115, 'Specular0', number('SpecularFactor', 0.0)),
    ]
    # the template also carries a short P of another name: harmless there (elem_props_find_first reads its flags only on a name match)
    yield 'material_factors', material_scene(mats, head=head_with_material_template(number('Shininess', 50.0), number('Opacity', 0.0), color('DiffuseColor', 0.7, 0.7, 0.7),
                                                                                     Node('P', [prop('S', 'SomethingElse'), prop('S', 'Number')])))
    # a DiffuseColor of the wrong type: Blender's importer asserts (its import fails); the reader declines the file
    yield 'material_assert_fallback', material_scene([material(100, 'Bad', vec('DiffuseColor', 0.5, 0.5, 0.5))])
    # a short P in a material's OWN block: the custom-property pass indexes its flags and fails
    yield 'material_shortp_fallback', material_scene([obj('Material', 100, 'ShortP', '', [Node('ShadingModel', [prop('S', 'phong')]), properties(
        Node('P', [prop('S', 'SomethingElse'), prop('S', 'Number')]), number('SpecularFactor', 0.5))])])

    # ---- textures: every slot, the compositions, the alpha rules, the shared datablocks and the copies
    objects = [
        texture(60, 'diffuse_rgb', T + 'rgb.png'), video(70, 'rgb.png', T + 'rgb.png'),
        texture(61, 'normal', T + 'rgb2.png'), video(71, 'rgb2.png', T + 'rgb2.png'),
        texture(62, 'rough', T + 'gray.png'), video(72, 'gray.png', T + 'gray.png'),
        texture(63, 'metal', T + 'gray2.png'), video(73, 'gray2.png', T + 'gray2.png'),
        texture(64, 'diffuse_rgba', T + 'rgba.png'), video(74, 'rgba.png', T + 'rgba.png'),
        texture(65, 'alpha_rgba', T + 'rgba.png'),
        texture(66, 'spec', T + 'gray.png'),
        texture(67, 'emit', T + 'rgb.png'),
        texture(68, 'emit_strength', T + 'gray.png'),
        texture(69, 'alpha_rgb', T + 'rgb.png'),
        texture(80, 'rough_rgba', T + 'rgba.png'),
        texture(81, 'metal_gray', T + 'gray.png'),
        texture(82, 'diffuse_rgb16', T + 'rgb16.png'), video(83, 'rgb16.png', T + 'rgb16.png'),
    ]
    mats = [material(100, 'Full', number('BumpFactor', 0.5), number('ReflectionFactor', 0.8), number('Shininess', 0.0)),
            material(101, 'DiffuseAlpha'), material(102, 'AlphaFirst'), material(103, 'SpecEmit', number('EmissiveFactor', 2.0)),
            material(104, 'MetalLum'), material(105, 'RoughOnly'), material(106, 'SameImageMR'), material(107, 'Rgb16')]
    links = [link(60, 100, 'DiffuseColor'), link(61, 100, 'NormalMap'), link(62, 100, 'ShininessExponent'), link(63, 100, 'ReflectionFactor'),
             link(64, 101, 'DiffuseColor'),
             link(65, 102, 'TransparencyFactor'), link(64, 102, 'DiffuseColor'),
             link(66, 103, 'SpecularFactor'), link(67, 103, 'EmissiveColor'), link(68, 103, 'EmissiveFactor'),
             link(63, 104, 'ReflectionFactor'), link(69, 104, 'TransparencyFactor'),
             link(80, 105, 'ShininessExponent'),
             link(81, 106, 'ReflectionFactor'), link(81, 106, 'ShininessExponent'),
             link(82, 107, 'DiffuseColor')]
    yield 'textures_basic', material_scene(mats, objects, links)

    # ---- the link names: 3dsMax's, Bump, the colour variants, an unknown one, a PP link, a Video linked directly, a slot
    # linked twice, the alpha rule reading the LAST DiffuseColor image beside a 3dsMax diffuse
    objects = [texture(60, 'a', T + 'rgb.png'), texture(61, 'b', T + 'rgb2.png'), texture(62, 'c', T + 'gray.png'), texture(63, 'd', T + 'rgba.png'),
               video(70, 'v', T + 'rgb2.png')]
    mats = [material(100, 'Max'), material(101, 'Colours'), material(102, 'Ignored'), material(103, 'Twice'), material(104, 'LastDiffuse'), material(105, 'MaxAfterDiffuse')]
    links = [link(60, 100, '3dsMax|maps|texmap_diffuse'), link(61, 100, '3dsMax|maps|texmap_bump'), link(62, 100, '3dsMax|maps|texmap_reflection'),
             link(60, 101, 'SpecularColor'), link(62, 101, 'ReflectionColor'), link(63, 101, 'TransparentColor'), link(61, 101, 'Bump'), link(62, 101, 'EmissiveFactor'),
             link(60, 102, 'AmbientColor'), pp(61, 102, 'DiffuseColor'), link(70, 102, 'DiffuseColor'),
             link(60, 103, 'DiffuseColor'), link(63, 103, 'DiffuseColor'),
             link(63, 104, 'DiffuseColor'), link(60, 104, '3dsMax|maps|texmap_diffuse'),
             link(60, 105, '3dsMax|maps|texmap_diffuse'), link(63, 105, 'DiffuseColor')]
    yield 'textures_links', material_scene(mats, objects, links)

    # ---- names and paths: a Texture without a Video, a Video named with a path, twins, a .PNG, a .tga holding PNG bytes, the
    # recursive search (a wrong directory, a different case), FileName and Filename fallbacks, an empty RelativeFilename,
    # a Texture whose path differs from its Video's, a composition over twins
    objects = [texture(60, 'lonely', T + 'rgb.png'),
               texture(61, 'pathname', T + 'rgb2.png'), video(71, 'dir/sub/rgb2.png', T + 'rgb2.png'),
               texture(62, 'twin1', T + 'same.png'), video(72, 'same.png', T + 'same.png'),
               texture(63, 'twin2', T + 'other\\same.png'), video(73, 'same.png', T + 'other\\same.png'),
               texture(64, 'upper', T + 'Upper.PNG'),
               texture(65, 'tganamed', T + 'png_named_tga.tga'),
               texture(66, 'wrongdir', 'nowhere\\found.png'),
               texture(67, 'wrongcase', 'SUB\\DEEP\\case.PNG'),
               texture(68, 'filename', filename='Q:\\nowhere\\rgb.png'),
               texture(69, 'emptyrel', filename=T + 'gray.png', empty_relative=True),
               video(79, 'videofile', filename=T + 'gray2.png'), texture(80, 'videofile', T + 'rgb.png'),
               texture(81, 'byvideoname'), video(82, 'Upper.PNG', T + 'Upper.PNG')]
    mats = [material(100, 'Lonely'), material(101, 'PathName'), material(102, 'Twins'), material(103, 'Upper'), material(104, 'TgaNamed'),
            material(105, 'WrongDir'), material(106, 'WrongCase'), material(107, 'FileName'), material(108, 'EmptyRel'), material(109, 'VideoFile'), material(110, 'ByVideoName')]
    links = [link(60, 100, 'DiffuseColor'), link(61, 101, 'DiffuseColor'), link(62, 102, 'ReflectionFactor'), link(63, 102, 'ShininessExponent'),
             link(64, 103, 'DiffuseColor'), link(65, 104, 'DiffuseColor'), link(66, 105, 'DiffuseColor'), link(67, 106, 'DiffuseColor'),
             link(68, 107, 'DiffuseColor'), link(69, 108, 'DiffuseColor'), link(80, 109, 'DiffuseColor'), link(81, 110, 'DiffuseColor')]
    yield 'textures_names', material_scene(mats, objects, links)

    # ---- missing files: the texture dropped; a missing metallic beside a roughness; both missing (nothing: the roughness fill
    # replaced the metallic's white before both were dropped); a missing alpha beside a base; a missing normal map; a file
    # that is not an image (a placeholder); a missing metallic alone (its white G fill stays: a 1x1 white image)
    objects = [texture(60, 'missing', T + 'missing.png'), texture(61, 'rough', T + 'gray.png'), texture(62, 'missing2', T + 'missing2.png'),
               texture(63, 'base', T + 'rgb.png'), texture(64, 'notimage', T + 'notimage.png')]
    mats = [material(100, 'MissingDiffuse', color('DiffuseColor', 0.2, 0.3, 0.4), number('ReflectionFactor', 0.6)), material(101, 'MissingMetal'), material(102, 'MissingBoth'),
            material(103, 'MissingAlpha'), material(104, 'MissingNormal'), material(105, 'NotImage'), material(106, 'MissingMetalAlone')]
    links = [link(60, 100, 'DiffuseColor'), link(60, 101, 'ReflectionFactor'), link(61, 101, 'ShininessExponent'), link(60, 102, 'ReflectionFactor'), link(62, 102, 'ShininessExponent'),
             link(63, 103, 'DiffuseColor'), link(60, 103, 'TransparencyFactor'), link(60, 104, 'NormalMap'), link(64, 105, 'DiffuseColor'), link(60, 106, 'ReflectionFactor')]
    yield 'textures_missing', material_scene(mats, objects, links)

    # ---- embedded Content: a packed image with a bogus path; Content beside an existing file of other pixels (the pixels
    # Blender holds are the file's, the bytes written are the packed data); Content on the Texture rather than the Video
    rgba = images()['tex/rgba.png']
    # ... a Video loaded from disk whose Texture carries Content (the Texture's Content packs over the shared image: has_data
    # is False throughout the step), a Video's Content followed by its Texture's other Content (the last packs), bytes no
    # reader takes packed (no pixels: the texture dropped)
    gray2 = images()['tex/gray2.png']
    objects = [video(70, 'packed.png', 'nowhere\\packed.png', content=rgba), texture(60, 'packed', 'nowhere\\packed.png'),
               video(71, 'overfile.png', T + 'rgb.png', content=rgba), texture(61, 'overfile', T + 'rgb.png'),
               texture(62, 'ontexture', 'nowhere\\ontexture.png', content=images()['tex/gray.png']),
               video(73, 'shared.png', T + 'rgb2.png'), texture(63, 'shared', T + 'rgb2.png', content=rgba),
               video(74, 'twice.png', 'nowhere\\twice.png', content=gray2), texture(64, 'twice', 'nowhere\\twice.png', content=rgba),
               video(75, 'junk.png', 'nowhere\\junk.png', content=b'not an image at all, packed'), texture(65, 'junk', 'nowhere\\junk.png')]
    mats = [material(100, 'Packed'), material(101, 'OverFile'), material(102, 'OnTexture'), material(103, 'SharedPath'), material(104, 'Twice'), material(105, 'Junk')]
    links = [link(60, 100, 'DiffuseColor'), link(61, 101, 'DiffuseColor'), link(62, 102, 'ReflectionFactor'), link(63, 103, 'DiffuseColor'), link(64, 104, 'DiffuseColor'), link(65, 105, 'DiffuseColor')]
    yield 'textures_content', material_scene(mats, objects, links)

    # ---- a JPEG: passed through by its extension; the same file as a normal map (a copied datablock, one written image)
    objects = [texture(60, 'jpg', T + 'tiny.jpg'), video(70, 'tiny.jpg', T + 'tiny.jpg'), texture(61, 'jpgnormal', T + 'tiny.jpg')]
    mats = [material(100, 'Jpeg')]
    yield 'textures_jpeg', material_scene(mats, objects, [link(60, 100, 'DiffuseColor'), link(61, 100, 'NormalMap')])
    # a JPEG inside a composition needs its pixels: left to Blender by name
    objects = [texture(60, 'jpg', T + 'tiny.jpg'), texture(61, 'rough', T + 'gray8.png')]
    yield 'textures_jpeg_composed_fallback', material_scene([material(100, 'JpegMetal')], objects, [link(60, 100, 'ReflectionFactor'), link(61, 100, 'ShininessExponent')])
    # JPEG bytes under a .png name: the exporter encodes them again
    objects = [texture(60, 'jpgpng', T + 'jpeg_named_png.png')]
    yield 'textures_jpeg_named_png_fallback', material_scene([material(100, 'JpegNamedPng')], objects, [link(60, 100, 'DiffuseColor')])

    # ---- the PNG colour types Blender's reader expands: palettes, tRNS, grey + alpha, sub-byte greys - in compositions
    # (their pixels) and as a diffuse (an alpha from tRNS makes a BLEND)
    objects = [texture(60, 'pal', T + 'pal.png'), texture(61, 'palt', T + 'palt.png'), texture(62, 'pal2', T + 'pal2.png'), texture(63, 'rgbt', T + 'rgbt.png'),
               texture(64, 'graya', T + 'graya.png'), texture(65, 'gray1', T + 'gray1.png'), texture(66, 'gray4', T + 'gray4.png'), texture(67, 'gray', T + 'gray.png')]
    mats = [material(100, 'Pal'), material(101, 'PalT'), material(102, 'Pal2'), material(103, 'RgbT'), material(104, 'GrayA'), material(105, 'Gray1Metal'), material(106, 'Gray4Rough'), material(107, 'PalMetal')]
    links = [link(60, 100, 'DiffuseColor'), link(61, 101, 'DiffuseColor'), link(62, 102, 'DiffuseColor'), link(63, 103, 'DiffuseColor'), link(64, 104, 'DiffuseColor'),
             link(65, 105, 'ReflectionFactor'), link(67, 105, 'ShininessExponent'), link(66, 106, 'ShininessExponent'), link(61, 107, 'ReflectionFactor')]
    yield 'textures_png_types', material_scene(mats, objects, links)

    # ---- wrap modes and transforms: a clamp alone (EXTEND, an identity Mapping node), a Rotation about X (skipped by the
    # exporter), a zero Scaling (inverted to 1), a Scaling the alpha slot inherits
    objects = [texture(60, 'clampu', T + 'rgb.png', props=(P('WrapModeU', 'enum', '', 1),)),
               texture(61, 'clampv', T + 'rgb2.png', props=(P('WrapModeV', 'enum', '', 1),)),
               texture(62, 'rotx', T + 'gray.png', props=(vec('Rotation', 10, 0, 0),)),
               texture(63, 'scale0', T + 'gray2.png', props=(vec('Scaling', 0, 0, 0),)),
               texture(64, 'clamprgba', T + 'rgba.png', props=(P('WrapModeU', 'enum', '', 1), P('WrapModeV', 'enum', '', 1)))]
    mats = [material(100, 'ClampU'), material(101, 'ClampV'), material(102, 'RotX'), material(103, 'Scale0'), material(104, 'ClampRgba')]
    links = [link(60, 100, 'DiffuseColor'), link(61, 101, 'NormalMap'), link(62, 102, 'ReflectionFactor'), link(63, 103, 'ShininessExponent'), link(64, 104, 'DiffuseColor')]
    yield 'textures_wrap', material_scene(mats, objects, links)
    # a transform the exporter writes as KHR_texture_transform: left to Blender by name
    objects = [texture(60, 'moved', T + 'rgb.png', props=(vec('Translation', 0.5, 0, 0),))]
    yield 'textures_transform_fallback', material_scene([material(100, 'Moved')], objects, [link(60, 100, 'DiffuseColor')])
    # an enum with flags: Blender's importer asserts
    objects = [texture(60, 'badenum', T + 'rgb.png', props=(Node('P', [prop('S', 'WrapModeU'), prop('S', 'enum'), prop('S', ''), prop('S', 'A'), prop('I', 1)]),))]
    yield 'textures_assert_fallback', material_scene([material(100, 'BadEnum')], objects, [link(60, 100, 'DiffuseColor')])

    # ---- 16-bit: a 16-bit RGB diffuse passes its bytes through (no alpha: planes 96); a 16-bit GREY diffuse has depth 32
    # (8 planes x 4) and goes into the alpha composition, whose pixels Blender takes from a float buffer: left by name
    objects = [texture(60, 'gray16', T + 'gray16.png')]
    yield 'textures_16bit_fallback', material_scene([material(100, 'Gray16')], objects, [link(60, 100, 'DiffuseColor')])
    # images of different sizes composed: Blender scales one
    objects = [texture(60, 'metal', T + 'gray.png'), texture(61, 'big', T + 'big.png')]
    yield 'textures_sizes_fallback', material_scene([material(100, 'Sizes')], objects, [link(60, 100, 'ReflectionFactor'), link(61, 100, 'ShininessExponent')])
    # a TGA, a TIFF: Blender reads them and the exporter encodes them again as PNG
    objects = [texture(60, 'tga', T + 'real.tga')]
    yield 'textures_tga_fallback', material_scene([material(100, 'Tga')], objects, [link(60, 100, 'DiffuseColor')])
    objects = [texture(60, 'tif', T + 'real.tif')]
    yield 'textures_tiff_fallback', material_scene([material(100, 'Tiff')], objects, [link(60, 100, 'DiffuseColor')])

    damaged = images()
    names = ['header_only.png', 'bad_crc.png', 'bad_filter.png', 'short_pixels.png', 'header_only.jpg']
    mats = [material(100 + i, 'Broken%d' % i) for i in range(6)]
    objects = [texture(60 + i, 'Broken%d' % i, relative='tex/' + name) for i, name in enumerate(names)]
    objects.append(texture(65, 'BrokenContent', relative='tex/rgb2.png', content=damaged['tex/bad_crc.png']))
    links = [link(60 + i, 100 + i, 'ReflectionFactor' if i == 2 else 'DiffuseColor') for i in range(6)]
    yield 'images_invalid', material_scene(mats, objects, links)
    yield 'png_tolerated_trailers', material_scene([material(100, 'NoEnd'), material(101, 'ExtraPixels')],
        [texture(60, 'NoEnd', relative='tex/no_iend.png'), texture(61, 'ExtraPixels', relative='tex/extra_pixels.png')],
        [link(60, 100, 'DiffuseColor'), link(61, 101, 'DiffuseColor')])
    yield 'cached_image_properties', material_scene([material(100, 'Cached')],
        [video(70, 'Video', relative='tex/rgb.png'), texture(60, 'Cached', relative='tex/rgb.png',
            props=[Node('P', [prop('S', 'IgnoredShortProperty')])])], [link(60, 100, 'DiffuseColor')])
    yield 'unused_equal_image', material_scene([material(100, 'Composed')],
        [texture(60, 'Transparent', relative='tex/transparent.png'), texture(61, 'Unused', relative='tex/unused_equal.png')],
        [link(60, 100, 'DiffuseColor')])
