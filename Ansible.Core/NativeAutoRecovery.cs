namespace Ansible.Core;

internal static class NativeAutoRecovery
{
    internal static T Run<T>(bool automatic, RecognitionOptions selected, CancellationToken token,
        Func<RecognitionOptions, T> execute, Action<NativeExecutionException> rejectGpu, Func<bool>? consumed = null)
    {
        try { return execute(selected); }
        catch (NativeExecutionException error) when (automatic && selected.Backend == NativeBackend.Vulkan &&
            !token.IsCancellationRequested && error.CanRetry)
        {
            // execute must complete its NativeOperation teardown before propagating this exception.
            rejectGpu(error);
            if (consumed?.Invoke() == true)
                throw new InvalidOperationException("Vulkan recognition failed after consuming live audio. Audio was not replayed. " +
                    "CPU is selected for the next attempt. " + error.Message, error);
            token.ThrowIfCancellationRequested();
            return execute(selected with { Backend = NativeBackend.Cpu });
        }
    }
}
