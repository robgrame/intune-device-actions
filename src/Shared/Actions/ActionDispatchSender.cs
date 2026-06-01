using Azure.Storage.Queues;

namespace IntuneDeviceActions.Actions;

/// <summary>
/// Thin DI wrapper around a <see cref="QueueClient"/> that targets the
/// <c>action-dispatch</c> queue. Wrapping in a dedicated type avoids
/// ambiguity with the existing wipe-requests <see cref="QueueClient"/>
/// registration without resorting to keyed services.
/// </summary>
public sealed class ActionDispatchSender
{
    public QueueClient Client { get; }
    public ActionDispatchSender(QueueClient client) => Client = client;
}
