"""deterministic.py <dll> - zero the two fields binutils still stamps after --no-insert-timestamp: the export
directory's TimeDateStamp and the optional header's CheckSum (a user-mode DLL loads with a zero checksum; the
checksum is only enforced for drivers). With them zero, two builds of the same sources are byte-equal, and the gate
can hold the committed DLL to a fresh build."""
import struct, sys

path = sys.argv[1]
data = bytearray(open(path, 'rb').read())
e_lfanew = struct.unpack_from('<I', data, 0x3C)[0]
assert data[e_lfanew:e_lfanew + 4] == b'PE\0\0', 'not a PE file'
coff = e_lfanew + 4
sections = struct.unpack_from('<H', data, coff + 2)[0]
opt_size = struct.unpack_from('<H', data, coff + 16)[0]
opt = coff + 20
magic = struct.unpack_from('<H', data, opt)[0]
assert magic == 0x20B, 'not PE32+'
struct.pack_into('<I', data, opt + 64, 0)                      # CheckSum
export_rva, export_size = struct.unpack_from('<II', data, opt + 112)   # data directory [0]: export table
sect = opt + opt_size
for i in range(sections):
    at = sect + 40 * i
    vsize, vaddr, rawsize, rawptr = struct.unpack_from('<IIII', data, at + 8)
    if vaddr <= export_rva < vaddr + max(vsize, rawsize):
        struct.pack_into('<I', data, rawptr + (export_rva - vaddr) + 4, 0)   # IMAGE_EXPORT_DIRECTORY.TimeDateStamp
        break
else:
    sys.exit('export directory not found in any section')
open(path, 'wb').write(data)
