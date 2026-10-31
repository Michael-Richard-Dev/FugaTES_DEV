using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FugaTES.Callback.Http.ManualRetry;

public sealed record CommandFileMetadata(bool Exists, bool IsDirectory, bool IsRegularFile, bool IsSymlink,
    long Length, UnixFileMode Mode, uint OwnerUid);

// Metadata capability deliberately has no content-reading operation.
public interface ICommandFileMetadataReader
{
    bool IsLinux { get; }
    uint EffectiveUid { get; }
    CommandFileMetadata Inspect(string path);
}

// Linux VM only. Dedicated secrets and approvals live outside source and release payload.
public static class PrivateCommandFile
{
    public static byte[] Read(string path, int maximumBytes, bool privateContent = true)
    {
        ValidateMetadata(path, maximumBytes, privateContent);
        return File.ReadAllBytes(Path.GetFullPath(path));
    }

    // Use for passfiles: prove trust and permissions without ever reading the secret.
    public static void ValidateMetadata(string path, int maximumBytes, bool privateContent = true) =>
        ValidateMetadata(path, maximumBytes, privateContent, LinuxMetadataReader.Instance);

    public static void ValidateMetadata(string path, int maximumBytes, bool privateContent, ICommandFileMetadataReader metadata)
    {
        if (!metadata.IsLinux) throw new InvalidOperationException("ACM_COMMAND_LINUX_VM_REQUIRED");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new InvalidOperationException("ACM_COMMAND_FILE_REFERENCE_INVALID");
        var full = Path.GetFullPath(path);
        if (privateContent && (Within(full, AppContext.BaseDirectory) || Within(full, Directory.GetCurrentDirectory())))
            throw new InvalidOperationException("ACM_COMMAND_EXTERNAL_PRIVATE_FILE_REQUIRED");
        var effectiveUid = metadata.EffectiveUid;
        for (string? node = full; node is not null; node = Path.GetDirectoryName(node))
        {
            var entry = metadata.Inspect(node);
            if (!entry.Exists) throw new InvalidOperationException("ACM_COMMAND_FILE_REFERENCE_INVALID");
            if (entry.IsSymlink) throw new InvalidOperationException("ACM_COMMAND_SYMLINK_REJECTED");
            var isFile = node == full;
            if ((isFile && (!entry.IsRegularFile || entry.IsDirectory)) || (!isFile && !entry.IsDirectory))
                throw new InvalidOperationException("ACM_COMMAND_REGULAR_FILE_REQUIRED");
            if (isFile && (entry.Length == 0 || entry.Length > maximumBytes))
                throw new InvalidOperationException("ACM_COMMAND_FILE_SIZE_INVALID");
            var forbidden = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            if (isFile && privateContent)
                forbidden |= UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute | UnixFileMode.UserExecute |
                    UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit;
            if ((entry.Mode & forbidden) != 0 || (isFile && privateContent && (entry.Mode & UnixFileMode.UserRead) == 0))
                throw new InvalidOperationException("ACM_COMMAND_PRIVATE_MODE_REQUIRED");
            if (entry.OwnerUid != 0 && entry.OwnerUid != effectiveUid)
                throw new InvalidOperationException("ACM_COMMAND_TRUSTED_OWNER_REQUIRED");
        }
    }
    static bool Within(string file, string directory) => file.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    sealed class LinuxMetadataReader : ICommandFileMetadataReader
    {
        public static readonly LinuxMetadataReader Instance = new();
        [DllImport("libc")] static extern uint geteuid();
        public bool IsLinux => OperatingSystem.IsLinux();
        public uint EffectiveUid => IsLinux ? geteuid() : throw new InvalidOperationException("ACM_COMMAND_LINUX_VM_REQUIRED");
        public CommandFileMetadata Inspect(string path)
        {
            if (!IsLinux) throw new InvalidOperationException("ACM_COMMAND_LINUX_VM_REQUIRED");
            if (!File.Exists(path) && !Directory.Exists(path)) return new(false, false, false, false, 0, 0, 0);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return new(true, false, false, true, 0, 0, 0);
            using var stat = new Process { StartInfo = new ProcessStartInfo("/usr/bin/stat")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            stat.StartInfo.ArgumentList.Add("--format=%u:%f"); stat.StartInfo.ArgumentList.Add("--"); stat.StartInfo.ArgumentList.Add(path);
            stat.Start(); var metadata = stat.StandardOutput.ReadToEnd().Trim().Split(':');
            if (!stat.WaitForExit(5000) || stat.ExitCode != 0 || metadata.Length != 2 || !uint.TryParse(metadata[0], out var owner) ||
                !uint.TryParse(metadata[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawMode))
                throw new InvalidOperationException("ACM_COMMAND_TRUSTED_OWNER_REQUIRED");
            var directory = (rawMode & 0xF000) == 0x4000;
            var regular = (rawMode & 0xF000) == 0x8000;
            return new(true, directory, regular, false, regular ? new FileInfo(path).Length : 0, (UnixFileMode)(rawMode & 0xFFF), owner);
        }
    }
}

