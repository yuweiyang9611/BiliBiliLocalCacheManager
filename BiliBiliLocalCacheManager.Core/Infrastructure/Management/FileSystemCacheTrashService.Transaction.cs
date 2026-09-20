using System.Security.Cryptography;
using System.Text;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

internal enum CacheTrashMutationOperation
{
    Move = 0,
    Restore = 1,
    Purge = 2,
    Statistics = 3,
    Delete = 4
}

public sealed partial class FileSystemCacheTrashService
{
    private static readonly TimeSpan MutationLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly object MutationGatesSync = new();
    private static readonly Dictionary<string, MutationGate> MutationGates =
        new(StringComparer.Ordinal);

    internal static bool HasMutationGateForTesting(string root)
    {
        lock (MutationGatesSync) { return MutationGates.ContainsKey(NormalizeRootKey(root)); }
    }

    internal Action<CacheTrashMutationOperation, string>? AfterMutationLockAcquiredForTesting
    {
        get;
        set;
    }

    private IDisposable EnterMutationTransaction(
        string normalizedRoot,
        CacheTrashMutationOperation operation)
    {
        var rootKey = NormalizeRootKey(normalizedRoot);
        MutationGate gate;
        lock (MutationGatesSync)
        {
            if (!MutationGates.TryGetValue(rootKey, out gate!))
            {
                gate = new MutationGate();
                MutationGates.Add(rootKey, gate);
            }
            gate.ReferenceCount++;
        }

        Mutex? processMutex = null;
        var mutexAcquired = false;
        var gateAcquired = false;
        try
        {
            gateAcquired = gate.Semaphore.Wait(MutationLockTimeout);
            if (!gateAcquired)
            {
                throw new TimeoutException(
                    "Timed out waiting for another cache-trash operation on the same root.");
            }

            processMutex = new Mutex(
                initiallyOwned: false,
                GetMutationMutexName(rootKey));
            try
            {
                mutexAcquired = processMutex.WaitOne(MutationLockTimeout);
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }

            if (!mutexAcquired)
            {
                throw new TimeoutException(
                    "Timed out waiting for another process to finish a cache-trash operation on the same root.");
            }

            AfterMutationLockAcquiredForTesting?.Invoke(operation, normalizedRoot);
            return new MutationTransactionLease(rootKey, gate, processMutex, mutexAcquired);
        }
        catch
        {
            if (mutexAcquired)
            {
                processMutex!.ReleaseMutex();
            }

            processMutex?.Dispose();
            ReleaseMutationGate(rootKey, gate, gateAcquired);
            throw;
        }
    }

    internal static string GetMutationMutexNameForTesting(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return GetMutationMutexName(NormalizeRootKey(Path.GetFullPath(rootDirectory)));
    }

    private static string NormalizeRootKey(string rootDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        return OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private static string GetMutationMutexName(string normalizedRootKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRootKey));
        var prefix = OperatingSystem.IsWindows()
            ? @"Local\BiliBiliLocalCacheManager.Trash."
            : "BiliBiliLocalCacheManager.Trash.";
        return $"{prefix}{Convert.ToHexString(hash)}";
    }

    private static void ReleaseMutationGate(string rootKey, MutationGate gate, bool acquired)
    {
        if (acquired)
        {
            gate.Semaphore.Release();
        }
        lock (MutationGatesSync)
        {
            if (--gate.ReferenceCount == 0)
            {
                MutationGates.Remove(rootKey);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class MutationGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class MutationTransactionLease(
        string rootKey,
        MutationGate gate,
        Mutex? processMutex,
        bool mutexAcquired) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (mutexAcquired)
                {
                    processMutex!.ReleaseMutex();
                }
            }
            finally
            {
                processMutex?.Dispose();
                ReleaseMutationGate(rootKey, gate, acquired: true);
            }
        }
    }
}
