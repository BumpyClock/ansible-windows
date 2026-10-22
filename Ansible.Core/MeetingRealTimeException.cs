namespace Ansible.Core;

/// <summary>
/// Auto qualification measured the selected device slower than a live meeting tolerates.
/// Unconfigured meetings continue down the documented model order; an explicit choice fails Start.
/// </summary>
public sealed class MeetingRealTimeException(string modelId, NativeBackend backend, double realTimeFactor, double maximum,
    string? earlierCandidates = null)
    : InvalidOperationException($"{modelId} measured {realTimeFactor:F2}x real time on {backend} with the public sample; " +
        $"live meetings need at most {maximum:F2}x." + (earlierCandidates is null ? "" : " Earlier candidates: " + earlierCandidates))
{
    public string ModelId { get; } = modelId;
    public NativeBackend Backend { get; } = backend;
    public double RealTimeFactor { get; } = realTimeFactor;
    public double Maximum { get; } = maximum;
}
