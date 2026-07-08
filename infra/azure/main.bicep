// One-shot Bicep template for the Pharmacy Poster stack on Azure.
// Usage:
//   az deployment group create \
//     --resource-group rg-pharmacyposter \
//     --template-file infra/azure/main.bicep \
//     --parameters @infra/azure/main.parameters.json
//
// Provisions:
//   - Azure Container Registry (ACR)
//   - Azure Cosmos DB account with Mongo API
//   - Azure Storage account + blob container
//   - App Service Plan (Linux)
//   - Two App Services for Containers (api + web)
//
// You'll still need to: push images to ACR, point the App Services at them,
// and set sensitive App Settings (Jwt__Secret, SMTP creds, etc.).

@description('Region for all resources.')
param location string = resourceGroup().location

@description('Short prefix used in resource names (lowercase, no symbols).')
@minLength(3)
@maxLength(11)
param namePrefix string = 'pharmposter'

@description('App Service plan SKU.')
@allowed([ 'B1', 'B2', 'P1v3', 'P2v3' ])
param appServiceSku string = 'B1'

@description('Cosmos DB throughput offer (RU/s) for the shared database.')
param cosmosThroughput int = 400

@secure()
@description('Initial JWT signing secret (64 chars random recommended).')
param jwtSecret string

@description('Public host of the web app (used by the API for CORS).')
param webHostname string = '${namePrefix}-web.azurewebsites.net'

var acrName = '${namePrefix}acr'
var cosmosName = '${namePrefix}-cosmos'
var storageName = '${namePrefix}store'
var planName = '${namePrefix}-plan'
var apiName = '${namePrefix}-api'
var webName = '${namePrefix}-web'

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: true }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosName
  location: location
  kind: 'MongoDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    apiProperties: { serverVersion: '7.0' }
    locations: [ { locationName: location } ]
    capabilities: [
      { name: 'EnableMongo' }
      { name: 'DisableRateLimitingResponses' }
    ]
  }
}

resource cosmosDb 'Microsoft.DocumentDB/databaseAccounts/mongodbDatabases@2024-05-15' = {
  parent: cosmos
  name: 'acme'
  properties: {
    resource: { id: 'acme' }
    options: { throughput: cosmosThroughput }
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'canvas-assets'
  properties: { publicAccess: 'None' }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: { name: appServiceSku }
  kind: 'linux'
  properties: { reserved: true }
}

var cosmosConnString = cosmos.listConnectionStrings().connectionStrings[0].connectionString
var storageConnString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: apiName
  location: location
  kind: 'app,linux,container'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOCKER|${acr.properties.loginServer}/acme-api:latest'
      acrUseManagedIdentityCreds: false
      appSettings: [
        { name: 'WEBSITES_PORT', value: '8080' }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'DOCKER_REGISTRY_SERVER_URL', value: 'https://${acr.properties.loginServer}' }
        { name: 'DOCKER_REGISTRY_SERVER_USERNAME', value: acr.listCredentials().username }
        { name: 'DOCKER_REGISTRY_SERVER_PASSWORD', value: acr.listCredentials().passwords[0].value }
        { name: 'Mongo__ConnectionString', value: cosmosConnString }
        { name: 'Mongo__Database', value: 'acme' }
        { name: 'Jwt__Secret', value: jwtSecret }
        { name: 'Jwt__Issuer', value: 'acme-api' }
        { name: 'Jwt__Audience', value: 'acme-clients' }
        { name: 'Jwt__ExpiryMinutes', value: '60' }
        { name: 'Auth__RefreshTokenDays', value: '30' }
        { name: 'AzureBlob__ConnectionString', value: storageConnString }
        { name: 'AzureBlob__Container', value: 'canvas-assets' }
        { name: 'Cors__Origins__0', value: 'https://${webHostname}' }
        { name: 'Sonar__UseMock', value: 'true' }
        // Messaging: default to mock. Set Mailjet secrets in App Service to go live:
        //   Messaging__UseMock          = false
        //   Messaging__Provider         = mailjet
        //   Messaging__Mailjet__ApiKey  = <public key>
        //   Messaging__Mailjet__SecretKey = <private key>  (put this in Key Vault!)
        //   Messaging__Mailjet__FromAddress = no-reply@your-verified-domain
        { name: 'Messaging__UseMock', value: 'true' }
        { name: 'Messaging__Provider', value: 'mailjet' }
      ]
    }
  }
}

resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: webName
  location: location
  kind: 'app,linux,container'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOCKER|${acr.properties.loginServer}/acme-web:latest'
      acrUseManagedIdentityCreds: false
      appSettings: [
        { name: 'WEBSITES_PORT', value: '3000' }
        { name: 'NODE_ENV', value: 'production' }
        { name: 'COOKIE_SECURE', value: 'true' }
        { name: 'DOCKER_REGISTRY_SERVER_URL', value: 'https://${acr.properties.loginServer}' }
        { name: 'DOCKER_REGISTRY_SERVER_USERNAME', value: acr.listCredentials().username }
        { name: 'DOCKER_REGISTRY_SERVER_PASSWORD', value: acr.listCredentials().passwords[0].value }
        { name: 'NEXT_PUBLIC_API_URL', value: 'https://${api.properties.defaultHostName}' }
        { name: 'API_INTERNAL_URL', value: 'https://${api.properties.defaultHostName}' }
      ]
    }
  }
}

output apiHostname string = api.properties.defaultHostName
output webHostname string = web.properties.defaultHostName
output acrLoginServer string = acr.properties.loginServer
output cosmosName string = cosmos.name
output storageName string = storage.name
