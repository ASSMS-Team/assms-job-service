output "id" {
  description = "Resource ID of the service NIC."
  value       = azurerm_network_interface.this.id
}
output "private_ip_address" {
  description = "Private IP address allocated to the service NIC."
  value       = azurerm_network_interface.this.private_ip_address
}
