#!/bin/sh

temporal worker deployment set-current-version \
  --deployment-name pbm-adjudication --build-id 2.0 \
  --yes --address localhost:7233
