data "terraform_remote_state" "platform" {
  count   = var.use_platform_remote_state ? 1 : 0
  backend = "azurerm"

  config = {
    resource_group_name  = var.platform_state_resource_group_name
    storage_account_name = var.platform_state_storage_account_name
    container_name       = var.platform_state_container_name
    key                  = var.platform_state_key
  }
}

locals {
  resource_group_name = coalesce(
    var.resource_group_name,
    try(data.terraform_remote_state.platform[0].outputs.resource_group_name, null)
  )
  services_subnet_id = coalesce(
    var.services_subnet_id,
    try(data.terraform_remote_state.platform[0].outputs.services_subnet_id, null)
  )

  security_rules = merge(
    var.enable_ssh && length(var.ssh_allowed_source_cidrs) > 0 ? {
      AllowSshFromApprovedCidrs = {
        priority                = 1000
        destination_port_ranges = ["22"]
        source_address_prefixes = var.ssh_allowed_source_cidrs
        description             = "Allow SSH only from approved administrator CIDRs."
      }
    } : {},
    var.enable_http_https && length(var.http_allowed_source_cidrs) > 0 ? {
      AllowHttpFromApprovedCidrs = {
        priority                = 1100
        destination_port_ranges = ["80"]
        source_address_prefixes = var.http_allowed_source_cidrs
        description             = "Allow HTTP only from explicitly approved CIDRs."
      }
      AllowHttpsFromApprovedCidrs = {
        priority                = 1110
        destination_port_ranges = ["443"]
        source_address_prefixes = var.http_allowed_source_cidrs
        description             = "Allow HTTPS only from explicitly approved CIDRs."
      }
    } : {}
  )
}

module "nsg" {
  source = "../../modules/nsg"

  name                = var.nsg_name
  resource_group_name = local.resource_group_name
  location            = var.location
  security_rules      = local.security_rules
  tags                = var.tags
}

module "public_ip" {
  count  = var.enable_public_ip ? 1 : 0
  source = "../../modules/public_ip"

  name                = var.public_ip_name
  resource_group_name = local.resource_group_name
  location            = var.location
  tags                = var.tags
}

module "nic" {
  source = "../../modules/nic"

  name                      = var.nic_name
  resource_group_name       = local.resource_group_name
  location                  = var.location
  subnet_id                 = local.services_subnet_id
  network_security_group_id = module.nsg.id
  public_ip_id              = try(module.public_ip[0].id, null)
  tags                      = var.tags
}

module "vm" {
  source = "../../modules/linux_vm"

  name                 = var.vm_name
  resource_group_name  = local.resource_group_name
  location             = var.location
  size                 = var.vm_size
  admin_username       = var.admin_username
  ssh_public_key       = var.ssh_public_key
  network_interface_id = module.nic.id
  tags                 = var.tags
}
