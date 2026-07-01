// ─────────────────────────────────────────────────────────────────────────────
// Additive module — AD object cleanup queue for the rename hybrid worker.
//
// This module is INTENTIONALLY standalone and does NOT modify infra/main.bicep,
// per the repository architecture rule (a new/extended capability integrates
// additively). Deploy it into the SAME resource group as main.bicep AFTER the
// main deployment, pointing at the existing Service Bus namespace and the
// rename Function App's user-assigned managed identity.
//
// It provisions:
//   1. the `ad-object-cleanup` queue on the existing Service Bus namespace;
//   2. a `worker-listen` Listen-only SAS authorization rule on that queue
//      (the on-prem hybrid worker authenticates with this SAS key);
//   3. an "Azure Service Bus Data Sender" role assignment on the queue for the
//      rename Function App UAMI, so the rename capability can enqueue via
//      managed identity.
//
// Example (az CLI):
//   az deployment group create -g <rg> \
//     --template-file infra/ad-object-cleanup.bicep \
//     --parameters sbNamespaceName=<ns> renameUamiPrincipalId=<guid>
//
// Retrieve the worker's Listen key post-deploy:
//   az servicebus queue authorization-rule keys list -g <rg> \
//     --namespace-name <ns> --queue-name ad-object-cleanup --name worker-listen \
//     --query primaryKey -o tsv
// ─────────────────────────────────────────────────────────────────────────────

@description('Name of the EXISTING Service Bus namespace created by main.bicep (e.g. idactions-sb-dev).')
param sbNamespaceName string

@description('Queue name the rename capability publishes AD-object-cleanup messages to.')
param queueName string = 'ad-object-cleanup'

@description('Name of the Listen-only SAS authorization rule the on-prem hybrid worker uses.')
param listenRuleName string = 'worker-listen'

@description('principalId (objectId) of the rename Function App user-assigned managed identity that enqueues cleanup messages. Leave empty to skip the Send role assignment (grant it manually).')
param renameUamiPrincipalId string = ''

// "Azure Service Bus Data Sender" — same role id used by main.bicep.
var sbDataSender = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: sbNamespaceName
}

// 1) The dedicated queue consumed by the on-prem hybrid worker. Mirrors the
//    per-capability queue settings (5m lock, 1d TTL, dead-letter on 5 retries).
resource sbQueueAdCleanup 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: sbNamespace
  name: queueName
  properties: {
    lockDuration: 'PT5M'
    defaultMessageTimeToLive: 'P1D'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    enablePartitioning: false
    requiresSession: false
    requiresDuplicateDetection: false
  }
}

// 2) Listen-only SAS rule for the on-prem worker (it cannot use managed identity
//    from outside Azure, so it authenticates with this SAS key over REST).
resource sbQueueListenRule 'Microsoft.ServiceBus/namespaces/queues/authorizationRules@2022-10-01-preview' = {
  parent: sbQueueAdCleanup
  name: listenRuleName
  properties: {
    rights: [
      'Listen'
    ]
  }
}

// 3) Rename Function App UAMI → Data Sender on the queue (managed-identity send).
resource raRenameSbSendAdCleanup 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(renameUamiPrincipalId)) {
  name: guid(sbQueueAdCleanup.id, renameUamiPrincipalId, 'sb-send')
  scope: sbQueueAdCleanup
  properties: {
    roleDefinitionId: sbDataSender
    principalId: renameUamiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('The AD-object-cleanup queue resource id.')
output queueId string = sbQueueAdCleanup.id

@description('Resource id of the Listen SAS authorization rule (use keys list to fetch the worker SAS key).')
output listenRuleId string = sbQueueListenRule.id

@description('Fully-qualified Service Bus namespace host for the worker -Namespace parameter.')
output namespaceHost string = '${sbNamespaceName}.servicebus.windows.net'
