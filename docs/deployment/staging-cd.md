# Job Service Staging CD

The Job CI workflow calls the reusable staging CD workflow only after Terraform validation and .NET build/test succeed for a push to `dev`. It checks out and tags the exact tested `github.sha`, builds a `linux/arm64` image, and deploys only `assms-job-service`.

Azure authentication uses GitHub OIDC and the staging managed identity. The runner receives temporary TCP 22 access limited to its public IPv4 `/32`; cleanup always runs. SSH verifies `JOB_VM_SSH_KNOWN_HOSTS`, transfers an image archive, and uses the protected `/etc/assms/job.env` unchanged. The container remains bound to `127.0.0.1:8080`; HTTPS health and DB-health endpoints must return 200.

Required Actions variables: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `JOB_API_URL`, `JOB_NSG_NAME`, `JOB_TEMP_SSH_RULE_PRIORITY`, `JOB_VM_HOST`, `JOB_VM_SSH_USERNAME`, and `JOB_VM_SSH_KNOWN_HOSTS`. Required secret: `JOB_DEPLOY_SSH_PRIVATE_KEY`.

Database migration remains an operator-controlled, idempotence-reviewed step. Kafka is Sprint 1 deferred and this workflow never starts it. Roll back by redeploying a previously CI-validated `dev` SHA.
