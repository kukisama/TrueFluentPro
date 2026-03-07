using System;
using System.Collections.Generic;
using System.Linq;

namespace TrueFluentPro.Services.EndpointTesting;

public enum EndpointBatchTestStatus
{
	Success,
	Failed,
	Skipped
}

public enum EndpointBatchTestLiveState
{
	Pending,
	Running,
	Success,
	Failed,
	Skipped,
	Canceled
}

public sealed class EndpointBatchTestItem
{
	public int Order { get; init; }
	public string EndpointId { get; init; } = "";
	public string EndpointName { get; init; } = "";
	public string EndpointTypeName { get; init; } = "";
	public string CapabilityName { get; init; } = "";
	public string ModelId { get; init; } = "";
	public EndpointBatchTestStatus Status { get; init; }
	public string Summary { get; init; } = "";
	public string Details { get; init; } = "";
	public string RequestSummary { get; init; } = "";
	public TimeSpan Duration { get; init; }

	public bool IsSuccess => Status == EndpointBatchTestStatus.Success;
	public bool IsFailed => Status == EndpointBatchTestStatus.Failed;
	public bool IsSkipped => Status == EndpointBatchTestStatus.Skipped;
}

public sealed class EndpointBatchTestProgressItem
{
	public int Order { get; init; }
	public string EndpointId { get; init; } = "";
	public string EndpointName { get; init; } = "";
	public string EndpointTypeName { get; init; } = "";
	public string CapabilityName { get; init; } = "";
	public string ModelId { get; init; } = "";
	public EndpointBatchTestLiveState State { get; init; }
	public string Summary { get; init; } = "";
	public string Details { get; init; } = "";
	public string RequestSummary { get; init; } = "";
	public TimeSpan Duration { get; init; }
}

public sealed class EndpointBatchTestProgressSnapshot
{
	public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
	public DateTimeOffset? CompletedAt { get; init; }
	public string EndpointId { get; init; } = "";
	public string EndpointName { get; init; } = "";
	public IReadOnlyList<EndpointBatchTestProgressItem> Items { get; init; } = Array.Empty<EndpointBatchTestProgressItem>();
	public bool IsCompleted { get; init; }

	public int TotalCount => Items.Count;
	public int PendingCount => Items.Count(item => item.State == EndpointBatchTestLiveState.Pending);
	public int RunningCount => Items.Count(item => item.State == EndpointBatchTestLiveState.Running);
	public int SuccessCount => Items.Count(item => item.State == EndpointBatchTestLiveState.Success);
	public int FailedCount => Items.Count(item => item.State == EndpointBatchTestLiveState.Failed);
	public int SkippedCount => Items.Count(item => item.State == EndpointBatchTestLiveState.Skipped);
	public int CanceledCount => Items.Count(item => item.State == EndpointBatchTestLiveState.Canceled);
}

public sealed class EndpointBatchTestReport
{
	public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
	public TimeSpan Duration { get; init; }
	public IReadOnlyList<EndpointBatchTestItem> Items { get; init; } = Array.Empty<EndpointBatchTestItem>();

	public int TotalCount => Items.Count;
	public int SuccessCount => Items.Count(item => item.Status == EndpointBatchTestStatus.Success);
	public int FailedCount => Items.Count(item => item.Status == EndpointBatchTestStatus.Failed);
	public int SkippedCount => Items.Count(item => item.Status == EndpointBatchTestStatus.Skipped);
}
