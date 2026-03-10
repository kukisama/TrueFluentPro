using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public sealed class BatchPackageStateService : IBatchPackageStateService
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, BatchPackageStateEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public BatchPackageStateService()
        {
        }

        public void EnsurePackages(IEnumerable<MediaFileItem> audioFiles)
        {
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
                }
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
