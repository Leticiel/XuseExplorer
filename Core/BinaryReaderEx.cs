using System;
using System.IO;

namespace XuseExplorer.Core
{
    public class BinaryReaderEx : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[8];

        public long Length => _stream.Length;

        public BinaryReaderEx(Stream stream)
        {
            _stream = stream;
        }

        public void Seek(long offset)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
        }

        public byte ReadByte(long offset)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            int b = _stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            return (byte)b;
        }

        public ushort ReadUInt16(long offset)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            ReadExact(_buffer, 2);
            return (ushort)(_buffer[0] | (_buffer[1] << 8));
        }

        public short ReadInt16(long offset)
        {
            return (short)ReadUInt16(offset);
        }

        public uint ReadUInt32(long offset)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            ReadExact(_buffer, 4);
            return (uint)_buffer[0] | ((uint)_buffer[1] << 8) | ((uint)_buffer[2] << 16) | ((uint)_buffer[3] << 24);
        }

        public int ReadInt32(long offset)
        {
            return (int)ReadUInt32(offset);
        }

        public long ReadInt64(long offset)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            ReadExact(_buffer, 8);
            return BitConverter.ToInt64(_buffer, 0);
        }

        public byte[] ReadBytes(long offset, int count)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = _stream.Read(buf, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return buf;
        }

        public int Read(long offset, byte[] buffer, int bufOffset, int count)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int read = _stream.Read(buffer, bufOffset + total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        public bool AsciiEqual(long offset, string text)
        {
            var bytes = ReadBytes(offset, text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (bytes[i] != (byte)text[i])
                    return false;
            }
            return true;
        }

        public string ReadAscii(long offset, int length)
        {
            var bytes = ReadBytes(offset, length);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = (char)bytes[i];
            return new string(chars);
        }

        private void ReadExact(byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = _stream.Read(buf, total, count - total);
                if (read <= 0) throw new EndOfStreamException();
                total += read;
            }
        }

        public void Dispose()
        {
        }
    }
}
