using Microsoft.Extensions.Logging;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Transfers;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.Core.Storage;

public sealed class NameConflictException : Exception
{
    public NameConflictException(string name) : base($"Un élément nommé « {name} » existe déjà à cet emplacement.") { }
}

public static class VirtualNames
{
    public static async Task<bool> ExistsAsync(IIndexRepository index, Guid folderId, string name, Guid? ignoreId = null, CancellationToken ct = default)
    {
        var f = await index.FindFileAsync(folderId, name, ct).ConfigureAwait(false);
        if (f is not null && f.FileId != ignoreId) return true;
        var d = await index.FindFolderAsync(folderId, name, ct).ConfigureAwait(false);
        return d is not null && d.FolderId != ignoreId;
    }

    public static async Task<string> UniqueAsync(IIndexRepository index, Guid folderId, string name, CancellationToken ct = default)
    {
        if (!await ExistsAsync(index, folderId, name, null, ct).ConfigureAwait(false)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        if (stem.Length == 0) { stem = name; ext = ""; }
        for (var i = 1; i < 100_000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!await ExistsAsync(index, folderId, candidate, null, ct).ConfigureAwait(false)) return candidate;
        }
        throw new IOException("Aucun nom libre trouvé.");
    }
}

public sealed record FragmentInfo(int Index, long Offset, long Size, string Sha256, string AccountEmail, ChunkStatus Status);

public sealed record FileProperties(VirtualFile File, string VirtualPath, IReadOnlyList<FragmentInfo> Fragments);

public sealed record DeleteReport(int FilesDeleted, int FoldersDeleted, IReadOnlyList<string> Problems)
{
    public bool Success => Problems.Count == 0;
}

public sealed class VirtualFileSystemService
{
    private readonly IIndexRepository _index;
    private readonly IAccountRepository _accounts;
    private readonly FragmentCleaner _cleaner;
    private readonly TransferManager _transfers;
    private readonly ILogger<VirtualFileSystemService> _log;

    public VirtualFileSystemService(IIndexRepository index, IAccountRepository accounts, FragmentCleaner cleaner,
        TransferManager transfers, ILogger<VirtualFileSystemService> log)
    {
        _index = index;
        _accounts = accounts;
        _cleaner = cleaner;
        _transfers = transfers;
        _log = log;
    }

    public event EventHandler? Changed;
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async Task<(IReadOnlyList<VirtualFolder> Folders, IReadOnlyList<VirtualFile> Files)> ListAsync(Guid folderId, CancellationToken ct = default)
    {
        var folders = await _index.GetSubFoldersAsync(folderId, ct).ConfigureAwait(false);
        var files = await _index.GetFilesAsync(folderId, ct).ConfigureAwait(false);
        return (folders, files.Where(f => f.Status != FileStatus.Deleting).ToList());
    }

    public Task<IReadOnlyList<VirtualFolder>> GetPathAsync(Guid folderId, CancellationToken ct = default) =>
        _index.GetFolderPathAsync(folderId, ct);

    public async Task<VirtualFolder> CreateFolderAsync(Guid parentId, string name, CancellationToken ct = default)
    {
        name = name.Trim();
        if (PathGuard.ValidateVirtualName(name) is { } problem) throw new ArgumentException(problem);
        _ = await _index.GetFolderAsync(parentId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Dossier parent introuvable.");
        if (await VirtualNames.ExistsAsync(_index, parentId, name, null, ct).ConfigureAwait(false)) throw new NameConflictException(name);
        var folder = new VirtualFolder { FolderId = Guid.NewGuid(), ParentId = parentId, Name = name };
        await _index.InsertFolderAsync(folder, ct).ConfigureAwait(false);
        RaiseChanged();
        return folder;
    }

    public async Task RenameAsync(ItemRef item, string newName, CancellationToken ct = default)
    {
        newName = newName.Trim();
        if (PathGuard.ValidateVirtualName(newName) is { } problem) throw new ArgumentException(problem);
        if (item.IsFolder)
        {
            var folder = await _index.GetFolderAsync(item.Id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Dossier introuvable.");
            if (folder.ParentId is null) throw new InvalidOperationException("La racine ne peut pas être renommée.");
            if (string.Equals(folder.Name, newName, StringComparison.Ordinal)) return;
            if (await VirtualNames.ExistsAsync(_index, folder.ParentId.Value, newName, folder.FolderId, ct).ConfigureAwait(false)) throw new NameConflictException(newName);
            folder.Name = newName;
            folder.ModifiedAt = DateTime.UtcNow;
            await _index.UpdateFolderAsync(folder, ct).ConfigureAwait(false);
        }
        else
        {
            var file = await _index.GetFileAsync(item.Id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Fichier introuvable.");
            if (string.Equals(file.Name, newName, StringComparison.Ordinal)) return;
            if (await VirtualNames.ExistsAsync(_index, file.FolderId, newName, file.FileId, ct).ConfigureAwait(false)) throw new NameConflictException(newName);
            file.Name = newName;
            await _index.UpdateFileAsync(file, ct).ConfigureAwait(false);
        }
        RaiseChanged();
    }

    public async Task MoveAsync(IReadOnlyList<ItemRef> items, Guid destinationFolderId, CancellationToken ct = default)
    {
        _ = await _index.GetFolderAsync(destinationFolderId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Dossier de destination introuvable.");
        var destPath = (await _index.GetFolderPathAsync(destinationFolderId, ct).ConfigureAwait(false)).Select(f => f.FolderId).ToHashSet();

        var plannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<VirtualFolder>();
        var files = new List<VirtualFile>();
        foreach (var item in items)
        {
            string name;
            if (item.IsFolder)
            {
                var f = await _index.GetFolderAsync(item.Id, ct).ConfigureAwait(false);
                if (f is null) continue;
                if (f.ParentId is null) throw new InvalidOperationException("La racine ne peut pas être déplacée.");
                if (f.FolderId == destinationFolderId || destPath.Contains(f.FolderId))
                    throw new InvalidOperationException($"Impossible de déplacer « {f.Name} » dans lui-même ou dans un de ses sous-dossiers.");
                if (f.ParentId == destinationFolderId) continue;
                name = f.Name;
                folders.Add(f);
            }
            else
            {
                var f = await _index.GetFileAsync(item.Id, ct).ConfigureAwait(false);
                if (f is null) continue;
                if (f.FolderId == destinationFolderId) continue;
                name = f.Name;
                files.Add(f);
            }
            if (!plannedNames.Add(name) || await VirtualNames.ExistsAsync(_index, destinationFolderId, name, null, ct).ConfigureAwait(false))
                throw new NameConflictException(name);
        }
        foreach (var f in folders) { f.ParentId = destinationFolderId; f.ModifiedAt = DateTime.UtcNow; await _index.UpdateFolderAsync(f, ct).ConfigureAwait(false); }
        foreach (var f in files) { f.FolderId = destinationFolderId; await _index.UpdateFileAsync(f, ct).ConfigureAwait(false); }
        RaiseChanged();
    }

    public async Task<SearchResult> SearchAsync(string text, int limit = 500, CancellationToken ct = default)
    {
        var r = await _index.SearchAsync(text, limit, ct).ConfigureAwait(false);
        return new SearchResult(r.Folders, r.Files.Where(f => f.Status != FileStatus.Deleting).ToList());
    }

    public async Task<FileProperties?> GetPropertiesAsync(Guid fileId, CancellationToken ct = default)
    {
        var file = await _index.GetFileAsync(fileId, ct).ConfigureAwait(false);
        if (file is null) return null;
        var path = "/" + string.Join("/", (await _index.GetFolderPathAsync(file.FolderId, ct).ConfigureAwait(false)).Select(f => f.Name).Append(file.Name));
        var chunks = await _index.GetChunksAsync(fileId, ct).ConfigureAwait(false);
        var accounts = (await _accounts.GetAllAsync(ct).ConfigureAwait(false)).ToDictionary(a => a.AccountId);
        var frags = chunks.Where(c => c.Status != ChunkStatus.Deleted).Select(c => new FragmentInfo(c.ChunkIndex, c.Offset, c.PlainSize, c.Sha256,
            accounts.TryGetValue(c.AccountId, out var a) ? a.Email : c.AccountId, c.Status)).ToList();
        return new FileProperties(file, path, frags);
    }


    public async Task<DeleteReport> DeleteAsync(IReadOnlyList<ItemRef> items, CancellationToken ct = default)
    {
        var problems = new List<string>();
        int files = 0, folders = 0;
        foreach (var item in items)
        {
            if (!item.IsFolder)
            {
                var f = await _index.GetFileAsync(item.Id, ct).ConfigureAwait(false);
                if (f is null) continue;
                if (await DeleteFileCoreAsync(f, problems, ct).ConfigureAwait(false)) files++;
            }
            else
            {
                var folder = await _index.GetFolderAsync(item.Id, ct).ConfigureAwait(false);
                if (folder is null) continue;
                if (folder.ParentId is null) throw new InvalidOperationException("La racine ne peut pas être supprimée.");
                var allFiles = await _index.GetFilesRecursiveAsync(folder.FolderId, ct).ConfigureAwait(false);
                var okAll = true;
                foreach (var entry in allFiles)
                {
                    if (await DeleteFileCoreAsync(entry.File, problems, ct).ConfigureAwait(false)) files++;
                    else okAll = false;
                }
                if (okAll)
                {
                    var sub = await _index.GetFoldersRecursiveAsync(folder.FolderId, ct).ConfigureAwait(false);
                    await _index.DeleteFolderAsync(folder.FolderId, ct).ConfigureAwait(false);
                    folders += 1 + sub.Count;
                }
                else problems.Add($"Le dossier « {folder.Name} » n'a pas été supprimé car certains fichiers n'ont pas pu être effacés des Drive.");
            }
        }
        RaiseChanged();
        return new DeleteReport(files, folders, problems);
    }

    private async Task<bool> DeleteFileCoreAsync(VirtualFile file, List<string> problems, CancellationToken ct)
    {
        foreach (var s in _transfers.GetSnapshots().Where(s => s.FileId == file.FileId && s.IsActive))
            _transfers.Cancel(s.TransferId);

        file.Status = FileStatus.Deleting;
        await _index.UpdateFileAsync(file, ct).ConfigureAwait(false);
        var report = await _cleaner.DeleteRemoteAsync(file.FileId, ct).ConfigureAwait(false);
        if (!report.Success)
        {
            foreach (var g in report.Failures.GroupBy(f => f.AccountLabel))
                problems.Add($"« {file.Name} » : {g.Count()} fragment(s) non supprimés sur {g.Key} ({g.First().Reason}). Le fichier reste en attente de suppression.");
            return false;
        }
        await _index.DeleteFileAsync(file.FileId, ct).ConfigureAwait(false);
        _log.LogInformation("Fichier supprimé ({Fragments} fragment(s) distants).", report.Deleted);
        return true;
    }

    public async Task<DeleteReport> ResumePendingDeletionsAsync(CancellationToken ct = default)
    {
        var problems = new List<string>();
        var count = 0;
        foreach (var f in await _index.GetFilesByStatusAsync(FileStatus.Deleting, ct).ConfigureAwait(false))
        {
            if (_transfers.GetSnapshots().Any(s => s.FileId == f.FileId && s.State == TransferState.Running)) continue;
            if (await DeleteFileCoreAsync(f, problems, ct).ConfigureAwait(false)) count++;
        }
        if (count > 0) RaiseChanged();
        return new DeleteReport(count, 0, problems);
    }

    public async Task<int> CountPendingDeletionsAsync(CancellationToken ct = default) =>
        (await _index.GetFilesByStatusAsync(FileStatus.Deleting, ct).ConfigureAwait(false)).Count;
}
