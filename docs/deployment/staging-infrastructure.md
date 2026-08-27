# Job Service Staging Infrastructure

## Current Infrastructure

| Item | Value |
|---|---|
| VM | `vm-assms-job-staging` |
| Region | Southeast Asia |
| SKU / architecture | `Standard_B2pls_v2`, Arm64, 2 vCPU / 4 GiB |
| OS | Ubuntu 22.04 Arm64 with Standard_LRS OS disk |
| Private network | Primary services subnet `10.20.1.0/24`; assigned private IP `10.20.1.5` |
| Public IP | Static Standard resource exists for future controlled access |
| State key | `job-service/staging.tfstate` |
| Platform dependency | Reads `platform/staging.tfstate` |

Password authentication is disabled. The service NSG has no custom inbound rules; Azure default deny-inbound remains effective. SSH, public application ports, MySQL, and Kafka are not exposed publicly.

## Application Status

The Job API, lifecycle/work-record logic, Docker runtime, and Kafka producers/consumers are **not deployed**. The VM is currently deallocated for staging cost control.

## ARM64 Note

The service uses the staging Arm64 VM pattern. Docker/runtime compatibility remains a future verification item: use multi-architecture .NET images and test any future Kafka/native dependency on Arm64 before deployment.

## Future Deployment Work

1. Start the VM and configure restricted deployment access.
2. Build/test a real Dockerfile locally.
3. Install Docker and deploy the Job API.
4. Configure reviewed private MySQL and Kafka settings, then verify health checks.

No application port, secret, or Kafka integration has been configured in Azure yet.
