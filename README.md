# assms-job-service

## Overview

## Responsibilities

## Technology

## Project Structure

## Local Development

## Environment Variables

## Testing

## Deployment

## Documentation

## Database Ownership

## Kafka Responsibilities

## Terraform Infrastructure

This repository owns only the Job Service VM, NIC, service-specific NSG, and optional public IP. Shared resource-group and services-subnet values are consumed from the platform Terraform remote state or supplied through explicit overrides; this repository does not recreate the shared VNet or subnets.
