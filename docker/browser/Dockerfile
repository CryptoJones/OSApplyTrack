# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
#
# The browser the agent drives — its own container, on purpose. The api/agent/poller
# images are hardened (read-only, cap_drop ALL, no-new-privileges, noexec /tmp) and
# Chromium cannot run under that: it needs a writable AND executable temp dir and
# user-namespace cloning for its sandbox. So the browser gets Playwright's own
# image, runs `playwright run-server`, and is reached over ws:// from the agent.
# It sits on an internal network with no route anywhere but the forward proxy.
# The playwright package itself is baked in because that network cannot reach npm.
FROM mcr.microsoft.com/playwright:v1.61.1-noble
RUN npm install -g --no-audit --no-fund playwright@1.61.1
USER pwuser
EXPOSE 3000
CMD ["playwright", "run-server", "--port", "3000", "--host", "0.0.0.0"]
