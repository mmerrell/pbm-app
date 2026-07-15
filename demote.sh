#!/bin/sh

docker run --rm --network pbm-app_pbm-net --entrypoint temporal temporalio/admin-tools:1.29.4 \
  worker deployment set-current-version --deployment-name pbm-adjudication --build-id 1.0 --yes --address temporal:7233
