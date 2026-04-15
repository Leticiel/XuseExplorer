using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Scripts
{
    public class CdAnetanScriptReader : IScriptReader
    {
        public string Tag => "CD/Anetan";
        public string Description => "Xuse CD script file (Anetan variant)";
        public string[] Extensions => new[] { ".cd" };

        private static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        private static readonly HashSet<ushort> TextOpcodes = new()
        {
            0x3B, 0x40, 0x41, 0x44, 0x61, 0x62, 0x63, 0x64, 0x65, 0x6A
        };

        private static readonly HashSet<ushort> ThirdUsingCmds = new()
        {
            0x12, 0x13, 0x14,
            0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B,
            0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21,
            0x24, 0x25, 0x26, 0x27, 0x28, 0x29,
            0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33,
            0x36, 0x37, 0x38,
            0x3B,
            0x40, 0x41,
            0x44,
            0x46,
            0x49,
            0x4A,
            0x55, 0x56,
            0x59,
            0x5B, 0x5F,
            0x61, 0x62, 0x63, 0x64, 0x65,
            0x6A,
            0x6D,
            0x6F,
            0x70,
            0x74,
            0x76,
            0x77, 0x78,
            0x7B, 0x7D, 0x7E, 0x7F, 0x80,
            0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B,
            0x8C,
            0x8D,
            0x8E,
            0x8F, 0x90,
            0x91,
            0x96,
            0x99,
        };

        private static readonly HashSet<ushort> UnknownThirdOffsetCmds = new()
        {
            0x50, 0x5C, 0x60, 0x75, 0x92, 0x97, 0x98, 0x9B
        };

        private static readonly Dictionary<uint, int> Op8FItemSize = new()
        {
            { 0x0200, 6 }, { 0x02C0, 24 }, { 0x2040, 13 },
            { 0x2140, 18 }, { 0x2240, 16 }, { 0x2340, 21 }
        };

        public ScriptFile? TryOpen(string filePath)
        {
            var data = File.ReadAllBytes(filePath);
            return TryOpenFromData(data, filePath);
        }

        public ScriptFile? TryOpenFromData(byte[] data, string virtualName)
        {
            try
            {
                if (data.Length < 0x130) return null;

                if (!HasValidTailMd5(data)) return null;

                var entries = ExtractText(data);
                if (entries == null) return null;

                return new ScriptFile
                {
                    FilePath = virtualName,
                    FormatTag = Tag,
                    FormatDescription = Description,
                    FileSize = data.Length,
                    Entries = entries,
                    RawData = data
                };
            }
            catch
            {
                return null;
            }
        }

        public byte[] Rebuild(byte[] originalData, List<ScriptEntry> modifiedEntries)
        {
            return ImportText(originalData, modifiedEntries);
        }

        private static bool HasValidTailMd5(byte[] data)
        {
            if (data.Length < 32) return false;
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(data, 0, data.Length - 16);
            for (int i = 0; i < 16; i++)
            {
                if (hash[i] != data[data.Length - 16 + i])
                    return false;
            }
            return true;
        }

        private class CdParsed
        {
            public byte[] Raw = Array.Empty<byte>();
            public int FirstCnt;
            public int SecondCnt;
            public int ThirdCnt;
            public List<(uint a, uint b, uint c)> FirstBlock = new();
            public int SecondStart;
            public byte[] SecondData = Array.Empty<byte>();
            public byte[] ThirdData = Array.Empty<byte>();
        }

        private class Instruction
        {
            public ushort Cmd;
            public int Pos;
            public bool HasText;
            public int ThirdOffset;
            public int StrLen;
            public int Cnt;
            public ushort A0;
        }

        private static CdParsed? ParseCdFile(byte[] data)
        {
            if (data.Length < 0x130) return null;

            var raw = (byte[])data.Clone();

            for (int i = 0x20; i < 0x124; i++)
                raw[i] ^= 0x16;

            int pos = 0;
            pos += 16;
            pos += 16;
            pos += 260;

            int firstCnt = (int)BitConverter.ToUInt32(raw, pos); pos += 4;
            int secondCnt = (int)BitConverter.ToUInt32(raw, pos); pos += 4;
            int thirdCnt = (int)BitConverter.ToUInt32(raw, pos); pos += 4;

            if (secondCnt <= 0 || secondCnt % 12 != 0) return null;
            if (thirdCnt < 0) return null;
            if (firstCnt < 0 || firstCnt > 10000) return null;

            var firstBlock = new List<(uint, uint, uint)>();
            for (int i = 0; i < firstCnt; i++)
            {
                uint a = BitConverter.ToUInt32(raw, pos); pos += 4;
                uint b = BitConverter.ToUInt32(raw, pos); pos += 4;
                uint c = BitConverter.ToUInt32(raw, pos); pos += 4;
                firstBlock.Add((a, b, c));
            }

            int secondStart = pos;

            if (secondStart + secondCnt + thirdCnt > data.Length) return null;

            byte[] secondData = new byte[secondCnt];
            Array.Copy(raw, secondStart, secondData, 0, secondCnt);

            byte[] thirdData = new byte[thirdCnt];
            Array.Copy(raw, secondStart + secondCnt, thirdData, 0, thirdCnt);

            return new CdParsed
            {
                Raw = raw,
                FirstCnt = firstCnt,
                SecondCnt = secondCnt,
                ThirdCnt = thirdCnt,
                FirstBlock = firstBlock,
                SecondStart = secondStart,
                SecondData = secondData,
                ThirdData = thirdData
            };
        }

        private static List<Instruction> ParseInstructions(byte[] secondData)
        {
            var instructions = new List<Instruction>();
            int pos = 0;
            while (pos + 12 <= secondData.Length)
            {
                ushort cmd = BitConverter.ToUInt16(secondData, pos);
                ushort aVal = BitConverter.ToUInt16(secondData, pos + 2);

                bool hasText = TextOpcodes.Contains(cmd);
                if (hasText)
                {
                    if (cmd == 0x61) hasText = false;
                    if (cmd == 0x62 && aVal != 0) hasText = false;
                    if (cmd >= 0x63 && cmd <= 0x65 && (aVal == 0xFFFF || aVal > 100)) hasText = false;
                    if (cmd == 0x6A && (aVal == 0xFFFF || aVal > 100)) hasText = false;
                }

                var inst = new Instruction
                {
                    Cmd = cmd,
                    Pos = pos,
                    HasText = hasText
                };

                if (inst.HasText)
                {
                    if (cmd == 0x40 || cmd == 0x41)
                    {
                        inst.A0 = aVal;
                        inst.ThirdOffset = (int)BitConverter.ToUInt32(secondData, pos + 4);
                    }
                    else if (cmd == 0x3B)
                    {
                        inst.StrLen = aVal;
                        inst.ThirdOffset = (int)BitConverter.ToUInt32(secondData, pos + 4);
                    }
                    else if (cmd == 0x44)
                    {
                        inst.ThirdOffset = (int)BitConverter.ToUInt32(secondData, pos + 4);
                    }
                    else if ((cmd >= 0x61 && cmd <= 0x65) || cmd == 0x6A)
                    {
                        inst.Cnt = aVal;
                        inst.ThirdOffset = (int)BitConverter.ToUInt32(secondData, pos + 4);
                    }
                }

                pos += 12;
                instructions.Add(inst);
            }
            return instructions;
        }

        private static string DecodeText(byte[] rawBytes)
        {
            byte[] dec = new byte[rawBytes.Length];
            for (int i = 0; i < rawBytes.Length; i++)
                dec[i] = (byte)(rawBytes[i] ^ 0x53);
            return ShiftJis.GetString(dec);
        }

        private static byte[] EncodeText(string text)
        {
            byte[] raw = ShiftJis.GetBytes(text);
            byte[] enc = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                enc[i] = (byte)(raw[i] ^ 0x53);
            return enc;
        }

        private static string ReadLenStr(byte[] thirdData, ref int pos)
        {
            uint len = BitConverter.ToUInt32(thirdData, pos);
            pos += 4;
            byte[] raw = new byte[len];
            Array.Copy(thirdData, pos, raw, 0, (int)len);
            pos += (int)len;
            return DecodeText(raw);
        }

        private List<ScriptEntry>? ExtractText(byte[] data)
        {
            var cd = ParseCdFile(data);
            if (cd == null) return null;

            var instructions = ParseInstructions(cd.SecondData);
            var entries = new List<ScriptEntry>();
            int cmdIndex = 0;

            foreach (var inst in instructions)
            {
                if (!inst.HasText) { cmdIndex++; continue; }

                var texts = new List<string>();

                try
                {
                    switch (inst.Cmd)
                    {
                        case 0x40:
                        case 0x41:
                            texts = ExtractShowText(inst, cd.ThirdData);
                            break;
                        case 0x44:
                            texts = ExtractTextRbFS(inst, cd.ThirdData);
                            break;
                        case 0x3B:
                            texts = ExtractLoadSimpStr(inst, cd.ThirdData);
                            break;
                        case 0x61:
                        case 0x62:
                        case 0x6A:
                            texts = ExtractScrSelTxt(inst, cd.ThirdData);
                            break;
                        case 0x63:
                            texts = ExtractScrSelTxtC(inst, cd.ThirdData);
                            break;
                        case 0x64:
                            texts = ExtractSSTxtVarA(inst, cd.ThirdData);
                            break;
                        case 0x65:
                            texts = ExtractSSTxtVarB(inst, cd.ThirdData);
                            break;
                    }
                }
                catch
                {
                    cmdIndex++;
                    continue;
                }

                if (texts.Count > 0)
                {
                    entries.Add(new ScriptEntry
                    {
                        Index = cmdIndex,
                        SeqId = cmdIndex,
                        Voice = 0,
                        Text = string.Join("\n", texts)
                    });
                }

                cmdIndex++;
            }

            return entries;
        }

        private static List<string> ExtractShowText(Instruction inst, byte[] thirdData)
        {
            int pos = inst.ThirdOffset;
            pos += 4;
            pos += 4;
            pos += 4;
            uint cnt = BitConverter.ToUInt32(thirdData, pos); pos += 4;

            var texts = new List<string>();
            for (int i = 0; i < (int)cnt; i++)
                texts.Add(ReadLenStr(thirdData, ref pos));

            pos += 8;
            return texts;
        }

        private static List<string> ExtractTextRbFS(Instruction inst, byte[] thirdData)
        {
            int pos = inst.ThirdOffset;
            pos += 2 + 2 + 4 + 4;
            return new List<string> { ReadLenStr(thirdData, ref pos) };
        }

        private static List<string> ExtractLoadSimpStr(Instruction inst, byte[] thirdData)
        {
            int pos = inst.ThirdOffset;
            byte[] raw = new byte[inst.StrLen];
            Array.Copy(thirdData, pos, raw, 0, inst.StrLen);
            return new List<string> { ShiftJis.GetString(raw) };
        }

        private static List<string> ExtractScrSelTxt(Instruction inst, byte[] thirdData)
        {
            int pos = inst.ThirdOffset;
            var texts = new List<string>();
            for (int i = 0; i < inst.Cnt; i++)
                texts.Add(ReadLenStr(thirdData, ref pos));
            return texts;
        }

        private static List<string> ExtractScrSelTxtC(Instruction inst, byte[] thirdData)
        {
            int pos = inst.ThirdOffset;
            pos += 4;
            var texts = new List<string>();
            for (int i = 0; i < inst.Cnt; i++)
                texts.Add(ReadLenStr(thirdData, ref pos));
            return texts;
        }

        private static List<string> ExtractSSTxtVarA(Instruction inst, byte[] thirdData)
        {
            int pos = inst.ThirdOffset;
            for (int i = 0; i < inst.Cnt; i++)
                pos += 4;
            var texts = new List<string>();
            for (int i = 0; i < inst.Cnt; i++)
                texts.Add(ReadLenStr(thirdData, ref pos));
            return texts;
        }

        private static List<string> ExtractSSTxtVarB(Instruction inst, byte[] thirdData)
        {
            int pos = inst.ThirdOffset;
            pos += 4;
            for (int i = 0; i < inst.Cnt; i++)
                pos += 4;
            var texts = new List<string>();
            for (int i = 0; i < inst.Cnt; i++)
                texts.Add(ReadLenStr(thirdData, ref pos));
            return texts;
        }

        private byte[] ImportText(byte[] originalData, List<ScriptEntry> modifiedEntries)
        {
            var cd = ParseCdFile(originalData);
            if (cd == null) throw new InvalidOperationException("Failed to parse original CD data");

            var secondData = (byte[])cd.SecondData.Clone();
            var thirdData = cd.ThirdData;
            var instructions = ParseInstructions(secondData);

            var patchMap = new Dictionary<int, string[]>();
            foreach (var entry in modifiedEntries)
            {
                if (!string.IsNullOrEmpty(entry.Text))
                    patchMap[entry.Index] = entry.Text.Split('\n');
            }

            if (patchMap.Count == 0)
                return originalData;

            var knownRefs = new List<(int offset, int size, int idx, ushort cmd)>();
            for (int idx = 0; idx < instructions.Count; idx++)
            {
                var inst = instructions[idx];
                if (!ThirdUsingCmds.Contains(inst.Cmd)) continue;
                int? off = GetThirdOffset(inst.Cmd, secondData, inst.Pos);
                if (off == null) continue;
                int sz = GetThirdRegionSize(inst.Cmd, thirdData, off.Value, secondData, inst.Pos);
                knownRefs.Add((off.Value, sz, idx, inst.Cmd));
            }
            knownRefs.Sort((a, b) => a.offset.CompareTo(b.offset));

            var splices = new List<(int origStart, int origEnd, byte[]? newBytes)>();
            var refToSplice = new Dictionary<int, int>();

            int cursor = 0;
            foreach (var (off, sz, idx, cmd) in knownRefs)
            {
                if (off > cursor)
                    splices.Add((cursor, off, null));
                else if (off < cursor)
                    continue;

                if (patchMap.ContainsKey(idx))
                {
                    byte[] newBytes = BuildTextRegion(cmd, thirdData, off, secondData,
                        instructions[idx].Pos, patchMap[idx]);
                    refToSplice[idx] = splices.Count;
                    splices.Add((off, off + sz, newBytes));
                }
                else
                {
                    refToSplice[idx] = splices.Count;
                    splices.Add((off, off + sz, null));
                }

                cursor = off + sz;
            }

            if (cursor < thirdData.Length)
                splices.Add((cursor, thirdData.Length, null));

            var newThirdMs = new MemoryStream();
            var spliceNewStarts = new int[splices.Count];

            for (int i = 0; i < splices.Count; i++)
            {
                var (origStart, origEnd, newBytes) = splices[i];
                spliceNewStarts[i] = (int)newThirdMs.Position;
                if (newBytes != null)
                    newThirdMs.Write(newBytes, 0, newBytes.Length);
                else
                    newThirdMs.Write(thirdData, origStart, origEnd - origStart);
            }

            var spliceMap = new List<(int origStart, int origEnd, int newStart, int? delta)>();
            for (int i = 0; i < splices.Count; i++)
            {
                var (origStart, origEnd, newBytes) = splices[i];
                int newStart = spliceNewStarts[i];
                if (newBytes != null)
                    spliceMap.Add((origStart, origEnd, newStart, null));
                else
                    spliceMap.Add((origStart, origEnd, newStart, newStart - origStart));
            }

            foreach (var (off, sz, idx, cmd) in knownRefs)
            {
                if (refToSplice.TryGetValue(idx, out int spliceIdx))
                    SetThirdOffset(secondData, instructions[idx].Pos, spliceNewStarts[spliceIdx]);
            }

            var knownIndices = new HashSet<int>(knownRefs.Select(r => r.idx));
            for (int idx = 0; idx < instructions.Count; idx++)
            {
                if (knownIndices.Contains(idx)) continue;
                var inst = instructions[idx];
                if (!UnknownThirdOffsetCmds.Contains(inst.Cmd)) continue;

                uint off = BitConverter.ToUInt32(secondData, inst.Pos + 4);
                if (off == 0 || off == 0xFFFFFFFF || off >= (uint)thirdData.Length) continue;

                int newOff = RemapOffset((int)off, spliceMap, spliceNewStarts, splices);
                if (newOff != (int)off)
                    SetThirdOffset(secondData, inst.Pos, newOff);
            }

            byte[] newThird = newThirdMs.ToArray();

            var raw = cd.Raw;
            for (int i = 0x20; i < 0x124; i++)
                raw[i] ^= 0x16;

            int headerSize = 16 + 16 + 260 + 12 + cd.FirstCnt * 12;
            BitConverter.GetBytes(cd.FirstCnt).CopyTo(raw, 16 + 16 + 260);
            BitConverter.GetBytes(secondData.Length).CopyTo(raw, 16 + 16 + 260 + 4);
            BitConverter.GetBytes(newThird.Length).CopyTo(raw, 16 + 16 + 260 + 8);

            using var outMs = new MemoryStream();
            outMs.Write(raw, 0, headerSize);
            outMs.Write(secondData, 0, secondData.Length);
            outMs.Write(newThird, 0, newThird.Length);

            int tailStart = cd.SecondStart + cd.SecondCnt + cd.ThirdCnt;
            if (tailStart < raw.Length - 16)
                outMs.Write(raw, tailStart, raw.Length - 16 - tailStart);

            outMs.Write(new byte[16], 0, 16);

            byte[] output = outMs.ToArray();

            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(output, 0, output.Length - 16);
            Array.Copy(hash, 0, output, output.Length - 16, 16);

            return output;
        }

        private static int RemapOffset(int origOff,
            List<(int origStart, int origEnd, int newStart, int? delta)> spliceMap,
            int[] spliceNewStarts,
            List<(int origStart, int origEnd, byte[]? newBytes)> splices)
        {
            foreach (var (sStart, sEnd, nStart, delta) in spliceMap)
            {
                if (origOff >= sStart && origOff < sEnd)
                    return delta.HasValue ? origOff + delta.Value : nStart;
            }

            if (spliceMap.Count > 0)
            {
                var last = spliceMap[spliceMap.Count - 1];
                if (last.delta.HasValue)
                    return origOff + last.delta.Value;

                int lastNewEnd = spliceNewStarts[spliceNewStarts.Length - 1];
                var lastSplice = splices[splices.Count - 1];
                lastNewEnd += lastSplice.newBytes != null
                    ? lastSplice.newBytes.Length
                    : lastSplice.origEnd - lastSplice.origStart;
                return lastNewEnd + (origOff - last.origEnd);
            }
            return origOff;
        }

        private static byte[] BuildTextRegion(ushort cmd, byte[] thirdData, int trdPos,
            byte[] secondData, int instPos, string[] newStrings)
        {
            switch (cmd)
            {
                case 0x40:
                case 0x41:
                {
                    uint a = BitConverter.ToUInt32(thirdData, trdPos);
                    uint offVal = BitConverter.ToUInt32(thirdData, trdPos + 4);
                    uint textIdx = BitConverter.ToUInt32(thirdData, trdPos + 8);
                    uint cnt = BitConverter.ToUInt32(thirdData, trdPos + 12);

                    int rpos = trdPos + 16;
                    for (int i = 0; i < (int)cnt; i++)
                    {
                        uint l = BitConverter.ToUInt32(thirdData, rpos);
                        rpos += 4 + (int)l;
                    }
                    uint b = BitConverter.ToUInt32(thirdData, rpos);
                    uint c = BitConverter.ToUInt32(thirdData, rpos + 4);

                    var encStrs = newStrings.Select(EncodeText).ToList();
                    uint newOffVal = (uint)(8 + encStrs.Sum(s => 4 + s.Length));

                    using var buf = new MemoryStream();
                    buf.Write(BitConverter.GetBytes(a), 0, 4);
                    buf.Write(BitConverter.GetBytes(newOffVal), 0, 4);
                    buf.Write(BitConverter.GetBytes(textIdx), 0, 4);
                    buf.Write(BitConverter.GetBytes((uint)newStrings.Length), 0, 4);
                    foreach (var s in encStrs)
                    {
                        buf.Write(BitConverter.GetBytes((uint)s.Length), 0, 4);
                        buf.Write(s, 0, s.Length);
                    }
                    buf.Write(BitConverter.GetBytes(b), 0, 4);
                    buf.Write(BitConverter.GetBytes(c), 0, 4);
                    return buf.ToArray();
                }
                case 0x3B:
                {
                    byte[] newStr = ShiftJis.GetBytes(newStrings[0]);
                    BitConverter.GetBytes((ushort)newStr.Length).CopyTo(secondData, instPos + 2);
                    return newStr;
                }
                case 0x44:
                {
                    ushort da = BitConverter.ToUInt16(thirdData, trdPos);
                    ushort db = BitConverter.ToUInt16(thirdData, trdPos + 2);
                    uint dc = BitConverter.ToUInt32(thirdData, trdPos + 4);
                    uint dd = BitConverter.ToUInt32(thirdData, trdPos + 8);

                    byte[] newStrEnc = EncodeText(newStrings[0]);
                    using var buf = new MemoryStream();
                    buf.Write(BitConverter.GetBytes(da), 0, 2);
                    buf.Write(BitConverter.GetBytes(db), 0, 2);
                    buf.Write(BitConverter.GetBytes(dc), 0, 4);
                    buf.Write(BitConverter.GetBytes(dd), 0, 4);
                    buf.Write(BitConverter.GetBytes((uint)newStrEnc.Length), 0, 4);
                    buf.Write(newStrEnc, 0, newStrEnc.Length);
                    return buf.ToArray();
                }
                case 0x61:
                case 0x62:
                case 0x6A:
                {
                    var encStrs = newStrings.Select(EncodeText).ToList();
                    using var buf = new MemoryStream();
                    foreach (var s in encStrs)
                    {
                        buf.Write(BitConverter.GetBytes((uint)s.Length), 0, 4);
                        buf.Write(s, 0, s.Length);
                    }
                    BitConverter.GetBytes((ushort)newStrings.Length).CopyTo(secondData, instPos + 2);
                    return buf.ToArray();
                }
                case 0x63:
                {
                    uint aVal = BitConverter.ToUInt32(thirdData, trdPos);
                    var encStrs = newStrings.Select(EncodeText).ToList();
                    using var buf = new MemoryStream();
                    buf.Write(BitConverter.GetBytes(aVal), 0, 4);
                    foreach (var s in encStrs)
                    {
                        buf.Write(BitConverter.GetBytes((uint)s.Length), 0, 4);
                        buf.Write(s, 0, s.Length);
                    }
                    BitConverter.GetBytes((ushort)newStrings.Length).CopyTo(secondData, instPos + 2);
                    return buf.ToArray();
                }
                case 0x64:
                {
                    int cnt = BitConverter.ToUInt16(secondData, instPos + 2);
                    using var buf = new MemoryStream();
                    buf.Write(thirdData, trdPos, cnt * 4);
                    var encStrs = newStrings.Select(EncodeText).ToList();
                    foreach (var s in encStrs)
                    {
                        buf.Write(BitConverter.GetBytes((uint)s.Length), 0, 4);
                        buf.Write(s, 0, s.Length);
                    }
                    BitConverter.GetBytes((ushort)newStrings.Length).CopyTo(secondData, instPos + 2);
                    return buf.ToArray();
                }
                case 0x65:
                {
                    uint aVal2 = BitConverter.ToUInt32(thirdData, trdPos);
                    int cnt = BitConverter.ToUInt16(secondData, instPos + 2);
                    using var buf = new MemoryStream();
                    buf.Write(BitConverter.GetBytes(aVal2), 0, 4);
                    buf.Write(thirdData, trdPos + 4, cnt * 4);
                    var encStrs = newStrings.Select(EncodeText).ToList();
                    foreach (var s in encStrs)
                    {
                        buf.Write(BitConverter.GetBytes((uint)s.Length), 0, 4);
                        buf.Write(s, 0, s.Length);
                    }
                    BitConverter.GetBytes((ushort)newStrings.Length).CopyTo(secondData, instPos + 2);
                    return buf.ToArray();
                }
                default:
                    throw new InvalidOperationException($"Unsupported text cmd 0x{cmd:X2} for import");
            }
        }

        private static int? GetThirdOffset(ushort cmd, byte[] secondData, int instPos)
        {
            if (cmd == 0x61) return null;

            if (cmd == 0x62)
            {
                ushort aVal = BitConverter.ToUInt16(secondData, instPos + 2);
                if (aVal != 0) return null;
            }

            if (cmd == 0x63 || cmd == 0x64 || cmd == 0x65 || cmd == 0x6A)
            {
                ushort aVal = BitConverter.ToUInt16(secondData, instPos + 2);
                if (aVal == 0xFFFF || aVal > 100) return null;
            }

            uint off = BitConverter.ToUInt32(secondData, instPos + 4);
            if (off == 0xFFFFFFFF) return null;

            return (int)off;
        }

        private static void SetThirdOffset(byte[] secondData, int instPos, int newOff)
        {
            BitConverter.GetBytes(newOff).CopyTo(secondData, instPos + 4);
        }

        private static int Func412DD3ExtraSize(int idf)
        {
            return idf switch
            {
                0 => 0, 1 => 2, 2 => 0, 3 => 4, 4 => 2, 5 => 0, 6 => 4,
                7 => 0, 8 => 3, 9 => 0, 10 => 20, 11 => 4, 12 => 28, 13 => 16,
                _ => 0
            };
        }

        private static int Func415283ExtraSize(int idf)
        {
            return idf switch
            {
                0 => 0, 1 => 0, 2 => 14, 3 => 0, 4 => 41, 5 => 36, 6 => 0, 7 => 0, 8 => 0,
                _ => 0
            };
        }

        private static int Sub413350ExtraSize(uint idf)
        {
            return idf switch
            {
                512 => 16,
                64 => 28,
                2147483649 => 2,
                320 => 38,
                _ => 0
            };
        }

        private static int GetThirdRegionSize(ushort cmd, byte[] thirdData, int trdPos, byte[] secondData, int instPos)
        {
            switch (cmd)
            {
                case 0x12: case 0x13: case 0x14: return 10;

                case 0x16: case 0x17: case 0x18: case 0x19: case 0x1A: case 0x1B:
                case 0x1C: case 0x1D: case 0x1E: case 0x1F: case 0x20: case 0x21:
                case 0x24: case 0x25: case 0x26: case 0x27: case 0x28: case 0x29:
                case 0x2A: case 0x2B: case 0x2C: case 0x2D: case 0x2E: case 0x2F:
                case 0x36: case 0x37: case 0x38: case 0x6F:
                    return 8;

                case 0x30: case 0x32:
                {
                    uint num = BitConverter.ToUInt32(thirdData, trdPos + 4);
                    return 8 + (int)num * 4;
                }
                case 0x31: case 0x33:
                {
                    uint num = BitConverter.ToUInt32(thirdData, trdPos + 4);
                    return 8 + (int)num * 8;
                }
                case 0x3B:
                    return BitConverter.ToUInt16(secondData, instPos + 2);

                case 0x40: case 0x41:
                {
                    uint cnt = BitConverter.ToUInt32(thirdData, trdPos + 12);
                    int rpos = trdPos + 16;
                    for (int i = 0; i < (int)cnt; i++)
                    {
                        uint l = BitConverter.ToUInt32(thirdData, rpos);
                        rpos += 4 + (int)l;
                    }
                    return rpos + 8 - trdPos;
                }
                case 0x44:
                {
                    int rpos = trdPos + 12;
                    uint l = BitConverter.ToUInt32(thirdData, rpos);
                    return rpos + 4 + (int)l - trdPos;
                }
                case 0x46: return 18;
                case 0x49: return 16;
                case 0x4A: return 10;
                case 0x55: return 11;
                case 0x56: return 10;
                case 0x59: return 8;
                case 0x5B: return 11;
                case 0x5F: return 8;

                case 0x61: case 0x62:
                {
                    int cnt = BitConverter.ToUInt16(secondData, instPos + 2);
                    int rpos = trdPos;
                    for (int i = 0; i < cnt; i++)
                    {
                        uint l = BitConverter.ToUInt32(thirdData, rpos);
                        rpos += 4 + (int)l;
                    }
                    return rpos - trdPos;
                }
                case 0x63:
                {
                    int cnt = BitConverter.ToUInt16(secondData, instPos + 2);
                    int rpos = trdPos + 4;
                    for (int i = 0; i < cnt; i++)
                    {
                        uint l = BitConverter.ToUInt32(thirdData, rpos);
                        rpos += 4 + (int)l;
                    }
                    return rpos - trdPos;
                }
                case 0x64:
                {
                    int cnt = BitConverter.ToUInt16(secondData, instPos + 2);
                    int rpos = trdPos + cnt * 4;
                    for (int i = 0; i < cnt; i++)
                    {
                        uint l = BitConverter.ToUInt32(thirdData, rpos);
                        rpos += 4 + (int)l;
                    }
                    return rpos - trdPos;
                }
                case 0x65:
                {
                    int cnt = BitConverter.ToUInt16(secondData, instPos + 2);
                    int rpos = trdPos + 4 + cnt * 4;
                    for (int i = 0; i < cnt; i++)
                    {
                        uint l = BitConverter.ToUInt32(thirdData, rpos);
                        rpos += 4 + (int)l;
                    }
                    return rpos - trdPos;
                }
                case 0x6A:
                {
                    int cnt = BitConverter.ToUInt16(secondData, instPos + 2);
                    int rpos = trdPos;
                    for (int i = 0; i < cnt; i++)
                    {
                        uint l = BitConverter.ToUInt32(thirdData, rpos);
                        rpos += 4 + (int)l;
                    }
                    return rpos - trdPos;
                }
                case 0x6D: return 24;
                case 0x70:
                {
                    int idf = (int)BitConverter.ToUInt32(thirdData, trdPos + 8);
                    return 16 + Func412DD3ExtraSize(idf);
                }
                case 0x74: return 24;
                case 0x76: return 12;
                case 0x77: return 17;
                case 0x78: return 16;
                case 0x7B: return 5;
                case 0x7D: return 7;
                case 0x7E: return 12;
                case 0x7F: return 20;
                case 0x80: return 13;
                case 0x81: case 0x89: return 9;
                case 0x82: return 7;
                case 0x83: return 6;
                case 0x84: return 5;
                case 0x85: return 6;
                case 0x86:
                {
                    uint idf = BitConverter.ToUInt32(thirdData, trdPos + 9);
                    return 13 + Sub413350ExtraSize(idf);
                }
                case 0x87: case 0x8A: return 10;
                case 0x88:
                {
                    int idf = (int)BitConverter.ToUInt32(thirdData, trdPos + 12);
                    return 20 + Func412DD3ExtraSize(idf);
                }
                case 0x8B: return 8;
                case 0x8C:
                {
                    int idf = (int)BitConverter.ToUInt32(thirdData, trdPos + 8);
                    return 12 + Func415283ExtraSize(idf);
                }
                case 0x8D: return 5;
                case 0x8E:
                {
                    int cnt = BitConverter.ToUInt16(secondData, instPos + 2);
                    return cnt * 44;
                }
                case 0x8F: case 0x90:
                {
                    uint v3 = BitConverter.ToUInt32(thirdData, trdPos + 9);
                    int header;
                    int cnt;
                    if (v3 >= 0x2000)
                    {
                        cnt = BitConverter.ToUInt16(thirdData, trdPos + 13);
                        header = 15;
                    }
                    else
                    {
                        cnt = (int)BitConverter.ToUInt32(thirdData, trdPos + 13);
                        header = 17;
                    }
                    int itemSz = Op8FItemSize.GetValueOrDefault(v3, 13);
                    return header + cnt * itemSz;
                }
                case 0x91: return 10;
                case 0x96: return 24;
                case 0x99:
                {
                    byte b0 = secondData[instPos + 2];
                    return b0 * 44;
                }
                default:
                    throw new InvalidOperationException($"Unknown third block size for cmd 0x{cmd:X2}");
            }
        }
    }
}
