using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Scripts
{
    public class Cd3ScriptReader : IScriptReader
    {
        public string Tag => "CD3/Xuse";
        public string Description => "Xuse CD3 script file (SMK/NORI)";
        public string[] Extensions => new[] { ".cd3", ".bin" };

        private static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        public ScriptFile? TryOpen(string filePath)
        {
            var data = File.ReadAllBytes(filePath);
            return TryOpenFromData(data, filePath);
        }

        public ScriptFile? TryOpenFromData(byte[] data, string virtualName)
        {
            try
            {
                var parsed = ParseCd3(data);
                if (parsed == null) return null;

                var entries = ScriptDump(data, parsed);

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
            return BuildCd3FromScript(originalData, modifiedEntries);
        }

        private class Cd3Parsed
        {
            public Dictionary<string, uint> Header = new();
            public List<ushort> Seps = new();
            public List<SectionEntry> Labels = new();
            public List<SectionEntry> Extras = new();
            public List<SectionEntry> Jumps = new();
            public List<Bytecode> Bytecodes = new();
            public List<TextBlock> TextBlocks = new();
            public int SecDOff;
            public int SecEOff;
            public byte[] SecERaw = Array.Empty<byte>();
        }

        private class SectionEntry
        {
            public uint Value;
            public string Name = "";
            public byte[] NameBytes = Array.Empty<byte>();
            public int Offset;
            public int Size;
        }

        private class Bytecode
        {
            public int Index;
            public int Offset;
            public ushort Op;
            public ushort P1;
            public uint P2;
        }

        private class TextBlock
        {
            public int Index;
            public int BlockOffset;
            public int End;
            public uint SeqId;
            public uint Voice;
            public string Text = "";
            public byte[] TextBytes = Array.Empty<byte>();
            public byte[] TextRaw = Array.Empty<byte>();
        }

        private static Cd3Parsed? ParseCd3(byte[] data)
        {
            if (data.Length < 42) return null;

            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic != 0x204B4D53 && magic != 0x49524F4E) return null;

            var h = new Dictionary<string, uint>();
            string[] hdrNames = { "magic", "ver", "lbl_n", "secA", "ext_n", "secB", "jmp_n", "secC", "secD", "secE" };
            for (int i = 0; i < 10; i++)
                h[hdrNames[i]] = BitConverter.ToUInt32(data, i * 4);

            var seps = new List<ushort>();
            int pos = 40;

            ushort ReadSep()
            {
                ushort s = BitConverter.ToUInt16(data, pos);
                pos += 2;
                seps.Add(s);
                return s;
            }

            ReadSep();
            var (labels, labelsEnd) = ParseSectionEntries(data, pos, (int)h["lbl_n"]);
            pos = labelsEnd;
            ReadSep();
            var (extras, extrasEnd) = ParseSectionEntries(data, pos, (int)h["ext_n"]);
            pos = extrasEnd;
            ReadSep();
            var (jumps, jumpsEnd) = ParseSectionEntries(data, pos, (int)h["jmp_n"]);
            pos = jumpsEnd;
            ReadSep();

            int secDOff = pos;
            var bytecodes = ParseBytecodes(data, pos, (int)h["secD"]);
            pos += (int)h["secD"];
            ReadSep();

            int secEOff = pos;
            int secESize = (int)h["secE"];
            byte[] secERaw = new byte[secESize];
            Array.Copy(data, pos, secERaw, 0, secESize);
            pos += secESize;
            ReadSep();

            var textBlocks = ParseTextBlocks(bytecodes, secERaw, secESize);

            foreach (var blk in textBlocks)
            {
                int txtStart = blk.BlockOffset + 8;
                var segs = new List<(int off, int len)>();
                foreach (var bc in bytecodes)
                {
                    if (bc.Op == 5 && blk.BlockOffset <= bc.P2 && bc.P2 < blk.End)
                        segs.Add(((int)bc.P2, bc.P1));
                }
                segs.Sort((a, b) => a.off.CompareTo(b.off));

                if (segs.Count <= 1) continue;

                var parts = new List<string>();
                foreach (var (segOff, segLen) in segs)
                {
                    int rel = segOff - txtStart;
                    if (rel < 0 || rel + segLen > blk.TextRaw.Length) continue;
                    byte[] raw = new byte[segLen];
                    Array.Copy(blk.TextRaw, rel, raw, 0, segLen);
                    byte[] dec = XorBytes(raw);
                    parts.Add(ShiftJis.GetString(dec));
                }
                blk.Text = string.Join("\n", parts);
            }

            return new Cd3Parsed
            {
                Header = h,
                Seps = seps,
                Labels = labels,
                Extras = extras,
                Jumps = jumps,
                Bytecodes = bytecodes,
                TextBlocks = textBlocks,
                SecDOff = secDOff,
                SecEOff = secEOff,
                SecERaw = secERaw
            };
        }

        private static (List<SectionEntry> entries, int endPos) ParseSectionEntries(byte[] data, int offset, int count)
        {
            var entries = new List<SectionEntry>();
            int pos = offset;
            for (int i = 0; i < count; i++)
            {
                uint rawVal = BitConverter.ToUInt32(data, pos);
                uint nameLen = BitConverter.ToUInt32(data, pos + 4) ^ 0x53535353;
                byte[] nameRaw = new byte[nameLen];
                Array.Copy(data, pos + 8, nameRaw, 0, (int)nameLen);
                byte[] nameDec = XorBytes(nameRaw);
                string name;
                try { name = ShiftJis.GetString(nameDec); }
                catch { name = Encoding.Latin1.GetString(nameDec); }

                entries.Add(new SectionEntry
                {
                    Value = rawVal,
                    Name = name,
                    NameBytes = nameDec,
                    Offset = pos,
                    Size = 8 + (int)nameLen
                });
                pos += 8 + (int)nameLen;
            }
            return (entries, pos);
        }

        private static List<Bytecode> ParseBytecodes(byte[] data, int offset, int size)
        {
            var result = new List<Bytecode>();
            int end = offset + size;
            int idx = 0;
            int p = offset;
            while (p + 8 <= end)
            {
                ushort op = BitConverter.ToUInt16(data, p);
                ushort p1 = BitConverter.ToUInt16(data, p + 2);
                uint p2 = BitConverter.ToUInt32(data, p + 4);
                result.Add(new Bytecode { Index = idx, Offset = p - offset, Op = op, P1 = p1, P2 = p2 });
                p += 8;
                idx++;
            }
            return result;
        }

        private static List<TextBlock> ParseTextBlocks(List<Bytecode> bytecodes, byte[] secERaw, int secESize)
        {
            var starts = new SortedSet<int>();
            foreach (var bc in bytecodes)
            {
                if (bc.Op == 3 && bc.P2 < (uint)secESize)
                    starts.Add((int)bc.P2);
            }

            var startList = starts.ToList();
            var blocks = new List<TextBlock>();

            for (int idx = 0; idx < startList.Count; idx++)
            {
                int s = startList[idx];
                int e = (idx + 1 < startList.Count) ? startList[idx + 1] : secESize;
                if (s + 8 > secESize) continue;

                uint seqId = BitConverter.ToUInt32(secERaw, s);
                uint voice = BitConverter.ToUInt32(secERaw, s + 4);

                byte[] rawTxt = new byte[e - s - 8];
                Array.Copy(secERaw, s + 8, rawTxt, 0, rawTxt.Length);
                byte[] decTxt = XorBytes(rawTxt);
                string text;
                try { text = ShiftJis.GetString(decTxt); }
                catch { text = Encoding.Latin1.GetString(decTxt); }

                blocks.Add(new TextBlock
                {
                    Index = idx,
                    BlockOffset = s,
                    End = e,
                    SeqId = seqId,
                    Voice = voice,
                    Text = text,
                    TextBytes = decTxt,
                    TextRaw = rawTxt
                });
            }
            return blocks;
        }

        private static List<ScriptEntry> ScriptDump(byte[] data, Cd3Parsed r)
        {
            var secERaw = r.SecERaw;
            var setupForOp3 = BlockSetupInfo(r.Bytecodes);
            var blocksOut = new List<ScriptEntry>();

            for (int bIdx = 0; bIdx < r.TextBlocks.Count; bIdx++)
            {
                var blk = r.TextBlocks[bIdx];
                var op3Bc = r.Bytecodes.FirstOrDefault(bc => bc.Op == 3 && bc.P2 == (uint)blk.BlockOffset);
                Bytecode? setupBc = null;
                if (op3Bc != null && setupForOp3.TryGetValue(op3Bc.Index, out var sb))
                    setupBc = sb;

                int nText = setupBc != null ? (int)setupBc.P2 : 0;

                var allOp5 = r.Bytecodes
                    .Where(bc => bc.Op == 5 && blk.BlockOffset <= bc.P2 && bc.P2 < blk.End)
                    .OrderBy(bc => bc.Index)
                    .ToList();

                var parts = new List<string>();
                foreach (var bc in allOp5.Take(nText))
                {
                    int off = (int)bc.P2;
                    int len = bc.P1;
                    if (off + len > secERaw.Length) continue;
                    byte[] raw = new byte[len];
                    Array.Copy(secERaw, off, raw, 0, len);
                    byte[] dec = XorBytes(raw);
                    try { parts.Add(ShiftJis.GetString(dec)); }
                    catch { parts.Add(Encoding.Latin1.GetString(dec)); }
                }

                blocksOut.Add(new ScriptEntry
                {
                    Index = bIdx,
                    SeqId = (int)blk.SeqId,
                    Voice = (int)blk.Voice,
                    Text = string.Join("\n", parts)
                });
            }

            return blocksOut;
        }

        private static Dictionary<int, Bytecode> BlockSetupInfo(List<Bytecode> bytecodes)
        {
            var result = new Dictionary<int, Bytecode>();
            var byI = bytecodes.ToDictionary(bc => bc.Index);
            foreach (var bc in bytecodes)
            {
                if (bc.Op == 3 && byI.TryGetValue(bc.Index - 1, out var prev))
                {
                    if (prev.Op == 1 || prev.Op == 60)
                        result[bc.Index] = prev;
                }
            }
            return result;
        }

        private static ushort Crc16Game(byte[] dataBytes, ushort init = 0)
        {
            ushort crc = init;
            foreach (byte b in dataBytes)
            {
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

        private static ushort FindStoredCrc(ushort dataCrc)
        {
            for (int val = 0; val < 0x10000; val++)
            {
                byte[] packed = BitConverter.GetBytes((ushort)val);
                if (Crc16Game(packed, dataCrc) == 0)
                    return (ushort)val;
            }
            throw new InvalidOperationException("Could not find stored CRC value");
        }

        private static ushort ComputeXorBlockCrc(byte[] rawBlockData, int itemCount)
        {
            ushort crc = 0;
            int offset = 0;

            for (int i = 0; i < itemCount; i++)
            {
                byte[] raw4 = new byte[4];
                Array.Copy(rawBlockData, offset, raw4, 0, 4);
                crc = Crc16Game(raw4, crc);

                uint enc4 = BitConverter.ToUInt32(rawBlockData, offset + 4);
                uint dec4 = enc4 ^ 0x53535353;
                byte[] dec4Bytes = BitConverter.GetBytes(dec4);
                crc = Crc16Game(dec4Bytes, crc);

                int innerCount = (int)dec4;
                byte[] decInner = new byte[innerCount];
                for (int j = 0; j < innerCount; j++)
                    decInner[j] = (byte)(rawBlockData[offset + 8 + j] ^ 0x53);
                crc = Crc16Game(decInner, crc);

                offset += 8 + innerCount;
            }

            return crc;
        }

        private static byte[] FixCd3Crcs(byte[] data)
        {
            var result = new byte[data.Length];
            Array.Copy(data, result, data.Length);

            uint[] h = new uint[10];
            for (int i = 0; i < 10; i++)
                h[i] = BitConverter.ToUInt32(result, i * 4);

            int[] pos = new int[5];
            uint[] sizes = { h[3], h[5], h[7], h[8], h[9] };
            uint[] counts = { h[2], h[4], h[6] };

            pos[0] = 0x2A;
            for (int i = 0; i < 4; i++)
                pos[i + 1] = pos[i] + (int)sizes[i] + 2;

            for (int i = 0; i < 3; i++)
            {
                byte[] blockData = new byte[sizes[i]];
                Array.Copy(result, pos[i], blockData, 0, (int)sizes[i]);
                ushort crc = ComputeXorBlockCrc(blockData, (int)counts[i]);
                ushort newStored = FindStoredCrc(crc);
                byte[] storedBytes = BitConverter.GetBytes(newStored);
                Array.Copy(storedBytes, 0, result, pos[i] + (int)sizes[i], 2);
            }

            for (int i = 3; i < 5; i++)
            {
                byte[] blockData = new byte[sizes[i]];
                Array.Copy(result, pos[i], blockData, 0, (int)sizes[i]);
                ushort crc = Crc16Game(blockData);
                ushort newStored = FindStoredCrc(crc);
                byte[] storedBytes = BitConverter.GetBytes(newStored);
                Array.Copy(storedBytes, 0, result, pos[i] + (int)sizes[i], 2);
            }

            byte[] hdrData = new byte[0x28];
            Array.Copy(result, 0, hdrData, 0, 0x28);
            ushort hdrCrc = Crc16Game(hdrData);
            ushort newHdrStored = FindStoredCrc(hdrCrc);
            byte[] hdrStoredBytes = BitConverter.GetBytes(newHdrStored);
            Array.Copy(hdrStoredBytes, 0, result, 0x28, 2);

            return result;
        }

        private static byte[] BuildEntryRaw(uint value, byte[] nameBytes)
        {
            byte[] enc = XorBytes(nameBytes);
            using var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(value), 0, 4);
            ms.Write(BitConverter.GetBytes((uint)(nameBytes.Length ^ 0x53535353)), 0, 4);
            ms.Write(enc, 0, enc.Length);
            return ms.ToArray();
        }

        private byte[] BuildCd3FromScript(byte[] originalData, List<ScriptEntry> blocksNew)
        {
            var r = ParseCd3(originalData);
            if (r == null) throw new InvalidOperationException("Failed to parse original CD3 data");

            var h = r.Header;
            var oldSecE = r.SecERaw;
            var bytecodes = r.Bytecodes;
            var blocksOld = r.TextBlocks;
            var byI = bytecodes.ToDictionary(bc => bc.Index);

            if (blocksNew.Count != blocksOld.Count)
                throw new InvalidOperationException(
                    $"Block count mismatch: original {blocksOld.Count}, script {blocksNew.Count}");

            int firstBlkOff = blocksOld.Count > 0 ? blocksOld[0].BlockOffset : (int)h["secE"];
            var setupForOp3 = BlockSetupInfo(bytecodes);

            var blkTextOp5 = new Dictionary<int, List<(int bcI, int p2, int p1)>>();
            var blkLabelOp5 = new Dictionary<int, List<(int bcI, int p2, int p1)>>();

            foreach (var bc in bytecodes)
            {
                if (bc.Op != 5) continue;
                for (int bIdx = 0; bIdx < blocksOld.Count; bIdx++)
                {
                    var blk = blocksOld[bIdx];
                    if (blk.BlockOffset <= bc.P2 && bc.P2 < blk.End)
                    {
                        if (!blkTextOp5.ContainsKey(bIdx))
                            blkTextOp5[bIdx] = new List<(int bcI, int p2, int p1)>();
                        blkTextOp5[bIdx].Add((bc.Index, (int)bc.P2, bc.P1));
                        break;
                    }
                }
            }

            foreach (var bIdx in blkTextOp5.Keys.ToList())
            {
                var op3Bc = bytecodes.FirstOrDefault(b => b.Op == 3 && b.P2 == (uint)blocksOld[bIdx].BlockOffset);
                Bytecode? setupBc = null;
                if (op3Bc != null && setupForOp3.TryGetValue(op3Bc.Index, out var sb))
                    setupBc = sb;
                int nText = setupBc != null ? (int)setupBc.P2 : 0;

                var allSorted = blkTextOp5[bIdx].OrderBy(x => x.bcI).ToList();
                blkTextOp5[bIdx] = allSorted.Take(nText).ToList();
                blkLabelOp5[bIdx] = allSorted.Skip(nText).ToList();
            }

            var offsetMap = new Dictionary<int, int>();
            using var newSecEMs = new MemoryStream();
            newSecEMs.Write(oldSecE, 0, firstBlkOff);
            for (int i = 0; i < firstBlkOff; i++)
                offsetMap[i] = i;

            var op5Update = new Dictionary<int, (int p2, int p1)>();
            var insertAfter = new Dictionary<int, List<(int p2, int p1)>>();
            var removeSet = new HashSet<int>();
            var setupPatch = new Dictionary<int, (int newP1, int newP2)>();

            for (int bIdx = 0; bIdx < blocksOld.Count; bIdx++)
            {
                var blk = blocksOld[bIdx];
                var blkData = blocksNew[bIdx];

                if (blkData.SeqId != (int)blk.SeqId)
                    throw new InvalidOperationException($"Block {bIdx} seq_id mismatch");

                var oldText = blkTextOp5.GetValueOrDefault(bIdx, new List<(int bcI, int p2, int p1)>());
                var oldLabel = blkLabelOp5.GetValueOrDefault(bIdx, new List<(int bcI, int p2, int p1)>());

                var newParts = blkData.Text.Split('\n');
                int newN = newParts.Length;
                int oldN = oldText.Count;

                var segBytes = new List<byte[]>();
                foreach (var part in newParts)
                    segBytes.Add(ShiftJis.GetBytes(part));

                var labelBytes = new List<byte[]>();
                foreach (var (bcI, p2, p1) in oldLabel)
                {
                    byte[] raw = new byte[p1];
                    Array.Copy(oldSecE, p2, raw, 0, p1);
                    labelBytes.Add(XorBytes(raw));
                }

                int cur = (int)newSecEMs.Position;
                offsetMap[blk.BlockOffset] = cur;
                int newTxtStart = cur + 8;

                var blkHdr = new byte[8];
                Array.Copy(BitConverter.GetBytes(blk.SeqId), 0, blkHdr, 0, 4);
                Array.Copy(BitConverter.GetBytes(blk.Voice), 0, blkHdr, 4, 4);

                var textOffsets = new List<(int off, int len)>();
                int off2 = newTxtStart;
                foreach (var raw in segBytes)
                {
                    textOffsets.Add((off2, raw.Length));
                    off2 += raw.Length;
                }

                var labelOffsets = new List<(int off, int len)>();
                foreach (var raw in labelBytes)
                {
                    labelOffsets.Add((off2, raw.Length));
                    off2 += raw.Length;
                }

                int nKeep = Math.Min(oldN, newN);
                for (int i = 0; i < nKeep; i++)
                {
                    var (bcI, oldP2, _) = oldText[i];
                    var (newP2, newP1) = textOffsets[i];
                    op5Update[bcI] = (newP2, newP1);
                    offsetMap[oldP2] = newP2;
                }

                for (int i = newN; i < oldN; i++)
                    removeSet.Add(oldText[i].bcI);

                if (newN > oldN)
                {
                    int anchor = nKeep > 0 ? oldText[nKeep - 1].bcI : -1;
                    var inserts = new List<(int p2, int p1)>();
                    for (int i = oldN; i < newN; i++)
                        inserts.Add(textOffsets[i]);
                    if (anchor >= 0)
                        insertAfter[anchor] = inserts;
                }

                for (int i = 0; i < oldLabel.Count; i++)
                {
                    var (bcI, oldP2, _) = oldLabel[i];
                    var (newP2, newP1) = labelOffsets[i];
                    op5Update[bcI] = (newP2, newP1);
                    offsetMap[oldP2] = newP2;
                }

                var op3BcR = bytecodes.FirstOrDefault(b => b.Op == 3 && b.P2 == (uint)blk.BlockOffset);
                if (op3BcR != null && setupForOp3.TryGetValue(op3BcR.Index, out var setupBcR))
                    setupPatch[setupBcR.Index] = (newN + 1, newN);

                if (oldText.Count == 0 && oldLabel.Count == 0)
                    offsetMap[blk.BlockOffset + 8] = newTxtStart;

                var allRaw = new List<byte>();
                foreach (var sb in segBytes) allRaw.AddRange(sb);
                foreach (var lb in labelBytes) allRaw.AddRange(lb);

                var encAll = XorBytes(allRaw.ToArray());
                newSecEMs.Write(blkHdr, 0, blkHdr.Length);
                newSecEMs.Write(encAll, 0, encAll.Length);
            }

            byte[] newSecE = newSecEMs.ToArray();
            int newSecESize = newSecE.Length;

            var sortedKeys = offsetMap.Keys.OrderBy(k => k).ToList();

            int Remap(int old)
            {
                if (offsetMap.TryGetValue(old, out int mapped))
                    return mapped;
                int idx = BinarySearchFloor(sortedKeys, old);
                if (idx < 0) return old;
                int baseKey = sortedKeys[idx];
                return offsetMap[baseKey] + (old - baseKey);
            }

            using var newSecDMs = new MemoryStream();
            foreach (var bc in bytecodes)
            {
                if (removeSet.Contains(bc.Index)) continue;

                if (setupPatch.TryGetValue(bc.Index, out var sp))
                {
                    WriteBC(newSecDMs, bc.Op, (ushort)sp.newP1, (uint)sp.newP2);
                }
                else if (bc.Op == 3 && offsetMap.ContainsKey((int)bc.P2))
                {
                    WriteBC(newSecDMs, bc.Op, bc.P1, (uint)Remap((int)bc.P2));
                }
                else if (op5Update.TryGetValue(bc.Index, out var upd))
                {
                    WriteBC(newSecDMs, bc.Op, (ushort)upd.p1, (uint)upd.p2);
                    if (insertAfter.TryGetValue(bc.Index, out var inserts))
                    {
                        foreach (var (insP2, insP1) in inserts)
                            WriteBC(newSecDMs, 5, (ushort)insP1, (uint)insP2);
                    }
                }
                else
                {
                    WriteBC(newSecDMs, bc.Op, bc.P1, bc.P2);
                }
            }
            byte[] newSecD = newSecDMs.ToArray();

            byte[] newSecA = BuildSection(r.Labels, false);
            byte[] newSecB = BuildSection(r.Extras, false);
            byte[] newSecC = BuildSectionWithRemap(r.Jumps, Remap);

            var seps = r.Seps;

            byte[] newHdr = new byte[40];
            WriteUInt32(newHdr, 0, h["magic"]);
            WriteUInt32(newHdr, 4, h["ver"]);
            WriteUInt32(newHdr, 8, h["lbl_n"]);
            WriteUInt32(newHdr, 12, (uint)newSecA.Length);
            WriteUInt32(newHdr, 16, h["ext_n"]);
            WriteUInt32(newHdr, 20, (uint)newSecB.Length);
            WriteUInt32(newHdr, 24, h["jmp_n"]);
            WriteUInt32(newHdr, 28, (uint)newSecC.Length);
            WriteUInt32(newHdr, 32, (uint)newSecD.Length);
            WriteUInt32(newHdr, 36, (uint)newSecESize);

            using var outMs = new MemoryStream();
            outMs.Write(newHdr, 0, newHdr.Length);
            WriteSep(outMs, seps[0]); outMs.Write(newSecA, 0, newSecA.Length);
            WriteSep(outMs, seps[1]); outMs.Write(newSecB, 0, newSecB.Length);
            WriteSep(outMs, seps[2]); outMs.Write(newSecC, 0, newSecC.Length);
            WriteSep(outMs, seps[3]); outMs.Write(newSecD, 0, newSecD.Length);
            WriteSep(outMs, seps[4]); outMs.Write(newSecE, 0, newSecE.Length);
            WriteSep(outMs, seps[5]);

            return FixCd3Crcs(outMs.ToArray());
        }

        private static byte[] XorBytes(byte[] data)
        {
            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ 0x53);
            return result;
        }

        private static int BinarySearchFloor(List<int> sorted, int target)
        {
            int lo = 0, hi = sorted.Count - 1;
            int result = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] <= target) { result = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return result;
        }

        private static void WriteBC(MemoryStream ms, ushort op, ushort p1, uint p2)
        {
            ms.Write(BitConverter.GetBytes(op), 0, 2);
            ms.Write(BitConverter.GetBytes(p1), 0, 2);
            ms.Write(BitConverter.GetBytes(p2), 0, 4);
        }

        private static void WriteSep(MemoryStream ms, ushort sep)
        {
            ms.Write(BitConverter.GetBytes(sep), 0, 2);
        }

        private static void WriteUInt32(byte[] buf, int offset, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, buf, offset, 4);
        }

        private static byte[] BuildSection(List<SectionEntry> entries, bool remap)
        {
            using var ms = new MemoryStream();
            foreach (var e in entries)
                ms.Write(BuildEntryRaw(e.Value, e.NameBytes), 0, BuildEntryRaw(e.Value, e.NameBytes).Length);
            return ms.ToArray();
        }

        private static byte[] BuildSectionWithRemap(List<SectionEntry> entries, Func<int, int> remap)
        {
            using var ms = new MemoryStream();
            foreach (var e in entries)
            {
                byte[] raw = BuildEntryRaw((uint)remap((int)e.Value), e.NameBytes);
                ms.Write(raw, 0, raw.Length);
            }
            return ms.ToArray();
        }
    }
}
