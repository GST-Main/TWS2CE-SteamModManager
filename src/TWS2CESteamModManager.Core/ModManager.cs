using System.Globalization;

namespace TWS2CESteamModManager.Core;

/// <summary>Owns a mod list and synchronizes its enabled order to the game's script.
/// Dispose before opening another manager for the same mod manifest.</summary>
public sealed class ModManager : IDisposable
{
    private readonly object gate = new();
    private readonly string dataPath;
    private readonly string disabledPath;
    private readonly string manifestPath;
    private readonly string cachePath;
    private readonly string appDataPath;
    private readonly IWorkshopModDownloader? downloader;
    private readonly FileStream lease;
    private List<ManagedMod> mods = [];
    private byte[]? manifestSnapshot;
    private byte[] scriptSnapshot = [];
    private UserScriptDocument script = null!;
    private bool disposed;
    private bool requiresReopen;
    private sealed record PendingInstall(string StagedPath, string TargetPath);
    private sealed record PendingMove(string SourcePath, string TargetPath);

    private ModManager(ManagerManifest folders, string modManifestPath,
        IWorkshopModDownloader? downloader, string? downloadCacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(modManifestPath);
        if (folders.AppDataPath is null || folders.GameInstallPath is null)
            throw new InvalidOperationException("Resolve the app-data and installation folders before opening mod management.");
        ManagerManifest validated = GameLocationService.SelectAppDataFolder(folders, folders.AppDataPath);
        validated = GameLocationService.SelectGameInstallFolder(validated, folders.GameInstallPath);
        appDataPath = validated.AppDataPath!;
        dataPath = Path.Combine(validated.GameInstallPath!, "data");
        disabledPath = Path.Combine(dataPath, "disabled_mods");
        manifestPath = Path.GetFullPath(modManifestPath);
        if (string.Equals(manifestPath, GameConstants.GetUserScriptPath(appDataPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The mod manifest must be separate from user.script.txt.", nameof(modManifestPath));
        RejectLink(dataPath);
        if (File.Exists(manifestPath)) RejectLink(manifestPath);
        cachePath = Path.GetFullPath(downloadCacheDirectory ?? Path.Combine(Path.GetDirectoryName(manifestPath)!, ".mod-downloads"));
        this.downloader = downloader;
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        lease = new FileStream(manifestPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static ModManager Open(ManagerManifest folders, string modManifestPath,
        IWorkshopModDownloader? downloader = null, string? downloadCacheDirectory = null)
    {
        var manager = new ModManager(folders, modManifestPath, downloader, downloadCacheDirectory);
        try
        {
            manager.Initialize();
            return manager;
        }
        catch { manager.Dispose(); throw; }
    }

    private void Initialize()
    {
        manifestSnapshot = AtomicFile.ReadOptional(manifestPath);
        mods = (manifestSnapshot is null ? new ModManifest() : ModManifestStore.Deserialize(manifestSnapshot))
            .Mods.OrderBy(mod => mod.Order).ToList();
        script = UserScriptService.LoadOrCreate(appDataPath);
        RejectLink(script.FilePath);
        scriptSnapshot = File.ReadAllBytes(script.FilePath);

        // Adopt explicitly referenced files, not all pack files in data: base-game
        // packs must never be registered merely because they exist in that folder.
        var next = mods.Select(mod => mod.Source == ModSource.Imported && string.IsNullOrWhiteSpace(mod.DisplayName)
            ? mod with { DisplayName = Path.GetFileNameWithoutExtension(mod.OriginalFileName) } : mod).ToList();
        foreach (string name in script.Mods)
        {
            if (!next.Any(mod => string.Equals(mod.FileName, name, StringComparison.OrdinalIgnoreCase)))
                next.Add(Imported(name, next.Count));
        }
        Commit(next);
    }

    public IReadOnlyList<ManagedMod> List()
    {
        lock (gate) { RequireOpen(); return mods.ToArray(); }
    }

    public IReadOnlyList<ScriptDiagnostic> ScriptDiagnostics
    {
        get { lock (gate) { RequireOpen(); return script.Diagnostics; } }
    }

    public string GetFilePath(Guid id)
    {
        lock (gate) { RequireOpen(); return StoragePath(Find(id)); }
    }

    public string? GetImagePath(Guid id)
    {
        lock (gate)
        {
            RequireOpen();
            string name = Find(id).FileName;
            return ModImages.Find(ModPath(name)) ?? ModImages.Find(DisabledModPath(name));
        }
    }

    public ManagedMod Import(string packFilePath, CancellationToken cancellationToken = default)
        => Import(packFilePath, imageFilePath: null, cancellationToken);

    /// <summary>Imports an optional image; null discovers an image beside the source pack.</summary>
    public ManagedMod Import(string packFilePath, string? imageFilePath, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RequireOpen();
            ArgumentException.ThrowIfNullOrWhiteSpace(packFilePath);
            cancellationToken.ThrowIfCancellationRequested();
            string source = Path.GetFullPath(packFilePath);
            string name = Path.GetFileName(source);
            RequirePack(name);
            RequireNewName(name);
            if (!File.Exists(source)) throw new FileNotFoundException("Import pack file not found.", source);
            RejectLink(source);
            string target = ModPath(name);
            var entry = Imported(name, mods.Count);
            var pending = new List<PendingInstall>();
            try
            {
                if (!string.Equals(source, DisabledModPath(name), StringComparison.OrdinalIgnoreCase))
                    RequireFreeDestination(DisabledModPath(name));
                if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(source, DisabledModPath(name), StringComparison.OrdinalIgnoreCase))
                {
                    RequireFreeDestination(target);
                    pending.Add(new PendingInstall(StageCopy(source, cancellationToken), target));
                }
                string? image = imageFilePath is null ? ModImages.Find(source) : Path.GetFullPath(imageFilePath);
                if (image is not null) StageImage(image, target, pending, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Commit([.. mods, entry], pending);
            }
            finally { foreach (PendingInstall install in pending) DeleteStaging(install.StagedPath); }
            return entry;
        }
    }

    public IReadOnlyList<ManagedMod> Download(ulong itemId, Action<string>? report = null,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RequireOpen();
            if (itemId is 0 or ulong.MaxValue) throw new ArgumentException("Invalid Workshop item ID.", nameof(itemId));
            if (downloader is null) throw new InvalidOperationException("Provide a Workshop downloader when opening the mod manager.");
            if (mods.Any(mod => mod.WorkshopItemId == itemId))
                throw new InvalidOperationException("This Workshop item is already managed.");
            EnsureCurrent();
            cancellationToken.ThrowIfCancellationRequested();
            WorkshopModPackage package = downloader.Download(itemId, cachePath, report, cancellationToken);
            string root = Path.GetFullPath(package.Directory);
            RejectLink(root);
            if (package.ThumbnailPath is { } thumbnail) RequirePackageFile(root, Path.GetFullPath(thumbnail));
            string[] files = package.PackFiles.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) throw new InvalidDataException("The Workshop item contains no .pack files.");
            var entries = new List<ManagedMod>();
            var pending = new List<PendingInstall>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string source = Path.GetFullPath(file);
                    RequirePackageFile(root, source);
                    string original = Path.GetFileName(source);
                    RequirePack(original);
                    string name = $"MOD_{itemId.ToString(CultureInfo.InvariantCulture)}_{original}";
                    RequirePack(name);
                    RequireNewName(name);
                    if (!names.Add(name)) throw new IOException("The Workshop package contains duplicate pack filenames.");
                    string target = ModPath(name);
                    RequireFreeDestination(target);
                    RequireFreeDestination(DisabledModPath(name));
                    entries.Add(new ManagedMod
                    {
                        Id = Guid.NewGuid(), FileName = name, OriginalFileName = original,
                        Source = ModSource.Workshop, WorkshopItemId = itemId,
                        DisplayName = string.IsNullOrWhiteSpace(package.Title) ? null : package.Title.Trim(),
                        Order = mods.Count + entries.Count, IsEnabled = true
                    });
                    pending.Add(new PendingInstall(StageCopy(source, cancellationToken), target));
                    if (package.ThumbnailPath is { } image) StageImage(image, target, pending, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Commit([.. mods, .. entries], pending);
                return entries.ToArray();
            }
            finally
            {
                foreach (PendingInstall install in pending) DeleteStaging(install.StagedPath);
            }
        }
    }

    /// <summary>Moves packs between data and disabled_mods, recovering missing Workshop packs when enabling.
    /// Set allowDownload=false for local-only changes; a missing Workshop pack then raises
    /// WorkshopModRestoreRequiredException so a UI can dispatch recovery to its Steam thread.</summary>
    public void SetEnabled(Guid id, bool enabled, Action<string>? report = null,
        CancellationToken cancellationToken = default, bool allowDownload = true)
    {
        lock (gate)
        {
            RequireOpen();
            ManagedMod entry = Find(id);
            EnsureCurrent();
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new List<PendingInstall>();
            try
            {
                if (enabled && !File.Exists(ModPath(entry.FileName)) && !File.Exists(DisabledModPath(entry.FileName)))
                {
                    if (entry.WorkshopItemId is not { } itemId)
                        throw new FileNotFoundException("The imported mod pack is missing. Restore the file before enabling it.", StoragePath(entry));
                    if (!allowDownload) throw new WorkshopModRestoreRequiredException(entry.Id, itemId);
                    StageWorkshopRestore(entry, pending, report, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Commit(mods.Select(mod => mod.Id == id ? mod with { IsEnabled = enabled } : mod).ToList(), pending);
            }
            finally { foreach (PendingInstall install in pending) DeleteStaging(install.StagedPath); }
        }
    }

    private void StageWorkshopRestore(ManagedMod entry, List<PendingInstall> pending,
        Action<string>? report, CancellationToken cancellationToken)
    {
        if (downloader is null) throw new InvalidOperationException("Provide a Workshop downloader to recover the missing mod.");
        WorkshopModPackage package = downloader.Download(entry.WorkshopItemId!.Value, cachePath, report, cancellationToken);
        string root = Path.GetFullPath(package.Directory);
        RejectLink(root);
        var matches = new List<string>();
        foreach (string file in package.PackFiles)
        {
            string source = Path.GetFullPath(file);
            RequirePackageFile(root, source);
            RequirePack(Path.GetFileName(source));
            if (Path.GetFileName(source).Equals(entry.OriginalFileName, StringComparison.OrdinalIgnoreCase)) matches.Add(source);
        }
        if (matches.Count != 1)
            throw new InvalidDataException("The Workshop package no longer contains a unique copy of this mod's original pack filename.");
        string target = ModPath(entry.FileName);
        RequireFreeDestination(target);
        RequireFreeDestination(DisabledModPath(entry.FileName));
        pending.Add(new PendingInstall(StageCopy(matches[0], cancellationToken), target));
        if (package.ThumbnailPath is { } thumbnail)
        {
            RequirePackageFile(root, Path.GetFullPath(thumbnail));
            if (GetImagePath(entry.Id) is null) StageImage(thumbnail, target, pending, cancellationToken);
        }
    }

    /// <summary>Fills missing Workshop names without replacing an existing saved display name.</summary>
    public bool SetWorkshopDisplayNames(IReadOnlyDictionary<ulong, string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        lock (gate)
        {
            RequireOpen();
            var next = mods.Select(mod => string.IsNullOrWhiteSpace(mod.DisplayName) && mod.WorkshopItemId is { } itemId
                && titles.TryGetValue(itemId, out string? title) && !string.IsNullOrWhiteSpace(title)
                ? mod with { DisplayName = title.Trim() } : mod).ToList();
            if (next.SequenceEqual(mods)) return false;
            Commit(next);
            return true;
        }
    }

    /// <summary>Moves an entry to a zero-based position, including disabled entries in that order.</summary>
    public void Move(Guid id, int newIndex)
    {
        lock (gate)
        {
            RequireOpen();
            if (newIndex < 0 || newIndex >= mods.Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
            ManagedMod entry = Find(id);
            var next = mods.ToList();
            next.Remove(entry);
            next.Insert(newIndex, entry);
            Commit(next);
        }
    }

    public void Delete(Guid id)
    {
        lock (gate)
        {
            RequireOpen();
            ManagedMod entry = Find(id);
            ValidateStorage();
            string active = ModPath(entry.FileName), disabled = DisabledModPath(entry.FileName);
            if (File.Exists(active) && File.Exists(disabled)) throw StorageCollision(entry.FileName);
            string pack = File.Exists(active) ? active : File.Exists(disabled) ? disabled : StoragePath(entry);
            Commit(mods.Where(mod => mod.Id != id).ToList(), deletions: new[] { pack }
                .Concat(ModImages.FindAll(active)).Concat(ModImages.FindAll(disabled)).ToArray());
        }
    }

    public void SynchronizeScript()
    {
        lock (gate) { RequireOpen(); Commit(mods); }
    }

    private void Commit(IReadOnlyList<ManagedMod> next, IReadOnlyList<PendingInstall>? additions = null,
        IReadOnlyList<string>? deletions = null)
    {
        EnsureCurrent();
        additions ??= Array.Empty<PendingInstall>();
        List<ManagedMod> ordered = next.Select((mod, index) => mod with { Order = index }).ToList();
        IReadOnlyList<PendingMove> moves = PlanStorageMoves(ordered);
        byte[] nextManifest = ModManifestStore.Serialize(new ModManifest { Mods = ordered });
        byte[] nextScript = UserScriptService.RenderEnabledMods(script,
            ordered.Where(mod => mod.IsEnabled).Select(mod => mod.FileName));
        int preambleLength = script.HasByteOrderMark ? script.Encoding.GetPreamble().Length : 0;
        string nextContent = script.Encoding.GetString(nextScript.AsSpan(preambleLength));
        UserScriptParseResult parsed = UserScriptService.Parse(nextContent);
        UserScriptDocument nextDocument = script with
        {
            Content = nextContent, Mods = parsed.Mods, Diagnostics = parsed.Diagnostics, WasCreated = false
        };

        var installed = new List<string>();
        var moved = new List<PendingMove>();
        var removed = new List<(string Original, string Staged, string? Backup)>();
        bool wroteManifest = false, wroteScript = false;
        try
        {
            foreach (PendingMove move in moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.TargetPath)!);
                ValidateStorage();
                RequireFreeDestination(move.TargetPath);
                RejectLink(move.SourcePath);
                File.Move(move.SourcePath, move.TargetPath);
                moved.Add(move);
            }
            foreach (PendingInstall addition in additions)
            {
                RequireFreeDestination(addition.TargetPath);
                File.Move(addition.StagedPath, addition.TargetPath);
                installed.Add(addition.TargetPath);
            }
            foreach (string deletion in deletions ?? Array.Empty<string>())
            {
                if (Directory.Exists(deletion)) throw new IOException("The managed pack path is a directory.");
                if (File.Exists(deletion))
                {
                    RejectLink(deletion);
                    string staged = Path.Combine(dataPath, $".tws2-{Guid.NewGuid():N}.deleted");
                    // The pack is deleted last. Only small sidecars need copies to
                    // recover if a later sidecar or the pack cannot be deleted.
                    string? backup = removed.Count == 0 ? null : staged + ".rollback";
                    removed.Add((deletion, staged, backup));
                    if (backup is not null) File.Copy(deletion, backup);
                    File.Move(deletion, staged);
                }
            }
            if (!AtomicFile.Equal(manifestSnapshot, nextManifest))
            {
                AtomicFile.Write(manifestPath, nextManifest);
                wroteManifest = true;
            }
            if (!AtomicFile.Equal(scriptSnapshot, nextScript))
            {
                AtomicFile.Write(script.FilePath, nextScript);
                wroteScript = true;
            }
            foreach (var deletion in removed.Skip(1).Concat(removed.Take(1))) File.Delete(deletion.Staged);
        }
        catch (Exception failure)
        {
            var rollbackErrors = new List<Exception>();
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception exception) { rollbackErrors.Add(exception); }
            }
            if (wroteScript) Restore(() => AtomicFile.Write(script.FilePath, scriptSnapshot));
            if (wroteManifest) Restore(() =>
            {
                if (manifestSnapshot is null) File.Delete(manifestPath);
                else AtomicFile.Write(manifestPath, manifestSnapshot);
            });
            foreach (var deletion in removed.AsEnumerable().Reverse()) Restore(() =>
            {
                if (!File.Exists(deletion.Original))
                    File.Move(File.Exists(deletion.Staged) ? deletion.Staged
                        : deletion.Backup ?? throw new IOException("The deleted pack could not be restored."), deletion.Original);
            });
            foreach (string file in installed.AsEnumerable().Reverse()) Restore(() => File.Delete(file));
            foreach (PendingMove move in moved.AsEnumerable().Reverse()) Restore(() =>
            {
                RequireFreeDestination(move.SourcePath);
                File.Move(move.TargetPath, move.SourcePath);
            });
            if (rollbackErrors.Count > 0)
            {
                requiresReopen = true;
                throw new AggregateException("Mod operation failed and rollback was incomplete. Inspect the files before reopening the manager.",
                    new[] { failure }.Concat(rollbackErrors));
            }
            throw;
        }
        finally
        {
            foreach (var deletion in removed)
            {
                // If rollback was incomplete, leave recovery copies for inspection.
                if (!requiresReopen && deletion.Backup is not null && File.Exists(deletion.Backup))
                {
                    try { File.SetAttributes(deletion.Backup, FileAttributes.Normal); File.Delete(deletion.Backup); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        mods = ordered;
        manifestSnapshot = nextManifest;
        scriptSnapshot = nextScript;
        script = nextDocument;
    }

    private void EnsureCurrent()
    {
        RequireOpen();
        RejectLink(dataPath);
        ValidateStorage();
        if (File.Exists(manifestPath)) RejectLink(manifestPath);
        if (File.Exists(script.FilePath)) RejectLink(script.FilePath);
        if (!AtomicFile.Equal(manifestSnapshot, AtomicFile.ReadOptional(manifestPath))
            || !AtomicFile.Equal(scriptSnapshot, AtomicFile.ReadOptional(script.FilePath)))
            throw new ModStateConflictException("The mod manifest or user.script.txt changed outside this manager. Dispose and reopen it before making changes.");
    }

    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (requiresReopen) throw new InvalidOperationException("This manager requires recovery/reopening after an incomplete rollback.");
    }

    private ManagedMod Find(Guid id) => mods.FirstOrDefault(mod => mod.Id == id)
        ?? throw new KeyNotFoundException($"Managed mod not found: {id}.");

    private static ManagedMod Imported(string name, int order) => new()
    {
        Id = Guid.NewGuid(), FileName = name, OriginalFileName = name,
        DisplayName = Path.GetFileNameWithoutExtension(name),
        Source = ModSource.Imported, WorkshopItemId = null, Order = order, IsEnabled = true
    };

    private static void RequirePack(string name)
    {
        if (!UserScriptService.IsPackFileName(name))
            throw new ArgumentException("Select a .pack file with a valid filename, not a relative path.");
    }

    private string ModPath(string name)
    {
        RequirePack(name);
        return Path.Combine(dataPath, name);
    }

    private string DisabledModPath(string name)
    {
        RequirePack(name);
        return Path.Combine(disabledPath, name);
    }

    private string StoragePath(ManagedMod mod) => mod.IsEnabled ? ModPath(mod.FileName) : DisabledModPath(mod.FileName);

    private void ValidateStorage()
    {
        if (!Path.Exists(disabledPath)) return;
        RejectLink(disabledPath);
        if (!Directory.Exists(disabledPath)) throw new IOException("The disabled_mods path must be a directory.");
    }

    private IReadOnlyList<PendingMove> PlanStorageMoves(IReadOnlyList<ManagedMod> next)
    {
        var moves = new List<PendingMove>();
        foreach (ManagedMod mod in next)
        {
            string target = StoragePath(mod);
            string other = mod.IsEnabled ? DisabledModPath(mod.FileName) : ModPath(mod.FileName);
            foreach (string path in new[] { target, other })
            {
                if (Directory.Exists(path)) throw new IOException("The managed pack path is a directory.");
                if (File.Exists(path)) RejectLink(path);
            }
            if (File.Exists(target) && File.Exists(other)) throw StorageCollision(mod.FileName);
            if (!File.Exists(target) && File.Exists(other)) moves.Add(new PendingMove(other, target));
        }
        return moves;
    }

    private static IOException StorageCollision(string name) => new($"The mod exists in both data and disabled_mods. Resolve the duplicate before continuing: {name}.");

    private void RequireNewName(string name)
    {
        if (mods.Any(mod => string.Equals(mod.FileName, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"This pack filename is already managed: {name}.");
    }

    private static void RequireFreeDestination(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"A file or folder already exists at the destination: {path}.");
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Linked pack files or managed directories are not supported: {path}.");
    }

    private static void RequirePackageFile(string root, string source)
    {
        string relative = Path.GetRelativePath(root, source);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The downloader returned a file outside its package directory.");
        for (string? current = source; current is not null; current = Path.GetDirectoryName(current))
        {
            RejectLink(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private string StageCopy(string source, CancellationToken cancellationToken)
    {
        string staging = Path.Combine(dataPath, $".tws2-{Guid.NewGuid():N}.tmp");
        try
        {
            using var input = File.OpenRead(source);
            using var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
            output.Flush(flushToDisk: true);
            return staging;
        }
        catch { DeleteStaging(staging); throw; }
    }

    private void StageImage(string source, string packTarget, List<PendingInstall> pending, CancellationToken cancellationToken)
    {
        source = Path.GetFullPath(source);
        if (!ModImages.IsSupported(source)) throw new ArgumentException("Select a PNG, JPG, JPEG, GIF, or BMP mod image.");
        RejectLink(source);
        string target = Path.ChangeExtension(packTarget, Path.GetExtension(source).ToLowerInvariant());
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
        RequireFreeDestination(target);
        pending.Add(new PendingInstall(StageCopy(source, cancellationToken), target));
    }

    private static void DeleteStaging(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lease.Dispose();
        }
    }
}
