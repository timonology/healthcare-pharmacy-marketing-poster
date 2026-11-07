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
//   - Three App Services for Containers (api + web + ai-service)
//
// You'll still need to: push images to ACR, point the App Services at them,
// and pass the sensitive values as parameters (jwtSecret, Mailjet, the
// social* / meta* / google* keys, AI keys). Do not add API settings by hand
// in the portal: this template's appSettings are the complete list, and a
// redeploy removes anything that is not in it. The ai-service in particular needs the video
// agent secrets set — see AZURE.md for the full env-var table.

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

@secure()
@description('Key the web app signs the browser address with, so the API can trust it (security review R2-03). Same value is given to both apps. Empty leaves it off, and the API warns at boot. Generate with: openssl rand -base64 48')
param clientIpSigningKey string = ''

@secure()
@description('Readiness token for GET /api/health/ready (pnpm smoke). Empty: the endpoint is 404.')
param readinessToken string = ''

@secure()
@description('Master key for signed public links (emailed poster images, video downloads), separate from the JWT secret so that can be rotated without breaking links (security review R2-13). Empty falls back to the JWT secret. openssl rand -base64 48')
param linkSigningSecret string = ''

@description('Public host of the web app (used by the API for CORS).')
param webHostname string = '${namePrefix}-web.azurewebsites.net'

@description('Web Content-Security-Policy: report-only (default), enforce, or off.')
@allowed(['report-only', 'enforce', 'off'])
param cspMode string = 'report-only'

// ─── Environment shape ───────────────────────────────────────────────
// The one deliberate difference between production and the QA environment.
//
// 'Staging' is NOT Development: the diagnostics endpoints stay 404, the
// developer exception page stays off, and HTTPS metadata is still required.
// What it unlocks is DevUserSeeder, and only in combination with
// seedDemoAccounts below. See docs/TEST-ENVIRONMENT.md.

@description('ASP.NET Core environment name. Production for live; Staging for the QA environment.')
@allowed([ 'Production', 'Staging' ])
param aspNetEnvironment string = 'Production'

@description('Create the demo pharmacy accounts on boot. Ignored unless aspNetEnvironment is Staging.')
param seedDemoAccounts bool = false

@secure()
@description('Password for the seeded demo accounts. Blank keeps the repo default, which is public knowledge.')
param demoAccountPassword string = ''

@secure()
@description('Bootstrap SuperAdmin password. Must be 16+ characters outside Development or the seeder refuses.')
param superAdminPassword string = ''

@description('Bootstrap SuperAdmin email address.')
param superAdminEmail string = 'superadmin@sonarinformatics.com'

// ─── Email ───────────────────────────────────────────────────────────

@description('Send real email. False routes everything to the mock sender.')
param sendRealEmail bool = false

@secure()
@description('Mailjet public API key.')
param mailjetApiKey string = ''

@secure()
@description('Mailjet private secret key.')
param mailjetSecretKey string = ''

@description('Verified sender address. Use a distinct subdomain per environment to protect sender reputation.')
param mailFromAddress string = ''

@description('Sender display name shown in the inbox.')
param mailFromName string = 'Sonar Marketing'

// ─── Video-agent secrets ─────────────────────────────────────────────
// All three are required for the ai-service to render videos. Leaving
// them blank at deploy time provisions the App Service with empty
// values — the container will boot but the worker refuses to start.
// You can also set these post-deploy via the portal or `az webapp
// config appsettings set`.

@secure()
@description('Anthropic API key for Claude script generation. Optional at deploy time.')
param anthropicApiKey string = ''

@secure()
@description('Tavily API key for the research node. Optional at deploy time.')
param tavilyApiKey string = ''

@secure()
@description('Pexels API key for stock B-roll. Optional at deploy time.')
param pexelsApiKey string = ''

@secure()
@description('ElevenLabs API key. Blank = fall back to Edge-TTS (free).')
param elevenLabsApiKey string = ''

@secure()
@description('Replicate API token. Blank = disable AI imagery (Pro feature).')
param replicateApiToken string = ''

// ─── Social publishing ───────────────────────────────────────────────
// App Service treats the appSettings below as the complete list: anything set
// by hand in the portal is removed by the next deployment of this template.
// So the social settings live here, as secure parameters, rather than being
// added afterwards. Values are passed at deploy time and never committed.
// Each group is only written when its values are supplied, so a deployment
// without them leaves social switched off rather than half-configured.

@secure()
@description('Credential encryption key: base64 of 32 random bytes (openssl rand -base64 32). Losing it means every pharmacy reconnects every account — keep a copy in Key Vault or a password manager.')
param socialEncryptionKey string = ''

@description('Version number of socialEncryptionKey. Bump it when rotating.')
param socialEncryptionKeyVersion int = 1

@secure()
@description('The previous encryption key, while rotating: still read, never written. Blank once nothing is encrypted under it.')
param socialEncryptionPreviousKey string = ''

@description('Version number of socialEncryptionPreviousKey.')
param socialEncryptionPreviousKeyVersion int = 0

@description('Meta app id (Facebook and Instagram).')
param metaAppId string = ''

@secure()
@description('Meta app secret.')
param metaAppSecret string = ''

@description('Meta Graph API version. Test a publish to a test Page before changing it.')
param metaGraphVersion string = 'v26.0'

@description('Google OAuth client id.')
param googleClientId string = ''

@secure()
@description('Google OAuth client secret.')
param googleClientSecret string = ''

@secure()
@description('Legacy, leave blank: Google sunset developer tokens on 10 Sep 2026 (access comes from the OAuth client\'s Cloud project).')
param googleAdsDeveloperToken string = ''

@description('Google Ads API version. Test against the test account before changing it.')
param googleAdsApiVersion string = 'v25'

@description('true: Google validates every campaign creation and creates nothing. For staging.')
param googleAdsValidateOnly bool = false

@description('LinkedIn app client id (developer.linkedin.com → app → Auth).')
param linkedInClientId string = ''

@secure()
@description('LinkedIn app client secret.')
param linkedInClientSecret string = ''

@description('Offer the signed-in person\'s own profile. false only for an app holding Community Management alone.')
param linkedInProfilesEnabled bool = true

@description('Offer company Pages. Only once LinkedIn has approved the app for the Community Management API.')
param linkedInPagesEnabled bool = false

@description('LinkedIn-Version header (YYYYMM). Post once to a test profile after changing it.')
param linkedInApiVersion string = '202610'

@description('TikTok app client key (developers.tiktok.com → your app).')
param tikTokClientKey string = ''

@secure()
@description('TikTok app client secret.')
param tikTokClientSecret string = ''

@description('X app OAuth 2.0 Client ID (console.x.com → your app → Keys and tokens).')
param xClientId string = ''

@secure()
@description('X app OAuth 2.0 Client Secret.')
param xClientSecret string = ''

@description('Docker Hub image the ai-service App Service pulls on boot.')
param aiImage string = 'infosonarinformatics/sonar-marketing-ai:dev'

// The api and web images default to the ACR this template provisions, which is
// how production was built. Supplying a full Docker Hub reference instead lets
// an environment be fed by `pnpm ship:<env>` — that pushes to Docker Hub, and
// an App Service holding ACR credentials cannot pull a docker.io image.
//
// The registry credential settings follow whichever is chosen; see
// apiRegistrySettings / webRegistrySettings below.

@description('Full image for the API app service. Blank = the ACR image this template provisions.')
param apiImage string = ''

@description('Full image for the Web app service. Blank = the ACR image this template provisions.')
param webImage string = ''

@description('Optional Docker Hub username. Only needed if the ai image is a private repo.')
param dockerHubUsername string = ''

@secure()
@description('Optional Docker Hub password/PAT. Only needed if the ai image is a private repo.')
param dockerHubPassword string = ''

var acrName = '${namePrefix}acr'
var cosmosName = '${namePrefix}-cosmos'
var storageName = '${namePrefix}store'
var planName = '${namePrefix}-plan'
var apiName = '${namePrefix}-api'
var webName = '${namePrefix}-web'
var aiName  = '${namePrefix}-ai'

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

// Email settings, assembled rather than listed, so the mock path carries no
// half-set credentials and the real path cannot be enabled with blanks.
var mailSettings = sendRealEmail ? [
  { name: 'Messaging__UseMock', value: 'false' }
  { name: 'Messaging__Provider', value: 'mailjet' }
  { name: 'Messaging__Mailjet__ApiKey', value: mailjetApiKey }
  { name: 'Messaging__Mailjet__SecretKey', value: mailjetSecretKey }
  { name: 'Messaging__Mailjet__FromAddress', value: mailFromAddress }
  { name: 'Messaging__Mailjet__FromName', value: mailFromName }
  { name: 'Messaging__Mailjet__SandboxMode', value: 'false' }
] : [
  { name: 'Messaging__UseMock', value: 'true' }
  { name: 'Messaging__Provider', value: 'mailjet' }
]

// Demo data. Both halves are required: the environment must be Staging AND the
// flag must be true, so neither alone can populate a database.
var seedSettings = (aspNetEnvironment == 'Staging' && seedDemoAccounts) ? concat([
  { name: 'DevData__SeedUsers', value: 'true' }
], empty(demoAccountPassword) ? [] : [
  { name: 'DevData__Password', value: demoAccountPassword }
]) : [
  { name: 'DevData__SeedUsers', value: 'false' }
]

// SuperAdmin bootstrap. Left disabled unless a password is supplied — the
// seeder itself refuses anything under 16 characters outside Development.
var superAdminSettings = empty(superAdminPassword) ? [
  { name: 'SuperAdmin__Enabled', value: 'false' }
] : [
  { name: 'SuperAdmin__Enabled', value: 'true' }
  { name: 'SuperAdmin__Email', value: superAdminEmail }
  { name: 'SuperAdmin__Password', value: superAdminPassword }
]

// Social publishing. The worker is only enabled with an encryption key, since
// without one it can neither read nor store a credential.
var socialEncryptionSettings = empty(socialEncryptionKey) ? [
  { name: 'Social__Worker__Enabled', value: 'false' }
] : concat([
  { name: 'Social__Worker__Enabled', value: 'true' }
  { name: 'Social__Encryption__ActiveVersion', value: string(socialEncryptionKeyVersion) }
  { name: 'Social__Encryption__Keys__0__Version', value: string(socialEncryptionKeyVersion) }
  { name: 'Social__Encryption__Keys__0__Key', value: socialEncryptionKey }
], empty(socialEncryptionPreviousKey) ? [] : [
  { name: 'Social__Encryption__Keys__1__Version', value: string(socialEncryptionPreviousKeyVersion) }
  { name: 'Social__Encryption__Keys__1__Key', value: socialEncryptionPreviousKey }
])

var metaSettings = (empty(metaAppId) || empty(metaAppSecret)) ? [] : [
  { name: 'Social__Meta__AppId', value: metaAppId }
  { name: 'Social__Meta__AppSecret', value: metaAppSecret }
  { name: 'Social__Meta__GraphVersion', value: metaGraphVersion }
]

// Google Ads needs only the OAuth client: developer tokens were sunset on
// 10 September 2026 and access now comes from the client's Cloud project.
// googleAdsDeveloperToken is kept for old parameter files and best left empty.
var googleSettings = concat((empty(googleClientId) || empty(googleClientSecret)) ? [] : [
  { name: 'Social__Google__ClientId', value: googleClientId }
  { name: 'Social__Google__ClientSecret', value: googleClientSecret }
  { name: 'Social__Google__AdsApiVersion', value: googleAdsApiVersion }
  { name: 'Social__Google__AdsValidateOnly', value: string(googleAdsValidateOnly) }
], empty(googleAdsDeveloperToken) ? [] : [
  { name: 'Social__Google__AdsDeveloperToken', value: googleAdsDeveloperToken }
])

var linkedInSettings = (empty(linkedInClientId) || empty(linkedInClientSecret)) ? [] : [
  { name: 'Social__LinkedIn__ClientId', value: linkedInClientId }
  { name: 'Social__LinkedIn__ClientSecret', value: linkedInClientSecret }
  { name: 'Social__LinkedIn__ProfilesEnabled', value: string(linkedInProfilesEnabled) }
  { name: 'Social__LinkedIn__PagesEnabled', value: string(linkedInPagesEnabled) }
  { name: 'Social__LinkedIn__ApiVersion', value: linkedInApiVersion }
]

var tikTokSettings = (empty(tikTokClientKey) || empty(tikTokClientSecret)) ? [] : [
  { name: 'Social__TikTok__ClientKey', value: tikTokClientKey }
  { name: 'Social__TikTok__ClientSecret', value: tikTokClientSecret }
]

var xSettings = (empty(xClientId) || empty(xClientSecret)) ? [] : [
  { name: 'Social__X__ClientId', value: xClientId }
  { name: 'Social__X__ClientSecret', value: xClientSecret }
]

var socialSettings = concat(socialEncryptionSettings, metaSettings, googleSettings, linkedInSettings, tikTokSettings, xSettings)

// Which image each app service pulls, and the credentials that go with it.
var apiImageRef = empty(apiImage) ? '${acr.properties.loginServer}/acme-api:latest' : apiImage
var webImageRef = empty(webImage) ? '${acr.properties.loginServer}/acme-web:latest' : webImage

var acrRegistrySettings = [
  { name: 'DOCKER_REGISTRY_SERVER_URL', value: 'https://${acr.properties.loginServer}' }
  { name: 'DOCKER_REGISTRY_SERVER_USERNAME', value: acr.listCredentials().username }
  { name: 'DOCKER_REGISTRY_SERVER_PASSWORD', value: acr.listCredentials().passwords[0].value }
]

// Public Docker Hub images need no credentials, and leaving the ACR ones in
// place would make App Service authenticate to the wrong registry.
var hubRegistrySettings = [
  { name: 'DOCKER_REGISTRY_SERVER_URL', value: 'https://index.docker.io' }
]

var apiRegistrySettings = empty(apiImage) ? acrRegistrySettings : hubRegistrySettings
var webRegistrySettings = empty(webImage) ? acrRegistrySettings : hubRegistrySettings

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
      // Security review R2-10: no FTP, TLS 1.2 minimum on the app and its
      // management (SCM) site.
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      linuxFxVersion: 'DOCKER|${apiImageRef}'
      acrUseManagedIdentityCreds: false
      appSettings: concat(mailSettings, seedSettings, superAdminSettings, socialSettings, apiRegistrySettings, [
        { name: 'WEBSITES_PORT', value: '8080' }
        { name: 'ASPNETCORE_ENVIRONMENT', value: aspNetEnvironment }
        { name: 'Mongo__ConnectionString', value: cosmosConnString }
        { name: 'Mongo__Database', value: 'acme' }
        { name: 'Jwt__Secret', value: jwtSecret }
        { name: 'Jwt__Issuer', value: 'acme-api' }
        { name: 'Jwt__Audience', value: 'acme-clients' }
        { name: 'Jwt__ExpiryMinutes', value: '20' } // R2-09: matches the web app's 20-minute idle sign-out; it refreshes every 10 while active
        { name: 'Auth__RefreshTokenDays', value: '30' }
        { name: 'AzureBlob__ConnectionString', value: storageConnString }
        { name: 'AzureBlob__Container', value: 'canvas-assets' }
        { name: 'Cors__Origins__0', value: 'https://${webHostname}' }
        // Where links in emails point, and the only OAuth redirect the API
        // will use (https://<web>/social/callback/meta|google) — register
        // exactly those with Meta and Google.
        { name: 'Public__WebBaseUrl', value: 'https://${webHostname}' }
        { name: 'Sonar__UseMock', value: 'true' }
        { name: 'Edge__ClientIpKey', value: clientIpSigningKey }
        { name: 'Readiness__Token', value: readinessToken }
        { name: 'Signing__LinkSecret', value: linkSigningSecret }
        // Messaging, demo seeding and the SuperAdmin bootstrap are prepended
        // above via mailSettings / seedSettings / superAdminSettings.
      ])
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
      // Security review R2-10: no FTP, TLS 1.2 minimum on the app and its
      // management (SCM) site.
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      linuxFxVersion: 'DOCKER|${webImageRef}'
      acrUseManagedIdentityCreds: false
      appSettings: concat(webRegistrySettings, [
        { name: 'WEBSITES_PORT', value: '3000' }
        { name: 'NODE_ENV', value: 'production' }
        { name: 'COOKIE_SECURE', value: 'true' }
        { name: 'NEXT_PUBLIC_API_URL', value: 'https://${api.properties.defaultHostName}' }
        { name: 'API_INTERNAL_URL', value: 'https://${api.properties.defaultHostName}' }
        { name: 'CLIENT_IP_SIGNING_KEY', value: clientIpSigningKey }
        // The public address for canonical links, the sitemap, robots.txt and
        // link previews. Set webHostname to the custom domain once it is live.
        { name: 'SITE_URL', value: 'https://${webHostname}' }
        // Content-Security-Policy: report-only until the reports at
        // /api/csp-report are clean, then 'enforce' (lib/csp.ts).
        { name: 'CSP_MODE', value: cspMode }
      ])
    }
  }
}

// Docker Hub-only settings — appended conditionally so anonymous pulls
// work without a phantom username field on the App Service.
var dockerHubSettings = empty(dockerHubUsername) ? [] : [
  { name: 'DOCKER_REGISTRY_SERVER_URL', value: 'https://index.docker.io' }
  { name: 'DOCKER_REGISTRY_SERVER_USERNAME', value: dockerHubUsername }
  { name: 'DOCKER_REGISTRY_SERVER_PASSWORD', value: dockerHubPassword }
]

resource ai 'Microsoft.Web/sites@2023-12-01' = {
  name: aiName
  location: location
  kind: 'app,linux,container'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      // Security review R2-10: no FTP, TLS 1.2 minimum on the app and its
      // management (SCM) site.
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      // Pull from Docker Hub — deploy.sh pushes `infosonarinformatics/
      // sonar-marketing-ai:dev` there. If the repo is private, set
      // dockerHubUsername + dockerHubPassword at deploy time.
      linuxFxVersion: 'DOCKER|${aiImage}'
      acrUseManagedIdentityCreds: false
      // App Service defaults idle timeout to ~20 min; the video worker
      // is long-lived so we keep the container warm with AlwaysOn.
      alwaysOn: true
      appSettings: concat(dockerHubSettings, [
        { name: 'WEBSITES_PORT', value: '8000' }
        // Shared with the .NET API — same Mongo, same Blob container.
        { name: 'Mongo__ConnectionString', value: cosmosConnString }
        { name: 'Mongo__Database', value: 'acme' }
        { name: 'AzureBlob__ConnectionString', value: storageConnString }
        { name: 'AzureBlob__Container', value: 'canvas-assets' }
        // JWT — for parity with the API (not currently checked by the
        // video worker, but the canvas endpoint validates JWTs).
        { name: 'JWT_SECRET', value: jwtSecret }
        { name: 'JWT_ISSUER', value: 'acme-api' }
        { name: 'JWT_AUDIENCE', value: 'acme-clients' }
        // Video-agent secrets. Leaving any of these blank will make the
        // worker log a WARN and refuse to process jobs for that node.
        { name: 'ANTHROPIC_API_KEY', value: anthropicApiKey }
        { name: 'ANTHROPIC_MODEL', value: 'claude-haiku-4-5-20251001' }
        { name: 'TAVILY_API_KEY', value: tavilyApiKey }
        { name: 'PEXELS_API_KEY', value: pexelsApiKey }
        { name: 'ELEVENLABS_API_KEY', value: elevenLabsApiKey }
        { name: 'REPLICATE_API_TOKEN', value: replicateApiToken }
        { name: 'REPLICATE_FLUX_MODEL', value: 'black-forest-labs/flux-schnell' }
        // Cost caps (see apps/ai-service/app/config.py).
        { name: 'AI_IMAGERY_MAX_IMAGES_PER_JOB', value: '8' }
        { name: 'AI_IMAGERY_BUDGET_PER_JOB_USD', value: '0.05' }
        { name: 'AI_IMAGERY_COST_PER_IMAGE_USD', value: '0.003' }
        // Worker knobs.
        { name: 'VIDEO_WORKER_ENABLED', value: 'true' }
        { name: 'VIDEO_WORKER_POLL_SECONDS', value: '4' }
      ])
    }
  }
}

output apiHostname string = api.properties.defaultHostName
output webHostname string = web.properties.defaultHostName
output aiHostname string  = ai.properties.defaultHostName
output acrLoginServer string = acr.properties.loginServer
output cosmosName string = cosmos.name
output storageName string = storage.name


// Security review R2-10: no username/password FTP publishing on any of the
// three apps; deploys go through the container registry and `az`. Basic auth
// on the SCM (Kudu) site is left on for now because `az webapp log tail`, which
// the runbooks use, relies on it — turn it off once log streaming moves to
// Log Analytics or the CLI's Entra sign-in is confirmed to work.
resource apiNoBasicFtp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: api
  name: 'ftp'
  properties: { allow: false }
}
resource webNoBasicFtp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: web
  name: 'ftp'
  properties: { allow: false }
}
resource aiNoBasicFtp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: ai
  name: 'ftp'
  properties: { allow: false }
}
