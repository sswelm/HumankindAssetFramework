"""The ORACLE side of tools/fbx_drill.sh: an independent reader of the binary FBX record tree, printing one line per
record - the same dump editor/FbxDocument.cs's drill prints - so the two readers are compared row for row.

    python fbx_nodes.py <file.fbx> [<file.fbx> ...]   -> the dump of each file to stdout, files separated by '=== <path>'

The dump: 'version <v>' first; then per record, pre-order: '<depth> <name> | <type codes> | <value> <value> ...' where a
Y/I/L is decimal, a C is 0/1, an F is the 8 hex digits of its bits, a D the 16, an S is 'S:<bytes>:<sha1 of the bytes>',
an R 'R:<bytes>:<sha1>', and an array '<t>[<count>]:<sha1 of the little-endian bytes of the decoded values>'
(a b array's bytes are 0/1). Last, 'records <n>'. Nothing is interpreted; a value is the file's.

Written against the format's description as ufbx and Blender's io_scene_fbx read it, with no code shared with the C#
side: the agreement of two readers written apart is the claim.
"""
import hashlib, struct, sys, zlib

ARRAY = {'f': ('f', 4), 'd': ('d', 8), 'l': ('q', 8), 'i': ('i', 4), 'b': ('b', 1), 'c': ('B', 1)}


def sha(b):
    return hashlib.sha1(b).hexdigest()


def read_records(data):
    if not data.startswith(b'Kaydara FBX Binary  \0\x1a\0') or len(data) < 27:
        raise ValueError('not a little-endian binary FBX')
    version = struct.unpack_from('<I', data, 23)[0]
    wide = version >= 7500
    hdr = '<QQQ' if wide else '<III'
    hdr_len = 24 if wide else 12

    def prop(pos):
        t = data[pos:pos + 1].decode('latin1'); pos += 1
        if t == 'Y': return ('Y', struct.unpack_from('<h', data, pos)[0]), pos + 2
        if t == 'Z': return ('Z', struct.unpack_from('<b', data, pos)[0]), pos + 1
        if t == 'C': return ('C', data[pos]), pos + 1
        if t == 'B': return ('B', 1 if data[pos] else 0), pos + 1
        if t == 'I': return ('I', struct.unpack_from('<i', data, pos)[0]), pos + 4
        if t == 'F': return ('F', data[pos:pos + 4]), pos + 4
        if t == 'D': return ('D', data[pos:pos + 8]), pos + 8
        if t == 'L': return ('L', struct.unpack_from('<q', data, pos)[0]), pos + 8
        if t in 'SR':
            n = struct.unpack_from('<I', data, pos)[0]; pos += 4
            return (t, data[pos:pos + n]), pos + n
        if t in ARRAY:
            n, enc, clen = struct.unpack_from('<III', data, pos); pos += 12
            raw = data[pos:pos + clen]; pos += clen
            if enc == 1:
                decoder = zlib.decompressobj()
                raw = decoder.decompress(raw) + decoder.flush()
                if not decoder.eof or decoder.unused_data or decoder.unconsumed_tail:
                    raise ValueError('incomplete zlib array or trailing data')
            elif enc != 0:
                raise ValueError('unknown array encoding %d' % enc)
            fmt, size = ARRAY[t]
            if len(raw) != n * size: raise ValueError('array %s[%d] decoded to %d bytes' % (t, n, len(raw)))
            if t == 'b': raw = bytes(1 if x else 0 for x in raw)
            return (t, n, enc, raw), pos
        raise ValueError('unknown property type %r at %d' % (t, pos))

    out = []

    def record(pos, depth):
        start = pos
        end, nprops, plen = struct.unpack_from(hdr, data, pos); pos += hdr_len
        nlen = data[pos]; pos += 1
        name = data[pos:pos + nlen].decode('utf-8'); pos += nlen
        if end == 0 and nprops == 0 and plen == 0 and nlen == 0: return None, pos
        props = []
        for _ in range(nprops):
            p, pos = prop(pos); props.append(p)
        if pos != start + hdr_len + 1 + nlen + plen: raise ValueError('record %r at %d: property bytes disagree' % (name, start))
        out.append((depth, name, props))
        has_scope, closed = pos < end, False
        while pos < end:
            child, pos = record(pos, depth + 1)
            if child is None:
                closed = True
                break
        if has_scope and not closed: raise ValueError('record %r: missing nested null record' % name)
        if pos != end: raise ValueError('record %r at %d: ends at %d, not %d' % (name, start, pos, end))
        return True, end

    pos = 27
    while True:
        if pos >= len(data): raise ValueError('missing top-level null record')
        r, pos = record(pos, 0)
        if r is None: break
    return version, out


def fmt_prop(p):
    t = p[0]
    if t in 'ZYILCB': return str(p[1])
    if t in 'FD': return p[1][::-1].hex()      # the bits, big-endian hex as C# prints them
    if t in 'SR': return '%s:%d:%s' % (t, len(p[1]), sha(p[1]))
    return '%s[%d]:%s' % (t, p[1], sha(p[3]))   # the storage encoding is not a value: both readers decode it away


def dump(path):
    data = open(path, 'rb').read()
    version, recs = read_records(data)
    lines = ['version %d' % version]
    for depth, name, props in recs:
        lines.append('%d %s | %s | %s' % (depth, name, ''.join(p[0] for p in props), ' '.join(fmt_prop(p) for p in props)))
    lines.append('records %d' % len(recs))
    return '\n'.join(lines)


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--reject':
        failed = 0
        for path in sys.argv[2:]:
            try:
                read_records(open(path, 'rb').read())
                print('FAIL accepted malformed FBX: ' + path)
                failed += 1
            except (ValueError, struct.error, zlib.error, IndexError):
                print('REFUSED ' + path)
        sys.exit(1 if failed or len(sys.argv) < 3 else 0)
    for path in sys.argv[1:]:
        print('=== ' + path)
        print(dump(path))
