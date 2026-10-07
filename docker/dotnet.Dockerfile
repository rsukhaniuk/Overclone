ARG DOTNET_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble
FROM ${DOTNET_IMAGE}

ARG DEV_UID=1000
ARG DEV_GID=1000
# .NET named mutexes need a resolvable user identity. Numeric --user alone
# fails on hosts whose UID does not already exist in the stock SDK image.
RUN if ! getent group "${DEV_GID}" >/dev/null; then \
        groupadd --gid "${DEV_GID}" developer; \
    fi \
    && if ! getent passwd "${DEV_UID}" >/dev/null; then \
        useradd --no-log-init --uid "${DEV_UID}" --gid "${DEV_GID}" \
            --create-home --shell /bin/bash developer; \
    fi
