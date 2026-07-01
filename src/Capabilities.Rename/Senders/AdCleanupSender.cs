using Azure.Messaging.ServiceBus;

namespace IntuneDeviceActions.Capabilities.Rename.Senders;

/// <summary>
/// Thin DI wrapper around a <see cref="ServiceBusSender"/> targeting the
/// dedicated <c>ad-object-cleanup</c> Service Bus queue consumed by the on-prem
/// hybrid worker. Wrapper type avoids ambiguity with the other
/// <see cref="ServiceBusSender"/> registrations (rename-action, action-dispatch, …).
/// </summary>
public sealed class AdCleanupSender
{
    public ServiceBusSender Sender { get; }
    public AdCleanupSender(ServiceBusSender sender) => Sender = sender;
}
