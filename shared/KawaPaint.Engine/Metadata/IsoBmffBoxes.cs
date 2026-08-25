using System.Buffers.Binary;

namespace KawaPaint.Engine.Metadata;

/// <summary>Minimal, strict ISO-BMFF top-level box walking shared by JPEG XL and JP2 metadata.
/// Supports ordinary, extended-size, and final-to-EOF boxes; it deliberately does not recurse
/// into structural superboxes.</summary>
internal static class IsoBmffBoxes
{
    internal readonly record struct Box(int Offset, int HeaderLength, int Length, string Type)
    {
        public int PayloadOffset => Offset + HeaderLength;
        public int PayloadLength => Length - HeaderLength;
        public int End => Offset + Length;
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, int offset, out Box box)
    {
        box = default;
        if (offset < 0 || offset + 8 > bytes.Length) return false;
        uint shortLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
        string type = System.Text.Encoding.ASCII.GetString(bytes.Slice(offset + 4, 4));
        int header = 8;
        long length;
        if (shortLength == 1)
        {
            if (offset + 16 > bytes.Length) return false;
            ulong extended = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset + 8, 8));
            if (extended > int.MaxValue) return false;
            length = (long)extended;
            header = 16;
        }
        else if (shortLength == 0)
        {
            length = bytes.Length - offset;
        }
        else length = shortLength;

        if (length < header || offset + length > bytes.Length) return false;
        box = new Box(offset, header, (int)length, type);
        return true;
    }

    public static bool TryWalk(ReadOnlySpan<byte> bytes, out List<Box> boxes)
    {
        boxes = new List<Box>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (!TryRead(bytes, offset, out Box box)) return false;
            boxes.Add(box);
            offset = box.End;
        }
        return offset == bytes.Length;
    }

    public static byte[] BoxBytes(string type, ReadOnlySpan<byte> payload)
    {
        if (type.Length != 4) throw new ArgumentException("Box type must contain four ASCII characters.", nameof(type));
        byte[] output = new byte[checked(8 + payload.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)output.Length);
        System.Text.Encoding.ASCII.GetBytes(type, output.AsSpan(4, 4));
        payload.CopyTo(output.AsSpan(8));
        return output;
    }
}
