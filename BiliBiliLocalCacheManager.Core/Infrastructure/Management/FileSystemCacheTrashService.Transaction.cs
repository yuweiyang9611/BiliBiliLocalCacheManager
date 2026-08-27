using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BiliBiliLocalCacheManager.Core.Infrastructure.Management;

internal enum CacheTrashMutationOperation
{
    Move = 0,
    Restore = 1,
    Purge = 2,
    Statistics = 3
}

public sealed partial class FileSystemCacheTrashService
{
    private static readonly TimeSpan MutationLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationGates =
        new(StringComparer.Ordinal);

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
        var gate = MutationGates.GetOrAdd(rootKey, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(MutationLockTimeout))
        {
            throw new TimeoutException(
                "Timed out waiting for another cache-trash operation on the same root.");
        }

        Mutex? processMutex = null;
        var mutexAcquired = false;
        try
        {
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
            return new MutationTransactionLease(gate, processMutex, mutexAcquired);
        }
        catch
        {
            if (mutexAcquired)
            {
                processMutex!.ReleaseMutex();
            }

            processMutex?.Dispose();
            gate.Release();
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

    private sealed class MutationTransactionLease(
        SemaphoreSlim gate,
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
                gate.Release();
            }
        }
    }
}
