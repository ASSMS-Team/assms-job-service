variable "name" {
  description = "Name of the service NIC."
  type        = string
}
variable "resource_group_name" {
  description = "Name of the shared resource group."
  type        = string
}
variable "location" {
  description = "Azure region for the NIC."
  type        = string
}
variable "subnet_id" {
  description = "Resource ID of the shared services subnet."
  type        = string
}
variable "network_security_group_id" {
  description = "Resource ID of the service-specific NSG."
  type        = string
}
variable "public_ip_id" {
  description = "Optional public IP resource ID."
  type        = string
  default     = null
  nullable    = true
}
variable "tags" {
  description = "Tags applied to the NIC."
  type        = map(string)
  default     = {}
}
