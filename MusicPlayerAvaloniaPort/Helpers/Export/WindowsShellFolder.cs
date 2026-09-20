using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayerAvaloniaPort.Helpers.Export;

/// <summary>
/// Copying into a phone (or camera) that is connected over USB. Windows does not mount those devices as a
/// drive: they only exist inside the shell namespace (MTP), so neither File.Copy nor the app's folder picker
/// can reach them - which is what made exporting to a phone a two-step dance in the DxMGP port (export to
/// the disk first, then drag the files over in Explorer).
///
/// What it takes to make the shell do the copy (all of it learned from the device):
///  * everything runs on ONE dedicated STA thread with its own message pump (see <see cref="StaMessagePump"/>)
///    - the shell object, the folder and the copies. The app's UI thread is not that thread: called from
///    there, CopyHere is accepted and then nothing happens, and the folder object never learns about the
///    new file either;
///  * a folder on a device cannot be reopened from its path (only the device itself can), so the folder
///    object the picker returned is kept and used as it is;
///  * completion is checked BY NAME, not by size: phones report size 0 for a freshly copied file for a long
///    time (and some never report one), so a "size reached" test would wait forever on a file that is
///    already on the device and playing fine.
///
/// On Linux this class is not needed: an MTP phone is mounted like any other filesystem (usually below
/// /run/user/&lt;id&gt;/gvfs/), so the normal folder export works there.
/// </summary>
public static class WindowsShellFolder
{
    /// <summary>ssfDRIVES ("This PC"), the root the shell's folder dialog opens on: the portable devices
    /// appear below it.</summary>
    const int BrowseRootThisPc = 17;

    /// <summary>
    /// 16 = answer "yes to all", 512 = don't ask before creating a folder. This is the set the published MTP
    /// copy recipes use; the flags that suppress the shell's UI entirely (4 and 1024) were tried and made no
    /// difference on the device this was written for.
    /// </summary>
    const int CopyHereFlags = 16 | 512;

    /// <summary>How often the copy is checked while it runs (cheap name lookup on the pump thread).</summary>
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Time to give a single song: a base plus a generous per-megabyte allowance.</summary>
    static readonly TimeSpan CopyStartTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CopyTimeoutPerMb = TimeSpan.FromSeconds(5);

    static readonly Lazy<StaMessagePump> lazyPump = new(() => new StaMessagePump());

    /// <summary>The thread all device work runs on (see the class comment).</summary>
    static StaMessagePump Pump => lazyPump.Value;

    /// <summary>Shell paths of a device folder, e.g.
    /// "::{20D04FE0-...}\?\usb#vid_...\...". They are no filesystem paths and cannot be resolved again
    /// (only the device itself can be opened by path).</summary>
    public static bool IsShellPath(string path) => path.StartsWith("::", StringComparison.Ordinal);

    /// <summary>Result of the folder dialog.</summary>
    public readonly record struct BrowseResult(Selection? Selection, string? Error);

    /// <summary>
    /// A folder picked in the Windows shell folder dialog, with the live shell objects the dialog returned.
    /// They belong to the pump apartment (see the class comment), so they may only be used from there.
    /// </summary>
    public sealed class Selection(string path, string title, object folder, object shell)
    {
        /// <summary>Shell path of the folder, e.g.
        /// "::{20D04FE0-...}\?\usb#vid_...\...". Shown to the user and used to recognize this selection,
        /// but it only identifies the folder - it is not a path that can be resolved again.</summary>
        public string Path { get; } = path;
        /// <summary>Display name of the folder (e.g. "Music").</summary>
        public string Title { get; } = title;
        /// <summary>The shell Folder object (COM) to copy into.</summary>
        public object Folder { get; } = folder;
        /// <summary>The Shell.Application object the folder was obtained from.</summary>
        public object Shell { get; } = shell;
    }

    /// <summary>The live shell objects of one destination. The Shell.Application is kept next to the folder
    /// on purpose and not just dropped after the dialog: it owns the copy threads and receives their events,
    /// so losing the reference can abort copies that are still running.</summary>
    readonly record struct ShellFolderHandle(object Folder, object Shell);

    readonly record struct ShellFolderOpenResult(ShellFolderHandle? Handle, string? Error);

    /// <summary>
    /// Opens the Windows folder dialog on the pump thread - the shell object and the folder it returns have
    /// to live in the apartment the copies run in, or the copy silently goes nowhere (see the class comment).
    /// The dialog is owned by <paramref name="ownerWindow"/>, so it stays modal to the export window.
    /// </summary>
    public static async Task<BrowseResult> BrowseForFolderAsync(IntPtr ownerWindow, string title)
    {
        if (!OperatingSystem.IsWindows())
            return new BrowseResult(null, "Selecting a phone folder is only supported on Windows. On Linux the device is mounted as a normal folder.");

        try
        {
            return await Pump.RunAsync(() => BrowseForFolder(ownerWindow, title));
        }
        catch (Exception ex)
        {
            return new BrowseResult(null, ex.Message);
        }
    }

    static BrowseResult BrowseForFolder(IntPtr ownerWindow, string title)
    {
        try
        {
            object? shell = CreateShellObject();
            if (shell == null)
                return new BrowseResult(null, "The Windows shell is not available.");

            // HWNDs are 32 bit values sign extended to 64 bit, so the cast is lossless; passing the owner
            // keeps the dialog on top of the export window.
            int ownerHandle = ownerWindow == IntPtr.Zero ? 0 : unchecked((int)ownerWindow.ToInt64());
            dynamic? folder = ((dynamic)shell).BrowseForFolder(ownerHandle, title, 0, BrowseRootThisPc);
            if (folder == null)
                return new BrowseResult(null, null); // Cancelled

            string path = (string)folder.Self.Path;
            if (string.IsNullOrWhiteSpace(path))
                return new BrowseResult(null, "The picked folder has no usable shell path.");

            string displayName = "";
            try
            {
                displayName = (string)folder.Title;
            }
            catch
            {
                // Not every shell folder reports a title; the name is only used for the status line.
            }

            return new BrowseResult(new Selection(path, displayName, (object)folder, shell), null);
        }
        catch (Exception ex)
        {
            return new BrowseResult(null, ex.Message);
        }
    }

    /// <summary>
    /// The per-file copier for an export into the folder of a <see cref="Selection"/>. Every step runs on the
    /// pump thread, including the shell calls themselves.
    /// </summary>
    public static LibraryExportCopier.CopyFile CreateCopier(Selection selection) =>
        CreateCopier(() => new ShellFolderOpenResult(new ShellFolderHandle(selection.Folder, selection.Shell), null));

    /// <summary>
    /// The per-file copier for a shell path the user typed or that was loaded from the config. Only the
    /// filesystem-like shell paths and the ROOT of a portable device can be resolved this way; for a folder
    /// inside a device the resolve fails with an explanation, since Windows itself has no way to reopen it.
    /// </summary>
    public static LibraryExportCopier.CopyFile CreateCopier(string shellFolderPath) =>
        CreateCopier(() => OpenFolder(shellFolderPath));

    static LibraryExportCopier.CopyFile CreateCopier(Func<ShellFolderOpenResult> openFolder)
    {
        ShellFolderHandle? handle = null;
        string? openError = null;
        bool openAttempted = false;

        async Task<ShellFolderHandle?> EnsureOpenAsync()
        {
            if (handle != null)
                return handle;
            if (openAttempted)
                return null;

            openAttempted = true;
            ShellFolderOpenResult opened = await Pump.RunAsync(() => OpenFolderOnPump(openFolder));
            if (opened.Handle == null)
            {
                openError = opened.Error;
                return null;
            }

            handle = opened.Handle;
            return handle;
        }

        return async (string sourceFilePath, CancellationToken cancellationToken) =>
        {
            ShellFolderHandle? target = await EnsureOpenAsync();
            if (target == null)
                return new ExportCopyStep(ExportCopyOutcome.Failed, openError);

            return await CopyIntoFolderAsync(target.Value, sourceFilePath, cancellationToken);
        };
    }

    static ShellFolderOpenResult OpenFolderOnPump(Func<ShellFolderOpenResult> openFolder)
    {
        ShellFolderOpenResult opened = openFolder();
        ExportLog.Write(opened.Handle == null
            ? $"  open failed: {opened.Error}"
            : $"  open ok: {DescribeFolder(opened.Handle.Value.Folder)}\n  folder chain: {DescribeFolderChain(opened.Handle.Value.Folder)}"
              + $"\n  ({DescribeThread()})");
        return opened;
    }

    /// <summary>Resolves a shell path to a folder (runs on the pump thread).</summary>
    static ShellFolderOpenResult OpenFolder(string shellFolderPath)
    {
        if (!OperatingSystem.IsWindows())
            return new ShellFolderOpenResult(null, "Copying to a shell folder is only supported on Windows.");

        try
        {
            object? shell = CreateShellObject();
            if (shell == null)
                return new ShellFolderOpenResult(null, "The Windows shell is not available.");

            dynamic? folder = ((dynamic)shell).NameSpace(shellFolderPath);
            if (folder == null)
            {
                return new ShellFolderOpenResult(null, $"Windows cannot open the folder \"{Abbreviate(shellFolderPath)}\".\n\n"
                    + "A folder on a phone (or any other portable device) cannot be reopened by its path - Windows "
                    + "only allows that for the device itself. Pick the folder again with \"Browse phone...\".");
            }

            return new ShellFolderOpenResult(new ShellFolderHandle((object)folder, shell), null);
        }
        catch (Exception ex)
        {
            return new ShellFolderOpenResult(null, ex.Message);
        }
    }

    /// <summary>
    /// Copies one song and waits until the device shows it. All shell calls happen on the pump thread (the
    /// awaits in between keep that thread's message loop free, which is what the copy needs).
    /// </summary>
    static async Task<ExportCopyStep> CopyIntoFolderAsync(ShellFolderHandle handle, string sourceFilePath, CancellationToken cancellationToken)
    {
        long sourceSize = 0;
        try
        {
            // The shell only accepts a platform path as the copy source. The library stores its root with
            // forward slashes and the scan appends "\song.mp3", so the paths this app produces look like
            // "N:/Media/Music\Song.mp3" - and CopyHere silently copies NOTHING for those (no error, no
            // dialog, nothing). File.Exists and playback do not care, which is what made this so hard to
            // see; Path.GetFullPath turns it into "N:\Media\Music\Song.mp3".
            sourceFilePath = Path.GetFullPath(sourceFilePath);
            sourceSize = new FileInfo(sourceFilePath).Length;
        }
        catch (Exception ex)
        {
            return new ExportCopyStep(ExportCopyOutcome.Failed, $"{Path.GetFileName(sourceFilePath)}: {ex.Message}");
        }

        string fileName = Path.GetFileName(sourceFilePath);

        bool alreadyThere;
        try
        {
            alreadyThere = await Pump.RunAsync(() =>
            {
                dynamic folder = handle.Folder;
                bool exists = folder.ParseName(fileName) != null;
                if (!exists)
                    folder.CopyHere(sourceFilePath, CopyHereFlags);

                return exists;
            });
        }
        catch (Exception ex)
        {
            ExportLog.Write($"  \"{fileName}\" failed: {ex.GetType().Name} (hresult=0x{ex.HResult:X8}): {ex.Message} "
                + $"(source {sourceFilePath})");
            return new ExportCopyStep(ExportCopyOutcome.Failed, $"{fileName}: {ex.Message}");
        }

        if (alreadyThere)
            return new ExportCopyStep(ExportCopyOutcome.SkippedAlreadyThere);

        DateTime startedAt = DateTime.UtcNow;
        DateTime deadline = startedAt + CopyStartTimeout + CopyTimeoutPerMb * (sourceSize / (1024 * 1024));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);

            // The name is what counts: this is the answer to "did the device take the file?" - the size a
            // phone reports for it is not trustworthy (often 0 until long after the file is playable).
            if (await Pump.RunAsync(() => FolderContainsFile(handle.Folder, fileName)))
                return new ExportCopyStep(ExportCopyOutcome.Copied);
        }

        // Nothing arrived: this is the line that has to explain a device problem, so it carries everything
        // that was needed to find the last one (the exact source path, the folder's contents).
        ExportLog.Write($"  \"{fileName}\" did not arrive within {(int)(DateTime.UtcNow - startedAt).TotalSeconds}s "
            + $"(source {sourceFilePath}, {sourceSize} bytes, exists={File.Exists(sourceFilePath)}); "
            + $"items now={await Pump.RunAsync(() => TryGetItemCount(handle.Folder))}; "
            + $"contents: {await Pump.RunAsync(() => DescribeItems(handle.Folder))}");

        return new ExportCopyStep(
            ExportCopyOutcome.Failed,
            $"{fileName}: the device did not show the file in time - is it still connected?");
    }

    /// <summary>
    /// Whether the folder holds a file of that name. Asked by name first (cheap); only if that misses, the
    /// folder is enumerated once, since some devices do not answer to a name even for a file they hold.
    /// </summary>
    static bool FolderContainsFile(dynamic folder, string fileName)
    {
        try
        {
            if (folder.ParseName(fileName) != null)
                return true;
        }
        catch
        {
            // Fall through to the enumeration.
        }

        try
        {
            dynamic? items = folder.Items();
            if (items == null)
                return false;

            int count = (int)items.Count;
            for (int i = 0; i < count; i++)
            {
                object? item = items.Item(i);
                if (item == null)
                    continue;

                if (string.Equals((string)((dynamic)item).Name, fileName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Enumeration itself failed: treat it as "not there" and let the caller report the timeout.
        }

        return false;
    }

    /// <summary>Which thread - and apartment - the shell is being called on.</summary>
    static string DescribeThread() =>
        OperatingSystem.IsWindows()
            ? $"thread {Environment.CurrentManagedThreadId}, apartment {Thread.CurrentThread.GetApartmentState()}, pump={Pump.IsPumpThread}"
            : $"thread {Environment.CurrentManagedThreadId}";

    /// <summary>Identity of the shell folder being copied into.</summary>
    static string DescribeFolder(object folderHandle)
    {
        try
        {
            dynamic folder = folderHandle;
            string name = "";
            string path = "";
            try { name = (string)folder.Self.Name; } catch { }
            try { path = (string)folder.Self.Path; } catch { }
            return $"name=\"{name}\", items={TryGetItemCount(folder)}, path={Abbreviate(path, 200)}";
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.Message}>";
        }
    }

    /// <summary>
    /// The picked folder and its parents. A phone shows the same folder names in several places (a "Music"
    /// under the device, under a storage card, under Android/media, ...), so the log records which one the
    /// copy writes into.
    /// </summary>
    static string DescribeFolderChain(object folderHandle, int maxLevels = 4)
    {
        try
        {
            List<string> chain = [];
            object? current = folderHandle;
            for (int level = 0; level < maxLevels && current != null; level++)
            {
                dynamic folder = current;
                string name = "";
                try { name = (string)folder.Self.Name; } catch { }
                chain.Add($"{name} (items={TryGetItemCount(current)})");

                try { current = (object?)folder.ParentFolder; } catch { current = null; }
            }

            return string.Join(" < ", chain);
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.Message}>";
        }
    }

    /// <summary>The first few names in the folder, so the log shows what the device reports back.</summary>
    static string DescribeItems(dynamic folderHandle, int maxItems = 15)
    {
        try
        {
            dynamic folder = folderHandle;
            dynamic? items = folder.Items();
            if (items == null)
                return "<not enumerable>";

            int count = (int)items.Count;
            List<string> names = [];
            for (int i = 0; i < count && i < maxItems; i++)
            {
                object? item = items.Item(i);
                if (item == null)
                    continue;
                try { names.Add((string)((dynamic)item).Name); } catch { }
            }

            return $"{count} item(s): {string.Join(", ", names)}{(count > maxItems ? ", ..." : "")}";
        }
        catch (Exception ex)
        {
            return $"<enumeration failed: {ex.Message}>";
        }
    }

    static int TryGetItemCount(dynamic folder)
    {
        try
        {
            dynamic? items = folder.Items();
            return items == null ? -1 : (int)items.Count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Shortens a shell path for an error message: the path of a folder on a phone is a few hundred
    /// characters of device ids, which makes a message unreadable (and it is not what the user has to fix).
    /// </summary>
    static string Abbreviate(string path, int maxLength = 110) =>
        path.Length <= maxLength ? path : path[..(maxLength / 2)] + "…" + path[^(maxLength / 2)..];

    static object? CreateShellObject()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        return shellType == null ? null : Activator.CreateInstance(shellType);
    }
}
