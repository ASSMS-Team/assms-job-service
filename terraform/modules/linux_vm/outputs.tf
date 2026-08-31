output "id" {
  description = "Resource ID of the service VM."
  value       = azurerm_linux_virtual_machine.this.id
}
output "name" {
  description = "Name of the service VM."
  value       = azurerm_linux_virtual_machine.this.name
}
