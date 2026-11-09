using MultiDriveCloud.Core.Models;

namespace MultiDriveCloud.Core.Storage;

public sealed class ChunkPlan
{
    public required IReadOnlyList<FileChunk> Chunks { get; init; }
    public required IReadOnlyDictionary<Guid, AllocationLease> Leases { get; init; }

    public void ReleaseAll(StorageAllocator allocator)
    {
        foreach (var l in Leases.Values) allocator.Release(l);
    }
}

public sealed class ChunkManager
{
    private readonly StorageAllocator _allocator;

    public ChunkManager(StorageAllocator allocator) => _allocator = allocator;

    public static long StoredSize(long plainSize, bool encrypted) =>
        encrypted ? AesGcmStreamCipher.EncryptedLength(plainSize) : plainSize;

    public static IReadOnlyList<FragmentRange> Split(long totalSize, long chunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        var list = new List<FragmentRange>();
        if (totalSize == 0) { list.Add(new FragmentRange(0, 0)); return list; }
        for (long off = 0; off < totalSize; off += chunkSize)
            list.Add(new FragmentRange(off, Math.Min(chunkSize, totalSize - off)));
        return list;
    }

    public async Task<ChunkPlan> PlanAsync(Guid fileId, long totalSize, long chunkSize, bool encrypted,
        IReadOnlyCollection<string>? excludedAccounts = null, CancellationToken ct = default)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        var chunks = new List<FileChunk>();
        var leases = new Dictionary<Guid, AllocationLease>();
        long offset = 0;
        var index = 0;
        try
        {
            do
            {
                var remaining = totalSize - offset;
                var desiredPlain = Math.Min(chunkSize, remaining);
                var desiredStored = StoredSize(desiredPlain, encrypted);
                var minPlain = Math.Min(desiredPlain, StorageAllocator.MinFragmentBytes);
                var minStored = StoredSize(minPlain, encrypted);

                var lease = await _allocator.TryAllocateAsync(desiredStored, minStored, excludedAccounts, ct).ConfigureAwait(false);
                if (lease is null)
                {
                    var usable = await _allocator.GetUsableBytesAsync(ct).ConfigureAwait(false);
                    throw new InsufficientStorageException(StoredSize(totalSize, encrypted), usable + SumLeases(leases));
                }

                var plain = desiredPlain;
                if (lease.Bytes < desiredStored)
                {
                    plain = encrypted ? AesGcmStreamCipher.MaxPlainForEncrypted(lease.Bytes) : lease.Bytes;
                    plain = Math.Min(plain, desiredPlain);
                    if (plain <= 0 && desiredPlain > 0)
                    {
                        _allocator.Release(lease);
                        throw new InsufficientStorageException(StoredSize(totalSize, encrypted), 0);
                    }
                }
                var stored = StoredSize(plain, encrypted);
                _allocator.Shrink(lease, stored);

                var chunk = new FileChunk
                {
                    ChunkId = Guid.NewGuid(),
                    FileId = fileId,
                    ChunkIndex = index,
                    Offset = offset,
                    PlainSize = plain,
                    StoredSize = stored,
                    AccountId = lease.AccountId,
                    Status = ChunkStatus.Planned,
                };
                chunks.Add(chunk);
                leases[chunk.ChunkId] = lease;
                offset += plain;
                index++;
            } while (offset < totalSize);
        }
        catch
        {
            foreach (var l in leases.Values) _allocator.Release(l);
            throw;
        }
        return new ChunkPlan { Chunks = chunks, Leases = leases };
    }

    private static long SumLeases(Dictionary<Guid, AllocationLease> leases) => leases.Values.Sum(l => l.Bytes);

    public static void ValidateLayout(IReadOnlyList<FileChunk> chunks, long totalSize)
    {
        long expected = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (c.ChunkIndex != i) throw new InvalidDataException($"Fragment manquant : l'index {i} est absent.");
            if (c.Offset != expected) throw new InvalidDataException($"Le fragment {i} n'est pas contigu au précédent.");
            expected += c.PlainSize;
        }
        if (expected != totalSize)
            throw new InvalidDataException($"Les fragments couvrent {expected} octets sur {totalSize} attendus.");
    }
}
