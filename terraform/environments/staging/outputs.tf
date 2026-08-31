output "vm_id" {
  description = "Resource ID of the service VM."
  value       = module.vm.id
}
output "vm_name" {
  description = "Name of the service VM."
  value       = module.vm.name
}
output "private_ip" {
  description = "Private IP address of the service VM."
  value       = module.nic.private_ip_address
}
output "public_ip" {
  description = "Optional public IP address of the service VM."
  value       = try(module.public_ip[0].ip_address, null)
}
output "nic_id" {
  description = "Resource ID of the service NIC."
  value       = module.nic.id
}
