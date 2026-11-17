#!/bin/sh
set -eu

# Self-signed certificate for local wss:// testing only. Clients must trust this
# certificate before ClientWebSocket or URLSessionWebSocketTask will connect.
openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
  -keyout server.key -out server.crt -subj "/CN=localhost"

echo "Generated server.crt and server.key."
echo "Start with: TLS_CERT=$PWD/server.crt TLS_KEY=$PWD/server.key npm start"
