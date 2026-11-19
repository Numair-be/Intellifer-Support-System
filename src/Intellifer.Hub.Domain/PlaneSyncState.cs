namespace Intellifer.Hub.Domain;

/// <summary>Where a message is in the queue to Plane. Messages that never go to Plane have no state.</summary>
public enum PlaneSyncState
{
    Pending = 1,
    Sent = 2,

    /// <summary>Gave up after the maximum number of attempts. Needs a manual look (dead letter).</summary>
    Failed = 3,

    /// <summary>Not sent on purpose: the sender is not a client contact of the Plane workspace's project.</summary>
    Skipped = 4
}
