#!/bin/sh
# Run once on the Mac after az login, using the subscription that owns the VM.
set -eu

resource_group=ecommerce-monthly-budget
vm_name=vm-ecommerce-demo
identity_name=id-ecommerce-github
vm_id=$(az vm show --resource-group "$resource_group" --name "$vm_name" --query id --output tsv)

az provider register --namespace Microsoft.ManagedIdentity --wait --output none
az identity create --resource-group "$resource_group" --name "$identity_name" \
    --location denmarkeast --output none
principal_id=$(az identity show --resource-group "$resource_group" --name "$identity_name" --query principalId --output tsv)

# Scope the identity to this single VM, not the subscription or resource group.
existing=$(az role assignment list --scope "$vm_id" \
    --query "[?principalId=='$principal_id' && roleDefinitionName=='Virtual Machine Contributor'] | length(@)" --output tsv)
if [ "$existing" = 0 ]; then
    az role assignment create --assignee-object-id "$principal_id" \
        --assignee-principal-type ServicePrincipal \
        --role 'Virtual Machine Contributor' --scope "$vm_id" --output none
fi

az identity federated-credential create --resource-group "$resource_group" \
    --identity-name "$identity_name" --name github-azure-demo \
    --issuer https://token.actions.githubusercontent.com \
    --subject repo:turingzhi/ecommerce:environment:azure-demo \
    --audiences api://AzureADTokenExchange --output none

printf 'Add these three values as GitHub azure-demo environment secrets:\n'
printf 'AZURE_CLIENT_ID='
az identity show --resource-group "$resource_group" --name "$identity_name" --query clientId --output tsv
printf 'AZURE_TENANT_ID='
az account show --query tenantId --output tsv
printf 'AZURE_SUBSCRIPTION_ID='
az account show --query id --output tsv
