targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param namePrefix string = 'intwipe'

param location string = resourceGroup().location

@description('Tenant ID where Graph calls are made')
param graphTenantId string = subscription().tenantId

@description('Comma-separated list of trusted CA certificate thumbprints (root and/or intermediate). At least one must appear in the client certificate chain. Required unless trustedCaCertificatesBase64 is provided.')
param trustedCaThumbprints string = ''

@description('Comma-separated list of base64-encoded DER CA certificates loaded into a custom trust store (no dependency on the machine trust store). Optional alternative or complement to trustedCaThumbprints.')
@secure()
param trustedCaCertificatesBase64 string = ''

@description('Optional comma-separated list of leaf certificate thumbprints to pin (defense-in-depth). Empty disables leaf pinning.')
param allowedLeafThumbprints string = ''

@description('Enable CRL/OCSP revocation checks on the client certificate chain.')
param checkRevocation bool = false

@description('Revocation lookup mode: Online | Offline | NoCheck')
@allowed([ 'Online', 'Offline', 'NoCheck' ])
param revocationMode string = 'Online'

@description('Revocation scope: ExcludeRoot | EntireChain | EndCertificateOnly')
@allowed([ 'ExcludeRoot', 'EntireChain', 'EndCertificateOnly' ])
param revocationFlag string = 'ExcludeRoot'

@description('Require Client Authentication EKU (1.3.6.1.5.5.7.3.2) on the client certificate.')
param requireClientAuthEku bool = true

@description('Which claim from the client certificate identifies the device. SubjectCN expects CN=<entraDeviceId GUID>. Disabled turns off cert<->device binding (NOT recommended).')
@allowed([ 'SubjectCN', 'SanDns', 'SanUri', 'Disabled' ])
param deviceIdBindingClaim string = 'SubjectCN'

@description('Maximum acceptable clock skew (seconds) between client X-Request-Timestamp and server time.')
param maxTimestampSkewSeconds int = 300

@description('Object Id of the Entra ID security group whose member devices are authorized to self-wipe')
param allowedGroupId string

@description('Wipe options')
param keepEnrollmentData bool = false
param keepUserData bool = false

@description('Storage queue name for wipe requests')
param wipeQueueName string = 'wipe-requests'

@description('Blob container name used as the idempotency ledger for wipe operations')
param ledgerContainerName string = 'wipe-ledger'

var suffix = uniqueString(resourceGroup().id)
var storageNameRaw = toLower('${namePrefix}st${suffix}')
var storageName    = length(storageNameRaw) > 24 ? substring(storageNameRaw, 0, 24) : storageNameRaw
var funcName  = toLower('${namePrefix}-func-${suffix}')
var planName  = toLower('${namePrefix}-plan-${suffix}')
var aiName    = toLower('${namePrefix}-ai-${suffix}')
var lawName   = toLower('${namePrefix}-law-${suffix}')
var uamiName  = toLower('${namePrefix}-uami-${suffix}')

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: lawName
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource ai 'Microsoft.Insights/components@2020-02-02' = {
  name: aiName
  location: location
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: law.id }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource queueSvc 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource wipeQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueSvc
  name: wipeQueueName
}

resource blobSvc 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource ledgerContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobSvc
  name: ledgerContainerName
  properties: { publicAccess: 'None' }
}

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: { name: 'EP1', tier: 'ElasticPremium' }
  kind: 'elastic'
  properties: { maximumElasticWorkerCount: 5 }
}

resource func 'Microsoft.Web/sites@2023-12-01' = {
  name: funcName
  location: location
  kind: 'functionapp'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${uami.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientCertEnabled: true
    clientCertMode: 'Required'
    clientCertExclusionPaths: '/api/healthz'
    keyVaultReferenceIdentity: uami.id
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      netFrameworkVersion: 'v10.0'
      use32BitWorkerProcess: false
      scmIpSecurityRestrictionsUseMain: true
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME',    value: 'dotnet-isolated' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE',    value: '1' }
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential',  value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId',    value: uami.properties.clientId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: ai.properties.ConnectionString }
        { name: 'AZURE_CLIENT_ID', value: uami.properties.clientId }
        { name: 'Queue__WipeQueueName', value: wipeQueueName }
        { name: 'Idempotency__BlobContainer', value: ledgerContainerName }
        { name: 'Idempotency__StorageAccount', value: storage.name }
        { name: 'ClientCert__TrustedCaThumbprints',    value: trustedCaThumbprints }
        { name: 'ClientCert__TrustedCaCertificates',   value: trustedCaCertificatesBase64 }
        { name: 'ClientCert__AllowedLeafThumbprints',  value: allowedLeafThumbprints }
        { name: 'ClientCert__CheckRevocation',         value: string(checkRevocation) }
        { name: 'ClientCert__RevocationMode',          value: revocationMode }
        { name: 'ClientCert__RevocationFlag',          value: revocationFlag }
        { name: 'ClientCert__RequireClientAuthEku',    value: string(requireClientAuthEku) }
        { name: 'ClientCert__RequireClientCert',       value: 'true' }
        { name: 'ClientCert__TrustForwardedHeader',    value: 'false' }
        { name: 'ClientCert__DeviceIdBindingClaim',    value: deviceIdBindingClaim }
        { name: 'Replay__MaxTimestampSkewSeconds',     value: string(maxTimestampSkewSeconds) }
        { name: 'Graph__TenantId', value: graphTenantId }
        { name: 'Graph__ManagedIdentityClientId', value: uami.properties.clientId }
        { name: 'Wipe__AllowedGroupId',     value: allowedGroupId }
        { name: 'Wipe__KeepEnrollmentData', value: string(keepEnrollmentData) }
        { name: 'Wipe__KeepUserData',       value: string(keepUserData) }
      ]
    }
  }
}

// RBAC for UAMI on storage (least privilege: blob + queue only — no table)
var blobDataOwner        = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var queueDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')

resource raBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, uami.id, 'blob')
  scope: storage
  properties: { roleDefinitionId: blobDataOwner, principalId: uami.properties.principalId, principalType: 'ServicePrincipal' }
}
resource raQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, uami.id, 'queue')
  scope: storage
  properties: { roleDefinitionId: queueDataContributor, principalId: uami.properties.principalId, principalType: 'ServicePrincipal' }
}

output functionAppName string = func.name
output functionAppHostname string = func.properties.defaultHostName
output uamiClientId string = uami.properties.clientId
output uamiPrincipalId string = uami.properties.principalId
output storageAccount string = storage.name
output wipeQueueName string = wipeQueueName
output ledgerContainerName string = ledgerContainerName
