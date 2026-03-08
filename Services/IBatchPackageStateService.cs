using System.Collections.Generic;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface IBatchPackageStateService
    {
        void EnsurePackages(IEnumerable<MediaFileItem> audioFiles);
        bool IsRemoved(string audioPath);
        bool IsPaused(string audioPath);
        bool IsExpanded(string audioPath);
        void SetRemoved(string audioPath, bool isRemoved);
        void SetPaused(string audioPath, bool isPaused);
        void SetExpanded(string audioPath, bool isExpanded);
    }
}
