using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using WinGit.Core;

namespace WinGit.Native;

/// <summary>
/// Interactive WinUI adapter for moving a file or folder to the Windows
/// Recycle Bin.
/// </summary>
/// <remarks>
/// The Core discard workflow injects this action so it remains testable and
/// never silently falls back to permanent deletion. This adapter uses
/// IFileOperation's explicit recycle flag and fails without a permanent-delete
/// fallback when the process is non-interactive or the shell operation fails.
/// </remarks>
public sealed class WindowsRecycleBinFileRecycler : IFileRecycler
{
    private const uint FofSilent = 0x0004;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxRecycleOnDelete = 0x0008_0000;
    private const uint FofxEarlyFailure = 0x0010_0000;

    private static readonly Guid FileOperationClassId =
        new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public Task RecycleAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInteractiveRecycleBin();

        RecycleCore(Path.GetFullPath(fullPath));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static void RecycleCore(string fullPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            throw new FileNotFoundException("The selected path no longer exists.", fullPath);
        }
        catch (DirectoryNotFoundException)
        {
            throw new DirectoryNotFoundException($"The selected path no longer exists: '{fullPath}'.");
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Reparse-point paths cannot be moved to the Recycle Bin safely.");
        }

        IFileOperation? fileOperation = null;
        IShellItem? shellItem = null;
        try
        {
            var shellItemId = typeof(IShellItem).GUID;
            var createResult = NativeMethods.SHCreateItemFromParsingName(
                fullPath,
                nint.Zero,
                ref shellItemId,
                out shellItem);
            Marshal.ThrowExceptionForHR(createResult);

            var fileOperationType = Type.GetTypeFromCLSID(
                FileOperationClassId,
                throwOnError: true);
            fileOperation = (IFileOperation?)Activator.CreateInstance(fileOperationType!)
                ?? throw new InvalidOperationException("Windows file operations are unavailable.");

            fileOperation.SetOperationFlags(
                FofSilent
                | FofAllowUndo
                | FofNoConfirmation
                | FofNoErrorUi
                | FofxRecycleOnDelete
                | FofxEarlyFailure);
            fileOperation.DeleteItem(shellItem, null);

            Exception? operationException = null;
            try
            {
                fileOperation.PerformOperations();
            }
            catch (Exception exception)
            {
                operationException = exception;
            }

            var operationsAborted = false;
            try
            {
                // The shell contract requires checking this even when
                // PerformOperations returned an error.
                operationsAborted = fileOperation.GetAnyOperationsAborted();
            }
            catch (Exception exception)
            {
                operationException ??= exception;
            }

            if (operationException is not null)
            {
                ExceptionDispatchInfo.Capture(operationException).Throw();
            }

            if (operationsAborted)
            {
                throw new IOException("The Windows Recycle Bin operation was aborted.");
            }
        }
        finally
        {
            ReleaseComObject(shellItem);
            ReleaseComObject(fileOperation);
        }
    }

    private static void EnsureInteractiveRecycleBin()
    {
        if (!Environment.UserInteractive)
        {
            // Microsoft.VisualBasic.FileIO falls back to File.Delete in this
            // mode. Refuse it before any shell operation so this adapter can
            // never become a permanent-delete path in a service host.
            throw new InvalidOperationException(
                "The Windows Recycle Bin requires an interactive WinUI process.");
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise([MarshalAs(UnmanagedType.Interface)] object progressSink, out uint cookie);

        void Unadvise(uint cookie);

        void SetOperationFlags(uint operationFlags);

        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

        void SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object progressDialog);

        void SetProperties([MarshalAs(UnmanagedType.Interface)] object propertyArray);

        void SetOwnerWindow(nint ownerWindow);

        void ApplyPropertiesToItem(IShellItem shellItem);

        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);

        void RenameItem(
            IShellItem shellItem,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            [MarshalAs(UnmanagedType.Interface)] object progressSink);

        void RenameItems(
            [MarshalAs(UnmanagedType.Interface)] object items,
            [MarshalAs(UnmanagedType.LPWStr)] string newName);

        void MoveItem(
            IShellItem shellItem,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            [MarshalAs(UnmanagedType.Interface)] object progressSink);

        void MoveItems(
            [MarshalAs(UnmanagedType.Interface)] object items,
            IShellItem destinationFolder);

        void CopyItem(
            IShellItem shellItem,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string copyName,
            [MarshalAs(UnmanagedType.Interface)] object progressSink);

        void CopyItems(
            [MarshalAs(UnmanagedType.Interface)] object items,
            IShellItem destinationFolder);

        void DeleteItem(
            IShellItem shellItem,
            [MarshalAs(UnmanagedType.Interface)] object? progressSink);

        void DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);

        uint NewItem(
            IShellItem destinationFolder,
            uint attributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string templateName,
            [MarshalAs(UnmanagedType.Interface)] object progressSink);

        void PerformOperations();

        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetAnyOperationsAborted();
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            nint bindingContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);
    }
}
