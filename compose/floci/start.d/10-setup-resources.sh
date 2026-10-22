#!/bin/bash

# S3 buckets. Each one also needs a <bucket>.floci network alias on the floci service in
# compose.yml, because the SDK addresses buckets by hostname.
aws s3 mb s3://cleanse-imports

# SQS queues
# aws sqs create-queue --queue-name my-queue
