namespace TrueFluentPro.ViewModels
{
    public enum AudioLabTabKind
    {
        Summary,
        Transcript,
        MindMap,
        Insight,
        Research,
        Podcast,
        Translation,
        Custom   // 自定义阶段（由 CustomStageKey 决定具体是哪个）
    }

    public enum ResearchPhase
    {
        Idle,
        GeneratingOutline,
        OutlineReady,
        OutlineEditing,
        GeneratingReport,
        ReportReady
    }
}
