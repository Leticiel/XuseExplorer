using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Scripts
{
    public class CdOldScriptReader : IScriptReader
    {
        public string Tag => "Old CD/Xuse";
        public string Description => "Xuse old CD script file";
        public string[] Extensions => new[] { ".cd" };

        private static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        private const ushort OPCODE_TEXT = 0x0001;
        private const ushort OPCODE_TEXT_VOICED = 0x006F;
        private const byte XOR_KEY = 0x53;

        public ScriptFile? TryOpen(string filePath)
        {
            var data = File.ReadAllBytes(filePath);
            return TryOpenFromData(data, filePath);
        }

        public ScriptFile? TryOpenFromData(byte[] data, string virtualName)
        {
            try
            {
                var parsed = ParseOldCd(data);
                if (parsed == null) return null;

                var entries = ExtractTextEntries(parsed);
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

        private class OldCdParsed
        {
            public byte[] Data = Array.Empty<byte>();
            public uint ASize;
            public uint BSize;
            public uint CSize;
            public byte[] BlockA = Array.Empty<byte>();
            public byte[] BlockB = Array.Empty<byte>();
            public byte[] BlockC = Array.Empty<byte>();
            public List<uint> Offsets = new();
            public List<(ushort opcode, ushort aVal, uint offVal)> Instructions = new();
        }

        private static OldCdParsed? ParseOldCd(byte[] data)
        {
            if (data.Length < 12) return null;

            uint aSize = BitConverter.ToUInt32(data, 0);
            uint bSize = BitConverter.ToUInt32(data, 4);
            uint cSize = BitConverter.ToUInt32(data, 8);

            if (12 + aSize + bSize + cSize != (uint)data.Length) return null;
            if (aSize % 4 != 0) return null;
            if (bSize % 8 != 0) return null;
            if (bSize == 0 || cSize == 0) return null;

            if (data.Length > 0x134 && aSize > 0x100)
            {
                uint firstOff = BitConverter.ToUInt32(data, 12);
                if (firstOff >= cSize) return null;
            }

            uint bStart = 12 + aSize;
            uint cStart = 12 + aSize + bSize;

            var blockA = new byte[aSize];
            Array.Copy(data, 12, blockA, 0, (int)aSize);

            var blockB = new byte[bSize];
            Array.Copy(data, (int)bStart, blockB, 0, (int)bSize);

            var blockC = new byte[cSize];
            Array.Copy(data, (int)cStart, blockC, 0, (int)cSize);

            int numOffsets = (int)(aSize / 4);
            var offsets = new List<uint>(numOffsets);
            for (int i = 0; i < numOffsets; i++)
                offsets.Add(BitConverter.ToUInt32(blockA, i * 4));

            int numInst = (int)(bSize / 8);
            var instructions = new List<(ushort opcode, ushort aVal, uint offVal)>(numInst);
            for (int i = 0; i < numInst; i++)
            {
                int pos = i * 8;
                ushort opcode = BitConverter.ToUInt16(blockB, pos);
                ushort aVal = BitConverter.ToUInt16(blockB, pos + 2);
                uint offVal = BitConverter.ToUInt32(blockB, pos + 4);
                instructions.Add((opcode, aVal, offVal));
            }

            return new OldCdParsed
            {
                Data = data,
                ASize = aSize,
                BSize = bSize,
                CSize = cSize,
                BlockA = blockA,
                BlockB = blockB,
                BlockC = blockC,
                Offsets = offsets,
                Instructions = instructions
            };
        }

        private static byte[] XorDecrypt(byte[] raw)
        {
            byte[] dec = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                dec[i] = (byte)(raw[i] ^ XOR_KEY);
            return dec;
        }

        private static byte[] XorEncrypt(byte[] raw)
        {
            return XorDecrypt(raw);
        }

        private List<ScriptEntry>? ExtractTextEntries(OldCdParsed cd)
        {
            var entries = new List<ScriptEntry>();
            int entryIdx = 0;

            for (int instIdx = 0; instIdx < cd.Instructions.Count; instIdx++)
            {
                var (opcode, aVal, offVal) = cd.Instructions[instIdx];
                if (opcode != OPCODE_TEXT && opcode != OPCODE_TEXT_VOICED) continue;
                if (offVal + 12 > cd.CSize) continue;

                uint v0 = BitConverter.ToUInt32(cd.BlockC, (int)offVal);
                uint v1 = BitConverter.ToUInt32(cd.BlockC, (int)offVal + 4);
                uint cnt = BitConverter.ToUInt32(cd.BlockC, (int)offVal + 8);

                if (cnt == 0 || cnt > 100) continue;

                int rpos = (int)offVal + 12;
                var strings = new List<string>();
                bool valid = true;

                for (int s = 0; s < cnt; s++)
                {
                    if (rpos + 4 > cd.CSize) { valid = false; break; }
                    uint slen = BitConverter.ToUInt32(cd.BlockC, rpos);
                    rpos += 4;
                    if (slen == 0) { strings.Add(""); continue; }
                    if (slen > 10000 || rpos + slen > cd.CSize) { valid = false; break; }

                    byte[] raw = new byte[slen];
                    Array.Copy(cd.BlockC, rpos, raw, 0, (int)slen);
                    rpos += (int)slen;

                    byte[] dec = XorDecrypt(raw);
                    try
                    {
                        strings.Add(ShiftJis.GetString(dec));
                    }
                    catch
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid || strings.Count == 0) continue;

                string speaker = "";
                string text;
                if (aVal == 0)
                {
                    text = string.Join("\n", strings);
                }
                else
                {
                    speaker = strings[0];
                    text = string.Join("\n", strings.Skip(1));
                }

                int voice = opcode == OPCODE_TEXT_VOICED ? (int)v0 : 0;

                entries.Add(new ScriptEntry
                {
                    Index = entryIdx,
                    SeqId = instIdx,
                    Voice = voice,
                    Text = (aVal != 0 && !string.IsNullOrEmpty(speaker))
                        ? $"【{speaker}】{text}"
                        : text,
                    Extra = new Dictionary<string, object>
                    {
                        ["type"] = aVal == 0 ? "narration" : "dialogue",
                        ["speaker"] = speaker,
                        ["text_idx"] = (int)v1,
                    }
                });
                entryIdx++;
            }

            return entries;
        }

        private byte[] ImportText(byte[] originalData, List<ScriptEntry> modifiedEntries)
        {
            var cd = ParseOldCd(originalData);
            if (cd == null) throw new InvalidOperationException("Failed to parse original CD data.");

            var transMap = new Dictionary<int, ScriptEntry>();
            foreach (var entry in modifiedEntries)
                transMap[entry.SeqId] = entry;

            var textRegions = new List<(uint offVal, int textEnd, int instIdx)>();
            for (int instIdx = 0; instIdx < cd.Instructions.Count; instIdx++)
            {
                var (opcode, aVal, offVal) = cd.Instructions[instIdx];
                if (opcode != OPCODE_TEXT && opcode != OPCODE_TEXT_VOICED) continue;

                int? end = GetTextRegionEnd(cd.BlockC, cd.CSize, offVal);
                if (end.HasValue)
                    textRegions.Add((offVal, end.Value, instIdx));
            }
            textRegions.Sort((a, b) => a.offVal.CompareTo(b.offVal));

            var splices = new List<(uint oldStart, int oldEnd, byte[] newData)>();
            foreach (var (offVal, textEnd, instIdx) in textRegions)
            {
                if (transMap.TryGetValue(instIdx, out var modEntry))
                {
                    var (opcode, aVal, _) = cd.Instructions[instIdx];
                    uint v0 = BitConverter.ToUInt32(cd.BlockC, (int)offVal);
                    uint v1 = BitConverter.ToUInt32(cd.BlockC, (int)offVal + 4);

                    string text = modEntry.Text;
                    string speaker = "";
                    string entryType = "narration";

                    if (modEntry.Extra != null)
                    {
                        if (modEntry.Extra.TryGetValue("type", out var t))
                            entryType = t?.ToString() ?? "narration";
                        if (modEntry.Extra.TryGetValue("speaker", out var s))
                            speaker = s?.ToString() ?? "";
                    }

                    if (text.StartsWith("【"))
                    {
                        int closeIdx = text.IndexOf('】');
                        if (closeIdx > 0)
                        {
                            speaker = text.Substring(1, closeIdx - 1);
                            text = text.Substring(closeIdx + 1);
                            entryType = "dialogue";
                        }
                    }

                    var lines = text.Split('\n');
                    var allStrings = new List<string>();
                    if (entryType == "dialogue" && !string.IsNullOrEmpty(speaker))
                        allStrings.Add(speaker);
                    allStrings.AddRange(lines);

                    byte[] newData = BuildTextData(v0, v1, allStrings);
                    splices.Add((offVal, textEnd, newData));
                }
                else
                {
                    byte[] origData = new byte[textEnd - (int)offVal];
                    Array.Copy(cd.BlockC, (int)offVal, origData, 0, origData.Length);
                    splices.Add((offVal, textEnd, origData));
                }
            }

            var newBlockC = new List<byte>();
            var deltaPoints = new List<(uint oldPos, int delta)>();
            int prevEnd = 0;

            foreach (var (oldStart, oldEnd, newData) in splices)
            {
                if ((int)oldStart > prevEnd)
                {
                    for (int i = prevEnd; i < (int)oldStart; i++)
                        newBlockC.Add(cd.BlockC[i]);
                }

                int currentDelta = newBlockC.Count - (int)oldStart;
                deltaPoints.Add((oldStart, currentDelta));

                newBlockC.AddRange(newData);

                int currentDeltaEnd = newBlockC.Count - oldEnd;
                deltaPoints.Add(((uint)oldEnd, currentDeltaEnd));

                prevEnd = oldEnd;
            }

            if (prevEnd < (int)cd.CSize)
            {
                for (int i = prevEnd; i < (int)cd.CSize; i++)
                    newBlockC.Add(cd.BlockC[i]);
            }
            deltaPoints.Add((cd.CSize, newBlockC.Count - (int)cd.CSize));

            int TranslateOffset(uint oldOff)
            {
                if (oldOff >= cd.CSize) return (int)oldOff;
                int lo = 0, hi = deltaPoints.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (deltaPoints[mid].oldPos <= oldOff) lo = mid;
                    else hi = mid - 1;
                }
                return (int)oldOff + deltaPoints[lo].delta;
            }

            byte[] newBlockB = (byte[])cd.BlockB.Clone();
            for (int i = 0; i < cd.Instructions.Count; i++)
            {
                var (_, _, offVal) = cd.Instructions[i];
                int newOff = TranslateOffset(offVal);
                byte[] offBytes = BitConverter.GetBytes((uint)newOff);
                Array.Copy(offBytes, 0, newBlockB, i * 8 + 4, 4);
            }

            byte[] newBlockA = (byte[])cd.BlockA.Clone();
            for (int i = 0; i < cd.Offsets.Count; i++)
            {
                int newOff = TranslateOffset(cd.Offsets[i]);
                byte[] offBytes = BitConverter.GetBytes((uint)newOff);
                Array.Copy(offBytes, 0, newBlockA, i * 4, 4);
            }

            uint newCSize = (uint)newBlockC.Count;
            var result = new byte[12 + cd.ASize + cd.BSize + newCSize];
            Array.Copy(BitConverter.GetBytes(cd.ASize), 0, result, 0, 4);
            Array.Copy(BitConverter.GetBytes(cd.BSize), 0, result, 4, 4);
            Array.Copy(BitConverter.GetBytes(newCSize), 0, result, 8, 4);
            Array.Copy(newBlockA, 0, result, 12, (int)cd.ASize);
            Array.Copy(newBlockB, 0, result, 12 + (int)cd.ASize, (int)cd.BSize);
            Array.Copy(newBlockC.ToArray(), 0, result, 12 + (int)cd.ASize + (int)cd.BSize, (int)newCSize);

            return result;
        }

        private static int? GetTextRegionEnd(byte[] blockC, uint cSize, uint offVal)
        {
            if (offVal + 12 > cSize) return null;

            uint cnt = BitConverter.ToUInt32(blockC, (int)offVal + 8);
            if (cnt == 0 || cnt > 100) return null;

            int rpos = (int)offVal + 12;
            for (int i = 0; i < cnt; i++)
            {
                if (rpos + 4 > cSize) return null;
                uint slen = BitConverter.ToUInt32(blockC, rpos);
                rpos += 4;
                if (slen > 10000 || rpos + slen > cSize) return null;
                rpos += (int)slen;
            }
            return rpos;
        }

        private static byte[] BuildTextData(uint v0, uint v1, List<string> lines)
        {
            var buf = new List<byte>();
            uint cnt = (uint)lines.Count;
            buf.AddRange(BitConverter.GetBytes(v0));
            buf.AddRange(BitConverter.GetBytes(v1));
            buf.AddRange(BitConverter.GetBytes(cnt));

            foreach (var line in lines)
            {
                byte[] encoded = ShiftJis.GetBytes(line);
                byte[] encrypted = XorEncrypt(encoded);
                buf.AddRange(BitConverter.GetBytes((uint)encrypted.Length));
                buf.AddRange(encrypted);
            }

            return buf.ToArray();
        }
    }
}
