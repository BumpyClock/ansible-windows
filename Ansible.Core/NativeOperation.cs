namespace Ansible.Core;

internal static class NativeOperation
{
    public static void Step(CancellationToken token, Action work)
    {
        token.ThrowIfCancellationRequested();
        work();
        token.ThrowIfCancellationRequested();
    }

    public static T Run<T>(Func<T> work, Action<bool> cleanup)
    {
        Exception? failure = null;
        var success = false;
        try
        {
            var result = work();
            success = true;
            return result;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try
            {
                cleanup(success);
            }
            catch (Exception cleanupError) when (failure is not null)
            {
                throw new AggregateException("Recognition and native cleanup failed.", failure, cleanupError);
            }
        }
    }

    public static void Cleanup(bool operationSucceeded, Action? reset, Action dispose, Action invalidate)
    {
        Run(() =>
        {
            reset?.Invoke();
            return true;
        }, resetSucceeded =>
        {
            Run(() =>
            {
                dispose();
                return true;
            }, disposed =>
            {
                if (!operationSucceeded || !resetSucceeded || !disposed)
                    invalidate();
            });
        });
    }
}
