using System.Text.Json;
using Runspace.Core;

namespace Runspace.Tests;

public sealed class WorkspaceTests
{
    private static WorkspaceDocument Sample() => new()
    {
        Layout = new()
        {
            Width = 1500, Height = 950, X = -1200, Y = 100, NavigationWidth = 280,
            ActionsWidth = 310, DiagnosticsHeight = 240, NavigationVisible = false,
            DiagnosticsVisible = true, HighContrast = true
        },
        ActiveView = ResourceReference.BuiltIn("processes"),
        Views =
        [
            new(ResourceReference.BuiltIn("processes"),
                [new("Id", true, 100), new("Name", false, 240)],
                [new("Id", true), new("Name", false)], "fixture"),
            new(new("missing-session", "missing-kit", "missing-resource"), [], [], "")
        ]
    };

    [Fact]
    public void RoundTripContainsOnlyPreferencesAndStableReferences()
    {
        var storage = new MemoryStorage();
        var store = new WorkspaceStore(storage);
        store.Load();
        var sample = Sample();
        store.Save(sample);
        var reloaded = new WorkspaceStore(storage).Load();
        Assert.Null(reloaded.RecoveryMessage);
        Assert.Equal(sample.Layout, reloaded.Document.Layout);
        Assert.Equal(sample.ActiveView, reloaded.Document.ActiveView);
        Assert.Equal(sample.Views[0].Columns, reloaded.Document.Views[0].Columns);
        Assert.Equal(sample.Views[0].Sorting, reloaded.Document.Views[0].Sorting);
        Assert.Equal("fixture", reloaded.Document.Views[0].Filter);
        using var json = JsonDocument.Parse(storage.Files[WorkspaceStore.FileName]);
        Assert.Equal(["Version", "Layout", "ActiveView", "Views"], json.RootElement.EnumerateObject().Select(item => item.Name));
        Assert.Equal(["Reference", "Columns", "Sorting", "Filter"],
            json.RootElement.GetProperty("Views")[0].EnumerateObject().Select(item => item.Name));
        Assert.Equal(["SessionId", "KitId", "ResourceId"],
            json.RootElement.GetProperty("ActiveView").EnumerateObject().Select(item => item.Name));
    }

    [Theory]
    [InlineData(1100, 750, 270, 290, 1100, 750, 270, 290)]
    [InlineData(20, 30, 40, 50, 900, 600, 160, 180)]
    [InlineData(9000, 8000, 700, 600, 2000, 1400, 350, 350)]
    public void MigrationClampsLegacySizesAndLeavesOriginalUntouched(double width, double height, double left, double right,
        double expectedWidth, double expectedHeight, double expectedLeft, double expectedRight)
    {
        var storage = new MemoryStorage();
        var original = JsonSerializer.Serialize(new { Width = width, Height = height, Left = left, Right = right });
        storage.Files[WorkspaceStore.LegacyFileName] = original;
        var store = new WorkspaceStore(storage);
        var loaded = store.Load();
        Assert.True(loaded.Migrated);
        Assert.Null(loaded.RecoveryMessage);
        Assert.Equal(expectedWidth, loaded.Document.Layout.Width);
        Assert.Equal(expectedHeight, loaded.Document.Layout.Height);
        Assert.Equal(expectedLeft, loaded.Document.Layout.NavigationWidth);
        Assert.Equal(expectedRight, loaded.Document.Layout.ActionsWidth);
        Assert.Null(loaded.Document.ActiveView);
        store.Save(loaded.Document);
        Assert.Equal(original, storage.Files[WorkspaceStore.LegacyFileName]);
        Assert.False(new WorkspaceStore(storage).Load().Migrated);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":99}")]
    [InlineData("{\"Version\":\"1\"}")]
    [InlineData("{\"Version\":1,\"Layout\":null,\"Views\":[]}")]
    [InlineData("{\"Version\":1,\"Layout\":{},\"Views\":null}")]
    [InlineData("{\"Version\":1,\"Layout\":{\"Width\":-1},\"Views\":[]}")]
    [InlineData("{\"Version\":1,\"Layout\":{},\"Views\":[],\"Credentials\":\"not-allowed\"}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Layout\":{},\"Views\":[]}")]
    [InlineData("{\"Version\":1,\"Layout\":{},\"Views\":[{\"Reference\":{\"SessionId\":\"local\",\"KitId\":\"builtin.local-system\",\"ResourceId\":\"processes\"},\"Columns\":[],\"Sorting\":[]}]}")]
    public void CorruptOrUnsupportedWorkspaceIsProtectedUntilExplicitBackup(string damaged)
    {
        var storage = new MemoryStorage();
        storage.Files[WorkspaceStore.FileName] = damaged;
        var store = new WorkspaceStore(storage);
        var loaded = store.Load();
        Assert.NotNull(loaded.RecoveryMessage);
        Assert.True(store.IsWriteBlocked);
        Assert.Throws<InvalidOperationException>(() => store.Save(new()));
        Assert.Equal(damaged, storage.Files[WorkspaceStore.FileName]);
        var backup = store.BackUpDamagedFile();
        store.Save(new());
        Assert.Equal(damaged, storage.Files[backup]);
        Assert.Null(new WorkspaceStore(storage).Load().RecoveryMessage);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Width\":0,\"Height\":800,\"Left\":220,\"Right\":220}")]
    [InlineData("{\"Width\":1200,\"Height\":800,\"Left\":-1,\"Right\":220}")]
    public void DamagedLegacyLayoutCannotBeBypassedBySavingDefaults(string original)
    {
        var storage = new MemoryStorage();
        storage.Files[WorkspaceStore.LegacyFileName] = original;
        var store = new WorkspaceStore(storage);
        Assert.NotNull(store.Load().RecoveryMessage);
        Assert.Throws<InvalidOperationException>(() => store.Save(new()));
        Assert.False(storage.Files.ContainsKey(WorkspaceStore.FileName));
        Assert.Equal(original, storage.Files[WorkspaceStore.LegacyFileName]);
    }

    [Fact]
    public void RepairedWorkspaceCanBeReloadedWithoutBackingUpOrResettingIt()
    {
        var storage = new MemoryStorage();
        storage.Files[WorkspaceStore.FileName] = "{";
        var store = new WorkspaceStore(storage);
        store.Load();
        storage.Files[WorkspaceStore.FileName] = JsonSerializer.Serialize(Sample());
        Assert.Null(store.Load().RecoveryMessage);
        Assert.False(store.IsWriteBlocked);
        store.Save(Sample());
        Assert.Empty(storage.Backups);
    }

    [Fact]
    public void FailedArchiveKeepsTheWriteGuardAndOriginal()
    {
        var storage = new MemoryStorage { FailArchive = true };
        storage.Files[WorkspaceStore.FileName] = "{";
        var store = new WorkspaceStore(storage);
        store.Load();
        Assert.Throws<IOException>(() => store.BackUpDamagedFile());
        Assert.True(store.IsWriteBlocked);
        Assert.Equal("{", storage.Files[WorkspaceStore.FileName]);
    }

    [Fact]
    public void InterruptedSaveKeepsLastCommittedWorkspaceAndIgnoresTemporaryData()
    {
        var storage = new MemoryStorage();
        var store = new WorkspaceStore(storage);
        store.Load();
        store.Save(Sample());
        var original = storage.Files[WorkspaceStore.FileName];
        storage.InterruptSave = true;
        Assert.Throws<IOException>(() => store.Save(new()));
        Assert.Equal(original, storage.Files[WorkspaceStore.FileName]);
        Assert.NotEmpty(storage.Files[WorkspaceStore.FileName + ".interrupted.tmp"]);
        Assert.Equal(Sample().Layout, new WorkspaceStore(storage).Load().Document.Layout);
        storage.InterruptSave = false;
        store.Save(new());
        Assert.Equal(new WorkspaceLayout(), new WorkspaceStore(storage).Load().Document.Layout);
    }

    [Fact]
    public void MissingReferencesRemainUntilDeliberateRemoval()
    {
        var sample = Sample();
        var missing = WorkspaceStore.Unresolved(sample, reference => reference == ResourceReference.BuiltIn("processes"));
        Assert.Equal(sample.Views[1].Reference, Assert.Single(missing));
        Assert.Equal(2, sample.Views.Count);
        var removed = WorkspaceStore.RemoveReferences(sample, missing);
        Assert.Equal(sample.ActiveView, removed.ActiveView);
        Assert.Single(removed.Views);
        removed = WorkspaceStore.RemoveReferences(removed, [removed.ActiveView!]);
        Assert.Null(removed.ActiveView);
        Assert.Empty(removed.Views);
    }

    [Fact]
    public void InvalidPreferencesAreRejectedBeforeStorageIsChanged()
    {
        var storage = new MemoryStorage();
        var store = new WorkspaceStore(storage);
        store.Load();
        store.Save(Sample());
        var original = storage.Files[WorkspaceStore.FileName];
        WorkspaceDocument[] invalid =
        [
            Sample() with { Layout = new() { Width = double.NaN } },
            Sample() with { Layout = new() { X = 42 } },
            Sample() with { Views = [Sample().Views[0], Sample().Views[0]] },
            Sample() with { Views = [Sample().Views[0] with { Filter = new string('a', 4097) }] },
            Sample() with { Views = [Sample().Views[0] with { Columns = [new("Id", false, 100)] }] },
            Sample() with { Views = [Sample().Views[0] with { Columns = [new("Id", true, double.PositiveInfinity)] }] },
            Sample() with { Views = [Sample().Views[0] with { Sorting = [new("Id", false), new("Id", true)] }] }
        ];
        foreach (var document in invalid)
        {
            Assert.Throws<InvalidDataException>(() => store.Save(document));
            Assert.Equal(original, storage.Files[WorkspaceStore.FileName]);
        }
    }

    [Fact]
    public void OversizedReadIsProtectedAndOversizedSaveIsRejectedBeforeCommit()
    {
        var storage = new MemoryStorage();
        storage.Files[WorkspaceStore.FileName] = new string(' ', WorkspaceStore.MaximumFileBytes + 1);
        var store = new WorkspaceStore(storage);
        Assert.Contains("4 MiB", store.Load().RecoveryMessage);
        Assert.True(store.IsWriteBlocked);
        store.BackUpDamagedFile();
        store.Save(new());
        var original = storage.Files[WorkspaceStore.FileName];
        var columns = Enumerable.Range(0, 200).Select(index => new WorkspaceColumn(
            index + new string('a', 190), true, 100)).ToArray();
        var large = new WorkspaceDocument
        {
            Views = Enumerable.Range(0, 500).Select(index =>
                new WorkspaceView(ResourceReference.BuiltIn("resource-" + index), columns, [], "")).ToArray()
        };
        Assert.Throws<InvalidDataException>(() => store.Save(large));
        Assert.Equal(original, storage.Files[WorkspaceStore.FileName]);
    }

    [Theory]
    [InlineData(-900, 100, 1.5)]
    [InlineData(5000, -5000, 2)]
    [InlineData(790, 590, 1)]
    public void RestoredWindowFitsWorkingAreaEvenAfterDisplayRemovalOrDpiChanges(int x, int y, double scaling)
    {
        WorkspaceDisplay[] displays = [new(0, 0, 800, 600, scaling), new(-1200, 0, 1200, 900, scaling)];
        var placement = WorkspacePlacement.Fit(new() { Width = 1900, Height = 1300, X = x, Y = y }, displays);
        var display = displays.Single(area => placement.X >= area.X && placement.X < area.X + area.Width);
        Assert.InRange(placement.X, display.X, display.X + display.Width - 1);
        Assert.InRange(placement.Y, display.Y, display.Y + display.Height - 1);
        Assert.True(placement.X + placement.Width * scaling <= display.X + display.Width);
        Assert.True(placement.Y + (placement.Height + 48) * scaling <= display.Y + display.Height);
    }

    [Fact]
    public void FileStorageCommitsAtomicallyAndLeavesLegacyAndInterruptedFilesAlone()
    {
        using var temporary = new TemporaryStorage();
        var storage = new FileWorkspaceStorage(temporary.Directory);
        var store = new WorkspaceStore(storage);
        Assert.Null(store.Load().RecoveryMessage);
        store.Save(Sample());
        var legacy = Path.Combine(temporary.Directory, WorkspaceStore.LegacyFileName);
        var interrupted = Path.Combine(temporary.Directory, WorkspaceStore.FileName + ".interrupted.tmp");
        File.WriteAllText(legacy, "{");
        File.WriteAllText(interrupted, "incomplete");
        store.Save(new());
        Assert.Equal("{", File.ReadAllText(legacy));
        Assert.Equal("incomplete", File.ReadAllText(interrupted));
        Assert.Equal(3, System.IO.Directory.GetFiles(temporary.Directory).Length);
        Assert.Null(new WorkspaceStore(storage).Load().RecoveryMessage);
        File.WriteAllText(Path.Combine(temporary.Directory, WorkspaceStore.FileName), "{");
        store.Load();
        var backup = store.BackUpDamagedFile();
        store.Save(new());
        Assert.Equal("{", File.ReadAllText(backup));
    }

    [Fact]
    public void FileStorageCommitFailureDoesNotReplaceTheLastGoodFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryStorage();
        var storage = new FileWorkspaceStorage(temporary.Directory);
        var store = new WorkspaceStore(storage);
        store.Load();
        store.Save(Sample());
        var file = Path.Combine(temporary.Directory, WorkspaceStore.FileName);
        var original = File.ReadAllText(file);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => store.Save(new()));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(original, File.ReadAllText(file));
        Assert.Single(System.IO.Directory.GetFiles(temporary.Directory));
    }

    [Fact]
    public void InvalidUtf8IsProtectedAndArchivedWithoutChangingOriginalBytes()
    {
        using var temporary = new TemporaryStorage();
        System.IO.Directory.CreateDirectory(temporary.Directory);
        var file = Path.Combine(temporary.Directory, WorkspaceStore.FileName);
        byte[] damaged = [0x7B, 0x22, 0xFF, 0x22, 0x7D];
        File.WriteAllBytes(file, damaged);
        var store = new WorkspaceStore(new FileWorkspaceStorage(temporary.Directory));
        Assert.NotNull(store.Load().RecoveryMessage);
        Assert.True(store.IsWriteBlocked);
        Assert.Throws<InvalidOperationException>(() => store.Save(new()));
        var backup = store.BackUpDamagedFile();
        store.Save(new());
        Assert.Equal(damaged, File.ReadAllBytes(backup));
    }

    private sealed class TemporaryStorage : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "RunspaceWorkspaceTests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class MemoryStorage : IWorkspaceStorage
    {
        public Dictionary<string, string> Files { get; } = [];
        public List<string> Backups { get; } = [];
        public bool InterruptSave { get; set; }
        public bool FailArchive { get; init; }
        public string? Read(string name) => Files.GetValueOrDefault(name);
        public void WriteAtomic(string name, string content)
        {
            if (InterruptSave)
            {
                Files[name + ".interrupted.tmp"] = content;
                throw new IOException("Interrupted before commit.");
            }
            Files[name] = content;
        }
        public string Archive(string name)
        {
            if (FailArchive) throw new IOException("Backup unavailable.");
            var backup = name + "." + Guid.NewGuid().ToString("N") + ".bak";
            Files[backup] = Files[name];
            Files.Remove(name);
            Backups.Add(backup);
            return backup;
        }
    }
}
