using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    public sealed partial class BatchBucketNavItem : ObservableObject
    {
        [ObservableProperty]
        private string _key = "";

        [ObservableProperty]
        private string _title = "";

        [ObservableProperty]
        private string _iconValue = "";

        [ObservableProperty]
        private int _count;

        /// <summary>Key == "failed" — 供 XAML Classes.danger 绑定</summary>
        public bool IsDanger => string.Equals(Key, "failed", System.StringComparison.Ordinal);
    }
}