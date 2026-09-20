"""Read dynamic linkage metadata from the ELF64 artifacts produced by the Android recipe."""
from __future__ import annotations

import struct


def inspect_linkage(data: bytes) -> dict:
    if len(data) < 64 or data[:6] != b'\x7fELF\x02\x01':
        raise ValueError('Expected a little-endian ELF64 library')
    header = struct.unpack_from('<HHIQQQIHHHHHH', data, 16)
    offset, size, count = header[5], header[10], header[11]
    if size != 64 or not count or offset + size * count > len(data):
        raise ValueError('Invalid or missing ELF section table')
    sections = [struct.unpack_from('<IIQQQQIIQQ', data, offset + size * i) for i in range(count)]

    def contents(section: tuple) -> bytes:
        start, length = section[4], section[5]
        if start + length > len(data):
            raise ValueError('Truncated ELF section')
        return data[start:start + length]

    def strings(section: tuple) -> bytes:
        link = section[6]
        if link >= count or sections[link][1] != 3:
            raise ValueError('Invalid ELF string-table link')
        return contents(sections[link])

    def string(table: bytes, start: int) -> str:
        end = table.find(b'\0', start)
        if start >= len(table) or end < 0:
            raise ValueError('Invalid ELF string offset')
        return table[start:end].decode('utf-8')

    exports, needed, sonames = set(), [], []
    for section in sections:
        kind, entry_size = section[1], section[9]
        if kind not in (6, 11):
            continue
        table, payload = strings(section), contents(section)
        expected_size = 16 if kind == 6 else 24
        if entry_size != expected_size or len(payload) % entry_size:
            raise ValueError('Invalid ELF dynamic entry size')
        for start in range(0, len(payload), entry_size):
            if kind == 6:
                tag, value = struct.unpack_from('<qQ', payload, start)
                if tag == 0:
                    break
                if tag == 1:
                    needed.append(string(table, value))
                elif tag == 14:
                    sonames.append(string(table, value))
                elif tag in (15, 29):
                    raise ValueError('Unexpected RPATH/RUNPATH in packaged Android library')
            else:
                name, info, visibility, index, _, _ = struct.unpack_from('<IBBHQQ', payload, start)
                if index and info >> 4 in (1, 2) and visibility & 3 in (0, 3):
                    exports.add(string(table, name))
    if len(sonames) != 1:
        raise ValueError('Expected exactly one ELF SONAME')
    return {'soname': sonames[0], 'needed': needed, 'exports': sorted(exports)}
