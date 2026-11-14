// Translated from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/storage/RelayPaths.kt (openVettedUpload, VettedUpload)
// Registered in docs/UPSTREAMS.md.
//
// Security (unchanged from upstream): verify the inode → open → re-check the opened fd's inode. The last link is not
// a symlink (lstat/stat inode equal), not a hard link (st_nlink == 1), a regular file, 0 < size <= max, and every
// later read and the descriptor handed to the shell service use that one fd, never the path again.

using Android.OS;
using Android.Systems;
using DshMobile.Runtime.Automation.Relay.Exec.Shizuku;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.App.Automation.Relay;

internal sealed class AndroidVettedUpload : IInstallUpload
{
    private readonly Java.IO.RandomAccessFile _handle;
    private readonly FileStream _stream;

    private AndroidVettedUpload(string path, Java.IO.RandomAccessFile handle, long size)
    {
        FilePath = path;
        _handle = handle;
        Size = size;
        // A managed view of the same fd (no second path resolution); disposed with the handle.
        var fd = handle.FD!;
        var raw = ParcelFileDescriptor.Dup(fd)!;
        _stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(raw.DetachFd(), ownsHandle: true), FileAccess.Read);
    }

    public string FilePath { get; }

    public long Size { get; }

    public static AndroidVettedUpload? Open(RelayPaths paths, string name, long maxBytes)
    {
        if (paths.ResolveUpload(name) is not { } path)
        {
            return null;
        }
        StructStat snapshot;
        try
        {
            snapshot = Os.Lstat(path)!;
            if (snapshot.StIno != Os.Stat(path)!.StIno)
            {
                return null;
            }
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
        if (snapshot.StNlink != 1L || snapshot.StSize <= 0L || snapshot.StSize > maxBytes || (snapshot.StMode & OsConstants.SIfmt) != OsConstants.SIfreg)
        {
            return null;
        }
        Java.IO.RandomAccessFile handle;
        try
        {
            handle = new Java.IO.RandomAccessFile(path, "r");
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
        try
        {
            var onDisk = Os.Fstat(handle.FD);
            if (onDisk is null || onDisk.StIno != snapshot.StIno || onDisk.StSize <= 0L || onDisk.StSize > maxBytes
                || (onDisk.StMode & OsConstants.SIfmt) != OsConstants.SIfreg)
            {
                handle.Close();
                return null;
            }
            return new AndroidVettedUpload(path, handle, onDisk.StSize);
        }
        catch (Java.Lang.Throwable)
        {
            try
            {
                handle.Close();
            }
            catch (Java.Lang.Throwable)
            {
            }
            return null;
        }
    }

    /// <summary>Positioned read on the vetted fd (pread: does not move the copy's start).</summary>
    public int ReadAt(long position, Span<byte> buffer) => RandomAccess.Read(_stream.SafeFileHandle, buffer, position);

    /// <summary>The vetted fd from offset 0; the caller copies at most <see cref="Size"/> bytes.</summary>
    public Stream OpenRead()
    {
        _stream.Position = 0;
        return new NonClosing(_stream);
    }

    /// <summary>Upstream <c>openDescriptor</c>: a dup of the vetted fd for the binder (never the path).</summary>
    public IDisposable? OpenDescriptor()
    {
        try
        {
            return ParcelFileDescriptor.Dup(_handle.FD);
        }
        catch (Java.Lang.Throwable)
        {
            return null;
        }
    }

    public void DeleteSource()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        try
        {
            _handle.Close();
        }
        catch (Java.Lang.Throwable)
        {
        }
        _handle.Dispose();
    }

    /// <summary>Callers dispose what OpenRead returns; the fd stays open until the upload is disposed.</summary>
    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
