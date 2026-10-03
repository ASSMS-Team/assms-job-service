locals {
  github_cd_principal_id = try(data.terraform_remote_state.platform[0].outputs.github_cd_principal_id, null)
}

resource "azurerm_role_assignment" "github_nsg" {
  count = local.github_cd_principal_id == null ? 0 : 1

  scope                            = module.nsg.id
  role_definition_name             = "Network Contributor"
  principal_id                     = local.github_cd_principal_id
  skip_service_principal_aad_check = true
}
