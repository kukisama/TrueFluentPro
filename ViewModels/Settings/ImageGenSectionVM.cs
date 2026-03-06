using System;
using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings
{
    public class ImageGenSectionVM : SettingsSectionBase
    {
        private ModelOption? _selectedImageModel;
        private List<ModelOption> _imageModels = new();

        private string _imageSize = "1024x1024";
        private string _imageQuality = "medium";
        private string _imageFormat = "png";
        private int _imageCount = 1;

        public List<ModelOption> ImageModels { get => _imageModels; set => SetProperty(ref _imageModels, value); }
        public ModelOption? SelectedImageModel { get => _selectedImageModel; set => Set(ref _selectedImageModel, value); }

        public List<string> ImageSizeOptions { get; } = ["1024x1024", "1024x1536", "1536x1024"];
        public List<string> ImageQualityOptions { get; } = ["low", "medium", "high"];
        public List<string> ImageFormatOptions { get; } = ["png", "jpeg"];
        public List<int> ImageCountOptions { get; } = [1, 2, 3, 4, 5];

        public string ImageSize { get => _imageSize; set => Set(ref _imageSize, value); }
        public string ImageQuality { get => _imageQuality; set => Set(ref _imageQuality, value); }
        public string ImageFormat { get => _imageFormat; set => Set(ref _imageFormat, value); }
        public int ImageCount { get => _imageCount; set => Set(ref _imageCount, value); }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            config.MediaGenConfig ??= new MediaGenConfig();
            var media = config.MediaGenConfig;

            _imageSize = string.IsNullOrWhiteSpace(media.ImageSize) ? "1024x1024" : media.ImageSize;
            _imageQuality = string.IsNullOrWhiteSpace(media.ImageQuality) ? "medium" : media.ImageQuality;
            _imageFormat = string.IsNullOrWhiteSpace(media.ImageFormat) ? "png" : media.ImageFormat;
            _imageCount = media.ImageCount <= 0 ? 1 : media.ImageCount;

            OnPropertyChanged(nameof(ImageSize));
            OnPropertyChanged(nameof(ImageQuality));
            OnPropertyChanged(nameof(ImageFormat));
            OnPropertyChanged(nameof(ImageCount));
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            var media = config.MediaGenConfig;
            media.ImageSize = string.IsNullOrWhiteSpace(_imageSize) ? "1024x1024" : _imageSize;
            media.ImageQuality = string.IsNullOrWhiteSpace(_imageQuality) ? "medium" : _imageQuality;
            media.ImageFormat = string.IsNullOrWhiteSpace(_imageFormat) ? "png" : _imageFormat;
            media.ImageCount = Math.Clamp(_imageCount, 1, 10);
            media.ImageModelRef = SelectedImageModel?.Reference;
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
