namespace Runspace.Core;

public static class RetentionPolicy
{
    public const int ResultRows = 25_000;
    public const int ResultSets = 4;
    public const int DiagnosticRecords = 2_000;
    public const int ErrorRecords = 1_000;
    public const int MessageCharacters = 16_384;
    public const int NavigationChildren = 200;
    public const int NavigationNodes = 1_000;
    public const int NavigationRoutes = 100;
    public const int HistoryEntries = 200;

    public static string BoundText(string text) => text.Length <= MessageCharacters ? text :
        text[..MessageCharacters] + "\n[Message shortened by retention policy; requery with file logging for full text.]";
}

/// <summary>Bounded stream tails with a separate error budget and one latest progress record.</summary>
public sealed class DiagnosticBuffer
{
    private readonly object gate = new();
    private readonly Queue<(long Sequence, DiagnosticRecord Record)> messages = new();
    private readonly Queue<(long Sequence, DiagnosticRecord Record)> errors = new();
    private (long Sequence, DiagnosticRecord Record)? progress;
    private long sequence;
    private long errorCount;
    private long evicted;
    private long coalesced;
    private long shortened;

    public long ErrorCount { get { lock (gate) return errorCount; } }
    public long ReceivedCount { get { lock (gate) return sequence; } }
    public int PeakCount { get; private set; }

    public void Add(DiagnosticRecord record)
    {
        lock (gate)
        {
            var text = RetentionPolicy.BoundText(record.Message);
            if (text != record.Message) shortened++;
            var entry = (++sequence, record with { Message = text });
            if (record.Stream == "Progress")
            {
                if (progress is not null) coalesced++;
                progress = entry;
            }
            else
            {
                var queue = record.Stream == "Error" ? errors : messages;
                var limit = record.Stream == "Error" ? RetentionPolicy.ErrorRecords : RetentionPolicy.DiagnosticRecords;
                if (record.Stream == "Error") errorCount++;
                if (queue.Count == limit) { queue.Dequeue(); evicted++; }
                queue.Enqueue(entry);
            }
            PeakCount = Math.Max(PeakCount, messages.Count + errors.Count + (progress is null ? 0 : 1));
        }
    }

    public IReadOnlyList<DiagnosticRecord> Snapshot()
    {
        lock (gate)
        {
            var entries = messages.Concat(errors);
            if (progress is { } latest) entries = entries.Append(latest);
            var records = entries.OrderBy(entry => entry.Sequence).Select(entry => entry.Record).ToList();
            if (evicted > 0 || coalesced > 0 || shortened > 0)
                records.Add(new(DateTimeOffset.Now, "Retention",
                    $"Streams: {evicted:N0} older records evicted, {coalesced:N0} progress updates coalesced, " +
                    $"{shortened:N0} messages shortened. Errors have a separate {RetentionPolicy.ErrorRecords:N0}-record budget. " +
                    "Copy/save diagnostics before replacement; requery with file logging for a complete transcript."));
            return records;
        }
    }
}
