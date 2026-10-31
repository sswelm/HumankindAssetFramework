"""Synthetic binary FBX files for the shapes the user's sources do NOT have (every one of them is a 7400/7500/7700
export of Blender or the FBX SDK: 32- or 64-bit records, zlib arrays, Phong materials, no empty names). A reader that
only ever saw those has not been tested on the rest of the format. Each fixture is small, deterministic and named for
the one shape it exercises; the drill dumps every one with both readers.

    python fbx_fixtures.py <outDir>        -> writes every fixture, prints one path per line

The writer here is a third implementation of the format (the two readers are compared on its output): a record is
EndOffset, NumProperties, PropertyListLen (uint32, or uint64 from version 7500), the name, the properties, the
children and - when there are children - a null record; the top level ends with a null record and a footer.
"""
import os, struct, sys, zlib


def prop(t, v, compress=False):
    if t == 'Y': return b'Y' + struct.pack('<h', v)
    if t == 'C': return b'C' + bytes([int(v)])
    if t == 'B': return b'B' + (b'\x01' if v else b'\x00')
    if t == 'Z': return b'Z' + struct.pack('<b', v)
    if t == 'I': return b'I' + struct.pack('<i', v)
    if t == 'F': return b'F' + struct.pack('<f', v)
    if t == 'D': return b'D' + struct.pack('<d', v)
    if t == 'L': return b'L' + struct.pack('<q', v)
    if t == 'S': raw = v.encode('utf-8'); return b'S' + struct.pack('<I', len(raw)) + raw
    if t == 'R': return b'R' + struct.pack('<I', len(v)) + v
    fmt = {'f': 'f', 'd': 'd', 'l': 'q', 'i': 'i', 'b': 'b', 'c': 'B'}[t]
    raw = struct.pack('<%d%s' % (len(v), fmt), *v)
    if compress:
        z = zlib.compress(raw, 9)
        return t.encode() + struct.pack('<III', len(v), 1, len(z)) + z
    return t.encode() + struct.pack('<III', len(v), 0, len(raw)) + raw


class Node:
    def __init__(self, name, props=(), children=()):
        self.name, self.props, self.children = name, list(props), list(children)


def encode(node, at, wide):
    """The bytes of a record that starts at offset `at`."""
    body = b''.join(node.props)
    name = node.name.encode('utf-8')
    hdr_len = 24 if wide else 12
    pos = at + hdr_len + 1 + len(name) + len(body)
    kids = b''
    for c in node.children:
        k = encode(c, pos, wide); kids += k; pos += len(k)
    if node.children:
        kids += null_record(wide); pos += hdr_len + 1
    end = pos
    hdr = struct.pack('<QQQ' if wide else '<III', end, len(node.props), len(body))
    return hdr + bytes([len(name)]) + name + body + kids


def null_record(wide):
    return b'\0' * ((24 if wide else 12) + 1)


def file_bytes(version, nodes):
    wide = version >= 7500
    out = bytearray(b'Kaydara FBX Binary  \0\x1a\0' + struct.pack('<I', version))
    for n in nodes:
        out += encode(n, len(out), wide)
    out += null_record(wide)
    out += bytes(16) + bytes(4) + struct.pack('<I', version) + bytes(120) + bytes.fromhex('f85a8c6a de f5 d9 7e ec e9 0c e3 75 8f 29 0b'.replace(' ', ''))   # a footer the readers never read
    return bytes(out)


def write_fbx(path, version, nodes):
    with open(path, 'wb') as f: f.write(file_bytes(version, nodes))


def bad_fixtures():
    """Framing and compressed-input regressions: both readers must refuse, including under Unity's Mono."""
    for version in (7400, 7500):
        header = file_bytes(version, [])[:27]
        yield 'missing_root_%d' % version, header
        yield 'cut_after_node_%d' % version, header + encode(Node('Last', [prop('I', 1)]), 27, version >= 7500)
        bad = bytearray(file_bytes(version, [Node('Parent', [], [Node('Child')])]))
        fmt, size = ('<Q', 25) if version >= 7500 else ('<I', 13)
        end, = struct.unpack_from(fmt, bad, 27)
        struct.pack_into(fmt, bad, 27, end - size)
        yield 'missing_scope_%d' % version, bad
    bad = bytearray(file_bytes(7400, [])); bad[21] = 0
    yield 'wrong_magic', bad
    bad = bytearray(file_bytes(7400, [])); bad[22] = 1
    yield 'unsupported_endian', bad

    def array(z, count=4, encoding=1):
        return file_bytes(7400, [Node('Array', [b'i' + struct.pack('<III', count, encoding, len(z)) + z])])

    raw = struct.pack('<4i', 5, 6, 7, 8)
    z = zlib.compress(raw)
    yield 'no_deflate_block', array(b'\x78\x9c\0\0\0\1', 0)
    yield 'oversize_count', array(z, 0xffffffff)
    yield 'short_decoded_data', array(z, 0x10000000)
    yield 'unknown_encoding', array(raw, encoding=7)
    yield 'preset_dictionary', array(z[:1] + b'\xbb' + z[2:])
    bad = bytearray(z); bad[0] = 0x88
    bad[1] = next(flag for flag in range(32) if ((bad[0] << 8) | flag) % 31 == 0)
    yield 'invalid_window', array(bad)
    bad = bytearray(z); bad[-1] ^= 1
    yield 'wrong_adler', array(bad)
    for cut in (1, 2, 4):
        yield 'cut_deflate_%d' % cut, array(z[:-4-cut] + z[-4:])
    for count in (1, 2, 1024):
        yield 'trailing_deflate_%d' % count, array(z[:-4] + b'\x2a' * count + z[-4:])


def fixtures():
    every_type = Node('Types', [prop('Y', -7), prop('C', True), prop('C', False), prop('I', -123456), prop('F', 1.5), prop('F', -0.0),
                                prop('D', 2.0 ** -1074), prop('D', float('inf')), prop('L', -(2 ** 40)), prop('S', 'héllo\x00\x01world'),
                                prop('R', bytes(range(256))), prop('i', [1, -2, 3]), prop('l', [2 ** 40, -1]), prop('f', [0.5, -0.25]),
                                prop('d', [1e300, -1e-300]), prop('b', [True, False, True]),
                                prop('C', 255), prop('B', True), prop('B', False), prop('Z', -127), prop('c', [0, 127, 255], True)])
    compressed = Node('Compressed', [prop('i', list(range(1000)), True), prop('d', [x * 0.001 for x in range(500)], True),
                                     prop('f', [x * 0.5 for x in range(300)], True), prop('l', [x * 7 for x in range(100)], True),
                                     prop('b', [x % 2 == 0 for x in range(64)], True)])
    empties = Node('Empty', [prop('i', []), prop('d', []), prop('S', ''), prop('R', b''), prop('b', [], True)])
    deep = Node('L0')
    cur = deep
    for i in range(1, 40):
        nxt = Node('L%d' % i, [prop('I', i)]); cur.children.append(nxt); cur = nxt
    noname = Node('', [prop('I', 1)], [Node('', [], [Node('leaf', [prop('S', 'x')])])])
    siblings = Node('Siblings', [], [Node('A', [prop('I', 1)]), Node('A', [prop('I', 2)]), Node('B', [], [Node('A', [prop('I', 3)])])])
    long_name = Node('N' * 255, [prop('I', 255)])
    yield 'fx_types_7400', 7400, [every_type, compressed, empties]
    yield 'fx_types_7500_wide', 7500, [every_type, compressed, empties]
    yield 'fx_types_7700_wide', 7700, [every_type, compressed, empties]
    yield 'fx_deep_nesting', 7400, [deep]
    yield 'fx_empty_names', 7400, [noname]
    yield 'fx_siblings', 7500, [siblings, long_name]
    yield 'fx_no_records', 7400, []
    yield 'fx_props_only', 7400, [Node('Only', [prop('D', 3.25), prop('S', 'no children')])]
    # the shapes of a real file, small: a header extension, global settings with a Properties70 block, one geometry
    p70 = Node('Properties70', [], [Node('P', [prop('S', 'UpAxis'), prop('S', 'int'), prop('S', 'Integer'), prop('S', ''), prop('I', 1)]),
                                    Node('P', [prop('S', 'UnitScaleFactor'), prop('S', 'double'), prop('S', 'Number'), prop('S', ''), prop('D', 100.0)])])
    objects = Node('Objects', [], [Node('Geometry', [prop('L', 1001), prop('S', 'Cube\x00\x01Geometry'), prop('S', 'Mesh')],
                                        [Node('Vertices', [prop('d', [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], True)]),
                                         Node('PolygonVertexIndex', [prop('i', [0, 1, 2, -4], True)]),
                                         Node('GeometryVersion', [prop('I', 124)])])])
    yield 'fx_scene_shape', 7400, [Node('FBXHeaderExtension', [], [Node('FBXVersion', [prop('I', 7400)]), Node('Creator', [prop('S', 'fixtures')])]),
                                   Node('GlobalSettings', [], [Node('Version', [prop('I', 1000)]), p70]), objects,
                                   Node('Connections', [], [Node('C', [prop('S', 'OO'), prop('L', 1001), prop('L', 0)])])]


if __name__ == '__main__':
    reject = sys.argv[1] == '--bad'
    out = sys.argv[2] if reject else sys.argv[1]
    os.makedirs(out, exist_ok=True)
    if reject:
        for name, data in bad_fixtures():
            path = os.path.join(out, name + '.fbx')
            with open(path, 'wb') as stream: stream.write(data)
            print(path.replace('\\', '/'))
    else:
        for name, version, nodes in fixtures():
            path = os.path.join(out, name + '.fbx')
            write_fbx(path, version, nodes)
            print(path.replace('\\', '/'))
