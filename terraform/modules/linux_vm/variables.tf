variable "name" {
  description = "Name of the service Linux VM."
  type        = string
}
variable "resource_group_name" {
  description = "Name of the shared resource group."
  type        = string
}
variable "location" {
  description = "Azure region for the VM."
  type        = string
}
variable "size" {
  description = "Azure VM size."
  type        = string
}
variable "source_image_sku" {
  description = "Canonical Ubuntu image SKU compatible with the selected VM architecture."
  type        = string
  default     = "22_04-lts-gen2"
}
variable "admin_username" {
  description = "Administrator username for the VM."
  type        = string
}
variable "ssh_public_key" {
  description = "SSH public key for VM administration."
  type        = string
}
variable "network_interface_id" {
  description = "Resource ID of the VM NIC."
  type        = string
}
variable "tags" {
  description = "Tags applied to the VM."
  type        = map(string)
  default     = {}
}
