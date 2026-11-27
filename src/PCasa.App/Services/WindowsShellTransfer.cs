using System.IO;
using System.Runtime.InteropServices;

namespace PCasa.App.Services;

// Windows-only physical transfer adapter. The scheduler still owns the
// operation lifetime, and the durable gallery receipt owns its exact names.
public static class WindowsShellTransfer
{
    public sealed record Result(bool Completed, bool Aborted, Exception? Error);

    public static Result Execute(string source, string destination, bool move,
        bool replace, IntPtr owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Result? result = null;
        // Shell COM and its native UI run on an STA. Join synchronously on
        // the owning scheduler worker, not on the WPF dispatcher: the
        // caller's path locks must stay held on the same thread.
        var thread = new Thread(() =>
        {
            try { result = ExecuteSta(source, destination, move, replace, owner, token); }
            catch (Exception ex) { result = new Result(false, token.IsCancellationRequested, ex); }
        }) { IsBackground = true, Name = "PCasa Windows shell transfer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result ?? throw new IOException("Windows shell did not return a transfer result.");
    }

    private static Result ExecuteSta(string source, string destination, bool move,
        bool replace, IntPtr owner, CancellationToken token)
    {
        var clsid = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
        var operation = (IFileOperation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
        IShellItem? item = null, folder = null;
        var sink = new TransferSink(destination, replace, token);
        uint cookie = 0;
        bool advised = false;
        try
        {
            var iid = typeof(IShellItem).GUID;
            Check(SHCreateItemFromParsingName(source, IntPtr.Zero, ref iid, out item));
            Check(SHCreateItemFromParsingName(Path.GetDirectoryName(destination)!,
                IntPtr.Zero, ref iid, out folder));
            Check(operation.SetOwnerWindow(owner));
            // No SILENT, NOERRORUI or RENAMEONCOLLISION flags. The user
            // sees native Windows progress. A preapproved PCasa replacement
            // need not ask the same conflict question a second time.
            Check(operation.SetOperationFlags(replace ? 0x0010u : 0u));
            Check(operation.Advise(sink, out cookie));
            advised = true;
            var name = Path.GetFileName(destination);
            Check(move ? operation.MoveItem(item!, folder!, name, null) :
                operation.CopyItem(item!, folder!, name, null));
            var hr = operation.PerformOperations();
            var abortHr = operation.GetAnyOperationsAborted(out var aborted);
            var errorHr = hr < 0 ? hr : abortHr < 0 ? abortHr : sink.ItemResult;
            var error = errorHr < 0 ? Marshal.GetExceptionForHR(errorHr) : null;
            return new Result(sink.Completed,
                aborted || token.IsCancellationRequested ||
                errorHr == unchecked((int)0x800704C7) ||
                errorHr == unchecked((int)0x80004004), error);
        }
        finally
        {
            if (advised) operation.Unadvise(cookie);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            if (item is not null) Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(operation);
            GC.KeepAlive(sink);
        }
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindingContext,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr value);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    // Declaration follows the SDK vtable, including unused slots.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IntPtr item, IntPtr name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, IntPtr name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem folder,
            [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink? sink);
        [PreserveSig] int MoveItems(IntPtr items, IntPtr folder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem folder,
            [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink? sink);
        [PreserveSig] int CopyItems(IntPtr items, IntPtr folder);
        [PreserveSig] int DeleteItem(IntPtr item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IntPtr folder, uint attributes, IntPtr name,
            IntPtr template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hr);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr name);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr name, int hr, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr name);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr name, int hr, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr name);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr name, int hr, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int hr, IntPtr created);
        [PreserveSig] int PreNewItem(uint flags, IntPtr folder, IntPtr name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr folder, IntPtr name,
            IntPtr template, uint attributes, int hr, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class TransferSink(string destination, bool replace, CancellationToken token) : IProgressSink
    {
        private const int Abort = unchecked((int)0x80004004);
        public bool Completed { get; private set; }
        public int ItemResult { get; private set; }
        private int Continue() => token.IsCancellationRequested ? Abort : 0;
        private int BeforeTransfer()
        {
            if (token.IsCancellationRequested) return Abort;
            // Never allow a newly appeared conflict to turn an immutable
            // no-replace receipt into an unapproved overwrite.
            if (!replace && SourceFilePresence.Probe(destination) != FilePresence.ConfirmedMissing)
                return unchecked((int)0x80070050);
            return 0;
        }
        private int AfterTransfer(int hr, IShellItem? created)
        {
            ItemResult = hr;
            if (hr != 0 || created is null) return 0;
            IntPtr ptr = IntPtr.Zero;
            try
            {
                // SIGDN_FILESYSPATH: confirm the exact reserved output.
                if (created.GetDisplayName(0x80058000u, out ptr) >= 0)
                    Completed = string.Equals(Marshal.PtrToStringUni(ptr),
                        destination, StringComparison.OrdinalIgnoreCase);
            }
            catch (COMException) { }
            finally { if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr); }
            return 0;
        }
        public int StartOperations() => Continue();
        public int FinishOperations(int hr) { if (ItemResult == 0 && hr < 0) ItemResult = hr; return 0; }
        public int PreRenameItem(uint flags, IntPtr item, IntPtr name) => Continue();
        public int PostRenameItem(uint flags, IntPtr item, IntPtr name, int hr, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr name) => BeforeTransfer();
        public int PostMoveItem(uint flags, IntPtr item, IntPtr folder, IntPtr name, int hr, IShellItem? created) => AfterTransfer(hr, created);
        public int PreCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr name) => BeforeTransfer();
        public int PostCopyItem(uint flags, IntPtr item, IntPtr folder, IntPtr name, int hr, IShellItem? created) => AfterTransfer(hr, created);
        public int PreDeleteItem(uint flags, IntPtr item) => Continue();
        public int PostDeleteItem(uint flags, IntPtr item, int hr, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr folder, IntPtr name) => Continue();
        public int PostNewItem(uint flags, IntPtr folder, IntPtr name, IntPtr template,
            uint attributes, int hr, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint completed) => Continue();
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
