using System.Runtime.InteropServices;

namespace ConflictStudio.Core;

internal static class OodleDecoder
{
    public static byte[] Decompress(byte[] compressed, int outputSize, string oodlePath)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(oodlePath);
        using OodleDecoderSession session = new(oodlePath);
        return session.Decompress(compressed, outputSize);
    }
}

internal sealed class OodleDecoderSession : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long DecompressDelegate(IntPtr compressed, long compressedSize, IntPtr output, long outputSize, int fuzzSafe, int checkCrc, int verbosity, IntPtr decoderBuffer, long decoderBufferSize, IntPtr callback, IntPtr callbackUserData, IntPtr decoderMemory, long decoderMemorySize, int threadPhase);

    private readonly nint _library;
    private readonly DecompressDelegate _decompress;

    public OodleDecoderSession(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _library = NativeLibrary.Load(path);
        try { _decompress = Marshal.GetDelegateForFunctionPointer<DecompressDelegate>(NativeLibrary.GetExport(_library, "OodleLZ_Decompress")); }
        catch
        {
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public static OodleDecoderSession? TryOpen(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return new OodleDecoderSession(path); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
    }

    public byte[] Decompress(byte[] compressed, int outputSize)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSize);
        byte[] output = new byte[outputSize];
        GCHandle compressedHandle = GCHandle.Alloc(compressed, GCHandleType.Pinned);
        GCHandle outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            long written = _decompress(compressedHandle.AddrOfPinnedObject(), compressed.Length, outputHandle.AddrOfPinnedObject(), output.Length, 1, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
            if (written != output.Length) throw new InvalidDataException($"Oodle returned {written:N0} bytes for an expected {output.Length:N0}-byte buffer.");
            return output;
        }
        finally
        {
            compressedHandle.Free();
            outputHandle.Free();
        }
    }

    public void Dispose() => NativeLibrary.Free(_library);
}
