using System.Security.Cryptography;
using System.Text;

namespace ResumableTransferLab;

internal sealed class TransferFailure(TransferError error) : Exception(error.Message)
{
    public TransferError Error { get; } = error;
    public static TransferFailure For(TransferErrorCode code, string message) => new(TransferError.Create(code, message));
}

internal sealed class WorkspaceLease : IDisposable
{
    private const string Marker = "Resumable Transfer Lab workspace 1\n";
    private readonly FileStream _lock;
    private readonly string _directory;
    private WorkspaceLease(string directory, FileStream stream) { _directory = directory; _lock = stream; }
    public string StatePath => Path.Combine(_directory, TransferLimits.StateFileName);
    public string PartPath => Path.Combine(_directory, TransferLimits.PartialFileName);
    public string StagePath => Path.Combine(_directory, TransferLimits.StagingFileName);

    public static WorkspaceLease Open(string directory, bool create)
    {
        TransferGuards.RequireSafeAncestors(directory);
        bool exists = Directory.Exists(directory);
        if (File.Exists(directory) || (!exists && !create))
            throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Workspace is not an owned directory.");
        string lockPath = Path.Combine(directory, TransferLimits.LockFileName);
        if (exists && !File.Exists(lockPath))
            throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Existing directories without an ownership marker are not adopted.");
        if (!exists) Directory.CreateDirectory(directory);
        TransferGuards.RequireSafeAncestors(lockPath);
        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, exists ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw TransferFailure.For(TransferErrorCode.ConcurrentTransferNotAllowed, "Workspace ownership could not be acquired exclusively.");
        }
        try
        {
            byte[] marker = Encoding.ASCII.GetBytes(Marker);
            if (!exists)
            {
                stream.Write(marker);
                stream.Flush(flushToDisk: true);
            }
            else
            {
                if (stream.Length != marker.Length)
                    throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Workspace ownership marker is invalid.");
                byte[] actual = new byte[marker.Length];
                stream.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(marker))
                    throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Workspace ownership marker is invalid.");
            }
            return new WorkspaceLease(directory, stream);
        }
        catch { stream.Dispose(); throw; }
    }

    public TransferState? LoadState(CancellationToken ct)
    {
        int entries = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(_directory))
        {
            if (++entries > 3)
                throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Workspace contains unexpected files.");
            TransferGuards.RequireSafeAncestors(path);
            string name = Path.GetFileName(path);
            if (Directory.Exists(path) || (name != TransferLimits.LockFileName &&
                name != TransferLimits.StateFileName && name != TransferLimits.PartialFileName))
                throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Workspace contains unexpected or uncommitted files.");
        }
        bool stateExists = File.Exists(StatePath);
        bool partExists = File.Exists(PartPath);
        if (stateExists != partExists)
            throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "State and partial file must exist together.");
        if (!stateExists) return null;
        byte[] bytes = TransferStorage.ReadStateBounded(StatePath, ct);
        if (!StateSerializer.TryParse(bytes, out TransferState? state, out TransferError? error))
            throw new TransferFailure(error!);
        string prefix = TransferStorage.HashFile(PartPath, state!.CommittedBytes, ct);
        if (prefix != state.CommittedPrefixSha256)
            throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Partial bytes differ from the committed prefix digest.");
        return state;
    }

    public void Dispose() => _lock.Dispose();
}

internal static class TransferStorage
{
    internal static bool IsLocalIo(Exception ex) => ex is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or NotSupportedException;

    internal static byte[] ReadStateBounded(string path, CancellationToken ct)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[TransferLimits.MaxStateFileBytes + 1];
        int used = 0;
        while (used < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, used, buffer.Length - used);
            if (read == 0) break;
            used += read;
        }
        if (used > TransferLimits.MaxStateFileBytes)
            throw TransferFailure.For(TransferErrorCode.LimitExceeded, "State exceeds the 64 KiB cap.");
        return buffer.AsSpan(0, used).ToArray();
    }

    internal static string HashFile(string path, long expectedLength, CancellationToken ct)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashStream(stream, expectedLength, ct);
    }

    private static string HashStream(Stream stream, long expectedLength, CancellationToken ct)
    {
        if (expectedLength < 0 || expectedLength > TransferLimits.MaxArtifactBytes || stream.Length != expectedLength)
            throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Partial length differs from the committed offset.");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[TransferLimits.MaxChunkBytes];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Local file changed while being verified.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (stream.ReadByte() != -1)
            throw TransferFailure.For(TransferErrorCode.AmbiguousLocalState, "Local file changed while being verified.");
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void WriteState(string statePath, TransferState state, CancellationToken ct)
    {
        byte[] bytes = StateSerializer.SerializeToUtf8Bytes(state);
        string next = Path.Combine(Path.GetDirectoryName(statePath)!, TransferLimits.StateStagingFileName);
        ct.ThrowIfCancellationRequested();
        using (FileStream stream = new(next, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        ct.ThrowIfCancellationRequested();
        // Retain the old checkpoint and the new stage on failure. LoadState refuses
        // such evidence instead of guessing whether an append was committed.
        File.Move(next, statePath, overwrite: true);
    }

    internal static void AppendPrefix(string source, string partial, long count, CancellationToken ct)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream output = new(partial, FileMode.Append, FileAccess.Write, FileShare.None);
        CopyExactly(input, output, count, ct);
        output.Flush(flushToDisk: true);
    }

    private static void CopyExactly(Stream input, Stream output, long count, CancellationToken ct)
    {
        byte[] buffer = new byte[TransferLimits.MaxChunkBytes];
        while (count > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new IOException("Local source ended early.");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }

    // The internal copier seam permits a real mid-write failure test without adding
    // a public fault-injection API or relying on machine-specific disk exhaustion.
    internal static string Publish(string partial, string destination, long expectedLength, string expectedDigest,
        CancellationToken ct, Action<Stream, Stream, CancellationToken>? copier = null)
    {
        string actual = HashFile(partial, expectedLength, ct);
        if (actual != expectedDigest)
            throw TransferFailure.For(TransferErrorCode.DigestMismatch, "Completed bytes differ from the caller-supplied SHA-256.");
        TransferGuards.RequireSafeAncestors(destination);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw TransferFailure.For(TransferErrorCode.DestinationAlreadyExists, "Destination exists and will not be overwritten.");
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        TransferGuards.RequireSafeAncestors(parent);
        string stage = Path.Combine(parent, $".rtl-{Guid.NewGuid():N}.stage");
        bool owned = false;
        try
        {
            using (FileStream input = new(partial, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = new(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                owned = true;
                if (copier is null) CopyExactly(input, output, expectedLength, ct);
                else copier(input, output, ct);
                output.Flush(flushToDisk: true);
                output.Position = 0;
                if (HashStream(output, expectedLength, ct) != expectedDigest)
                    throw TransferFailure.For(TransferErrorCode.DigestMismatch, "Publication stage differs from the expected digest.");
            }
            ct.ThrowIfCancellationRequested();
            TransferGuards.RequireSafeAncestors(destination);
            try { File.Move(stage, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                throw TransferFailure.For(TransferErrorCode.DestinationAlreadyExists, "Destination exists and will not be overwritten.");
            }
            owned = false;
            return actual;
        }
        finally { if (owned) DeleteOwnedStage(stage); }
    }

    internal static void DeleteOwnedStage(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (IsLocalIo(ex)) { /* Residual stages are evidence, not permission to adopt or overwrite them. */ }
    }
}
