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

## Deployment Verification

| Check | Result |
|---|---|
| Application source SHA | `edd867585f018baf3fc6facfe465ffcccc57b5a3` (developer CI manually verified PASS) |
| DevOps branch / commit | `ASSMS-3-us-03-staging-deployment` / `c35b749` |
| Image | `assms-job-service:edd867585f018baf3fc6facfe465ffcccc57b5a3` (`linux/arm64`) |
| Database migration | `database/migrations/V01__create_jobs.sql` — PASS |
| Runtime | Docker container `assms-job-service`, loopback-only `127.0.0.1:8080` |
| HTTPS endpoint | `https://assms-job-staging-45ff260826.southeastasia.cloudapp.azure.com` |
| Health / DB health | `GET /api/health` 200; `GET /api/health/db` 200 |

Manual smoke verification created `JOB-GYG1XB` with status `CREATED` and verified both retrieval routes (`GET /api/jobs/{id}` and `GET /api/jobs/reference/{jobReference}`). The persisted customer, asset, category, problem description, priority, and region matched the submitted request. A request missing required fields returned 400, and a real mismatched customer/asset request returned 409. No invalid job was accepted.

`KAFKA_SPRINT1_DEFERRED`: the Kafka VM remains deallocated; JobCreated publication was not claimed or tested.

Nginx terminates HTTPS and proxies only to the loopback container. The certificate is active with automatic renewal enabled. TCP 8080, MySQL, Kafka, and SSH are not publicly exposed; no secrets are stored in this document.

## ARM64 Note

The service uses the staging Arm64 VM pattern. Docker/runtime compatibility remains a future verification item: use multi-architecture .NET images and test any future Kafka/native dependency on Arm64 before deployment.

## Future Deployment Work

1. Start the VM and configure restricted deployment access.
2. Build/test a real Dockerfile locally.
3. Install Docker and deploy the Job API.
4. Configure reviewed private MySQL and Kafka settings, then verify health checks.

No application port, secret, or Kafka integration has been configured in Azure yet.
