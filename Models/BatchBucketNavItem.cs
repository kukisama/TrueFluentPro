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
    }
}