using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CnoawaProtocol
{
    public static class FrameCodec
    {
        public const int HeaderSize = 4;
        public const int MaxFrameSize = 65536;

        public static byte[] Encode(MessageType type, ReadOnlySpan<byte> payload)
        {
            var frameLen = 1 + payload.Length;
            var frame = new byte[HeaderSize + frameLen];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frameLen);
            frame[HeaderSize] = (byte)type;
            if (payload.Length > 0)
                payload.CopyTo(frame.AsSpan(HeaderSize + 1));
            return frame;
        }

        public static byte[] EncodeEmpty(MessageType type)
        {
            var frame = new byte[HeaderSize + 1];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, 1);
            frame[HeaderSize] = (byte)type;
            return frame;
        }

        public static async Task<(MessageType type, byte[] payload)?> ReadFrameAsync(
            Stream stream, byte[] headerBuf, byte[] payloadBuf, CancellationToken ct)
        {
            if (!await ReadExactAsync(stream, headerBuf, 0, HeaderSize, ct))
                return null;

            var frameLen = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf);
            if (frameLen == 0 || frameLen > MaxFrameSize)
                return null;

            var len = (int)frameLen;
            var buf = len <= payloadBuf.Length ? payloadBuf : new byte[len];

            if (!await ReadExactAsync(stream, buf, 0, len, ct))
                return null;

            var type = (MessageType)buf[0];
            var payload = len > 1 ? buf.AsSpan(1, len - 1).ToArray() : Array.Empty<byte>();
            return (type, payload);
        }

        static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
                if (read == 0) return false;
                totalRead += read;
            }
            return true;
        }
    }
}
