variable "environment" {
  description = "Deployment environment name."
  type        = string
  default     = "staging"
}
variable "location" {
  description = "Azure region for the service VM."
  type        = string
  default     = "southeastasia"
}
variable "use_platform_remote_state" {
  description = "Whether shared resource group and subnet values come from platform remote state."
  type        = bool
  default     = true
}
variable "platform_state_resource_group_name" {
  description = "Resource group containing the Terraform state Storage Account."
  type        = string
  default     = "rg-assms-tfstate"
}
variable "platform_state_storage_account_name" {
  description = "Globally unique Storage Account containing platform state."
  type        = string
}
variable "platform_state_container_name" {
  description = "Blob container containing Terraform states."
  type        = string
  default     = "tfstate"
}
variable "platform_state_key" {
  description = "Remote-state key for shared platform outputs."
  type        = string
  default     = "platform/staging.tfstate"
}
variable "resource_group_name" {
  description = "Optional direct shared resource-group override."
  type        = string
  default     = null
  nullable    = true
}
variable "services_subnet_id" {
  description = "Optional direct shared services-subnet ID override."
  type        = string
  default     = null
  nullable    = true
}
variable "vm_name" {
  description = "Name of the Job Service VM."
  type        = string
  default     = "vm-assms-job-staging"
}
variable "vm_size" {
  description = "Azure VM size for the service."
  type        = string
  default     = "Standard_B2pls_v2"
}
variable "source_image_sku" {
  description = "Canonical Ubuntu image SKU compatible with the selected VM architecture."
  type        = string
  default     = "22_04-lts-arm64"
}
variable "admin_username" {
  description = "Administrator username for the VM."
  type        = string
  default     = "assmsadmin"
}
variable "ssh_public_key" {
  description = "SSH public key used to administer the VM."
  type        = string
}
variable "nsg_name" {
  description = "Name of the service-specific NSG."
  type        = string
  default     = "nsg-assms-job-staging"
}
variable "nic_name" {
  description = "Name of the service NIC."
  type        = string
  default     = "nic-assms-job-staging"
}
variable "public_ip_name" {
  description = "Name of the optional service public IP."
  type        = string
  default     = "pip-assms-job-staging"
}
variable "public_ip_domain_name_label" {
  description = "Optional Azure Public IP DNS label for the staging API endpoint."
  type        = string
  default     = null
  nullable    = true
}
variable "enable_public_ip" {
  description = "Whether to create a public IP for the service VM."
  type        = bool
  default     = true
}
variable "enable_ssh" {
  description = "Whether to add an SSH inbound rule."
  type        = bool
  default     = false
}
variable "ssh_allowed_source_cidrs" {
  description = "CIDRs allowed to connect over SSH when enabled."
  type        = list(string)
  default     = []
  validation {
    condition     = alltrue([for cidr in var.ssh_allowed_source_cidrs : cidr != "0.0.0.0/0"])
    error_message = "Unrestricted SSH from 0.0.0.0/0 is not permitted."
  }
}
variable "enable_http_https" {
  description = "Whether to add HTTP and HTTPS inbound rules."
  type        = bool
  default     = false
}
variable "http_allowed_source_cidrs" {
  description = "CIDRs allowed to connect over HTTP/HTTPS when enabled. Public HTTP/HTTPS is permitted for the staging reverse proxy; port 8080 remains private."
  type        = list(string)
  default     = []
}
variable "tags" {
  description = "Tags applied to service infrastructure."
  type        = map(string)
  default = {
    Project     = "ASSMS"
    Environment = "staging"
    Service     = "Job Service"
    ManagedBy   = "Terraform"
  }
}
