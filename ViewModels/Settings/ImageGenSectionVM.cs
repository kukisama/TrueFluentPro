using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings
{
    public class ImageGenSectionVM : SettingsSectionBase
    {
        private ModelOption? _selectedImageModel;
        private List<ModelOption> _imageModels = new();
        private bool _enableChatImageGeneration = true;

        private string _imageSize = "1024x1024";
        private string _imageQuality = "medium";
        private string _imageFormat = "png";
        private int _imageCount = 1;
        private ImageEditMode _imageEditMode = ImageEditMode.V2ResponsesApi;

        public List<ModelOption> ImageModels { get => _imageModels; set => SetProperty(ref _imageModels, value); }
        public ModelOption? SelectedImageModel
        {
            get => _selectedImageModel;
            set
            {
                if (Set(ref _selectedImageModel, value))
                    OnPropertyChanged(nameof(HasImageModel));
            }
        }

        /// <summary>是否已选择图片模型（控制「图像意图分析」复选框是否可用）</summary>
        public bool HasImageModel => _selectedImageModel != null;

        /// <summary>会话聊天中启用图像意图分析</summary>
        public bool EnableChatImageGeneration { get => _enableChatImageGeneration; set => Set(ref _enableChatImageGeneration, value); }

        public ObservableCollection<string> ImageSizeOptions { get; } = new(Helpers.ImageSizeCalculator.Presets);
        public List<string> ImageQualityOptions { get; } = ["auto", "low", "medium", "high"];
        public List<string> ImageFormatOptions { get; } = ["png", "jpeg"];
        public List<int> ImageCountOptions { get; } = [1, 2, 3, 4, 5];
        public List<ImageEditMode> ImageEditModeOptions { get; } = [ImageEditMode.V2ResponsesApi, ImageEditMode.V1Multipart];

        public string ImageSize
        {
            get => _imageSize;
            set
            {
                if (string.Equals(_imageSize, value)) return;
                // 先确保值在列表中（单自定义槽位），再触发 PropertyChanged
                EnsureSizeInList(value);
                Set(ref _imageSize, value);
            }
        }

        private string? _customSizeItem;
        /// <summary>保证值在 ImageSizeOptions 中，非预设值只保留一个自定义槽位</summary>
        private void EnsureSizeInList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || Helpers.ImageSizeCalculator.PresetSet.Contains(value))
                return;
            if (_customSizeItem != null)
                ImageSizeOptions.Remove(_customSizeItem);
            _customSizeItem = value;
            ImageSizeOptions.Add(value);
        }
        public string ImageQuality { get => _imageQuality; set => Set(ref _imageQuality, value); }
        public string ImageFormat { get => _imageFormat; set => Set(ref _imageFormat, value); }
        public int ImageCount { get => _imageCount; set => Set(ref _imageCount, value); }
        public ImageEditMode ImageEditMode { get => _imageEditMode; set => Set(ref _imageEditMode, value); }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            config.MediaGenConfig ??= new MediaGenConfig();
            var media = config.MediaGenConfig;

            _imageSize = string.IsNullOrWhiteSpace(media.ImageSize) ? "1024x1024" : media.ImageSize;
            _imageQuality = string.IsNullOrWhiteSpace(media.ImageQuality) ? "medium" : media.ImageQuality;
            _imageFormat = string.IsNullOrWhiteSpace(media.ImageFormat) ? "png" : media.ImageFormat;
            _imageCount = media.ImageCount <= 0 ? 1 : media.ImageCount;
            _imageEditMode = media.ImageEditMode;
            _enableChatImageGeneration = media.EnableChatImageGeneration;

            OnPropertyChanged(nameof(ImageSize));
            OnPropertyChanged(nameof(ImageQuality));
            OnPropertyChanged(nameof(ImageFormat));
            OnPropertyChanged(nameof(ImageCount));
            OnPropertyChanged(nameof(ImageEditMode));
            OnPropertyChanged(nameof(EnableChatImageGeneration));
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            var media = config.MediaGenConfig;
            media.ImageSize = string.IsNullOrWhiteSpace(_imageSize) ? "1024x1024" : _imageSize;
            media.ImageQuality = string.IsNullOrWhiteSpace(_imageQuality) ? "medium" : _imageQuality;
            media.ImageFormat = string.IsNullOrWhiteSpace(_imageFormat) ? "png" : _imageFormat;
            media.ImageCount = Math.Clamp(_imageCount, 1, 10);
            // 改图模式固定为 V2ResponsesApi（file_id + Responses API），不再由用户选择
            media.ImageEditMode = ImageEditMode.V2ResponsesApi;
            media.ImageModelRef = SelectedImageModel?.Reference;
            media.EnableChatImageGeneration = _enableChatImageGeneration;
        }

        public void SelectModels(ModelReference? imageModelRef, List<ModelOption> imageModels)
        {
            ImageModels = imageModels;
            SelectModelOption(imageModelRef, imageModels, v => _selectedImageModel = v, nameof(SelectedImageModel));
        }

        public void RefreshModels(List<ModelOption> imageModels)
        {
            var imageRef = SelectedImageModel?.Reference;
            ImageModels = imageModels;
            SelectModelOption(imageRef, imageModels, v => _selectedImageModel = v, nameof(SelectedImageModel));
        }

        private void SelectModelOption(ModelReference? reference, List<ModelOption> options, Action<ModelOption?> setter, string propertyName)
        {
            var match = reference == null ? null
                : options.FirstOrDefault(o => o.Reference.EndpointId == reference.EndpointId && o.Reference.ModelId == reference.ModelId);
            setter(match);
            OnPropertyChanged(propertyName);
        }
    }
}
