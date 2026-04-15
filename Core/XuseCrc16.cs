using System;

namespace XuseExplorer.Core
{
    public static class XuseCrc16
    {
        public static ushort Compute(byte[] data, int offset, int length, ushort init = 0)
        {
            ushort crc = init;
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                for (int bit = 7; bit >= 0; bit--)
                {
                    int top = (crc >> 15) & 1;
                    int dataBit = (b >> bit) & 1;
                    crc = (ushort)(((crc << 1) | dataBit) & 0xFFFF);
                    if (top != 0)
                        crc ^= 0x1021;
                }
            }
            return crc;
        }

        public static ushort Compute(byte[] data, ushort init = 0)
        {
            return Compute(data, 0, data.Length, init);
        }

        public static ushort FindStoredCrc(ushort dataCrc)
        {
            for (int val = 0; val < 0x10000; val++)
            {
                byte[] packed = BitConverter.GetBytes((ushort)val);
                if (Compute(packed, dataCrc) == 0)
                    return (ushort)val;
            }
            throw new InvalidOperationException("Could not find stored CRC value");
        }

        public static byte[] ComputeStoredCrcBytes(byte[] data, int offset, int length)
        {
            ushort crc = Compute(data, offset, length);
            ushort stored = FindStoredCrc(crc);
            return BitConverter.GetBytes(stored);
        }

        public static byte[] ComputeStoredCrcBytes(byte[] data)
        {
            return ComputeStoredCrcBytes(data, 0, data.Length);
        }

        public static byte[] ComputeStoredCrcBytesForTarget(byte[] data, int offset, int prefixLength, ushort targetCrc)
        {
            ushort prefixCrc = Compute(data, offset, prefixLength);
            for (int val = 0; val < 0x10000; val++)
            {
                byte[] packed = BitConverter.GetBytes((ushort)val);
                if (Compute(packed, 0, 2, prefixCrc) == targetCrc)
                    return packed;
            }
            throw new InvalidOperationException("Could not find stored CRC value for target");
        }

        public static ushort ComputeCcitt(byte[] data, int offset, int length, ushort init = 0)
        {
            ushort crc = init;
            for (int i = 0; i < length; i++)
            {
                crc ^= (ushort)(data[offset + i] << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)(((crc << 1) ^ 0x1021) & 0xFFFF);
                    else
                        crc = (ushort)((crc << 1) & 0xFFFF);
                }
            }
            return crc;
        }

        public static byte[] ComputeCcittStoredCrcBytes(byte[] data, int offset, int length)
        {
            ushort prefixCrc = ComputeCcitt(data, offset, length);
            for (int val = 0; val < 0x10000; val++)
            {
                byte[] packed = BitConverter.GetBytes((ushort)val);
                if (ComputeCcitt(packed, 0, 2, prefixCrc) == 0)
                    return packed;
            }
            throw new InvalidOperationException("Could not find CCITT CRC value");
        }
    }
}
