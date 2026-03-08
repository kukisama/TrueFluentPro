using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public sealed class BatchPackageStateService : IBatchPackageStateService
    {
        private readonly object _gate = new();
        private readonly string _filePath;
        private readonly string _backupPath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly Dictionary<string, BatchPackageStateEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public BatchPackageStateService()
        {
            _filePath = PathManager.Instance.GetConfigFile("batch-packages.json");
            _backupPath = _filePath + ".bak";
            Load();
        }

        public void EnsurePackages(IEnumerable<MediaFileItem> audioFiles)
        {
            var changed = false;
            lock (_gate)
            {
                foreach (var audioFile in audioFiles)
                {
                    if (string.IsNullOrWhiteSpace(audioFile.FullPath))
                    {
                        continue;
                    }

                    if (_entries.ContainsKey(audioFile.FullPath))
                    {
                        continue;
                    }

                    _entries[audioFile.FullPath] = new BatchPackageStateEntry
                    {
                        AudioPath = audioFile.FullPath,
                        DisplayName = audioFile.Name,
                        IsExpanded = false,
                        IsPaused = false,
                        IsRemoved = false,
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    changed = true;
                }
            }

            if (changed)
            {
                Save();
            }
        }

        public bool IsRemoved(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return false;
            }

            lock (_gate)
            {
                return _entries.TryGetValue(audioPath, out var entry) && entry.IsRemoved;
            }
        }

        public bool IsPaused(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return false;
            }

            lock (_gate)
            {
                return _entries.TryGetValue(audioPath, out var entry) && entry.IsPaused;
            }
        }

        public bool IsExpanded(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return false;
            }

            lock (_gate)
            {
                return _entries.TryGetValue(audioPath, out var entry) && entry.IsExpanded;
            }
        }

        public void SetRemoved(string audioPath, bool isRemoved)
        {
            if (!TryGetOrCreate(audioPath, out var entry))
            {
                return;
            }

            lock (_gate)
            {
                if (entry.IsRemoved == isRemoved)
                {
                    return;
                }

                entry.IsRemoved = isRemoved;
                if (isRemoved)
                {
                    entry.IsPaused = false;
                }
                entry.UpdatedAtUtc = DateTime.UtcNow;
            }

            Save();
        }

        public void SetPaused(string audioPath, bool isPaused)
        {
            if (!TryGetOrCreate(audioPath, out var entry))
            {
                return;
            }

            lock (_gate)
            {
                if (entry.IsPaused == isPaused)
                {
                    return;
                }

                entry.IsPaused = isPaused;
                if (isPaused)
                {
                    entry.IsRemoved = false;
                }
                entry.UpdatedAtUtc = DateTime.UtcNow;
            }

            Save();
        }

        public void SetExpanded(string audioPath, bool isExpanded)
        {
            if (!TryGetOrCreate(audioPath, out var entry))
            {
                return;
            }

            lock (_gate)
            {
                if (entry.IsExpanded == isExpanded)
                {
                    return;
                }

                entry.IsExpanded = isExpanded;
                entry.UpdatedAtUtc = DateTime.UtcNow;
            }

            Save();
        }

        private bool TryGetOrCreate(string audioPath, out BatchPackageStateEntry entry)
        {
            entry = null!;
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return false;
            }

            lock (_gate)
            {
                if (_entries.TryGetValue(audioPath, out entry!))
                {
                    return true;
                }

                entry = new BatchPackageStateEntry
                {
                    AudioPath = audioPath,
                    DisplayName = Path.GetFileName(audioPath),
                    UpdatedAtUtc = DateTime.UtcNow
                };
                _entries[audioPath] = entry;
                return true;
            }
        }

        private void Load()
        {
            var loaded = TryLoadFrom(_filePath) || TryLoadFrom(_backupPath);
            if (!loaded)
            {
                lock (_gate)
                {
                    _entries.Clear();
                }
            }
        }

        private bool TryLoadFrom(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                var json = File.ReadAllText(path);
                var index = JsonSerializer.Deserialize<BatchPackageStateIndex>(json, _jsonOptions);
                if (index?.Packages == null)
                {
                    return false;
                }

                lock (_gate)
                {
                    _entries.Clear();
                    foreach (var package in index.Packages.Where(p => !string.IsNullOrWhiteSpace(p.AudioPath)))
                    {
                        _entries[package.AudioPath] = package;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Save()
        {
            BatchPackageStateIndex snapshot;
            lock (_gate)
            {
                snapshot = new BatchPackageStateIndex
                {
                    Version = 1,
                    Packages = _entries.Values
                        .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? PathManager.Instance.AppDataPath);
                var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(_filePath))
                {
                    File.Copy(_filePath, _backupPath, overwrite: true);
                }

                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch
            {
            }
        }

        private sealed class BatchPackageStateIndex
        {
            public int Version { get; set; }
            public List<BatchPackageStateEntry> Packages { get; set; } = new();
        }

        private sealed class BatchPackageStateEntry
        {
            public string AudioPath { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public bool IsRemoved { get; set; }
            public bool IsPaused { get; set; }
            public bool IsExpanded { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
        }
    }
}
