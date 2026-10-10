@description('Azure region for fleet resources.')
param location string = resourceGroup().location

@description('Container image for capybarad and broker.')
param capybaraImage string

@description('No public ingress by default; keep true only behind private networking.')
param internalOnly bool = true

var namePrefix = 'capybara'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-mi'
  location: location
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-aca'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource vault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: '${namePrefix}-kv-${uniqueString(resourceGroup().id)}'
  location: location
  properties: {
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

resource capybara 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-capybarad'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: {
        external: !internalOnly
        targetPort: 8080
        transport: 'http'
      }
      secrets: [
        {
          name: 'github-token'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/github-token'
          identity: identity.id
        }
      ]
    }
    template: {
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
      containers: [
        {
          name: 'capybarad'
          image: capybaraImage
          env: [
            {
              name: 'Capybara__Runtime__AutoStart'
              value: 'true'
            }
            {
              name: 'GitHubToken'
              secretRef: 'github-token'
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
        {
          name: 'broker'
          image: capybaraImage
          command: [ '/app/capybara-broker' ]
          resources: {
            cpu: json('0.25')
            memory: '512Mi'
          }
        }
      ]
    }
  }
}

// ACA Sandboxes for agent workspaces are provisioned separately by AcaSandboxHost with default-deny egress Transform rules.
output managedIdentityClientId string = identity.properties.clientId
output keyVaultUri string = vault.properties.vaultUri
output containerAppName string = capybara.name
