using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WurmMiningCounter.Services;

/// <summary>
/// Supplies an opaque physical-directory identity when the current platform can
/// provide one. Callers must treat a missing value as unknown, not as equality.
/// </summary>
internal interface IDirectoryPhysicalIdentityProvider
{
    string? GetIdentity(string directoryPath);
}

internal sealed class WindowsDirectoryPhysicalIdentityProvider : IDirectoryPhysicalIdentityProvider
{
    // Keep a directory's namespace binding stable while a source is opened/read.
    // File rotation inside the directory remains permitted.
    internal static DirectoryReadGuard? TryOpenReadGuard(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var handles = new List<SafeFileHandle>();
        var transferred = false;
        try
        {
            // Protect the logical namespace and each immediate link target,
            // including links hidden inside another junction's target path.
            if (!TryGuardNamespace(directoryPath, handles, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0))
                return null;
            var target = OpenGuard(directoryPath, openReparsePoint: false);
            if (target is null)
                return null;
            handles.Add(target);
            transferred = true;
            return new DirectoryReadGuard(handles);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
        finally
        {
            if (!transferred)
                foreach (var handle in handles)
                    handle.Dispose();
        }
    }

    private static bool TryGuardNamespace(string directoryPath, List<SafeFileHandle> handles,
        HashSet<string> guardedPaths, int linkDepth)
    {
        if (linkDepth > 32)
            return false;
        var paths = new Stack<string>();
        for (var path = directoryPath; path is not null; path = Path.GetDirectoryName(path))
            paths.Push(path);
        while (paths.TryPop(out var path))
        {
            if (!guardedPaths.Add(path))
                continue;
            if (handles.Count >= 128)
                return false;
            var handle = OpenGuard(path, openReparsePoint: true);
            if (handle is null)
                return false;
            handles.Add(handle);
            if (!GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInformation attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
                return false;
            if (((FileAttributes)attributes.FileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: false);
                if (target is null || !TryGuardNamespace(target.FullName, handles, guardedPaths, linkDepth + 1))
                    return false;
            }
        }
        return true;
    }

    private static SafeFileHandle? OpenGuard(string path, bool openReparsePoint)
    {
        // A metadata-only (zero-access) handle does not enforce delete sharing.
        // GENERIC_READ excludes rename/delete without locking files inside.
        var handle = CreateFile(path, 0x80000000, FileShare.ReadWrite, IntPtr.Zero, 3,
            0x02000000u | (openReparsePoint ? 0x00200000u : 0u), IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;
        handle.Dispose();
        return null;
    }

    internal sealed class DirectoryReadGuard(List<SafeFileHandle> handles) : IDisposable
    {
        private bool _namespaceCaptured;
        private string? _canonicalNamespacePath;

        // This is used only at registration to reject ambiguous aliases, never
        // as physical evidence for merging or promoting persisted identities.
        internal string? CanonicalNamespacePath
        {
            get
            {
                if (!_namespaceCaptured)
                {
                    var buffer = new StringBuilder(512);
                    var length = GetFinalPathNameByHandle(handles[^1], buffer, (uint)buffer.Capacity, 0);
                    if (length >= buffer.Capacity && length < 32768)
                    {
                        buffer = new StringBuilder((int)length + 1);
                        length = GetFinalPathNameByHandle(handles[^1], buffer, (uint)buffer.Capacity, 0);
                    }
                    _canonicalNamespacePath = length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
                    _namespaceCaptured = true;
                }
                return _canonicalNamespacePath;
            }
        }

        // A retained OS handle must still be usable (for example after an SMB
        // disconnect). An allocated SafeHandle alone is not continuity evidence.
        internal bool IsAvailable => handles.All(handle => !handle.IsClosed
            && GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInformation _,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()));

        public void Dispose()
        {
            for (var index = handles.Count - 1; index >= 0; index--)
                handles[index].Dispose();
        }
    }

    public string? GetIdentity(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        using var handle = CreateFile(directoryPath, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out FileIdInformation information, (uint)Marshal.SizeOf<FileIdInformation>()))
            return null;

        return FormatIdentity(information.VolumeSerialNumber, information.FileId);
    }

    internal static string? FormatIdentity(ulong volumeSerialNumber, byte[] fileId)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        if (fileId.Length != 16)
            throw new ArgumentException("A Windows File ID must contain 16 bytes.", nameof(fileId));

        // FILE_ID_INFO specifies an all-zero FileId when the filesystem cannot
        // supply a 128-bit identifier. It is unknown evidence, not a shared ID.
        if (fileId.All(value => value == 0))
            return null;

        return $"{volumeSerialNumber:X16}:{Convert.ToHexString(fileId)}";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }
}

internal sealed class DelegateDirectoryPhysicalIdentityProvider(Func<string, string?> getIdentity) : IDirectoryPhysicalIdentityProvider
{
    public string? GetIdentity(string directoryPath) => getIdentity(directoryPath);
}
