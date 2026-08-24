variable "name" {
  description = "Name of the service-specific NSG."
  type        = string
}
variable "resource_group_name" {
  description = "Name of the shared platform resource group."
  type        = string
}
variable "location" {
  description = "Azure region for the NSG."
  type        = string
}
variable "security_rules" {
  description = "Opt-in inbound rules keyed by rule name."
  type = map(object({
    priority                = number
    destination_port_ranges = list(string)
    source_address_prefixes = list(string)
    description             = string
  }))
  default = {}
}
variable "tags" {
  description = "Tags applied to the NSG."
  type        = map(string)
  default     = {}
}
