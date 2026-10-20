namespace ALMARFELD.SubFlow.Core.Jobs;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    CompletedWithWarnings,
    Skipped,
    Failed,
    Cancelled,
    /// <summary>The application stopped while the item was running. It can be resumed.</summary>
    Interrupted,
}

public enum ExistingTrackPolicy
{
    /// <summary>Do not translate a language that the video already contains.</summary>
    Skip,
    /// <summary>Translate anyway and leave the original track out of the new output (explicit user choice).</summary>
    Replace,
    /// <summary>Translate anyway and keep both tracks.</summary>
    KeepBoth,
}

public enum OutputMode
{
    /// <summary>Write an MKV with the original streams plus new subtitle tracks.</summary>
    Mkv,
    /// <summary>Only write subtitle files next to the output location.</summary>
    SubtitleFiles,
    /// <summary>Both an MKV and subtitle files.</summary>
    MkvAndSubtitleFiles,
}

public enum ExistingOutputPolicy
{
    /// <summary>Skip the file if the output already exists.</summary>
    Skip,
    /// <summary>Replace a previous SubFlow output (never the source file).</summary>
    Overwrite,
    /// <summary>Write next to it with a numbered name.</summary>
    Rename,
}

/// <summary>Per-batch processing options chosen in the Translation page.</summary>
public sealed record ProcessingOptions
{
    /// <summary>ISO 639-1 code or "auto".</summary>
    public string SourceLanguage { get; init; } = "auto";
    public IReadOnlyList<string> TargetLanguages { get; init; } = Array.Empty<string>();
    public ExistingTrackPolicy ExistingTrackPolicy { get; init; } = ExistingTrackPolicy.Skip;
    public OutputMode OutputMode { get; init; } = OutputMode.Mkv;
    public ExistingOutputPolicy ExistingOutputPolicy { get; init; } = ExistingOutputPolicy.Skip;
    /// <summary>Empty = a "SubFlow Output" folder inside each import root.</summary>
    public string? OutputFolder { get; init; }
    public bool PreserveFolderStructure { get; init; } = true;
    public bool MakeFirstNewTrackDefault { get; init; }
    public string ProviderId { get; init; } = "ollama";
}

public sealed class JobItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid BatchId { get; init; }
    public required string SourcePath { get; init; }
    /// <summary>Folder the user imported; used to rebuild the relative folder structure in the output.</summary>
    public required string RootPath { get; init; }
    public string? OutputPath { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public double Progress { get; set; }
    public string? Message { get; set; }
    public List<string> Warnings { get; set; } = new();
    /// <summary>Absolute ffprobe stream index chosen as translation source (null = choose automatically).</summary>
    public int? SourceStreamIndex { get; set; }
    public required ProcessingOptions Options { get; init; }
    /// <summary>Target languages completed so far (resume skips them when their output exists).</summary>
    public List<string> CompletedLanguages { get; set; } = new();
    public List<string> SkippedLanguages { get; set; } = new();
    public List<string> SubtitleFiles { get; set; } = new();
    public string? SourceLanguageDetected { get; set; }
    public long SourceSize { get; set; }
    public DateTime SourceModifiedUtc { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public TimeSpan? Elapsed { get; set; }
    public string? EngineDescription { get; set; }

    public bool IsFinished => Status is JobStatus.Completed or JobStatus.CompletedWithWarnings or JobStatus.Skipped;
    public bool CanRetry => Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Interrupted or JobStatus.CompletedWithWarnings;
}

public interface IJobStore
{
    void Upsert(JobItem item);
    void Delete(Guid id);
    JobItem? Get(Guid id);
    /// <summary>Resumable items (pending, running, cancelled, interrupted). Failed items are listed in history.</summary>
    IReadOnlyList<JobItem> GetQueue();
    IReadOnlyList<JobItem> GetHistory(int limit = 500);
    /// <summary>Marks items left in Running state by a previous session as Interrupted.</summary>
    int MarkRunningAsInterrupted();
    void ClearHistory();
}
