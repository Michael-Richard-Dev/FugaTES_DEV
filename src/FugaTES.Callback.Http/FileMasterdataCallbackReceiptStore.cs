using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace FugaTES.Callback.Http;

public interface IMasterdataCallbackReceiptStore
{
    Task PersistAsync(Guid receiptId, DateTimeOffset receivedUtc, ReadOnlyMemory<byte> body, CancellationToken ct);
}

public sealed class FileMasterdataCallbackReceiptStore(string directory) : IMasterdataCallbackReceiptStore
{
    public async Task PersistAsync(Guid receiptId, DateTimeOffset receivedUtc, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (receiptId == Guid.Empty || body.Length > MasterdataCallbackOptions.MaximumBodyBytes)
            throw new IOException("RECEIPT_INVALID");
        ValidatePrivateDirectory(directory);
        var stem = receiptId.ToString("N");
        var pending = Path.Combine(directory, stem + ".pending");
        var committed = Path.Combine(directory, stem + ".receipt.json");
        using var payload = JsonDocument.Parse(body);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            receiptFormat = 1, receiptId, receivedUtc, contentType = "application/json",
            bodySizeBytes = body.Length, bodySha256 = Convert.ToHexString(SHA256.HashData(body.Span)),
            payload = payload.RootElement
        });
        var pendingCreated = false;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(pending, options))
            {
                pendingCreated = true;
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            // Acknowledgement requires both file data and its committed directory entry to be durable.
            if (OperatingSystem.IsLinux())
            {
                File.Move(pending, committed, overwrite: false);
                var fd = OpenDirectory(directory, 0x90000); // O_DIRECTORY | O_CLOEXEC, read-only.
                if (fd < 0) throw new IOException("RECEIPT_DIRECTORY_OPEN_FAILED");
                try { if (SyncDirectory(fd) != 0) throw new IOException("RECEIPT_DIRECTORY_SYNC_FAILED"); }
                finally { CloseDirectory(fd); }
            }
            else if (OperatingSystem.IsWindows())
            {
                if (!MoveWriteThrough(pending, committed, 8)) throw new IOException("RECEIPT_RENAME_FAILED");
            }
            else throw new IOException("RECEIPT_PLATFORM_UNSUPPORTED");
        }
        finally
        {
            // Never delete a possibly committed receipt after an uncertain outcome.
            try { if (pendingCreated && File.Exists(pending)) File.Delete(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidatePrivateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new IOException("RECEIPT_DIRECTORY_UNAVAILABLE");
        var current = new DirectoryInfo(path);
        for (var ancestor = current; ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("RECEIPT_DIRECTORY_LINK_FORBIDDEN");
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(path);
            var other = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            var owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if ((mode & other) != 0 || (mode & owner) != owner) throw new IOException("RECEIPT_DIRECTORY_NOT_PRIVATE");
        }
        else if (OperatingSystem.IsWindows())
        {
            var acl = current.GetAccessControl();
            if (!acl.AreAccessRulesProtected) throw new IOException("RECEIPT_DIRECTORY_NOT_PRIVATE");
            var allowed = new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference.Value, StringComparer.Ordinal))
                    throw new IOException("RECEIPT_DIRECTORY_NOT_PRIVATE");
        }
        else throw new IOException("RECEIPT_PLATFORM_UNSUPPORTED");
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncDirectory(int fd);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseDirectory(int fd);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWriteThrough(string source, string target, int flags);
}
