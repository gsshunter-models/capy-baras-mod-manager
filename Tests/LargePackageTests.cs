using MXSimModManager.Core;

public static class LargePackageTests
{
    public static void Run()
    {
        // Exercise the real archive copy loop above the former limit without
        // allocating a huge buffer or writing tens of gigabytes to disk.
        foreach (var size in new[] { 40L * 1024 * 1024 * 1024 + 1, 64L * 1024 * 1024 * 1024 })
        {
            using var input = new SizedStream(size);
            using var output = new CountingStream();
            ModInstaller.CopyExactLength(input, output, size);
            if (output.BytesWritten != size) throw new InvalidOperationException("Large archive entry was truncated.");
        }
        Throws(() => ModInstaller.CopyExactLength(new SizedStream(10), Stream.Null, 9));
        Throws(() => ModInstaller.CopyExactLength(new SizedStream(9), Stream.Null, 10));
        Throws(() => ModInstaller.CopyExactLength(new SizedStream(0), Stream.Null, -1));
    }

    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Archive length mismatches must still be rejected.");
    }

    private sealed class SizedStream(long remaining) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(remaining, count);
            remaining -= read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten = checked(BytesWritten + count);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
