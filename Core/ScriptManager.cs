using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using XuseExplorer.Core.Scripts;
using XuseExplorer.Models;

namespace XuseExplorer.Core
{
    public class ScriptManager
    {
        private readonly List<IScriptReader> _readers;
        private readonly Dictionary<string, IScriptReader> _readerByTag = new();

        public IReadOnlyList<IScriptReader> Readers => _readers;
        public List<string> LastOpenErrors { get; private set; } = new();

        public ScriptManager()
        {
            _readers = new List<IScriptReader>
            {
                new Cd3ScriptReader(),
                new CdAnetanScriptReader(),
                new CdScriptReader(),
                new CdOldScriptReader(),
            };
            foreach (var r in _readers)
                _readerByTag[r.Tag] = r;
        }

        public static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cd", ".cd3"
        };

        public static bool IsScriptFile(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            return ScriptExtensions.Contains(ext);
        }

        public ScriptFile? OpenScript(string filePath)
        {
            var errors = new List<string>();
            foreach (var reader in _readers)
            {
                try
                {
                    var script = reader.TryOpen(filePath);
                    if (script != null) return script;
                }
                catch (Exception ex)
                {
                    errors.Add($"{reader.Tag}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            LastOpenErrors = errors;
            return null;
        }

        public ScriptFile? OpenScriptFromData(byte[] data, string virtualName)
        {
            var errors = new List<string>();
            foreach (var reader in _readers)
            {
                try
                {
                    var script = reader.TryOpenFromData(data, virtualName);
                    if (script != null) return script;
                }
                catch (Exception ex)
                {
                    errors.Add($"{reader.Tag}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            LastOpenErrors = errors;
            return null;
        }

        public static string ExportToJson(ScriptFile script)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            if (script.FormatTag == "CD3/Xuse")
            {
                var blocks = new List<Dictionary<string, object>>();
                foreach (var entry in script.Entries)
                {
                    blocks.Add(new Dictionary<string, object>
                    {
                        ["seq_id"] = entry.SeqId,
                        ["voice"] = entry.Voice,
                        ["text"] = entry.Text
                    });
                }
                var wrapper = new Dictionary<string, object>
                {
                    [script.FileName] = blocks
                };
                return JsonSerializer.Serialize(wrapper, options);
            }
            else if (script.FormatTag == "CDold/Xuse")
            {
                var entries = new List<Dictionary<string, object>>();
                foreach (var entry in script.Entries)
                {
                    var dict = new Dictionary<string, object>
                    {
                        ["index"] = entry.Index,
                    };
                    if (entry.Extra != null)
                    {
                        if (entry.Extra.TryGetValue("text_idx", out var tidx))
                            dict["text_idx"] = tidx;
                        if (entry.Extra.TryGetValue("type", out var t))
                            dict["type"] = t;
                        if (entry.Extra.TryGetValue("speaker", out var spk))
                            dict["speaker"] = spk;
                    }
                    dict["text"] = entry.Text;
                    entries.Add(dict);
                }
                return JsonSerializer.Serialize(entries, options);
            }
            else
            {
                var entries = new List<Dictionary<string, object>>();
                foreach (var entry in script.Entries)
                {
                    entries.Add(new Dictionary<string, object>
                    {
                        ["index"] = entry.Index,
                        ["seq_id"] = entry.SeqId,
                        ["voice"] = entry.Voice,
                        ["text"] = entry.Text
                    });
                }
                return JsonSerializer.Serialize(entries, options);
            }
        }

        public byte[] ImportFromJson(ScriptFile script, string json)
        {
            if (!_readerByTag.TryGetValue(script.FormatTag, out var reader))
                throw new InvalidOperationException($"No reader for format: {script.FormatTag}");

            var entries = ParseJsonEntries(script, json);
            return reader.Rebuild(script.RawData, entries);
        }

        public byte[] Rebuild(ScriptFile script, List<ScriptEntry> modifiedEntries)
        {
            if (!_readerByTag.TryGetValue(script.FormatTag, out var reader))
                throw new InvalidOperationException($"No reader for format: {script.FormatTag}");

            return reader.Rebuild(script.RawData, modifiedEntries);
        }

        public static List<ScriptEntry> ParseJsonEntries(ScriptFile script, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var entries = new List<ScriptEntry>();

            if (script.FormatTag == "CD3/Xuse")
            {
                var root = doc.RootElement;
                foreach (var prop in root.EnumerateObject())
                {
                    int idx = 0;
                    foreach (var block in prop.Value.EnumerateArray())
                    {
                        entries.Add(new ScriptEntry
                        {
                            Index = idx,
                            SeqId = block.GetProperty("seq_id").GetInt32(),
                            Voice = block.GetProperty("voice").GetInt32(),
                            Text = block.GetProperty("text").GetString() ?? ""
                        });
                        idx++;
                    }
                    break;
                }
            }
            else if (script.FormatTag == "CDold/Xuse")
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var entry = new ScriptEntry
                    {
                        Index = item.GetProperty("index").GetInt32(),
                        Text = item.GetProperty("text").GetString() ?? "",
                        Extra = new Dictionary<string, object>()
                    };
                    if (item.TryGetProperty("text_idx", out var tidx))
                        entry.Extra["text_idx"] = tidx.GetInt32();
                    if (item.TryGetProperty("type", out var t))
                        entry.Extra["type"] = t.GetString() ?? "";
                    if (item.TryGetProperty("speaker", out var spk))
                        entry.Extra["speaker"] = spk.GetString() ?? "";
                    entries.Add(entry);
                }
            }
            else
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    entries.Add(new ScriptEntry
                    {
                        Index = item.GetProperty("index").GetInt32(),
                        SeqId = item.TryGetProperty("seq_id", out var sid) ? sid.GetInt32() : 0,
                        Voice = item.TryGetProperty("voice", out var v) ? v.GetInt32() : 0,
                        Text = item.GetProperty("text").GetString() ?? ""
                    });
                }
            }

            return entries;
        }

        public string FileFilter =>
            "Script Files (*.cd;*.cd3;*.bin)|*.cd;*.cd3;*.bin|All Files (*.*)|*.*";
    }
}
