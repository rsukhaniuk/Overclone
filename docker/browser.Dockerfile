ARG BROWSER_IMAGE=mcr.microsoft.com/playwright:v1.56.1-noble
FROM ${BROWSER_IMAGE}

# Match the browsers already supplied by the pinned base image. Install once
# at image build time; individual test runs need neither npm nor registry access.
RUN npm install --ignore-scripts --no-audit --no-fund --omit=optional \
    --prefix /opt/browser-test playwright@1.56.1
ENV NODE_PATH=/opt/browser-test/node_modules
