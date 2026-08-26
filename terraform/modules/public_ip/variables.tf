variable "name" {
  description = "Name of the optional public IP."
  type        = string
}
variable "resource_group_name" {
  description = "Name of the shared resource group."
  type        = string
}
variable "location" {
  description = "Azure region for the public IP."
  type        = string
}
variable "tags" {
  description = "Tags applied to the public IP."
  type        = map(string)
  default     = {}
}
