namespace TrueFluentPro.Models
{
    public sealed class EndpointTemplateDefinition
    {
        public string ProfileId { get; init; } = "";
        public EndpointApiType Type { get; init; }
        public string DisplayName { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string Glyph { get; init; } = "";
        public string Summary { get; init; } = "";
        public string DefaultNamePrefix { get; init; } = "";
        public string DefaultApiVersion { get; init; } = "";
        public string IconAssetPath { get; init; } = "";
        public string ResolvedIconAssetPath => string.IsNullOrWhiteSpace(IconAssetPath)
            ? ""
            : "/" + IconAssetPath.TrimStart('/', '\\').Replace('\\', '/');
        public bool SupportsAad { get; init; }
    }
}