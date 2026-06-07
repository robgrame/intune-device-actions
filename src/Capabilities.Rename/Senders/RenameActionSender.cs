using Azure.Messaging.ServiceBus;

namespace IntuneDeviceActions.Capabilities.Rename.Senders;

/// <summary>
/// Thin DI wrapper around a <see cref="ServiceBusSender"/> targeting the
/// per-capability <c>rename-action</c> Service Bus queue. Mirrors
/// <c>BitLockerActionSender</c> — wrapper type avoids ambiguity with the
/// other ServiceBusSender registrations (action-requests, action-dispatch,
/// wipe-action, autopilot-action, bitlocker-action).
/// </summary>
public sealed class RenameActionSender
{
    public ServiceBusSender Sender { get; }
    public RenameActionSender(ServiceBusSender sender) => Sender = sender;
}
