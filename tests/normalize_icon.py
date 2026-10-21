"""Preserve artwork; convert small PNG ICO frames to conventional 32-bit DIBs.
Run manually with Pillow installed when regenerating the application icon.
"""
from io import BytesIO
from pathlib import Path
import struct
from PIL import Image


def normalize(path):
    data = path.read_bytes()
    _, _, count = struct.unpack_from('<HHH', data)
    frames = []
    for index in range(count):
        w, h, _, _, _, _, length, offset = struct.unpack_from('<BBBBHHII', data, 6 + 16 * index)
        size = w or 256
        payload = data[offset:offset + length]
        if size < 256 and payload.startswith(b'\x89PNG'):
            image = Image.open(BytesIO(payload)).convert('RGBA')
            pixels = image.tobytes()
            xor = bytearray()
            mask = bytearray()
            stride = ((size + 31) // 32) * 4
            for y in range(size - 1, -1, -1):
                row_mask = bytearray(stride)
                for x in range(size):
                    start = 4 * (y * size + x)
                    r, g, b, a = pixels[start:start + 4]
                    xor.extend((b, g, r, a))
                    if a == 0:
                        row_mask[x // 8] |= 0x80 >> (x % 8)
                mask.extend(row_mask)
            payload = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0,
                                  len(xor) + len(mask), 0, 0, 0, 0) + xor + mask
        frames.append((w, h, payload))
    offset = 6 + 16 * count
    directory = bytearray(struct.pack('<HHH', 0, 1, count))
    for w, h, payload in frames:
        directory.extend(struct.pack('<BBBBHHII', w, h, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    path.write_bytes(directory + b''.join(payload for _, _, payload in frames))
    print(f'Normalized {count} frames: {path}')


if __name__ == '__main__':
    normalize(Path(__file__).resolve().parents[1] / 'Limiter/Assets/Limiter.ico')
