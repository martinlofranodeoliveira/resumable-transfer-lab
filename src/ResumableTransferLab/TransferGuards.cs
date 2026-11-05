using System.Net;
using System.Net.Http.Headers;

namespace ResumableTransferLab;

public static class TransferGuards
{
    public static bool ValidateArtifactId(string? artifactId, out TransferError? error)
    {
        if (string.IsNullOrEmpty(artifactId) || artifactId is "." or ".." ||
            artifactId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            return Reject(TransferErrorCode.InvalidRequest, "Artifact identifier must contain only ASCII letters, digits, dot, underscore or hyphen.", out error);
        if (artifactId.Length > TransferLimits.MaxArtifactIdLength)
            return Reject(TransferErrorCode.LimitExceeded, "Artifact identifier exceeds 128 characters.", out error);
        error = null;
        return true;
    }

    public static bool ValidateSha256Hex(string? rawHex, out string normalizedHex, out TransferError? error)
    {
        normalizedHex = string.Empty;
        if (rawHex is null || rawHex.Length != TransferLimits.Sha256HexLength || !rawHex.All(char.IsAsciiHexDigit))
            return Reject(TransferErrorCode.InvalidRequest, "SHA-256 must contain exactly 64 hexadecimal characters.", out error);
        normalizedHex = rawHex.ToLowerInvariant();
        error = null;
        return true;
    }

    public static bool ValidateStrongETag(string? rawETag, out string normalizedStrongETag, out TransferError? error)
    {
        normalizedStrongETag = string.Empty;
        if (rawETag is null || rawETag.Length > TransferLimits.MaxETagLength)
            return Reject(TransferErrorCode.MissingOrWeakETag, "A bounded strong ETag is required.", out error);
        string tag = rawETag.Trim(' ', '\t');
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"' ||
            tag[1..^1].Any(c => c < 0x21 || c > 0x7e || c is '"' or '\\') ||
            !EntityTagHeaderValue.TryParse(tag, out EntityTagHeaderValue? parsed) || parsed is null || parsed.IsWeak)
            return Reject(TransferErrorCode.MissingOrWeakETag, "ETag must be a non-empty, supported ASCII strong validator.", out error);
        normalizedStrongETag = tag;
        error = null;
        return true;
    }

    public static bool ValidateLoopbackHttpUrl(string? rawUrl, out Uri? validatedUri, out TransferError? error)
    {
        validatedUri = null;
        if (rawUrl is null || rawUrl.Length == 0 || rawUrl.Length > TransferLimits.MaxUrlLength ||
            rawUrl.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\'))
            return Reject(TransferErrorCode.NonLoopbackOrInvalidUrl, "A bounded explicit loopback HTTP URL is required.", out error);
        // Check the supplied authority before Uri canonicalizes legacy numeric IP
        // spellings or inserts the default port. No host names reach HttpClient.
        if (!rawUrl.StartsWith("http://", StringComparison.Ordinal))
            return Reject(TransferErrorCode.NonLoopbackOrInvalidUrl, "Only lowercase http:// with an explicit literal host and port is supported.", out error);
        int end = rawUrl.IndexOfAny(['/', '?', '#'], 7);
        string authority = end < 0 ? rawUrl[7..] : rawUrl[7..end];
        string port;
        if (authority.StartsWith("127.0.0.1:", StringComparison.Ordinal)) port = authority[10..];
        else if (authority.StartsWith("[::1]:", StringComparison.Ordinal)) port = authority[6..];
        else return Reject(TransferErrorCode.NonLoopbackOrInvalidUrl, "Host must be exactly 127.0.0.1 or [::1], with a port.", out error);
        if (port.Length is < 1 or > 5 || port.Any(c => c is < '0' or > '9') ||
            !int.TryParse(port, out int number) || number is < 1 or > 65535 ||
            !Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) || uri is null ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !IPAddress.TryParse(uri.DnsSafeHost, out IPAddress? ip) ||
            !(ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback)))
            return Reject(TransferErrorCode.NonLoopbackOrInvalidUrl, "URL must have a valid explicit port and no credentials, query or fragment.", out error);
        // Even empty query/fragment delimiters are excluded by this small contract.
        if (rawUrl.Contains('?') || rawUrl.Contains('#'))
            return Reject(TransferErrorCode.NonLoopbackOrInvalidUrl, "Query and fragment delimiters are not supported.", out error);
        validatedUri = uri;
        error = null;
        return true;
    }

    public static bool ValidatePaths(string? workspaceDirectory, string? destinationPath,
        out string fullWorkspace, out string fullDestination, out TransferError? error)
    {
        fullDestination = string.Empty;
        if (!ValidateWorkspaceOnlyPath(workspaceDirectory, out fullWorkspace, out error) ||
            !NormalizePath(destinationPath, out fullDestination, out error)) return false;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullWorkspace, fullDestination, comparison) ||
            fullDestination.StartsWith(fullWorkspace + Path.DirectorySeparatorChar, comparison))
            return Reject(TransferErrorCode.WorkspacePathUnsafe, "Destination must be outside the workspace.", out error);
        error = null;
        return true;
    }

    public static bool ValidateWorkspaceOnlyPath(string? workspaceDirectory, out string fullWorkspace, out TransferError? error)
    {
        if (!NormalizePath(workspaceDirectory, out fullWorkspace, out error)) return false;
        if (Path.GetPathRoot(fullWorkspace) == fullWorkspace)
            return Reject(TransferErrorCode.WorkspacePathUnsafe, "A filesystem root cannot be a workspace.", out error);
        foreach (string name in new[] { TransferLimits.LockFileName, TransferLimits.StateFileName,
            TransferLimits.PartialFileName, TransferLimits.StagingFileName, TransferLimits.StateStagingFileName })
        {
            if (HasUnsafeAncestor(Path.Combine(fullWorkspace, name)))
                return Reject(TransferErrorCode.WorkspacePathUnsafe, "Workspace paths must not use symlinks or reparse points.", out error);
        }
        error = null;
        return true;
    }

    private static bool NormalizePath(string? raw, out string full, out TransferError? error)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096 || raw.Any(char.IsControl) ||
            raw.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains(".."))
            return Reject(TransferErrorCode.WorkspacePathUnsafe, "Path is empty, too long, contains control characters or parent traversal.", out error);
        if (OperatingSystem.IsWindows() && raw.IndexOf(':', 2) >= 0)
            return Reject(TransferErrorCode.WorkspacePathUnsafe, "Alternate data streams are not supported.", out error);
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return Reject(TransferErrorCode.WorkspacePathUnsafe, "Path is invalid.", out error);
        }
        if (HasUnsafeAncestor(full))
            return Reject(TransferErrorCode.WorkspacePathUnsafe, "Paths must not use symlinks or reparse points.", out error);
        error = null;
        return true;
    }

    internal static void RequireSafeAncestors(string path)
    {
        if (HasUnsafeAncestor(path))
            throw TransferFailure.For(TransferErrorCode.WorkspacePathUnsafe, "Path uses a symlink, reparse point or inaccessible ancestor.");
    }

    private static bool HasUnsafeAncestor(string path)
    {
        string? current = path;
        while (current is not null)
        {
            if (IsSymlinkOrReparsePoint(current)) return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    public static bool IsSymlinkOrReparsePoint(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null) return true;
            return (File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool Reject(TransferErrorCode code, string message, out TransferError? error)
    {
        error = TransferError.Create(code, message);
        return false;
    }
}
