using System;
using System.Buffers.Binary;

namespace ChallengeEditor.Psg;

// Decoder for EA's RefPack LZ77-style compression (used by Skate 2/3 PSF
// CompressedChunkArena chunks). Format reverse-engineered from
// "Stream File Tool.exe" sub_140035020 (the encoder).
//
// Header:
//   byte 0: 0x10 (3-byte uncompressed size, files <16MB) or 0x90 (4-byte size).
//   byte 1: 0xFB
//   bytes 2..N: uncompressed size, big-endian (3 or 4 bytes).
// Body opcodes:
//   0x00..0x7F  short ref     2 bytes   len 3..10,    offset 1..1024
//   0x80..0xBF  medium ref    3 bytes   len 4..67,    offset 1..16384
//   0xC0..0xDF  long ref      4 bytes   len 5..1028,  offset 1..131072
//   0xE0..0xFB  literal run   1 byte    4..112 literal bytes follow
//   0xFC..0xFF  end-of-stream 1 byte    0..3 trailing literal bytes
public static class RefPack
{
    public static byte[] Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length < 5) throw new InvalidDataException("RefPack stream too short.");

        byte flag = input[0];
        if (input[1] != 0xFB) throw new InvalidDataException($"RefPack signature byte mismatch: expected 0xFB at [1], got 0x{input[1]:X2}.");

        int headerSize;
        int uncompressed;
        if ((flag & 0x80) == 0)
        {
            // 0x10-style: 3-byte big-endian uncompressed size.
            uncompressed = (input[2] << 16) | (input[3] << 8) | input[4];
            headerSize = 5;
        }
        else
        {
            // 0x90-style: 4-byte big-endian uncompressed size.
            if (input.Length < 6) throw new InvalidDataException("RefPack 0x90 header truncated.");
            uncompressed = (input[2] << 24) | (input[3] << 16) | (input[4] << 8) | input[5];
            headerSize = 6;
        }

        if (uncompressed < 0) throw new InvalidDataException($"RefPack uncompressed size negative: {uncompressed}.");

        byte[] output = new byte[uncompressed];
        int outPos = 0;
        int inPos = headerSize;

        while (inPos < input.Length)
        {
            byte op = input[inPos++];

            int literals;
            int matchLen;
            int matchOff;

            if ((op & 0x80) == 0)
            {
                // Short ref: 0LLDDDLL DDDDDDDD
                if (inPos >= input.Length) throw new InvalidDataException("RefPack truncated in short ref.");
                byte b1 = input[inPos++];
                literals = op & 0x03;
                matchLen = ((op >> 2) & 0x07) + 3;
                matchOff = (((op & 0x60) << 3) | b1) + 1;
            }
            else if ((op & 0x40) == 0)
            {
                // Medium ref: 10LLLLLL DDDDDDDD DDDDDDDD (length-4 in low 6 bits, literals in high 2 of next byte)
                if (inPos + 1 >= input.Length) throw new InvalidDataException("RefPack truncated in medium ref.");
                byte b1 = input[inPos++];
                byte b2 = input[inPos++];
                literals = (b1 >> 6) & 0x03;
                matchLen = (op & 0x3F) + 4;
                matchOff = (((b1 & 0x3F) << 8) | b2) + 1;
            }
            else if ((op & 0x20) == 0)
            {
                // Long ref: 110D LLLD  DDDD DDDD  DDDD DDDD  LLLL LLLL
                // op bits: 110 (high), then offset bit 16, length high bits, literals.
                if (inPos + 2 >= input.Length) throw new InvalidDataException("RefPack truncated in long ref.");
                byte b1 = input[inPos++];
                byte b2 = input[inPos++];
                byte b3 = input[inPos++];
                literals = op & 0x03;
                matchLen = (((op & 0x0C) << 6) | b3) + 5;
                matchOff = (((op & 0x10) << 12) | (b1 << 8) | b2) + 1;
            }
            else if (op < 0xFC)
            {
                // Literal-only run: ((op & 0x1F) + 1) * 4 literals follow.
                literals = ((op & 0x1F) + 1) << 2;
                if (inPos + literals > input.Length) throw new InvalidDataException("RefPack truncated in literal run.");
                if (outPos + literals > output.Length) throw new InvalidDataException("RefPack literal run overruns output.");
                input.Slice(inPos, literals).CopyTo(output.AsSpan(outPos, literals));
                inPos += literals;
                outPos += literals;
                continue;
            }
            else
            {
                // End-of-stream: 1..3 trailing literals (or 0).
                literals = op & 0x03;
                if (inPos + literals > input.Length) throw new InvalidDataException("RefPack truncated in EOS literals.");
                if (outPos + literals > output.Length) throw new InvalidDataException("RefPack EOS literals overrun output.");
                input.Slice(inPos, literals).CopyTo(output.AsSpan(outPos, literals));
                inPos += literals;
                outPos += literals;
                break;
            }

            // Literals before the back-ref.
            if (literals > 0)
            {
                if (inPos + literals > input.Length) throw new InvalidDataException("RefPack truncated in opcode literals.");
                if (outPos + literals > output.Length) throw new InvalidDataException("RefPack opcode literals overrun output.");
                input.Slice(inPos, literals).CopyTo(output.AsSpan(outPos, literals));
                inPos += literals;
                outPos += literals;
            }

            // Back-ref copy. Source overlaps with destination for run-length style copies, so byte-by-byte.
            if (matchOff > outPos) throw new InvalidDataException($"RefPack match offset {matchOff} exceeds current output position {outPos}.");
            if (outPos + matchLen > output.Length) throw new InvalidDataException("RefPack back-ref overruns output.");
            int src = outPos - matchOff;
            for (int i = 0; i < matchLen; i++) output[outPos + i] = output[src + i];
            outPos += matchLen;
        }

        if (outPos != uncompressed)
            throw new InvalidDataException($"RefPack decode size mismatch: produced {outPos}, header says {uncompressed}.");

        return output;
    }

    public static bool LooksLikeRefPack(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length - offset < 2) return false;
        byte b0 = data[offset];
        return data[offset + 1] == 0xFB && (b0 == 0x10 || b0 == 0x90);
    }
}
