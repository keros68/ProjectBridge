using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LocalProjectBridge.Core.Security;

public interface IRuntimeCredentialStore
{
    void Save(string target, string secret);
    bool Exists(string target);
    string Read(string target);
    void Delete(string target);
}

/// <summary>Stores the tunnel runtime key in Windows Credential Manager for the current user.</summary>
public sealed class WindowsRuntimeCredentialStore : IRuntimeCredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 2560;

    public void Save(string target, string secret)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MaximumCredentialBlobSize)
            throw new ArgumentException("运行密钥长度超出 Windows 凭据管理器限制。", nameof(secret));

        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法把运行密钥保存到 Windows 凭据管理器。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public bool Exists(string target)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (CredRead(target, CredentialTypeGeneric, 0, out var pointer))
        {
            CredFree(pointer);
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new Win32Exception(error, "无法读取 Windows 凭据管理器。");
    }

    public string Read(string target)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!CredRead(target, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) throw new InvalidOperationException("Windows 凭据管理器中没有保存运行密钥。");
            throw new Win32Exception(error, "无法读取 Windows 凭据管理器。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                throw new InvalidOperationException("Windows 凭据管理器中的运行密钥为空。");
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Delete(string target)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (CredDelete(target, CredentialTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw new Win32Exception(error, "无法删除 Windows 凭据管理器中的运行密钥。");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows 凭据管理器仅在 Windows 上可用。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
