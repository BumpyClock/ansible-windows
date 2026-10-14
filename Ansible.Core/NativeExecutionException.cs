namespace Ansible.Core;

internal enum NativeStatus
{
    Ok = 0,
    InvalidArgument = 1,
    UnsupportedFamily = 2,
    LoadFailed = 3,
    Runtime = 4,
    OutOfMemory = 5,
    OutOfRange = 6,
    NotAvailable = 7
}

internal sealed class NativeExecutionException(string message, string operation = "", int status = (int)NativeStatus.Runtime)
    : InvalidOperationException(message)
{
    internal int StatusCode { get; } = status;
    internal bool IsMemoryFailure => StatusCode == (int)NativeStatus.OutOfMemory ||
        Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("OUT_OF_DEVICE_MEMORY", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("OUT_OF_HOST_MEMORY", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("bad_alloc", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("allocation failed", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("allocation failure", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("alloc failed", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("cannot allocate", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("could not allocate", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("does not fit in device memory", StringComparison.OrdinalIgnoreCase) ||
        Message.Contains("failed to create", StringComparison.OrdinalIgnoreCase) &&
            Message.Contains("graph allocator", StringComparison.OrdinalIgnoreCase);
    private bool IsExecutionOperation => operation.StartsWith("create the native recognition session", StringComparison.Ordinal) ||
        operation is "transcribe the recording" or "benchmark public sample" or "start native dictation" or
            "process a dictation audio chunk" or "finish native dictation";
    internal bool IsQualificationFatal => IsMemoryFailure || !IsExecutionOperation ||
        StatusCode is not (int)NativeStatus.Runtime and not (int)NativeStatus.NotAvailable;
    internal bool CanRetry => !IsQualificationFatal;
}
