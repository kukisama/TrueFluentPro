using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings
{
    public class ReviewSectionVM : SettingsSectionBase
    {
        private string _reviewSystemPrompt = "";
        private string _reviewUserContentTemplate = "";
        private ObservableCollection<ReviewSheetPreset> _reviewSheets = new();
        private ModelOption? _selectedReviewModel;
        private List<ModelOption> _textModels = new();

        public string ReviewSystemPrompt { get => _reviewSystemPrompt; set => Set(ref _reviewSystemPrompt, value); }
        public string ReviewUserContentTemplate { get => _reviewUserContentTemplate; set => Set(ref _reviewUserContentTemplate, value); }
        public ObservableCollection<ReviewSheetPreset> ReviewSheets { get => _reviewSheets; set => SetProperty(ref _reviewSheets, value); }
        public List<ModelOption> TextModels { get => _textModels; set => SetProperty(ref _textModels, value); }
        public ModelOption? SelectedReviewModel { get => _selectedReviewModel; set => Set(ref _selectedReviewModel, value); }

        public void NotifyReviewSheetsChanged()
        {
            OnPropertyChanged(nameof(ReviewSheets));
            OnChanged();
        }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            var ai = config.AiConfig ?? new AiConfig();
            ReviewSystemPrompt = AiConfig.GetEffectiveReviewSystemPrompt(ai.ReviewSystemPrompt);
            ReviewUserContentTemplate = ai.ReviewUserContentTemplate;

            var reviewSource = ai.ReviewSheets.Count > 0 ? ai.ReviewSheets : new AiConfig().ReviewSheets;
            _reviewSheets = new ObservableCollection<ReviewSheetPreset>(
                reviewSource.Select(s => new ReviewSheetPreset
                {
                    Name = s.Name,
                    FileTag = s.FileTag,
                    Prompt = s.Prompt,
                    IncludeInBatch = s.IncludeInBatch
                }));
            OnPropertyChanged(nameof(ReviewSheets));
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            var ai = config.AiConfig ?? new AiConfig();
            ai.ReviewSystemPrompt = AiConfig.GetEffectiveReviewSystemPrompt(ReviewSystemPrompt);
            ai.ReviewUserContentTemplate = ReviewUserContentTemplate?.Trim() ?? "";
            ai.ReviewSheets = ReviewSheets
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new ReviewSheetPreset
                {
                    Name = s.Name.Trim(),
                    FileTag = string.IsNullOrWhiteSpace(s.FileTag) ? "summary" : s.FileTag.Trim(),
                    Prompt = s.Prompt?.Trim() ?? "",
                    IncludeInBatch = s.IncludeInBatch
                }).ToList();
            ai.ReviewModelRef = SelectedReviewModel?.Reference;
            config.AiConfig = ai;
        }

        public void SelectModels(AiConfig ai, List<ModelOption> textModels)
        {
            TextModels = textModels;
            SelectModelOption(ai.ReviewModelRef, textModels, v => _selectedReviewModel = v, nameof(SelectedReviewModel));
        }

        public void RefreshModels(List<ModelOption> textModels)
        {
            var reviewRef = SelectedReviewModel?.Reference;
            TextModels = textModels;
            SelectModelOption(reviewRef, textModels, v => _selectedReviewModel = v, nameof(SelectedReviewModel));
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
